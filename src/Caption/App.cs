using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Threading;
using Caption.Captioning;
using Caption.Controls;
using Caption.Services;
using Caption.ViewModels;
using Caption.Views;

namespace Caption
{
    public sealed class App : Application
    {
        public const string ShowSignalName = "Caption.ShowWindow.Signal.v1";
        public const string QuitSignalName = "Caption.Quit.Signal.v1";
        public const string MutexName = "Caption.SingleInstance.v1";

        private readonly bool _startHidden;
        private readonly int _afterUpdatePid;
        private readonly string? _previousVersion;
        private Mutex? _mutex;

        private AppSettings _settings = null!;
        private UpdateService _updater = null!;
        private CaptionController _controller = null!;
        private MainViewModel _vm = null!;
        private MainWindow _window = null!;
        private CaptionBarWindow? _bar;
        private TranscriptWindow? _transcript;
        private UpdatePrompt? _prompt;
        private TrayIcon? _tray;
        private HotkeyManager? _hotkeys;
        private EventWaitHandle? _showSignal, _quitSignal;
        private RegisteredWaitHandle? _showWait, _quitWait;
        private bool _cleanedUp;

        public App(bool startHidden, int afterUpdatePid, string? previousVersion, Mutex? mutex)
        {
            _startHidden = startHidden;
            _afterUpdatePid = afterUpdatePid;
            _previousVersion = previousVersion;
            _mutex = mutex;
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
        }

        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);
            DispatcherUnhandledException += (_, args) =>
            {
                Log.Error("Unhandled UI exception", args.Exception);
                args.Handled = true;
            };
            try
            {
                Start();
            }
            catch (Exception ex)
            {
                Log.Error("Startup failed", ex);
                MessageBox.Show($"Caption couldn't start.\n\n{ex.Message}\n\nDetails were saved to:\n{Log.FilePath}",
                    "Caption", MessageBoxButton.OK, MessageBoxImage.Error);
                Cleanup();
                Shutdown(1);
            }
        }

        private void Start()
        {
            Resources.MergedDictionaries.Add(new ResourceDictionary
            {
                Source = new Uri("pack://application:,,,/Caption;component/Views/Styles.xaml", UriKind.Absolute)
            });
            _settings = SettingsStore.Load();
            ThemeManager.Initialize(Resources, _settings.Theme, _settings.Accent);
            StartupManager.Apply(_settings.StartWithWindows);
            if (Installer.IsRunningFromInstallDir) Installer.Register(Paths.ExePath);

            _updater = new UpdateService(_settings);
            _controller = new CaptionController(_settings, Dispatcher);
            _vm = new MainViewModel(_settings, _controller, _updater);
            _window = new MainWindow(_vm);

            WireViewModel();
            WireController();
            CreateTray();
            CreateHotkeys();

            _updater.Ready += (_, _) => ShowUpdatePrompt();
            _updater.StartAutomaticChecks();

            _showSignal = new EventWaitHandle(false, EventResetMode.AutoReset, ShowSignalName);
            _showWait = ThreadPool.RegisterWaitForSingleObject(_showSignal, (_, _) =>
                Dispatcher.InvokeAsync(() => _window.ShowFront()), null, Timeout.Infinite, false);
            _quitSignal = new EventWaitHandle(false, EventResetMode.AutoReset, QuitSignalName);
            _quitWait = ThreadPool.RegisterWaitForSingleObject(_quitSignal, (_, _) =>
                Dispatcher.InvokeAsync(Quit), null, Timeout.Infinite, false);

            bool resume = false;
            if (_afterUpdatePid > 0)
            {
                resume = ReadResumeState();
                _tray?.ShowBalloon("Caption updated", $"You're now on version {UpdateService.AppVersion}.");
                // Tell the old copy we started fine once the UI is up, then take over single-instance.
                var healthy = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
                healthy.Tick += (_, _) =>
                {
                    healthy.Stop();
                    UpdateService.ReportHealthy(_afterUpdatePid);
                    Task.Run(() =>
                    {
                        try { using var p = System.Diagnostics.Process.GetProcessById(_afterUpdatePid); p.WaitForExit(30000); } catch { }
                        Dispatcher.InvokeAsync(() => { try { _mutex ??= new Mutex(true, MutexName, out _); } catch { } });
                    });
                };
                healthy.Start();
            }

            if (_vm.NeedsSetup || !_startHidden) _window.ShowFront();
            if (resume) _ = _controller.StartAsync();
            Log.Info($"Started v{UpdateService.AppVersion} (hidden: {_startHidden}, portable: {Paths.IsPortable})");
        }

        // ---------------------------------------------------------------- wiring

        private void WireViewModel()
        {
            _vm.ToggleCaptionsRequested += (_, _) => ToggleCaptions();
            _vm.ShowTranscriptRequested += (_, _) => ShowTranscript();
            _vm.MoveBarRequested += (_, _) => ToggleMoveBar();
            _vm.QuitRequested += (_, _) => Quit();
            _vm.RestartToUpdateRequested += (_, _) => _ = RestartToUpdateAsync();
            _vm.RollbackRequested += (_, v) => _ = RollbackAsync(v);
            _vm.HotkeysChanged += (_, _) => ApplyHotkeys();
            _vm.BarStyleChanged += (_, _) => _bar?.ApplyStyle();
        }

        private CaptionBarWindow Bar()
        {
            if (_bar != null) return _bar;
            _bar = new CaptionBarWindow(_settings);
            _bar.SettingsChanged += (_, _) => _vm.OnBarEditedDirectly();
            _bar.EditFinished += (_, _) =>
            {
                _vm.OnBarEditedDirectly();
                if (!_controller.IsOn) _bar?.Hide();
            };
            return _bar;
        }

        private void WireController()
        {
            _controller.StateChanged += () =>
            {
                var bar = Bar();
                switch (_controller.State)
                {
                    case CaptionState.Starting:
                        bar.Clear();
                        bar.ShowNotice("Starting captions…");
                        if (!bar.IsVisible) bar.Show();
                        break;
                    case CaptionState.Running:
                        bar.ShowNotice(null);
                        if (!bar.IsVisible) bar.Show();
                        break;
                    case CaptionState.Paused:
                        bar.SetPartial("");
                        bar.ShowNotice("Paused");
                        break;
                    default:
                        if (!bar.IsEditing) { bar.Hide(); bar.Clear(); }
                        break;
                }
                UpdateTrayTooltip();
            };
            _controller.PartialChanged += t => _bar?.SetPartial(t);
            _controller.LineAdded += line =>
            {
                bool showOriginal = _settings.CurrentProfile.ShowOriginal;
                _bar?.AddLine(line.Display, showOriginal ? line.Original : null);
            };
            _controller.LineUpdated += line =>
            {
                bool showOriginal = _settings.CurrentProfile.ShowOriginal;
                _bar?.UpdateLastLine(line.Text, line.Display, showOriginal ? line.Text : null);
            };
            _controller.Notice += text =>
            {
                if (!_window.IsVisible) _tray?.ShowBalloon("Caption", text);
            };
            _controller.NeedsSetup += () =>
            {
                _vm.Page = "home";
                _window.ShowFront();
            };
        }

        private void ToggleCaptions()
        {
            if (_controller.IsOn) _controller.Stop();
            else _ = _controller.StartAsync();
        }

        private void ShowTranscript()
        {
            if (_transcript == null)
            {
                _transcript = new TranscriptWindow(_controller, _settings);
                _transcript.Closed += (_, _) => _transcript = null;
            }
            _transcript.Show();
            if (_transcript.WindowState == WindowState.Minimized) _transcript.WindowState = WindowState.Normal;
            _transcript.Activate();
        }

        private void ToggleMoveBar()
        {
            var bar = Bar();
            if (bar.IsEditing)
            {
                bar.SetEditing(false);
                _vm.OnBarEditedDirectly();
                if (!_controller.IsOn) bar.Hide();
            }
            else
            {
                bar.SetEditing(true);
            }
        }

        // ---------------------------------------------------------------- tray

        private void CreateTray()
        {
            _tray = new TrayIcon("Caption");
            _tray.LeftClick += (_, _) => _window.ShowFront();
            _tray.RightClick += (_, _) => ShowTrayMenu();
            _tray.BalloonClick += (_, _) => _window.ShowFront();
            UpdateTrayTooltip();
        }

        private void UpdateTrayTooltip()
        {
            string state = _controller.State switch
            {
                CaptionState.Running => "on",
                CaptionState.Paused => "paused",
                CaptionState.Starting => "starting",
                _ => "off",
            };
            _tray?.SetTooltip($"Caption · captions {state} · {_settings.CurrentProfile.Name}");
        }

        private void ShowTrayMenu()
        {
            var menu = new ContextMenu();
            MenuItem Item(string header, Action action, bool enabled = true, bool isChecked = false)
            {
                var mi = new MenuItem { Header = header, IsEnabled = enabled, IsChecked = isChecked };
                mi.Click += (_, _) => action();
                return mi;
            }

            string hk = _settings.ToggleHotkey != null ? "   " + HotkeyManager.Format(_settings.ToggleHotkey) : "";
            menu.Items.Add(Item((_controller.IsOn ? "Stop captions" : "Start captions") + hk, ToggleCaptions));
            menu.Items.Add(Item(_controller.State == CaptionState.Paused ? "Resume" : "Pause", () => _controller.TogglePause(),
                _controller.State is CaptionState.Running or CaptionState.Paused));
            var profiles = new MenuItem { Header = "Profile" };
            foreach (var p in _settings.Profiles.ToList())
            {
                string id = p.Id;
                profiles.Items.Add(Item(p.Name, () => { _vm.SetActiveProfile(id); UpdateTrayTooltip(); }, true, p.Id == _settings.ActiveProfile));
            }
            menu.Items.Add(profiles);
            menu.Items.Add(new Separator());
            menu.Items.Add(Item("Transcript", ShowTranscript));
            menu.Items.Add(Item("Move caption bar", ToggleMoveBar));
            menu.Items.Add(Item("Settings", () => _window.OpenSettings()));
            menu.Items.Add(Item("Open Caption", () => _window.ShowFront()));
            if (_updater.UpdateReady)
            {
                menu.Items.Add(new Separator());
                menu.Items.Add(Item($"Restart to update ({_updater.AvailableVersion})", () => _ = RestartToUpdateAsync()));
            }
            menu.Items.Add(new Separator());
            menu.Items.Add(Item("Quit", Quit));

            if (_tray != null) Native.SetForegroundWindow(_tray.Handle);
            menu.Placement = PlacementMode.MousePoint;
            menu.IsOpen = true;
        }

        // ---------------------------------------------------------------- hotkeys

        private void CreateHotkeys()
        {
            _hotkeys = new HotkeyManager();
            _hotkeys.FailuresChanged += (_, _) => _vm.SetHotkeyFailures(_hotkeys?.Failed ?? Array.Empty<string>());
            HotkeyRecorder.RecordingStarted += OnRecordingStarted;
            HotkeyRecorder.RecordingStopped += OnRecordingStopped;
            ApplyHotkeys();
        }

        private void OnRecordingStarted(object? sender, EventArgs e) => _hotkeys?.Suspend();
        private void OnRecordingStopped(object? sender, EventArgs e) => _hotkeys?.Resume();

        private void ApplyHotkeys()
        {
            if (_hotkeys == null) return;
            var s = _settings;
            var list = new System.Collections.Generic.List<HotkeyRegistration>();
            if (s.ToggleHotkey != null) list.Add(new HotkeyRegistration("Show or hide captions", s.ToggleHotkey, ToggleCaptions));
            if (s.PauseHotkey != null) list.Add(new HotkeyRegistration("Pause", s.PauseHotkey, () => _controller.TogglePause()));
            if (s.MoveHotkey != null) list.Add(new HotkeyRegistration("Move caption bar", s.MoveHotkey, ToggleMoveBar));
            if (s.TranscriptHotkey != null) list.Add(new HotkeyRegistration("Transcript", s.TranscriptHotkey, ShowTranscript));
            _hotkeys.Set(list);
        }

        // ---------------------------------------------------------------- updates

        private void ShowUpdatePrompt()
        {
            if (_prompt != null || _updater.AvailableVersion == null) return;
            _prompt = new UpdatePrompt(_updater.AvailableVersion);
            _prompt.RestartClicked += (_, _) => _ = RestartToUpdateAsync();
            _prompt.Closed += (_, _) => _prompt = null;
            _prompt.Show();
        }

        private sealed class ResumeState
        {
            public bool CaptionsOn { get; set; }
        }

        private bool ReadResumeState()
        {
            try
            {
                if (!File.Exists(Paths.StateFile)) return false;
                var st = JsonSerializer.Deserialize<ResumeState>(File.ReadAllText(Paths.StateFile));
                File.Delete(Paths.StateFile);
                return st?.CaptionsOn == true;
            }
            catch
            {
                return false;
            }
        }

        private async Task RestartToUpdateAsync() =>
            await HandOffAsync(() => _updater.RestartToUpdateAsync(HideForHandoff, RestoreAfterFailedHandoff));

        private async Task RollbackAsync(string version)
        {
            if (MessageBox.Show($"Go back to Caption {version}? Caption restarts.", "Caption",
                    MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK) return;
            await HandOffAsync(() => _updater.RollbackAsync(version, HideForHandoff, RestoreAfterFailedHandoff));
        }

        private bool _wasOnBeforeHandoff;

        private async Task HandOffAsync(Func<Task<bool>> handoff)
        {
            _wasOnBeforeHandoff = _controller.IsOn;
            try
            {
                Directory.CreateDirectory(Paths.DataDir);
                File.WriteAllText(Paths.StateFile, JsonSerializer.Serialize(new ResumeState { CaptionsOn = _wasOnBeforeHandoff }));
            }
            catch { }
            bool ok = await handoff();
            if (ok)
            {
                Log.Info("Handed off to the new version; exiting.");
                Cleanup();
                Shutdown();
            }
            else
            {
                try { File.Delete(Paths.StateFile); } catch { }
            }
        }

        private void HideForHandoff()
        {
            _controller.Stop();
            _controller.Dispose();
            _window.SaveState();
            _window.Hide();
            _bar?.Hide();
            _transcript?.Close();
            _prompt?.Close();
            try { _tray?.Dispose(); } catch { }
            _tray = null;
            try { _hotkeys?.Dispose(); } catch { }
            _hotkeys = null;
            // The new copy takes single-instance once we exit.
            try { _mutex?.ReleaseMutex(); } catch { }
            try { _mutex?.Dispose(); } catch { }
            _mutex = null;
        }

        private void RestoreAfterFailedHandoff()
        {
            try { _mutex = new Mutex(true, MutexName, out _); } catch { }
            CreateTray();
            CreateHotkeys();
            _window.ShowFront();
            _vm.ShowNotice(_updater.Status);
            if (_wasOnBeforeHandoff) _ = _controller.StartAsync();
        }

        // ---------------------------------------------------------------- shutdown

        private void Quit()
        {
            _window.AllowClose();
            Cleanup();
            Shutdown();
        }

        private void Cleanup()
        {
            if (_cleanedUp) return;
            _cleanedUp = true;
            try { _window?.SaveState(); } catch { }
            try { _controller?.Dispose(); } catch { }
            try { _tray?.Dispose(); } catch { }
            HotkeyRecorder.RecordingStarted -= OnRecordingStarted;
            HotkeyRecorder.RecordingStopped -= OnRecordingStopped;
            try { _hotkeys?.Dispose(); } catch { }
            try { _showWait?.Unregister(null); } catch { }
            try { _quitWait?.Unregister(null); } catch { }
            try { _showSignal?.Dispose(); } catch { }
            try { _quitSignal?.Dispose(); } catch { }
            try { _bar?.Close(); } catch { }
            try { _transcript?.Close(); } catch { }
            try { _mutex?.ReleaseMutex(); } catch { }
        }

        protected override void OnSessionEnding(SessionEndingCancelEventArgs e)
        {
            _window?.AllowClose();
            Cleanup();
            base.OnSessionEnding(e);
        }

        protected override void OnExit(ExitEventArgs e)
        {
            Cleanup();
            base.OnExit(e);
        }
    }
}
