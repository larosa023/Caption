using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using Caption.Services;
using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;

namespace Caption.Audio
{
    public sealed record DeviceChoice(string Id, string Name);

    public sealed record AppChoice(string Exe, string Name, bool PlayingNow);

    /// <summary>
    /// Opens the sources a profile asks for (whole system, one output, one or more apps, plus the mic),
    /// mixes them on a steady 16 kHz clock and hands blocks to the caption pipeline.
    /// Loopback delivers nothing while nothing plays, so the clock fills gaps with silence.
    /// </summary>
    public sealed class AudioCapture : IDisposable, IMMNotificationClient
    {
        private sealed class Lane
        {
            public AudioSource Source = null!;
            public readonly object Gate = new();
            public float[] Buffer = new float[16000 * 4];
            public int Count;
            public bool IsApp;
            public string Exe = "";
        }

        private readonly Profile _profile;
        private readonly Action<float[]> _output;
        private readonly List<Lane> _lanes = new();
        private readonly object _lanesGate = new();
        private readonly MMDeviceEnumerator _enumerator = new();
        private Thread? _clockThread;
        private volatile bool _stop;
        private long _produced;
        private int _restartPending;

        /// <summary>Human-readable state: "Listening to Discord", "Waiting for Discord to play…", errors.</summary>
        public string Status { get; private set; } = "";
        public event Action? StatusChanged;

        /// <summary>Recent input level 0..1 (for the little meter).</summary>
        public float Level { get; private set; }

        public AudioCapture(Profile profile, Action<float[]> output)
        {
            _profile = profile.Clone();
            _output = output;
        }

        public void Start()
        {
            OpenSources();
            try { _enumerator.RegisterEndpointNotificationCallback(this); } catch { }
            _clockThread = new Thread(ClockLoop) { IsBackground = true, Name = "Caption audio clock", Priority = ThreadPriority.AboveNormal };
            _clockThread.Start();
        }

        private void SetStatus(string s)
        {
            if (Status == s) return;
            Status = s;
            StatusChanged?.Invoke();
        }

        // ---------------------------------------------------------------- sources

        private void OpenSources()
        {
            var problems = new List<string>();
            switch (_profile.Source)
            {
                case "device":
                {
                    MMDevice? dev = null;
                    try { if (!string.IsNullOrEmpty(_profile.DeviceId)) dev = _enumerator.GetDevice(_profile.DeviceId); } catch { }
                    if (dev == null || dev.State != DeviceState.Active)
                    {
                        problems.Add($"{(_profile.DeviceName.Length > 0 ? _profile.DeviceName : "That output")} isn't available, so Caption is listening to the default output");
                        dev = DefaultRender();
                    }
                    if (dev != null) AddLane(new DeviceSource(dev, loopback: true, _profile.DialogueFocus));
                    break;
                }
                case "apps":
                    RefreshApps();
                    break;
                default:
                {
                    var dev = DefaultRender();
                    if (dev != null) AddLane(new DeviceSource(dev, loopback: true, _profile.DialogueFocus));
                    else problems.Add("No output device found");
                    break;
                }
            }

            if (_profile.IncludeMic)
            {
                MMDevice? mic = null;
                try { if (!string.IsNullOrEmpty(_profile.MicDeviceId)) mic = _enumerator.GetDevice(_profile.MicDeviceId); } catch { }
                if (mic == null || mic.State != DeviceState.Active)
                {
                    try { mic = _enumerator.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Communications); } catch { mic = null; }
                }
                if (mic != null) AddLane(new DeviceSource(mic, loopback: false));
                else problems.Add("No microphone found");
            }
            UpdateStatus(problems);
        }

        private List<string> _lastProblems = new();

        private void UpdateStatus(List<string>? problems = null)
        {
            if (problems != null) _lastProblems = problems;
            List<string> names;
            lock (_lanesGate) names = _lanes.Select(l => l.Source.Name).Distinct().ToList();
            string s;
            if (_profile.Source == "apps" && !_lanes.Any(l => l.IsApp))
                s = "Waiting for " + JoinNames(_profile.Apps.Select(Friendly)) + " to start…";
            else
                s = names.Count == 0 ? "Nothing to listen to" : "Listening to " + JoinNames(names);
            if (_lastProblems.Count > 0) s += " · " + string.Join(" · ", _lastProblems);
            SetStatus(s);
        }

        private static string Friendly(string exe) => Path.GetFileNameWithoutExtension(exe);

        private static string JoinNames(IEnumerable<string> names)
        {
            var list = names.ToList();
            if (list.Count <= 2) return string.Join(" and ", list);
            return string.Join(", ", list.Take(list.Count - 1)) + " and " + list[^1];
        }

