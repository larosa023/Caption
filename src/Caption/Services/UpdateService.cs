using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Reflection;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Threading;
using Caption.ViewModels;

namespace Caption.Services
{
    /// <summary>
    /// Built-in updater, the same model as Hangar's own (docs/ARCHITECTURE.md, "Hangar's own updater"):
    /// check larosa023/Caption releases (Stable, or Beta = versions with a "-"), download Caption.exe in
    /// the background, verify SHA-256 against GitHub's asset digest and SHA256SUMS.txt, then on restart swap
    /// the exe and hand off to the new copy. If the new copy doesn't report healthy in time, the old exe
    /// is put back and that version is skipped. Models and settings live outside the exe, so they stay.
    /// </summary>
    public sealed class UpdateService : ObservableObject
    {
        public const string Repo = "larosa023/Caption";
        public const string RepoUrl = "https://github.com/" + Repo;
        private const string IndexUrl = "https://raw.githubusercontent.com/larosa023/Hangar/index/releases.json";
        private static readonly TimeSpan CheckEvery = TimeSpan.FromHours(4);

        private readonly AppSettings _settings;
        private readonly DispatcherTimer _timer;
        private CancellationTokenSource? _cts;
        private string _status = "";
        private bool _isBusy;
        private bool _updateReady;
        private string? _availableVersion;
        private string? _readyExe;
        private string? _releaseNotes;
        private double _progress;

        public UpdateService(AppSettings settings)
        {
            _settings = settings;
            _timer = new DispatcherTimer { Interval = CheckEvery };
            _timer.Tick += (_, _) => { if (_settings.AutoUpdate) _ = CheckAsync(manual: false); };
            FindExistingDownload();
        }

        public static string AppVersion { get; } = ReadVersion();

        private static string ReadVersion()
        {
            var info = Assembly.GetExecutingAssembly().GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
            if (string.IsNullOrEmpty(info)) info = Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "0.0.0";
            int plus = info.IndexOf('+');
            return plus >= 0 ? info.Substring(0, plus) : info;
        }

        public string CurrentVersion => AppVersion;
        public string Status { get => _status; private set => SetProperty(ref _status, value); }
        public bool IsBusy { get => _isBusy; private set => SetProperty(ref _isBusy, value); }
        public bool UpdateReady { get => _updateReady; private set => SetProperty(ref _updateReady, value); }
        public string? AvailableVersion { get => _availableVersion; private set => SetProperty(ref _availableVersion, value); }
        public string? ReleaseNotes { get => _releaseNotes; private set => SetProperty(ref _releaseNotes, value); }
        public double Progress { get => _progress; private set => SetProperty(ref _progress, value); }

        /// <summary>Raised on the UI thread when a verified update is downloaded and ready.</summary>
        public event EventHandler? Ready;

        /// <summary>Earlier versions kept for rollback (newest first).</summary>
        public IReadOnlyList<string> RollbackVersions =>
            Directory.Exists(Paths.VersionsDir)
                ? Directory.GetDirectories(Paths.VersionsDir)
                    .Where(d => File.Exists(Path.Combine(d, "Caption.exe")))
                    .Select(Path.GetFileName).OfType<string>()
                    .Where(v => CompareVersions(v, AppVersion) != 0)
                    .OrderByDescending(v => v, Comparer<string>.Create(CompareVersions)).ToList()
                : Array.Empty<string>();

        public void StartAutomaticChecks()
        {
            _timer.Start();
            if (!_settings.AutoUpdate) return;
            // Shortly after launch, so startup itself stays quick.
            var once = new DispatcherTimer { Interval = TimeSpan.FromSeconds(15) };
            once.Tick += (_, _) => { once.Stop(); _ = CheckAsync(manual: false); };
            once.Start();
        }

        public void OnAutoUpdateChanged()
        {
            if (_settings.AutoUpdate && !UpdateReady && !IsBusy) _ = CheckAsync(manual: false);
        }

