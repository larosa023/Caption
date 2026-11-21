using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using Caption.Audio;
using Caption.Captioning;
using Caption.Services;
using Caption.Whisper;

namespace Caption.ViewModels
{
    public sealed class MainViewModel : ObservableObject
    {
        private readonly AppSettings _s;
        private string _page = "home";
        private CancellationTokenSource? _downloadCts;
        private ProfileItem? _selectedProfile;
        private string _newAppText = "";
        private string _ollamaStatus = "";
        private bool _ollamaBusy;
        private string _customAccent = "";
        private string _hotkeyWarning = "";
        private string _notice = "";
        private string _setupStatus = "";
        private bool _setupBusy;
        private double _setupProgress;
        private ModelItem? _setupModel;
        private readonly System.Windows.Threading.DispatcherTimer _noticeTimer;

        public MainViewModel(AppSettings settings, CaptionController controller, UpdateService updater)
        {
            _s = settings;
            Controller = controller;
            Updater = updater;
            Gpu = GpuInfo.Detect();

            RefreshDevices();
            Profiles = new ObservableCollection<ProfileItem>(_s.Profiles.Select(MakeProfileItem));
            UpdateActiveProfile();
            _selectedProfile = Profiles.FirstOrDefault(p => p.IsActive) ?? Profiles.FirstOrDefault();

            Models = new ObservableCollection<ModelItem>(ModelCatalog.Models.Select(MakeModelItem));
            _setupModel = Models.FirstOrDefault(m => m.Info.Id == ModelCatalog.Suggested(Gpu.HasNvidia).Id);
            UpdateModelStates();

            BuildThemes();
            AccentSwatches = new ObservableCollection<SwatchItem>(
                new[] { new SwatchItem("theme", "Theme", Brushes.Transparent), new SwatchItem("windows", "Windows", ThemeManager.Brush(ThemeManager.Rgb(ThemeManager.SystemAccent()))) }
                .Concat(ThemeManager.AccentSwatches.Select(c => new SwatchItem(ThemeManager.Hex(c), ThemeManager.Hex(c), ThemeManager.Brush(ThemeManager.Rgb(c))))));
            _customAccent = _s.Accent.StartsWith("#") ? _s.Accent : "";
            UpdateAccentSelection();

            FontFamilies = Fonts.SystemFontFamilies.Select(f => f.Source).Distinct().OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList();

            controller.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName is nameof(CaptionController.State))
                {
                    OnPropertyChanged(nameof(ToggleText));
                    OnPropertyChanged(nameof(StateTitle));
                    OnPropertyChanged(nameof(PauseText));
                    OnPropertyChanged(nameof(NeedsSetup));
                }
            };
            controller.LineAdded += line =>
            {
                RecentLines.Add(line);
                while (RecentLines.Count > 4) RecentLines.RemoveAt(0);
            };
            controller.Notice += ShowNotice;
            ThemeManager.ThemeChanged += (_, _) => { foreach (var t in Themes) t.IsSelected = t.Id == _s.Theme; };
            updater.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(UpdateService.UpdateReady)) OnPropertyChanged(nameof(UpdateBadge));
            };

            _noticeTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(9) };
            _noticeTimer.Tick += (_, _) => { _noticeTimer.Stop(); Notice = ""; };

            // ----- commands -----
            NavigateCommand = new RelayCommand(p => Page = p as string ?? "home");
            ToggleCaptionsCommand = new RelayCommand(() => ToggleCaptionsRequested?.Invoke(this, EventArgs.Empty));
            PauseCommand = new RelayCommand(() => Controller.TogglePause());
            ShowTranscriptCommand = new RelayCommand(() => ShowTranscriptRequested?.Invoke(this, EventArgs.Empty));
            MoveBarCommand = new RelayCommand(() => MoveBarRequested?.Invoke(this, EventArgs.Empty));
            UseProfileCommand = new RelayCommand(p => { if (p is ProfileItem item) SetActiveProfile(item.Id); });
            AddProfileCommand = new RelayCommand(AddProfile);
            DuplicateProfileCommand = new RelayCommand(DuplicateProfile);
            DeleteProfileCommand = new RelayCommand(DeleteProfile, _ => Profiles.Count > 1);
            AddAppCommand = new RelayCommand(p =>
            {
                string exe = p is AppChoice a ? a.Exe : p as string ?? NewAppText;
                SelectedProfile?.AddApp(exe);
                NewAppText = "";
            });
            RemoveAppCommand = new RelayCommand(p => { if (p is string exe) SelectedProfile?.RemoveApp(exe); });
            RefreshAppsCommand = new RelayCommand(RefreshApps);
            RefreshDevicesCommand = new RelayCommand(() => { RefreshDevices(); foreach (var p in Profiles) p.RefreshDevices(); });
            SetupDownloadCommand = new RelayCommand(_ => _ = SetupAsync(), _ => !SetupBusy && SetupModel != null);
            CancelDownloadCommand = new RelayCommand(() => _downloadCts?.Cancel());
            RefreshOllamaCommand = new RelayCommand(() => _ = RefreshOllamaAsync());
            SelectThemeCommand = new RelayCommand(p => { if (p is string id) { _s.Theme = id; ApplyTheme(); } });
            SelectAccentCommand = new RelayCommand(p => { if (p is string a) { _s.Accent = a; ApplyTheme(); } });
            ApplyCustomAccentCommand = new RelayCommand(() =>
            {
                if (ThemeManager.TryParseHex(CustomAccent, out uint c)) { _s.Accent = ThemeManager.Hex(c); ApplyTheme(); }
            });
            OpenThemesFolderCommand = new RelayCommand(() => { Directory.CreateDirectory(Paths.ThemesDir); Open(Paths.ThemesDir); });
            ReloadThemesCommand = new RelayCommand(() => { ThemeManager.LoadCustomThemes(); BuildThemes(); ApplyTheme(); });
            SaveThemeCommand = new RelayCommand(() =>
            {
                try { string path = ThemeManager.SaveCurrentAsFile(); ShowNotice($"Saved {Path.GetFileName(path)} in your themes folder."); ThemeManager.LoadCustomThemes(); BuildThemes(); }
                catch (Exception ex) { ShowNotice("Couldn't save the theme: " + ex.Message); }
            });
            ApplyBarPresetCommand = new RelayCommand(p => { if (p is string preset) ApplyBarPreset(preset); });
            SetBarCommand = new RelayCommand(p =>
            {
                if (p is not string kv) return;
                var parts = kv.Split('=');
                if (parts.Length != 2) return;
                switch (parts[0])
                {
                    case "layout": BarLayout = parts[1]; break;
                    case "position": BarPosition = parts[1]; break;
                    case "align": BarAlign = parts[1]; break;
                    case "text": BarTextColor = parts[1]; break;
                    case "bg": BarBackground = parts[1]; break;
                }
            });
            SetTextColorCommand = new RelayCommand(p => { if (p is string v) BarTextColor = v; });
            SetBgColorCommand = new RelayCommand(p => { if (p is string v) BarBackground = v; });
            SetProcessorCommand = new RelayCommand(p => { if (p is string v) Processor = v; });
            SetSourceCommand = new RelayCommand(p => { if (p is string v && SelectedProfile != null) SelectedProfile.Source = v; });
            SetTranslateCommand = new RelayCommand(p => { if (p is string v && SelectedProfile != null) SelectedProfile.Translate = v; });
            SetChannelCommand = new RelayCommand(p => { if (p is string v) UpdateChannel = v; });
            CheckUpdatesCommand = new RelayCommand(() => _ = Updater.CheckAsync(manual: true));
            RestartToUpdateCommand = new RelayCommand(() => RestartToUpdateRequested?.Invoke(this, EventArgs.Empty));
            RollbackCommand = new RelayCommand(p => { if (p is string v) RollbackRequested?.Invoke(this, v); });
            OpenDataFolderCommand = new RelayCommand(() => Open(Paths.DataDir));
            OpenTranscriptsFolderCommand = new RelayCommand(() => { Directory.CreateDirectory(Paths.TranscriptsDir); Open(Paths.TranscriptsDir); });
            OpenLogCommand = new RelayCommand(() => Open(Paths.LogFile));
            OpenGitHubCommand = new RelayCommand(() => Open(UpdateService.RepoUrl));
            OpenReleaseNotesCommand = new RelayCommand(() => Open(UpdateService.RepoUrl + "/releases"));
            QuitCommand = new RelayCommand(() => QuitRequested?.Invoke(this, EventArgs.Empty));
            DismissNoticeCommand = new RelayCommand(() => Notice = "");
        }

        public AppSettings Settings => _s;
        public CaptionController Controller { get; }
        public UpdateService Updater { get; }
        public GpuInfo Gpu { get; }

        public event EventHandler? ToggleCaptionsRequested;
        public event EventHandler? ShowTranscriptRequested;
        public event EventHandler? MoveBarRequested;
        public event EventHandler? QuitRequested;
        public event EventHandler? RestartToUpdateRequested;
        public event EventHandler<string>? RollbackRequested;
        public event EventHandler? HotkeysChanged;
        public event EventHandler? BarStyleChanged;
        public event EventHandler? ProfilesChanged;

        public void Save() => SettingsStore.Save(_s);

        private static void Open(string target)
        {
            try { Process.Start(new ProcessStartInfo(target) { UseShellExecute = true }); }
            catch (Exception ex) { Log.WarnOnce("Opening " + target, ex); }
        }

        // ================================================================ navigation

        public string Page
        {
            get => _page;
            set
            {
                if (!SetProperty(ref _page, value)) return;
                if (value == "translation") _ = RefreshOllamaAsync();
                if (value == "profiles") { RefreshApps(); }
                if (value == "model") UpdateModelStates();
                if (value == "general") OnPropertyChanged(nameof(RollbackVersions));
            }
        }

        public string AppVersion => "Caption " + UpdateService.AppVersion + (Paths.IsPortable ? " · portable" : "");
        public bool UpdateBadge => Updater.UpdateReady;

        public string Notice { get => _notice; private set => SetProperty(ref _notice, value); }

        public void ShowNotice(string text)
        {
            Notice = text;
            _noticeTimer.Stop();
            _noticeTimer.Start();
        }

        // ================================================================ home

        public ICommand NavigateCommand { get; }
        public ICommand ToggleCaptionsCommand { get; }
        public ICommand PauseCommand { get; }
        public ICommand ShowTranscriptCommand { get; }
        public ICommand MoveBarCommand { get; }
        public ICommand DismissNoticeCommand { get; }

        public ObservableCollection<CaptionLine> RecentLines { get; } = new();

        public string ToggleText => Controller.State switch
        {
            CaptionState.Off or CaptionState.Error => "Start captions",
            CaptionState.Starting => "Cancel",
            _ => "Stop captions",
        };

        public string StateTitle => Controller.State switch
        {
            CaptionState.Running => "Captions are on",
            CaptionState.Paused => "Captions are paused",
            CaptionState.Starting => "Getting ready…",
            CaptionState.Error => "Captions stopped",
            _ => "Captions are off",
        };

        public string PauseText => Controller.State == CaptionState.Paused ? "Resume" : "Pause";

        public string ToggleHotkeyText => HotkeyManager.Format(_s.ToggleHotkey);

        /// <summary>No model downloaded yet: Home shows the first-run setup.</summary>
        public bool NeedsSetup => !ModelCatalog.Models.Any(m => m.IsInstalled);

        // ---------- first-run setup ----------

        public ICommand SetupDownloadCommand { get; }
        public ICommand CancelDownloadCommand { get; }

        public string GpuSummary => Gpu.Summary;
        public bool HasGpu => Gpu.HasNvidia;

        public string EngineDownloadText
        {
            get
            {
                var v = EngineManager.Pick(_s.Processor);
                return v.IsInstalled ? $"Speech engine for the {(v.Gpu ? "GPU" : "CPU")} is installed."
                    : $"Plus the speech engine for your {(v.Gpu ? "GPU" : "CPU")}: {v.SizeText}, downloaded once.";
            }
        }

        public ModelItem? SetupModel
        {
            get => _setupModel;
            set => SetProperty(ref _setupModel, value);
        }

        public bool SetupBusy { get => _setupBusy; private set => SetProperty(ref _setupBusy, value); }
        public double SetupProgress { get => _setupProgress; private set => SetProperty(ref _setupProgress, value); }
        public string SetupStatus { get => _setupStatus; private set => SetProperty(ref _setupStatus, value); }

        /// <summary>First run: download the engine and the chosen model, then start captions.</summary>
        private async Task SetupAsync()
        {
            var model = SetupModel;
            if (model == null) return;
            SetupBusy = true;
            _downloadCts = new CancellationTokenSource();
            var ct = _downloadCts.Token;
            try
            {
                var engine = EngineManager.Pick(_s.Processor);
                if (!engine.IsInstalled)
                {
                    SetupStatus = "Downloading the speech engine…";
                    var p = new Progress<DownloadProgress>(x => { SetupProgress = x.Fraction * 0.3; SetupStatus = "Speech engine · " + x.Text; });
                    await Task.Run(() => EngineManager.InstallAsync(engine, p, ct), ct);
                }
                if (!model.Info.IsInstalled)
                {
                    var p = new Progress<DownloadProgress>(x => { SetupProgress = 0.3 + x.Fraction * 0.7; SetupStatus = $"{model.Name} · {x.Text}"; });
                    await Task.Run(() => ModelCatalog.DownloadAsync(model.Info, p, ct), ct);
                }
                _s.Model = model.Info.Id;
                _s.FirstRunDone = true;
                Save();
                SetupProgress = 1;
                SetupStatus = "";
                UpdateModelStates();
                OnPropertyChanged(nameof(NeedsSetup));
                OnPropertyChanged(nameof(EngineDownloadText));
                await Controller.StartAsync();
            }
            catch (OperationCanceledException)
            {
                SetupStatus = "Download paused. Press the button again to continue where it stopped.";
            }
            catch (Exception ex)
            {
                Log.Error("Setup download", ex);
                SetupStatus = "Download failed: " + ex.Message;
            }
            finally
            {
                SetupBusy = false;
            }
        }

        // ================================================================ profiles

        public ObservableCollection<ProfileItem> Profiles { get; }
        public List<DeviceChoice> OutputDevices { get; private set; } = new();
        public List<DeviceChoice> InputDevices { get; private set; } = new();
        public ObservableCollection<AppChoice> RunningApps { get; } = new();
        public List<Choice> SpokenLanguages { get; } = Languages.SpokenChoices.Select(l => new Choice(l.Code, l.Name)).ToList();
        public string[] TargetLanguages => Languages.TranslationTargets;

        public ICommand UseProfileCommand { get; }
        public ICommand AddProfileCommand { get; }
        public ICommand DuplicateProfileCommand { get; }
        public ICommand DeleteProfileCommand { get; }
        public ICommand AddAppCommand { get; }
        public ICommand RemoveAppCommand { get; }
        public ICommand RefreshAppsCommand { get; }
        public ICommand RefreshDevicesCommand { get; }
        public ICommand SetSourceCommand { get; }
        public ICommand SetTranslateCommand { get; }

        public ProfileItem? SelectedProfile
        {
            get => _selectedProfile;
            set => SetProperty(ref _selectedProfile, value);
        }

        public ProfileItem? ActiveProfile
        {
            get => Profiles.FirstOrDefault(p => p.IsActive);
            set { if (value != null) SetActiveProfile(value.Id); }
        }

        public string NewAppText { get => _newAppText; set => SetProperty(ref _newAppText, value); }

        private ProfileItem MakeProfileItem(Profile p) => new(p, OnProfileEdited,
            id => OutputDevices.FirstOrDefault(d => d.Id == id),
            id => InputDevices.FirstOrDefault(d => d.Id == id));

        private void RefreshDevices()
        {
            OutputDevices = AudioCapture.OutputDevices();
            InputDevices = new[] { new DeviceChoice("", "Default microphone") }.Concat(AudioCapture.InputDevices()).ToList();
            OnPropertyChanged(nameof(OutputDevices));
            OnPropertyChanged(nameof(InputDevices));
        }

        private void RefreshApps()
        {
            RunningApps.Clear();
            foreach (var a in AudioCapture.RunningApps()) RunningApps.Add(a);
        }

        private void OnProfileEdited(ProfileItem item, bool sourcesChanged)
        {
            Save();
            if (item.IsActive) Controller.ApplySettings(sourcesChanged);
            ProfilesChanged?.Invoke(this, EventArgs.Empty);
        }

        public void SetActiveProfile(string id)
        {
            if (_s.ActiveProfile == id) return;
            _s.ActiveProfile = id;
            Save();
            UpdateActiveProfile();
            Controller.ApplySettings(sourcesChanged: true);
            ProfilesChanged?.Invoke(this, EventArgs.Empty);
        }

        private void UpdateActiveProfile()
        {
            foreach (var p in Profiles) p.IsActive = p.Id == _s.ActiveProfile;
            OnPropertyChanged(nameof(ActiveProfile));
        }

        private void AddProfile()
        {
            var p = new Profile { Name = "New profile" };
            _s.Profiles.Add(p);
            var item = MakeProfileItem(p);
            Profiles.Add(item);
            SelectedProfile = item;
            Save();
            ProfilesChanged?.Invoke(this, EventArgs.Empty);
        }

        private void DuplicateProfile()
        {
            if (SelectedProfile == null) return;
            var copy = SelectedProfile.Profile.Clone();
            copy.Id = Guid.NewGuid().ToString("N").Substring(0, 8);
            copy.Name = SelectedProfile.Name + " copy";
            _s.Profiles.Add(copy);
            var item = MakeProfileItem(copy);
            Profiles.Add(item);
            SelectedProfile = item;
            Save();
            ProfilesChanged?.Invoke(this, EventArgs.Empty);
        }

        private void DeleteProfile(object? _)
        {
            var item = SelectedProfile;
            if (item == null || Profiles.Count <= 1) return;
            int index = Profiles.IndexOf(item);
            Profiles.Remove(item);
            _s.Profiles.Remove(item.Profile);
            if (_s.ActiveProfile == item.Id) SetActiveProfile(Profiles[0].Id);
            SelectedProfile = Profiles[Math.Clamp(index, 0, Profiles.Count - 1)];
            Save();
            ProfilesChanged?.Invoke(this, EventArgs.Empty);
        }

        // ================================================================ caption bar

        public List<string> FontFamilies { get; }
        public ICommand ApplyBarPresetCommand { get; }
        public ICommand SetBarCommand { get; }
        public ICommand SetTextColorCommand { get; }
        public ICommand SetBgColorCommand { get; }

        public static readonly string[] TextSwatches = { "#FFFFFF", "#FFE45C", "#7CF2C4", "#8FD3FF", "#FFB3D9", "#000000" };
        public static readonly string[] BackgroundSwatches = { "#000000", "#16161A", "#1E2A44", "#2B1D3A", "#FFFFFF", "#FFE45C" };
        public string[] TextColors => TextSwatches;
        public string[] BackgroundColors => BackgroundSwatches;

        private void BarChanged([System.Runtime.CompilerServices.CallerMemberName] string? name = null)
        {
            OnPropertyChanged(name);
            Save();
            BarStyleChanged?.Invoke(this, EventArgs.Empty);
        }

        private void Custom()
        {
            if (_s.Bar.Preset != "custom") { _s.Bar.Preset = "custom"; OnPropertyChanged(nameof(BarPreset)); }
        }

        public string BarPreset => _s.Bar.Preset;

        public void ApplyBarPreset(string preset)
        {
            var b = _s.Bar;
            b.Preset = preset;
            switch (preset)
            {
                case "classic": b.TextColor = "#FFFFFF"; b.BackgroundColor = "#000000"; b.BackgroundOpacity = 0.72; b.Shadow = false; b.Bold = true; break;
                case "cinema": b.TextColor = "#FFE45C"; b.BackgroundColor = "#000000"; b.BackgroundOpacity = 0; b.Shadow = true; b.Bold = true; break;
                case "minimal": b.TextColor = "#FFFFFF"; b.BackgroundColor = "#000000"; b.BackgroundOpacity = 0.0; b.Shadow = true; b.Bold = false; break;
                case "contrast": b.TextColor = "#FFE45C"; b.BackgroundColor = "#000000"; b.BackgroundOpacity = 1.0; b.Shadow = false; b.Bold = true; break;
                case "theme": b.BackgroundOpacity = 0.92; b.Shadow = false; b.Bold = true; break;
            }
            foreach (var n in new[] { nameof(BarPreset), nameof(BarTextColor), nameof(BarBackground), nameof(BarOpacity), nameof(BarShadow), nameof(BarBold) })
                OnPropertyChanged(n);
            Save();
            BarStyleChanged?.Invoke(this, EventArgs.Empty);
        }

        public string BarLayout { get => _s.Bar.Layout; set { _s.Bar.Layout = value; BarChanged(); } }
        public string BarPosition { get => _s.Bar.Position; set { _s.Bar.Position = value; BarChanged(); } }
        public string BarAlign { get => _s.Bar.Align; set { _s.Bar.Align = value; BarChanged(); } }
        public string BarFont { get => _s.Bar.FontFamily; set { if (!string.IsNullOrWhiteSpace(value)) { _s.Bar.FontFamily = value; BarChanged(); } } }
        public double BarFontSize { get => _s.Bar.FontSize; set { _s.Bar.FontSize = Math.Round(value); BarChanged(); } }
        public bool BarBold { get => _s.Bar.Bold; set { _s.Bar.Bold = value; Custom(); BarChanged(); } }
        public string BarTextColor { get => _s.Bar.TextColor; set { if (ThemeManager.TryParseHex(value, out uint c)) { _s.Bar.TextColor = ThemeManager.Hex(c); Custom(); BarChanged(); } } }
        public string BarBackground { get => _s.Bar.BackgroundColor; set { if (ThemeManager.TryParseHex(value, out uint c)) { _s.Bar.BackgroundColor = ThemeManager.Hex(c); Custom(); BarChanged(); } } }
        public double BarOpacity { get => _s.Bar.BackgroundOpacity; set { _s.Bar.BackgroundOpacity = Math.Round(value, 2); Custom(); BarChanged(); } }
        public bool BarShadow { get => _s.Bar.Shadow; set { _s.Bar.Shadow = value; Custom(); BarChanged(); } }
        public bool BarDimPartial { get => _s.Bar.DimPartial; set { _s.Bar.DimPartial = value; BarChanged(); } }
        public double BarBottomMargin { get => _s.Bar.BottomMargin; set { _s.Bar.BottomMargin = Math.Round(value); BarChanged(); } }
        public double BarWidth { get => _s.Bar.Width; set { _s.Bar.Width = Math.Round(value); BarChanged(); } }
        public double BarBoxHeight { get => _s.Bar.BoxHeight; set { _s.Bar.BoxHeight = Math.Round(value); BarChanged(); } }

        /// <summary>The bar was moved or resized directly: refresh the controls.</summary>
        public void OnBarEditedDirectly()
        {
            foreach (var n in new[] { nameof(BarPosition), nameof(BarWidth), nameof(BarBoxHeight), nameof(BarFontSize) }) OnPropertyChanged(n);
            Save();
        }

        // ================================================================ model + listening

        public ObservableCollection<ModelItem> Models { get; }
        public ICommand SetProcessorCommand { get; }

        private ModelItem MakeModelItem(ModelInfo info)
        {
            var item = new ModelItem(info);
            item.DownloadCommand = new RelayCommand(_ => _ = DownloadModelAsync(item), _ => !item.IsDownloading);
            item.UseCommand = new RelayCommand(() => _ = UseModelAsync(item));
            item.CancelCommand = new RelayCommand(() => _downloadCts?.Cancel());
            item.DeleteCommand = new RelayCommand(() =>
            {
                if (item.IsActive && Controller.IsOn) { ShowNotice("Stop captions before deleting the model they use."); return; }
                if (MessageBox.Show($"Delete the {item.Name} model ({item.SizeText})? You can download it again later.", "Caption",
                        MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK) return;
                ModelCatalog.Delete(item.Info);
                if (_s.Model == item.Info.Id) { _s.Model = Models.FirstOrDefault(m => m.Info.IsInstalled)?.Info.Id ?? ""; Save(); }
                UpdateModelStates();
                OnPropertyChanged(nameof(NeedsSetup));
            });
            return item;
        }

        private void UpdateModelStates()
        {
            bool gpu = EngineManager.Pick(_s.Processor).Gpu;
            foreach (var m in Models)
            {
                m.IsActive = m.Info.Id == _s.Model && m.Info.IsInstalled;
                m.SpeedLabel = m.SpeedText(gpu) + (gpu ? " on your GPU" : " on the CPU");
                m.Refresh();
            }
            OnPropertyChanged(nameof(ModelsDiskText));
        }

        public string ModelsDiskText
        {
            get
            {
                long bytes = Models.Where(m => m.Info.IsInstalled).Sum(m => SafeSize(m.Info.Path)) + EngineManager.InstalledBytes();
                return $"Models and engine use {DownloadProgress.Format(bytes)} in {Paths.DataDir}";
            }
        }

        private static long SafeSize(string path)
        {
            try { return new FileInfo(path).Length; } catch { return 0; }
        }

        private async Task DownloadModelAsync(ModelItem item)
        {
            if (Models.Any(m => m.IsDownloading)) { ShowNotice("One download at a time."); return; }
            item.IsDownloading = true;
            _downloadCts = new CancellationTokenSource();
            try
            {
                var p = new Progress<DownloadProgress>(x => { item.Progress = x.Fraction; item.ProgressText = x.Text; });
                await Task.Run(() => ModelCatalog.DownloadAsync(item.Info, p, _downloadCts.Token));
                if (string.IsNullOrEmpty(_s.Model) || !ModelCatalog.Find(_s.Model)!.IsInstalled)
                {
                    _s.Model = item.Info.Id;
                    Save();
                }
                OnPropertyChanged(nameof(NeedsSetup));
            }
            catch (OperationCanceledException)
            {
                item.ProgressText = "Paused. Download again to continue.";
            }
            catch (Exception ex)
            {
                Log.Error("Model download", ex);
                ShowNotice($"{item.Name} didn't download: {ex.Message}");
            }
            finally
            {
                item.IsDownloading = false;
                UpdateModelStates();
            }
        }

        private async Task UseModelAsync(ModelItem item)
        {
            if (!item.Info.IsInstalled) return;
            _s.Model = item.Info.Id;
            Save();
            UpdateModelStates();
            if (!item.Info.TranslatesWell && Profiles.Any(p => p.Translate == "english"))
                ShowNotice($"{item.Name} isn't trained to translate: profiles set to English translation work better with Medium or Large v3.");
            await Controller.ReloadAsync();
        }

        public string Processor
        {
            get => _s.Processor;
            set
            {
                if (_s.Processor == value) return;
                _s.Processor = value;
                Save();
                OnPropertyChanged();
                OnPropertyChanged(nameof(EngineDownloadText));
                UpdateModelStates();
                _ = Controller.ReloadAsync();
            }
        }

        public bool KeepModelLoaded
        {
            get => _s.KeepModelLoaded;
            set { _s.KeepModelLoaded = value; Save(); OnPropertyChanged(); Controller.OnKeepLoadedChanged(); }
        }

        public double UpdateIntervalMs
        {
            get => _s.UpdateIntervalMs;
            set { _s.UpdateIntervalMs = (int)Math.Round(value / 50) * 50; Save(); OnPropertyChanged(); OnPropertyChanged(nameof(UpdateIntervalText)); Controller.ApplySettings(false); }
        }
        public string UpdateIntervalText => $"{_s.UpdateIntervalMs / 1000.0:0.00} s";

        public double PauseToEndMs
        {
            get => _s.PauseToEndMs;
            set { _s.PauseToEndMs = (int)Math.Round(value / 50) * 50; Save(); OnPropertyChanged(); OnPropertyChanged(nameof(PauseToEndText)); Controller.ApplySettings(false); }
        }
        public string PauseToEndText => $"{_s.PauseToEndMs / 1000.0:0.00} s";

        public double MaxLineSeconds
        {
            get => _s.MaxLineSeconds;
            set { _s.MaxLineSeconds = (int)Math.Round(value); Save(); OnPropertyChanged(); OnPropertyChanged(nameof(MaxLineText)); Controller.ApplySettings(false); }
        }
        public string MaxLineText => $"{_s.MaxLineSeconds} s";

        /// <summary>Shown as sensitivity: higher = picks up quieter speech (lower threshold).</summary>
        public double VadSensitivity
        {
            get => Math.Round(1 - _s.VadThreshold, 2);
            set { _s.VadThreshold = Math.Clamp(Math.Round(1 - value, 2), 0.1, 0.95); Save(); OnPropertyChanged(); Controller.ApplySettings(false); }
        }

        public bool ShowSoundLabels
        {
            get => _s.ShowSoundLabels;
            set { _s.ShowSoundLabels = value; Save(); OnPropertyChanged(); Controller.ApplySettings(false); }
        }

        public bool FilterHallucinations
        {
            get => _s.FilterHallucinations;
            set { _s.FilterHallucinations = value; Save(); OnPropertyChanged(); Controller.ApplySettings(false); }
        }

        // ================================================================ translation

        public ICommand RefreshOllamaCommand { get; }
        public ObservableCollection<string> OllamaModels { get; } = new();

        public string OllamaUrl
        {
            get => _s.OllamaUrl;
            set
            {
                if (string.IsNullOrWhiteSpace(value)) return;
                string v = value.Trim().TrimEnd('/');
                if (!Uri.TryCreate(v, UriKind.Absolute, out var uri) || !(uri.IsLoopback || uri.Host.EndsWith(".local") || IsPrivate(uri.Host)))
                {
                    ShowNotice("Ollama must run on this PC or your own network, so nothing goes to the internet.");
                    OnPropertyChanged();
                    return;
                }
                _s.OllamaUrl = v;
                Save();
                OnPropertyChanged();
                _ = RefreshOllamaAsync();
            }
        }

        private static bool IsPrivate(string host)
        {
            if (!System.Net.IPAddress.TryParse(host, out var ip)) return false;
            var b = ip.GetAddressBytes();
            return b.Length == 4 && (b[0] == 10 || (b[0] == 172 && b[1] >= 16 && b[1] <= 31) || (b[0] == 192 && b[1] == 168) || b[0] == 127);
        }

        public string OllamaModel
        {
            get => _s.OllamaModel;
            set { if (value != null && value != _s.OllamaModel) { _s.OllamaModel = value; Save(); OnPropertyChanged(); } }
        }

        public string OllamaStatus { get => _ollamaStatus; private set => SetProperty(ref _ollamaStatus, value); }
        public bool OllamaBusy { get => _ollamaBusy; private set => SetProperty(ref _ollamaBusy, value); }

        public bool CurrentModelTranslatesWell => ModelCatalog.Find(_s.Model)?.TranslatesWell ?? true;

        private async Task RefreshOllamaAsync()
        {
            if (OllamaBusy) return;
            OllamaBusy = true;
            OllamaStatus = "Looking for Ollama…";
            try
            {
                var models = await new OllamaClient(_s.OllamaUrl).ListModelsAsync();
                string keep = _s.OllamaModel;
                OllamaModels.Clear();
                if (models == null)
                {
                    OllamaStatus = $"Ollama isn't running at {_s.OllamaUrl}. Start Ollama, then press Refresh.";
                    if (keep.Length > 0) OllamaModels.Add(keep);
                    return;
                }
                foreach (var m in models) OllamaModels.Add(m);
                if (keep.Length > 0 && !OllamaModels.Contains(keep)) OllamaModels.Insert(0, keep);
                if (keep.Length == 0 && OllamaModels.Count > 0) OllamaModel = OllamaModels[0];
                OnPropertyChanged(nameof(OllamaModel));
                OllamaStatus = models.Count == 0
                    ? "Ollama is running but has no models. Pull one, for example: ollama pull qwen2.5:7b"
                    : $"Connected · {models.Count} model{(models.Count == 1 ? "" : "s")}";
            }
            finally
            {
                OllamaBusy = false;
                OnPropertyChanged(nameof(CurrentModelTranslatesWell));
            }
        }

        // ================================================================ appearance

        public ObservableCollection<ThemeItem> Themes { get; } = new();
        public ObservableCollection<SwatchItem> AccentSwatches { get; }
        public ICommand SelectThemeCommand { get; }
        public ICommand SelectAccentCommand { get; }
        public ICommand ApplyCustomAccentCommand { get; }
        public ICommand OpenThemesFolderCommand { get; }
        public ICommand ReloadThemesCommand { get; }
        public ICommand SaveThemeCommand { get; }

        public string CustomAccent { get => _customAccent; set => SetProperty(ref _customAccent, value); }

        private void BuildThemes()
        {
            Themes.Clear();
            foreach (var t in ThemeManager.AllThemes) Themes.Add(new ThemeItem(t) { IsSelected = t.Id == _s.Theme });
        }

        private void ApplyTheme()
        {
            Save();
            ThemeManager.Apply(_s.Theme, _s.Accent);
            foreach (var t in Themes) t.IsSelected = t.Id == _s.Theme;
            UpdateAccentSelection();
            BarStyleChanged?.Invoke(this, EventArgs.Empty);
        }

        private void UpdateAccentSelection()
        {
            foreach (var a in AccentSwatches) a.IsSelected = string.Equals(a.Value, _s.Accent, StringComparison.OrdinalIgnoreCase);
        }

        // ================================================================ hotkeys

        public HotkeyBinding? ToggleHotkey { get => _s.ToggleHotkey; set { _s.ToggleHotkey = value; HotkeyEdited(); OnPropertyChanged(nameof(ToggleHotkeyText)); } }
        public HotkeyBinding? PauseHotkey { get => _s.PauseHotkey; set { _s.PauseHotkey = value; HotkeyEdited(); } }
        public HotkeyBinding? MoveHotkey { get => _s.MoveHotkey; set { _s.MoveHotkey = value; HotkeyEdited(); } }
        public HotkeyBinding? TranscriptHotkey { get => _s.TranscriptHotkey; set { _s.TranscriptHotkey = value; HotkeyEdited(); } }

        private void HotkeyEdited([System.Runtime.CompilerServices.CallerMemberName] string? name = null)
        {
            Save();
            HotkeysChanged?.Invoke(this, EventArgs.Empty);
        }

        public string HotkeyWarning { get => _hotkeyWarning; private set => SetProperty(ref _hotkeyWarning, value); }

        public void SetHotkeyFailures(IReadOnlyList<string> failed) =>
            HotkeyWarning = failed.Count == 0 ? "" : "Another app is already using the keys for: " + string.Join(", ", failed) + ". Pick different keys.";

        // ================================================================ general

        public ICommand SetChannelCommand { get; }
        public ICommand CheckUpdatesCommand { get; }
        public ICommand RestartToUpdateCommand { get; }
        public ICommand RollbackCommand { get; }
        public ICommand OpenDataFolderCommand { get; }
        public ICommand OpenTranscriptsFolderCommand { get; }
        public ICommand OpenLogCommand { get; }
        public ICommand OpenGitHubCommand { get; }
        public ICommand OpenReleaseNotesCommand { get; }
        public ICommand QuitCommand { get; }

        public bool StartWithWindows
        {
            get => _s.StartWithWindows;
            set { _s.StartWithWindows = value; StartupManager.Apply(value); Save(); OnPropertyChanged(); }
        }

        public bool AutoUpdate
        {
            get => _s.AutoUpdate;
            set { _s.AutoUpdate = value; Save(); OnPropertyChanged(); Updater.OnAutoUpdateChanged(); }
        }

        public string UpdateChannel
        {
            get => _s.UpdateChannel;
            set
            {
                if (_s.UpdateChannel == value) return;
                _s.UpdateChannel = value;
                Save();
                OnPropertyChanged();
                _ = Updater.CheckAsync(manual: true);
            }
        }

        public bool AutoSaveTranscripts
        {
            get => _s.AutoSaveTranscripts;
            set { _s.AutoSaveTranscripts = value; Save(); OnPropertyChanged(); }
        }

        public IReadOnlyList<string> RollbackVersions => Updater.RollbackVersions;

        public string DataFolder => Paths.DataDir;
        public string PortableText => Paths.IsPortable
            ? "Portable mode: everything is kept in the data folder next to Caption.exe."
            : "To run portable, put an empty file named \"portable\" next to Caption.exe.";
    }
}
