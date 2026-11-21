using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Threading;
using Caption.Audio;
using Caption.Services;
using Caption.ViewModels;
using Caption.Whisper;

namespace Caption.Captioning
{
    public enum CaptionState { Off, Starting, Running, Paused, Error }

    /// <summary>
    /// Runs captions: the speech worker, the audio capture and the pipeline, plus the optional Ollama step
    /// and the transcript. Everything it raises arrives on the UI thread.
    /// </summary>
    public sealed class CaptionController : ObservableObject, IDisposable
    {
        private readonly AppSettings _settings;
        private readonly Dispatcher _ui;
        private WorkerClient? _worker;
        private string _workerKey = "";
        private WorkerLoadInfo? _loadInfo;
        private CaptionPipeline? _pipeline;
        private AudioCapture? _capture;
        private CancellationTokenSource? _startCts;
        private readonly DispatcherTimer _statsTimer;
        private readonly SemaphoreSlim _translateGate = new(1, 1);
        private CaptionState _state = CaptionState.Off;
        private string _status = "Captions are off";
        private string _partial = "";
        private string _sourceStatus = "";
        private string _engineText = "";
        private double _progress = -1;
        private int _lineNumber;
        private bool _ollamaWarned;

        public CaptionController(AppSettings settings, Dispatcher ui)
        {
            _settings = settings;
            _ui = ui;
            _statsTimer = new DispatcherTimer(DispatcherPriority.Background, ui) { Interval = TimeSpan.FromMilliseconds(500) };
            _statsTimer.Tick += (_, _) => OnPropertyChanged(nameof(Stats));
        }

        public Transcript Transcript { get; } = new();

        public CaptionState State { get => _state; private set { if (SetProperty(ref _state, value)) { OnPropertyChanged(nameof(IsOn)); OnPropertyChanged(nameof(IsBusy)); StateChanged?.Invoke(); } } }
        public bool IsOn => State is CaptionState.Running or CaptionState.Paused or CaptionState.Starting;
        public bool IsBusy => State == CaptionState.Starting;
        public string Status { get => _status; private set => SetProperty(ref _status, value); }
        public string SourceStatus { get => _sourceStatus; private set => SetProperty(ref _sourceStatus, value); }
        public string EngineText { get => _engineText; private set => SetProperty(ref _engineText, value); }
        /// <summary>Download progress 0..1 while preparing, or -1.</summary>
        public double Progress { get => _progress; private set => SetProperty(ref _progress, value); }
        /// <summary>The line being spoken right now (corrected in place), "" when nobody's talking.</summary>
        public string Partial { get => _partial; private set => SetProperty(ref _partial, value); }

        public string Stats
        {
            get
            {
                var p = _pipeline;
                if (p == null || State != CaptionState.Running) return "";
                string s = $"Delay ~{Math.Max(0.1, p.LastDelayMs / 1000.0):0.0} s · read in {p.LastInferenceMs} ms";
                if (!string.IsNullOrEmpty(p.DetectedLanguage)) s += " · " + Languages.Name(p.DetectedLanguage);
                return s;
            }
        }

        public float Level => _capture?.Level ?? 0;

        public event Action? StateChanged;
        public event Action<CaptionLine>? LineAdded;
        public event Action<CaptionLine>? LineUpdated;
        public event Action<string>? PartialChanged;
        /// <summary>Something the user should know (shown as a small notice).</summary>
        public event Action<string>? Notice;
        /// <summary>No model yet: the first-run setup should be shown.</summary>
        public event Action? NeedsSetup;

        private Profile Profile => _settings.CurrentProfile;

        // ---------------------------------------------------------------- start / stop

        public async Task StartAsync()
        {
            if (State is CaptionState.Running or CaptionState.Starting) return;
            var model = ModelCatalog.Find(_settings.Model);
            if (model == null || !model.IsInstalled)
            {
                // Use whatever model is on disk, if any.
                var any = ModelCatalog.Models.FirstOrDefault(m => m.IsInstalled);
                if (any != null) { _settings.Model = any.Id; SettingsStore.Save(_settings); model = any; }
            }
            if (model == null || !model.IsInstalled)
            {
                NeedsSetup?.Invoke();
                return;
            }

            State = CaptionState.Starting;
            Status = "Starting…";
            Partial = "";
            _startCts = new CancellationTokenSource();
            var ct = _startCts.Token;
            try
            {
                var variant = EngineManager.Pick(_settings.Processor);
                await EnsureEngineAsync(variant, ct);
                if (!File.Exists(ModelCatalog.VadPath)) _ = ModelCatalog.EnsureVadAsync(CancellationToken.None);

                Status = $"Loading {model.Name}…";
                await LoadWorkerAsync(variant, model, ct);
                ct.ThrowIfCancellationRequested();
                StartListening();
                State = CaptionState.Running;
                Status = "Listening";
                _statsTimer.Start();
            }
            catch (OperationCanceledException)
            {
                TearDown(keepWorker: false);
                State = CaptionState.Off;
                Status = "Captions are off";
                Progress = -1;
            }
            catch (Exception ex)
            {
                Log.Error("Starting captions", ex);
                TearDown(keepWorker: false);
                State = CaptionState.Error;
                Status = "Couldn't start: " + ex.Message;
                Progress = -1;
                Notice?.Invoke("Captions couldn't start: " + ex.Message);
            }
        }