        private void FindExistingDownload()
        {
            // A verified download from an earlier session that we never restarted into.
            try
            {
                if (!Directory.Exists(Paths.UpdatesDir)) return;
                foreach (var dir in Directory.GetDirectories(Paths.UpdatesDir))
                {
                    string ver = Path.GetFileName(dir);
                    string exe = Path.Combine(dir, "Caption.exe");
                    bool newer = CompareVersions(ver, AppVersion) > 0 && File.Exists(exe) && File.Exists(exe + ".verified");
                    if (newer && !_settings.SkipVersions.Contains(ver) && ChannelAllows(ver))
                    {
                        if (_readyExe == null || CompareVersions(ver, AvailableVersion ?? "0") > 0)
                        {
                            _readyExe = exe;
                            AvailableVersion = ver;
                            UpdateReady = true;
                            Status = $"Version {ver} is ready. Restart to update.";
                        }
                    }
                    else if (!newer)
                    {
                        try { Directory.Delete(dir, true); } catch { }
                    }
                }
            }
            catch (Exception ex)
            {
                Log.WarnOnce("Looking for downloaded updates", ex);
            }
        }

        private bool ChannelAllows(string version) => _settings.UpdateChannel == "beta" || !version.Contains('-');

        // ---------------------------------------------------------------- check + download

        private sealed record ReleaseInfo(string Version, bool Prerelease, string ExeUrl, long ExeSize, string? ExeDigest,
            string? SumsUrl, string? Notes);

