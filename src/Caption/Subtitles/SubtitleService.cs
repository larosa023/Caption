using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Caption.Services;
using Caption.Whisper;

namespace Caption.Subtitles
{
    /// <summary>
    /// Runs subtitle jobs for apps (one at a time; a new one replaces the old) on its own speech worker,
    /// loaded with word timing. The worker exits a couple of minutes after the last job, freeing the GPU.
    /// </summary>
    public sealed class SubtitleService : IDisposable
    {
        private readonly AppSettings _settings;
        private readonly object _gate = new();
        private readonly Timer _idleTimer;
        private WorkerClient? _worker;
        private WorkerLoadInfo? _info;
        private string _workerKey = "";
        private SubtitleJob? _job;
        private DateTime _lastActivity = DateTime.UtcNow;

        public SubtitleService(AppSettings settings)
        {
            _settings = settings;
            _idleTimer = new Timer(_ => Tick(), null, TimeSpan.FromSeconds(20), TimeSpan.FromSeconds(20));
        }

        public SubtitleJob? Current { get { lock (_gate) return _job; } }

        /// <summary>Self-test only: use this engine folder and model file instead of the downloaded ones.</summary>
        public (string EngineDir, string ModelPath)? TestOverride { get; set; }

        /// <summary>Raised (any thread) when a job starts, changes or ends.</summary>
        public event Action? Changed;

        public string Status
        {
            get
            {
                var j = Current;
                if (j == null) return "";
                return j.State switch
                {
                    "error" => "Subtitles: " + j.Error,
                    "done" => $"Subtitles for {j.Title}: done",
                    _ => $"Making subtitles for {(j.Title.Length > 0 ? j.Title : "the video")}" + (j.DoneUntil > 0 ? $" · ready to {Fmt(j.DoneUntil)}" : "…"),
                };
            }
        }

        private static string Fmt(double s)
        {
            var t = TimeSpan.FromSeconds(s);
            return t.TotalHours >= 1 ? t.ToString(@"h\:mm\:ss") : t.ToString(@"m\:ss");
        }

        /// <summary>Best model on disk for subtitles: the setting, else Large v3, Turbo, Medium, Small.</summary>
        public ModelInfo? PickModel()
        {
            var chosen = ModelCatalog.Find(_settings.SubtitleModel);
            if (chosen is { IsInstalled: true }) return chosen;
            foreach (var id in new[] { "large-v3", "large-v3-turbo", "medium", "small" })
            {
                var m = ModelCatalog.Find(id);
                if (m is { IsInstalled: true }) return m;
            }
            return null;
        }

        public static int DtwPreset(string modelPath)
        {
            string f = Path.GetFileNameWithoutExtension(modelPath).ToLowerInvariant().Replace("ggml-", "");
            if (f.StartsWith("large-v3-turbo")) return 14;
            if (f.StartsWith("large-v3")) return 13;
            if (f.StartsWith("large-v2")) return 12;
            if (f.StartsWith("large-v1") || f == "large") return 11;
            if (f.StartsWith("medium.en")) return 9;
            if (f.StartsWith("medium")) return 10;
            if (f.StartsWith("small.en")) return 7;
            if (f.StartsWith("small")) return 8;
            if (f.StartsWith("base.en")) return 5;
            if (f.StartsWith("base")) return 6;
            if (f.StartsWith("tiny.en")) return 3;
            if (f.StartsWith("tiny")) return 4;
            return 0;
        }

        /// <summary>Starts subtitles for a stream (replacing any running job). Slow the first time (FFmpeg, model load).</summary>
        public async Task<SubtitleJob> StartAsync(JobRequest req, CancellationToken ct = default)
        {
            if (string.IsNullOrWhiteSpace(req.Url)) throw new ArgumentException("No stream URL.");
            ModelInfo model;
            EngineVariant engine;
            if (TestOverride is { } t)
            {
                model = new ModelInfo { Id = "test", Name = "Test model", File = Path.GetFileName(t.ModelPath) };
                engine = new EngineVariant { Id = "test", Gpu = false };
                _testPaths = t;
            }
            else
            {
                model = PickModel() ?? throw new InvalidOperationException("No speech model is downloaded yet. Open Caption to pick one.");
                engine = EngineManager.Pick(_settings.Processor);
                if (!engine.IsInstalled) await EngineManager.InstallAsync(engine, null, ct);
            }
            if (!Ffmpeg.IsInstalled) await Ffmpeg.EnsureAsync(null, ct);
            if (_testPaths == null && !File.Exists(ModelCatalog.VadPath)) await ModelCatalog.EnsureVadAsync(ct);

            Stop();
            var (worker, info) = await Task.Run(() => GetWorker(engine, model), ct);
            var job = new SubtitleJob(req, worker, info.HasVad, info.GpuActive ? 5 : 2);
            job.Changed += () => Changed?.Invoke();
            lock (_gate) _job = job;
            _lastActivity = DateTime.UtcNow;
            Log.Info($"Subtitle job {job.Id} for \"{req.Title}\" from {req.Position:0}s with {model.Name}");
            Changed?.Invoke();
            return job;
        }

        private (string, string)? _testPaths;

        private (WorkerClient, WorkerLoadInfo) GetWorker(EngineVariant engine, ModelInfo model)
        {
            string engineDir = _testPaths?.Item1 ?? engine.Dir;
            string modelPath = _testPaths?.Item2 ?? model.Path;
            string key = engineDir + "|" + modelPath;
            lock (_gate)
            {
                if (_worker is { IsAlive: true } && _workerKey == key && _info != null) return (_worker, _info);
                try { _worker?.Dispose(); } catch { }
                _worker = null;
            }
            var w = new WorkerClient();
            try
            {
                string vad = _testPaths != null ? Path.Combine(Path.GetDirectoryName(modelPath)!, "vad.bin") : ModelCatalog.VadPath;
                var info = w.Load(engineDir, modelPath, engine.Gpu, vad, engine.Gpu ? 4 : Math.Clamp(Environment.ProcessorCount / 2, 2, 8), DtwPreset(modelPath));
                lock (_gate) { _worker = w; _info = info; _workerKey = key; }
                return (w, info);
            }
            catch
            {
                w.Dispose();
                throw;
            }
        }

        public SubtitleJob? Find(string id)
        {
            lock (_gate) return _job != null && _job.Id == id ? _job : null;
        }

        public void Stop(string? id = null)
        {
            SubtitleJob? job;
            lock (_gate)
            {
                if (_job == null || (id != null && _job.Id != id)) return;
                job = _job;
                _job = null;
            }
            _lastActivity = DateTime.UtcNow;
            Task.Run(() => job.Dispose());
            Changed?.Invoke();
        }

        private void Tick()
        {
            var job = Current;
            // An app that stopped asking (closed without saying so) has had its job for a minute: stop it.
            if (job != null && DateTime.UtcNow - job.LastSeen > TimeSpan.FromMinutes(2)) Stop(job.Id);
            if (Current == null && !_settings.KeepModelLoaded && DateTime.UtcNow - _lastActivity > TimeSpan.FromMinutes(2))
            {
                lock (_gate)
                {
                    if (_worker != null)
                    {
                        try { _worker.Dispose(); } catch { }
                        _worker = null;
                        _workerKey = "";
                        _info = null;
                        Native.TrimWorkingSet();
                    }
                }
            }
        }

        public void Dispose()
        {
            _idleTimer.Dispose();
            Stop();
            lock (_gate)
            {
                try { _worker?.Dispose(); } catch { }
                _worker = null;
            }
        }
    }
}