        private async Task EnsureEngineAsync(EngineVariant variant, CancellationToken ct)
        {
            if (variant.IsInstalled) return;
            Status = $"Downloading the speech engine ({variant.SizeText}, one time)…";
            var progress = new Progress<DownloadProgress>(p =>
            {
                Progress = p.Fraction;
                Status = $"Downloading the speech engine… {p.Text}";
            });
            await Task.Run(() => EngineManager.InstallAsync(variant, progress, ct), ct);
            Progress = -1;
        }

        private async Task LoadWorkerAsync(EngineVariant variant, ModelInfo model, CancellationToken ct)
        {
            string key = $"{variant.Dir}|{model.Path}";
            if (_worker is { IsAlive: true } && _workerKey == key && _loadInfo != null) return;
            DisposeWorker();
            int threads = Math.Clamp(Environment.ProcessorCount / 2, 2, 8);
            try
            {
                (_worker, _loadInfo) = await Task.Run(() =>
                {
                    var w = new WorkerClient();
                    try { return (w, w.Load(variant.Dir, model.Path, variant.Gpu, ModelCatalog.VadPath, variant.Gpu ? 4 : threads)); }
                    catch { w.Dispose(); throw; }
                }, ct);
                _workerKey = key;
                if (variant.Gpu && !_loadInfo.GpuActive)
                    Notice?.Invoke("The GPU couldn't be used, so captions are running on the CPU.");
            }
            catch (Exception ex) when (variant.Gpu && ex is not OperationCanceledException)
            {
                // CUDA engine failed to load (old driver?): fall back to the CPU build.
                Log.Error("GPU engine", ex);
                Notice?.Invoke("The GPU engine couldn't start, so Caption is using the CPU. " + ex.Message);
                var cpu = EngineManager.Cpu;
                await EnsureEngineAsync(cpu, ct);
                (_worker, _loadInfo) = await Task.Run(() =>
                {
                    var w = new WorkerClient();
                    try { return (w, w.Load(cpu.Dir, model.Path, false, ModelCatalog.VadPath, threads)); }
                    catch { w.Dispose(); throw; }
                }, ct);
                _workerKey = $"{cpu.Dir}|{model.Path}";
            }
            EngineText = $"{model.Name} on {(_loadInfo!.GpuActive ? "GPU" : "CPU")}" + (_loadInfo.HasVad ? "" : " · basic voice detection");
        }

        private PipelineOptions Options()
        {
            var p = Profile;
            return new PipelineOptions
            {
                Language = p.Language,
                TranslateToEnglish = p.Translate == "english",
                KeepOriginal = p.Translate == "english" && p.ShowOriginal,
                UpdateIntervalMs = _settings.UpdateIntervalMs,
                PauseToEndMs = _settings.PauseToEndMs,
                MaxLineSeconds = _settings.MaxLineSeconds,
                VadThreshold = (float)_settings.VadThreshold,
                ShowSoundLabels = _settings.ShowSoundLabels,
                FilterHallucinations = _settings.FilterHallucinations,
            };
        }

        private void StartListening()
        {
            var worker = _worker ?? throw new InvalidOperationException("No speech worker.");
            var pipeline = new CaptionPipeline(worker, _loadInfo?.HasVad == true, Options());
            pipeline.Partial += text => _ui.InvokeAsync(() =>
            {
                if (_pipeline != pipeline) return;
                Partial = text;
                PartialChanged?.Invoke(text);
            });
            pipeline.Finished += line => _ui.InvokeAsync(() => { if (_pipeline == pipeline) OnFinished(line); });
            pipeline.Failed += ex => _ui.InvokeAsync(() => { if (_pipeline == pipeline) OnPipelineFailed(ex); });
            _pipeline = pipeline;

            var capture = new AudioCapture(Profile, pipeline.Feed);
            capture.StatusChanged += () => _ui.InvokeAsync(() => { if (_capture == capture) SourceStatus = capture.Status; });
            _capture = capture;
            capture.Start();
            SourceStatus = capture.Status;
        }

        private void OnFinished(FinishedLine f)
        {
            var line = new CaptionLine
            {
                Number = ++_lineNumber,
                Wall = DateTime.Now,
                Start = f.Start,
                End = f.End,
                Language = f.Language,
                Original = f.Original,
                Text = f.Text,
            };
            Partial = "";
            PartialChanged?.Invoke("");
            Transcript.Add(line);
            LineAdded?.Invoke(line);
            if (Profile.Translate == "ollama") _ = TranslateAsync(line);
        }