        private MMDevice? DefaultRender()
        {
            try { return _enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia); }
            catch { return null; }
        }

        private void AddLane(AudioSource source, bool isApp = false, string exe = "")
        {
            var lane = new Lane { Source = source, IsApp = isApp, Exe = exe };
            source.Samples += (_, s) => Push(lane, s);
            source.Stopped += (src, ex) =>
            {
                Log.Info($"Source {src.Name} stopped: {ex?.Message}");
                RemoveLane(lane);
                if (!isApp) Interlocked.Exchange(ref _restartPending, 1);
                UpdateStatus();
            };
            try
            {
                source.Start();
                lock (_lanesGate) _lanes.Add(lane);
            }
            catch (Exception ex)
            {
                Log.Error("Starting " + source.Name, ex);
                source.Dispose();
                _lastProblems.Add($"Couldn't open {source.Name}");
            }
        }

        private void RemoveLane(Lane lane)
        {
            lock (_lanesGate) _lanes.Remove(lane);
            try { lane.Source.Dispose(); } catch { }
        }

        /// <summary>Apps mode: capture each listed app's top process (its whole tree), and follow apps starting/closing.</summary>
        private void RefreshApps()
        {
            if (_profile.Source != "apps") return;
            var procs = Native.GetProcesses();
            var byPid = procs.ToDictionary(p => p.Pid, p => p);
            var wanted = new HashSet<string>(_profile.Apps.Select(a => a.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? a : a + ".exe"),
                StringComparer.OrdinalIgnoreCase);
            // Root processes: matching exe whose parent isn't the same exe (browsers spawn many children).
            var roots = procs.Where(p => wanted.Contains(p.ExeName) &&
                    !(byPid.TryGetValue(p.ParentPid, out var parent) && string.Equals(parent.ExeName, p.ExeName, StringComparison.OrdinalIgnoreCase)))
                .ToList();

            List<Lane> current;
            lock (_lanesGate) current = _lanes.Where(l => l.IsApp).ToList();
            foreach (var lane in current)
            {
                var src = (ProcessSource)lane.Source;
                if (!roots.Any(r => r.Pid == src.ProcessId)) RemoveLane(lane);
            }
            foreach (var root in roots)
            {
                bool have;
                lock (_lanesGate) have = _lanes.Any(l => l.IsApp && ((ProcessSource)l.Source).ProcessId == root.Pid);
                if (!have) AddLane(new ProcessSource(root.Pid, Friendly(root.ExeName), _profile.DialogueFocus), isApp: true, exe: root.ExeName);
            }
            UpdateStatus();
        }

        private void Push(Lane lane, float[] samples)
        {
            lock (lane.Gate)
            {
                int max = lane.Buffer.Length;
                if (samples.Length >= max)
                {
                    Array.Copy(samples, samples.Length - max, lane.Buffer, 0, max);
                    lane.Count = max;
                    return;
                }
                if (lane.Count + samples.Length > max)
                {
                    int drop = lane.Count + samples.Length - max;
                    Array.Copy(lane.Buffer, drop, lane.Buffer, 0, lane.Count - drop);
                    lane.Count -= drop;
                }
                Array.Copy(samples, 0, lane.Buffer, lane.Count, samples.Length);
                lane.Count += samples.Length;
            }
        }

        // ---------------------------------------------------------------- clock + mix

        private void ClockLoop()
        {
            var clock = Stopwatch.StartNew();
            long lastAppScan = 0;
            const int MaxBacklog = 16000 * 3 / 10; // keep sources within 300 ms of the clock
            while (!_stop)
            {
                Thread.Sleep(40);
                if (_stop) break;

                if (Interlocked.Exchange(ref _restartPending, 0) == 1) ReopenDevices();
                if (_profile.Source == "apps" && clock.ElapsedMilliseconds - lastAppScan > 2000)
                {
                    lastAppScan = clock.ElapsedMilliseconds;
                    try { RefreshApps(); } catch (Exception ex) { Log.WarnOnce("Scanning apps", ex); }
                }

                // Hold back 60 ms so late packets still line up.
                long target = (long)((clock.Elapsed.TotalSeconds - 0.06) * 16000);
                int need = (int)Math.Min(16000, target - _produced);
                if (need <= 0) continue;

                var mix = new float[need];
                Lane[] lanes;
                lock (_lanesGate) lanes = _lanes.ToArray();
                foreach (var lane in lanes)
                {
                    lock (lane.Gate)
                    {
                        if (lane.Count > need + MaxBacklog)
                        {
                            int drop = lane.Count - need - MaxBacklog;
                            Array.Copy(lane.Buffer, drop, lane.Buffer, 0, lane.Count - drop);
                            lane.Count -= drop;
                        }
                        int take = Math.Min(need, lane.Count);
                        for (int i = 0; i < take; i++) mix[i] += lane.Buffer[i];
                        Array.Copy(lane.Buffer, take, lane.Buffer, 0, lane.Count - take);
                        lane.Count -= take;
                    }
                }
                float peak = 0;
                for (int i = 0; i < mix.Length; i++)
                {
                    float v = mix[i];
                    if (v > 1f) v = 1f; else if (v < -1f) v = -1f;
                    mix[i] = v;
                    float a = Math.Abs(v);
                    if (a > peak) peak = a;
                }
                Level = Math.Max(peak, Level * 0.8f);
                _produced += need;
                try { _output(mix); } catch (Exception ex) { Log.WarnOnce("Audio output", ex); }
            }
        }

        private void ReopenDevices()
        {
            List<Lane> devices;
            lock (_lanesGate) devices = _lanes.Where(l => !l.IsApp).ToList();
            foreach (var l in devices) RemoveLane(l);
            Thread.Sleep(300);
            if (_stop) return;
            try { OpenSources(); }
            catch (Exception ex) { Log.Error("Reopening audio", ex); }
        }

        // ---------------------------------------------------------------- device notifications

        void IMMNotificationClient.OnDefaultDeviceChanged(DataFlow flow, Role role, string defaultDeviceId)
        {
            bool relevant = (flow == DataFlow.Render && role == Role.Multimedia && _profile.Source == "system") ||
                            (flow == DataFlow.Capture && role == Role.Communications && _profile.IncludeMic && string.IsNullOrEmpty(_profile.MicDeviceId));
            if (relevant) Interlocked.Exchange(ref _restartPending, 1);
        }

        void IMMNotificationClient.OnDeviceStateChanged(string deviceId, DeviceState newState) { }
        void IMMNotificationClient.OnDeviceAdded(string pwstrDeviceId) { }
        void IMMNotificationClient.OnDeviceRemoved(string deviceId) { }
        void IMMNotificationClient.OnPropertyValueChanged(string pwstrDeviceId, NAudio.CoreAudioApi.PropertyKey key) { }

        public void Dispose()
        {
            _stop = true;
            try { _enumerator.UnregisterEndpointNotificationCallback(this); } catch { }
            try { _clockThread?.Join(1000); } catch { }
            List<Lane> lanes;
            lock (_lanesGate) { lanes = _lanes.ToList(); _lanes.Clear(); }
            foreach (var l in lanes) try { l.Source.Dispose(); } catch { }
            try { _enumerator.Dispose(); } catch { }
        }

        // ---------------------------------------------------------------- pickers for Settings

        public static List<DeviceChoice> OutputDevices() => Devices(DataFlow.Render);
        public static List<DeviceChoice> InputDevices() => Devices(DataFlow.Capture);

        private static List<DeviceChoice> Devices(DataFlow flow)
        {
            var list = new List<DeviceChoice>();
            try
            {
                using var e = new MMDeviceEnumerator();
                foreach (var d in e.EnumerateAudioEndPoints(flow, DeviceState.Active))
                {
                    try { list.Add(new DeviceChoice(d.ID, d.FriendlyName)); }
                    finally { d.Dispose(); }
                }
            }
            catch (Exception ex)
            {
                Log.WarnOnce("Listing devices", ex);
            }
            return list;
        }

        /// <summary>Apps you might want: ones playing audio right now first, then other apps with windows.</summary>
        public static List<AppChoice> RunningApps()
        {
            var playing = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                using var e = new MMDeviceEnumerator();
                foreach (var d in e.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active))
                {
                    try
                    {
                        var sessions = d.AudioSessionManager.Sessions;
                        for (int i = 0; i < sessions.Count; i++)
                        {
                            var s = sessions[i];
                            if (s.IsSystemSoundsSession) continue;
                            string? path = Native.GetProcessPath(s.GetProcessID);
                            if (path != null) playing.Add(Path.GetFileName(path));
                        }
                    }
                    catch
                    {
                    }
                    finally
                    {
                        d.Dispose();
                    }
                }
            }
            catch (Exception ex)
            {
                Log.WarnOnce("Listing audio apps", ex);
            }

            var withWindows = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var p in Process.GetProcesses())
            {
                try
                {
                    if (p.MainWindowHandle != IntPtr.Zero && p.Id != Environment.ProcessId)
                        withWindows.Add(p.ProcessName + ".exe");
                }
                catch
                {
                }
                finally
                {
                    p.Dispose();
                }
            }
            var skip = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                { "explorer.exe", "ApplicationFrameHost.exe", "TextInputHost.exe", "SystemSettings.exe", "Caption.exe", "ShellExperienceHost.exe" };
            return playing.Union(withWindows, StringComparer.OrdinalIgnoreCase)
                .Where(x => !skip.Contains(x))
                .Select(x => new AppChoice(x, Friendly(x), playing.Contains(x)))
                .OrderByDescending(a => a.PlayingNow).ThenBy(a => a.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
    }
}