        public async Task CheckAsync(bool manual)
        {
            if (IsBusy) return;
            if (manual && UpdateReady) { Status = $"Version {AvailableVersion} is ready. Restart to update."; return; }
            IsBusy = true;
            Progress = 0;
            Status = "Checking for updates…";
            _cts = new CancellationTokenSource();
            var ct = _cts.Token;
            try
            {
                var releases = await GetReleasesAsync(ct);
                var best = releases
                    .Where(r => ChannelAllows(r.Version) && !(r.Prerelease && _settings.UpdateChannel != "beta"))
                    .Where(r => !_settings.SkipVersions.Contains(r.Version))
                    .Where(r => CompareVersions(r.Version, AppVersion) > 0)
                    .OrderByDescending(r => r.Version, Comparer<string>.Create(CompareVersions))
                    .FirstOrDefault();
                if (best == null)
                {
                    Status = $"You're up to date (checked {DateTime.Now:h:mm tt}).";
                    return;
                }

                AvailableVersion = best.Version;
                ReleaseNotes = best.Notes;
                Status = $"Downloading version {best.Version}…";
                string dir = Path.Combine(Paths.UpdatesDir, best.Version);
                string exe = Path.Combine(dir, "Caption.exe");
                var progress = new Progress<DownloadProgress>(p =>
                {
                    Progress = p.Fraction;
                    Status = $"Downloading version {best.Version}… {p.Text}";
                });
                var result = await Task.Run(() => Downloader.DownloadAsync(best.ExeUrl, exe, progress, ct, best.ExeSize), ct);

                // Verify: GitHub's own digest and the published SHA256SUMS.txt must both agree when present,
                // and at least one must be present.
                string? sums = best.SumsUrl != null ? await GetChecksumFromSumsAsync(best.SumsUrl, "Caption.exe", ct) : null;
                string? digest = best.ExeDigest;
                if (digest == null && sums == null)
                    throw new InvalidDataException("The release has no checksum, so it can't be verified.");
                if (digest != null && !string.Equals(digest, result.Sha256, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("The download doesn't match GitHub's checksum.");
                if (sums != null && !string.Equals(sums, result.Sha256, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("The download doesn't match SHA256SUMS.txt.");
                File.WriteAllText(exe + ".verified", result.Sha256);

                _readyExe = exe;
                UpdateReady = true;
                Progress = 1;
                Status = $"Version {best.Version} is ready. Restart to update.";
                Log.Info($"Update {best.Version} downloaded and verified ({result.Sha256})");
                Ready?.Invoke(this, EventArgs.Empty);
            }
            catch (OperationCanceledException)
            {
                Status = "";
            }
            catch (HttpRequestException ex)
            {
                Status = manual ? "Couldn't reach GitHub. Check your connection." : "";
                Log.Info("Update check: " + ex.Message);
            }
            catch (Exception ex)
            {
                Status = "Update failed: " + ex.Message;
                Log.Error("Update", ex);
                if (ex is InvalidDataException && AvailableVersion != null)
                {
                    try { Directory.Delete(Path.Combine(Paths.UpdatesDir, AvailableVersion), true); } catch { }
                }
            }
            finally
            {
                IsBusy = false;
            }
        }

        private async Task<List<ReleaseInfo>> GetReleasesAsync(CancellationToken ct)
        {
            // 1) The suite's release index (no API rate limit), if it's fresh and knows about Caption.
            try
            {
                using var resp = await Downloader.Client.GetAsync(IndexUrl, ct);
                if (resp.IsSuccessStatusCode)
                {
                    using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
                    long generated = doc.RootElement.TryGetProperty("generated", out var g) && g.TryGetInt64(out long gv) ? gv : 0;
                    bool fresh = DateTimeOffset.UtcNow.ToUnixTimeSeconds() - generated < 24 * 3600;
                    if (fresh && doc.RootElement.TryGetProperty("repos", out var repos) &&
                        repos.TryGetProperty(Repo.ToLowerInvariant(), out var arr) && arr.ValueKind == JsonValueKind.Array)
                    {
                        var list = ParseReleases(arr);
                        // The index can lag a few minutes; only trust it if it has something.
                        if (list.Count > 0) return list;
                    }
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                Log.Info("Release index: " + ex.Message);
            }

            // 2) The GitHub API directly.
            using var req = new HttpRequestMessage(HttpMethod.Get, $"https://api.github.com/repos/{Repo}/releases?per_page=15");
            req.Headers.Accept.ParseAdd("application/vnd.github+json");
            req.Headers.Add("X-GitHub-Api-Version", "2022-11-28");
            using var api = await Downloader.Client.SendAsync(req, ct);
            api.EnsureSuccessStatusCode();
            using var apiDoc = JsonDocument.Parse(await api.Content.ReadAsStringAsync(ct));
            return ParseReleases(apiDoc.RootElement);
        }

        private static List<ReleaseInfo> ParseReleases(JsonElement array)
        {
            var list = new List<ReleaseInfo>();
            foreach (var r in array.EnumerateArray())
            {
                if (r.TryGetProperty("draft", out var d) && d.ValueKind == JsonValueKind.True) continue;
                string tag = r.TryGetProperty("tag_name", out var t) ? t.GetString() ?? "" : "";
                if (!tag.StartsWith("v", StringComparison.OrdinalIgnoreCase)) continue;
                string version = tag.Substring(1);
                bool pre = r.TryGetProperty("prerelease", out var p) && p.ValueKind == JsonValueKind.True;
                string? exeUrl = null, sumsUrl = null, digest = null;
                long size = 0;
                if (r.TryGetProperty("assets", out var assets) && assets.ValueKind == JsonValueKind.Array)
                {
                    foreach (var a in assets.EnumerateArray())
                    {
                        string name = a.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
                        string url = a.TryGetProperty("browser_download_url", out var u) ? u.GetString() ?? "" : "";
                        if (string.Equals(name, "Caption.exe", StringComparison.OrdinalIgnoreCase))
                        {
                            exeUrl = url;
                            size = a.TryGetProperty("size", out var s) && s.TryGetInt64(out long sv) ? sv : 0;
                            string dg = a.TryGetProperty("digest", out var dd) && dd.ValueKind == JsonValueKind.String ? dd.GetString() ?? "" : "";
                            if (dg.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase)) digest = dg.Substring(7).ToLowerInvariant();
                        }
                        else if (string.Equals(name, "SHA256SUMS.txt", StringComparison.OrdinalIgnoreCase))
                        {
                            sumsUrl = url;
                        }
                    }
                }
                if (exeUrl == null) continue;
                string? notes = r.TryGetProperty("body", out var b) && b.ValueKind == JsonValueKind.String ? b.GetString() : null;
                list.Add(new ReleaseInfo(version, pre || version.Contains('-'), exeUrl, size, digest, sumsUrl, notes));
            }
            return list;
        }

        private static async Task<string?> GetChecksumFromSumsAsync(string url, string file, CancellationToken ct)
        {
            string text = await Downloader.Client.GetStringAsync(url, ct);
            foreach (var raw in text.Split('\n'))
            {
                var parts = raw.Trim().Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length >= 2 && string.Equals(parts[^1].TrimStart('*'), file, StringComparison.OrdinalIgnoreCase) && parts[0].Length == 64)
                    return parts[0].ToLowerInvariant();
            }
            return null;
        }

        public void Cancel() => _cts?.Cancel();

        // ---------------------------------------------------------------- restart handoff

        /// <summary>
        /// Swaps in the downloaded exe and starts it. Returns true if the new version took over (the caller should
        /// exit now); false if it failed to start and the old version was restored (the caller carries on).
        /// <paramref name="hideUi"/> runs before the new copy starts; <paramref name="restoreUi"/> runs if it fails.
        /// </summary>
        public Task<bool> RestartToUpdateAsync(Action hideUi, Action restoreUi) =>
            _readyExe != null && AvailableVersion != null
                ? HandoffAsync(_readyExe, AvailableVersion, hideUi, restoreUi, skipOnFailure: true)
                : Task.FromResult(false);

        /// <summary>Goes back to a kept earlier version (and skips the current one so it isn't re-installed).</summary>
        public async Task<bool> RollbackAsync(string version, Action hideUi, Action restoreUi)
        {
            string exe = Path.Combine(Paths.VersionsDir, version, "Caption.exe");
            if (!File.Exists(exe)) return false;
            if (!_settings.SkipVersions.Contains(AppVersion)) _settings.SkipVersions.Add(AppVersion);
            SettingsStore.Save(_settings);
            bool ok = await HandoffAsync(exe, version, hideUi, restoreUi, skipOnFailure: false);
            if (!ok)
            {
                _settings.SkipVersions.Remove(AppVersion);
                SettingsStore.Save(_settings);
            }
            return ok;
        }

        private async Task<bool> HandoffAsync(string newExe, string newVersion, Action hideUi, Action restoreUi, bool skipOnFailure)
        {
            string current = Paths.ExePath;
            string old = current + ".old";
            string staged = Path.Combine(Path.GetTempPath(), $"Caption-{newVersion}-{Guid.NewGuid():N}.exe");
            Process? child = null;
            bool swapped = false;
            int myPid = Environment.ProcessId;
            using var healthy = new EventWaitHandle(false, EventResetMode.ManualReset, HealthyEventName(myPid));
            try
            {
                File.Copy(newExe, staged, true);
                // Keep this version for manual rollback (last 2 kept).
                KeepVersion(current, AppVersion);

                hideUi();
                try { File.Delete(old); } catch { }
                File.Move(current, old);           // a running exe can be renamed, not overwritten
                swapped = true;
                File.Copy(staged, current, true);

                var psi = new ProcessStartInfo(current) { UseShellExecute = false, WorkingDirectory = Paths.ExeDir };
                psi.ArgumentList.Add("--after-update");
                psi.ArgumentList.Add(myPid.ToString());
                psi.ArgumentList.Add(AppVersion);
                child = Process.Start(psi);
                Log.Info($"Handing off to {newVersion} (pid {child?.Id})");

                bool ok = await Task.Run(() =>
                {
                    var sw = Stopwatch.StartNew();
                    while (sw.Elapsed < TimeSpan.FromSeconds(30))
                    {
                        if (healthy.WaitOne(250)) return true;
                        if (child == null || child.HasExited) return false;
                    }
                    return false;
                });
                if (ok)
                {
                    Log.Info($"Version {newVersion} started fine; exiting {AppVersion}.");
                    try { Directory.Delete(Path.Combine(Paths.UpdatesDir, newVersion), true); } catch { }
                    return true;
                }
                throw new TimeoutException($"Version {newVersion} didn't start.");
            }
            catch (Exception ex)
            {
                Log.Error($"Update to {newVersion}", ex);
                try { if (child is { HasExited: false }) child.Kill(true); } catch { }
                if (swapped)
                {
                    try
                    {
                        Thread.Sleep(300);
                        File.Delete(current);
                        File.Move(old, current);
                    }
                    catch (Exception restoreEx)
                    {
                        Log.Error("Restoring previous exe", restoreEx);
                    }
                }
                if (skipOnFailure)
                {
                    if (!_settings.SkipVersions.Contains(newVersion)) _settings.SkipVersions.Add(newVersion);
                    SettingsStore.Save(_settings);
                    UpdateReady = false;
                    _readyExe = null;
                    try { Directory.Delete(Path.Combine(Paths.UpdatesDir, newVersion), true); } catch { }
                }
                Status = $"Version {newVersion} couldn't start, so Caption stayed on {AppVersion}.";
                restoreUi();
                return false;
            }
            finally
            {
                try { File.Delete(staged); } catch { }
            }
        }

        public static string HealthyEventName(int oldPid) => $"Caption.Healthy.{oldPid}";

        private static void KeepVersion(string exe, string version)
        {
            try
            {
                string dir = Path.Combine(Paths.VersionsDir, version);
                Directory.CreateDirectory(dir);
                File.Copy(exe, Path.Combine(dir, "Caption.exe"), true);
                var all = Directory.GetDirectories(Paths.VersionsDir)
                    .OrderByDescending(d => Path.GetFileName(d), Comparer<string>.Create(CompareVersions)).ToList();
                foreach (var extra in all.Skip(2))
                    try { Directory.Delete(extra, true); } catch { }
            }
            catch (Exception ex)
            {
                Log.WarnOnce("Keeping previous version", ex);
            }
        }

        /// <summary>
        /// Called by the new copy (started with --after-update): tell the old copy we're healthy, then
        /// clean up the .old exe once it has exited.
        /// </summary>
        public static void ReportHealthy(int oldPid)
        {
            try
            {
                using var ev = EventWaitHandle.OpenExisting(HealthyEventName(oldPid));
                ev.Set();
            }
            catch (Exception ex)
            {
                Log.Info("Healthy signal: " + ex.Message);
            }
            Task.Run(() =>
            {
                try
                {
                    using var p = Process.GetProcessById(oldPid);
                    p.WaitForExit(30000);
                }
                catch
                {
                }
                for (int i = 0; i < 10; i++)
                {
                    try
                    {
                        string old = Paths.ExePath + ".old";
                        if (File.Exists(old)) File.Delete(old);
                        break;
                    }
                    catch
                    {
                        Thread.Sleep(1000);
                    }
                }
            });
        }

        // ---------------------------------------------------------------- versions

        /// <summary>Semantic version compare: 1.2.10 > 1.2.9, 1.3.0 > 1.3.0-beta.2 > 1.3.0-beta.1.</summary>
        public static int CompareVersions(string? a, string? b)
        {
            a = (a ?? "0").Trim().TrimStart('v', 'V');
            b = (b ?? "0").Trim().TrimStart('v', 'V');
            SplitVersion(a, out var ca, out var pa);
            SplitVersion(b, out var cb, out var pb);
            for (int i = 0; i < Math.Max(ca.Length, cb.Length); i++)
            {
                long x = i < ca.Length ? ca[i] : 0, y = i < cb.Length ? cb[i] : 0;
                if (x != y) return x.CompareTo(y);
            }
            if (pa == null && pb == null) return 0;
            if (pa == null) return 1;
            if (pb == null) return -1;
            var sa = pa.Split('.');
            var sb = pb.Split('.');
            for (int i = 0; i < Math.Max(sa.Length, sb.Length); i++)
            {
                if (i >= sa.Length) return -1;
                if (i >= sb.Length) return 1;
                bool na = long.TryParse(sa[i], out long xa), nb = long.TryParse(sb[i], out long xb);
                int c = na && nb ? xa.CompareTo(xb) : na ? -1 : nb ? 1 : string.CompareOrdinal(sa[i], sb[i]);
                if (c != 0) return c;
            }
            return 0;
        }

        private static void SplitVersion(string v, out long[] core, out string? pre)
        {
            int plus = v.IndexOf('+');
            if (plus >= 0) v = v.Substring(0, plus);
            int dash = v.IndexOf('-');
            pre = dash >= 0 ? v.Substring(dash + 1) : null;
            string c = dash >= 0 ? v.Substring(0, dash) : v;
            core = c.Split('.').Select(x => long.TryParse(x, out long n) ? n : 0).ToArray();
        }
    }
}