        private async Task TranslateAsync(CaptionLine line)
        {
            string model = _settings.OllamaModel;
            string target = Profile.TargetLanguage;
            if (string.IsNullOrWhiteSpace(model))
            {
                WarnOllama("Pick an Ollama model in Settings → Translation to translate captions.");
                return;
            }
            string? targetCode = Languages.CodeForName(target);
            if (targetCode != null && targetCode == line.Language) return; // already in that language

            // Context: the two lines before this one.
            var prev = Transcript.Lines.TakeWhile(l => l != line).Reverse().Take(2).Reverse().Select(l => l.Text).ToList();
            string context = string.Join("\n", prev);
            await _translateGate.WaitAsync();
            try
            {
                var client = new OllamaClient(_settings.OllamaUrl);
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                string? result = await client.TranslateAsync(model, line.Text, target, Languages.Name(line.Language), context, cts.Token);
                if (result != null)
                {
                    line.Translation = result;
                    LineUpdated?.Invoke(line);
                }
            }
            catch (Exception ex)
            {
                Log.WarnOnce("Ollama translation", ex);
                WarnOllama("Ollama isn't answering, so captions aren't being translated. Is Ollama running?");
            }
            finally
            {
                _translateGate.Release();
            }
        }

        private void WarnOllama(string message)
        {
            if (_ollamaWarned) return;
            _ollamaWarned = true;
            Notice?.Invoke(message);
        }

        private DateTime _lastFailure = DateTime.MinValue;

        private void OnPipelineFailed(Exception ex)
        {
            // Usually the worker process died (driver reset, out of VRAM). Try once to bring it back.
            TearDown(keepWorker: false);
            if (DateTime.Now - _lastFailure < TimeSpan.FromMinutes(1))
            {
                State = CaptionState.Error;
                Status = "The speech engine keeps stopping: " + ex.Message;
                Notice?.Invoke(Status);
                return;
            }
            _lastFailure = DateTime.Now;
            Notice?.Invoke("The speech engine stopped unexpectedly. Restarting it…");
            State = CaptionState.Off;
            _ = StartAsync();
        }

        public void Stop()
        {
            _startCts?.Cancel();
            if (State == CaptionState.Off) return;
            AutoSave();
            TearDown(keepWorker: _settings.KeepModelLoaded);
            State = CaptionState.Off;
            Status = "Captions are off";
            SourceStatus = "";
            Partial = "";
            PartialChanged?.Invoke("");
            Progress = -1;
            Native.TrimWorkingSet();
        }

        public void TogglePause()
        {
            if (_pipeline == null) return;
            if (State == CaptionState.Running)
            {
                _pipeline.Paused = true;
                State = CaptionState.Paused;
                Status = "Paused";
                Partial = "";
                PartialChanged?.Invoke("");
            }
            else if (State == CaptionState.Paused)
            {
                _pipeline.Paused = false;
                State = CaptionState.Running;
                Status = "Listening";
            }
        }

        /// <summary>Profile or listening settings changed: apply without reloading the model.</summary>
        public void ApplySettings(bool sourcesChanged)
        {
            if (_pipeline == null) return;
            _pipeline.UpdateOptions(Options());
            if (sourcesChanged && State is CaptionState.Running or CaptionState.Paused)
            {
                _capture?.Dispose();
                var capture = new AudioCapture(Profile, _pipeline.Feed);
                capture.StatusChanged += () => _ui.InvokeAsync(() => { if (_capture == capture) SourceStatus = capture.Status; });
                _capture = capture;
                try { capture.Start(); SourceStatus = capture.Status; }
                catch (Exception ex) { Log.Error("Switching audio", ex); SourceStatus = "Couldn't open audio: " + ex.Message; }
            }
        }

        /// <summary>Model or processor changed: the next start loads the new one.</summary>
        public async Task ReloadAsync()
        {
            bool wasOn = IsOn;
            Stop();
            DisposeWorker();
            if (wasOn) await StartAsync();
        }

        public void OnKeepLoadedChanged()
        {
            if (!_settings.KeepModelLoaded && State == CaptionState.Off) DisposeWorker();
        }

        private void TearDown(bool keepWorker)
        {
            _statsTimer.Stop();
            try { _capture?.Dispose(); } catch { }
            _capture = null;
            try { _pipeline?.Dispose(); } catch { }
            _pipeline = null;
            if (!keepWorker) DisposeWorker();
        }

        private void DisposeWorker()
        {
            try { _worker?.Dispose(); } catch { }
            _worker = null;
            _workerKey = "";
            _loadInfo = null;
        }

        private void AutoSave()
        {
            if (!_settings.AutoSaveTranscripts || Transcript.Lines.Count == 0) return;
            try
            {
                Directory.CreateDirectory(Paths.TranscriptsDir);
                string name = Transcript.StartedAt.ToString("yyyy-MM-dd HHmm");
                Transcript.Save(Path.Combine(Paths.TranscriptsDir, name + ".txt"));
                Transcript.Save(Path.Combine(Paths.TranscriptsDir, name + ".srt"));
            }
            catch (Exception ex)
            {
                Log.WarnOnce("Auto-saving transcript", ex);
            }
        }

        public void Dispose()
        {
            _startCts?.Cancel();
            AutoSave();
            TearDown(keepWorker: false);
        }
    }
}
