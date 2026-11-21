using System;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Caption.Services;

namespace Caption.Subtitles
{
    /// <summary>
    /// FFmpeg, used to read a movie's audio straight from the stream ahead of playback. Downloaded once
    /// (the LGPL build from BtbN/FFmpeg-Builds on GitHub, checked against the release's SHA-256 list).
    /// </summary>
    public static class Ffmpeg
    {
        private const string Release = "https://github.com/BtbN/FFmpeg-Builds/releases/download/latest/";
        private const string Asset = "ffmpeg-n8.1-latest-win64-lgpl-shared-8.1.zip";
        public const long ApproxSize = 81_500_000;

        public static string Dir => Path.Combine(Paths.DataDir, "ffmpeg");
        public static string Exe => Path.Combine(Dir, "ffmpeg.exe");
        public static bool IsInstalled => File.Exists(Exe) && File.Exists(Path.Combine(Dir, ".ok"));

        private static readonly SemaphoreSlim Gate = new(1, 1);

        public static async Task EnsureAsync(IProgress<DownloadProgress>? progress, CancellationToken ct)
        {
            if (IsInstalled) return;
            await Gate.WaitAsync(ct);
            try
            {
                if (IsInstalled) return;
                string sums = await Downloader.Client.GetStringAsync(Release + "checksums.sha256", ct);
                string? expected = sums.Split('\n')
                    .Select(l => l.Trim().Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries))
                    .Where(p => p.Length >= 2 && p[^1].TrimStart('*') == Asset && p[0].Length == 64)
                    .Select(p => p[0].ToLowerInvariant()).FirstOrDefault();
                if (expected == null) throw new InvalidDataException("FFmpeg's checksum list doesn't include the build Caption needs.");

                string zip = Path.Combine(Paths.DataDir, "downloads", Asset);
                var result = await Downloader.DownloadAsync(Release + Asset, zip, progress, ct);
                if (!string.Equals(result.Sha256, expected, StringComparison.OrdinalIgnoreCase))
                {
                    File.Delete(zip);
                    throw new InvalidDataException("The FFmpeg download is damaged (checksum mismatch). Try again.");
                }
                await Task.Run(() =>
                {
                    string tmp = Dir + ".tmp";
                    if (Directory.Exists(tmp)) Directory.Delete(tmp, true);
                    Directory.CreateDirectory(tmp);
                    using (var archive = ZipFile.OpenRead(zip))
                    {
                        foreach (var e in archive.Entries)
                        {
                            string name = e.FullName.Replace('\\', '/');
                            if (!name.Contains("/bin/")) continue;
                            string file = Path.GetFileName(name);
                            if (file.Length == 0 || file is "ffplay.exe" or "ffprobe.exe") continue;
                            if (!(file.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) || file == "ffmpeg.exe")) continue;
                            e.ExtractToFile(Path.Combine(tmp, file), true);
                        }
                    }
                    if (!File.Exists(Path.Combine(tmp, "ffmpeg.exe"))) throw new InvalidDataException("ffmpeg.exe is missing from the download.");
                    File.WriteAllText(Path.Combine(tmp, ".ok"), expected);
                    if (Directory.Exists(Dir)) Directory.Delete(Dir, true);
                    Directory.Move(tmp, Dir);
                }, ct);
                try { File.Delete(zip); } catch { }
                Log.Info("FFmpeg installed");
            }
            finally
            {
                Gate.Release();
            }
        }

        /// <summary>
        /// Starts decoding a stream's audio from <paramref name="startSeconds"/> as 16 kHz stereo float on stdout
        /// (stereo so Caption's dialogue focus can work on it).
        /// </summary>
        public static Process StartAudio(string url, string? headers, string? userAgent, double startSeconds, int audioIndex = 0)
        {
            var psi = new ProcessStartInfo(Exe)
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                RedirectStandardInput = false,
                CreateNoWindow = true,
                WorkingDirectory = Dir,
            };
            foreach (var a in new[] { "-nostdin", "-hide_banner", "-loglevel", "error" }) psi.ArgumentList.Add(a);
            bool http = url.StartsWith("http", StringComparison.OrdinalIgnoreCase);
            if (http)
            {
                foreach (var a in new[] { "-reconnect", "1", "-reconnect_streamed", "1", "-reconnect_delay_max", "5", "-rw_timeout", "30000000" })
                    psi.ArgumentList.Add(a);
                if (!string.IsNullOrWhiteSpace(userAgent)) { psi.ArgumentList.Add("-user_agent"); psi.ArgumentList.Add(userAgent); }
                if (!string.IsNullOrWhiteSpace(headers)) { psi.ArgumentList.Add("-headers"); psi.ArgumentList.Add(headers); }
            }
            if (startSeconds > 0.5)
            {
                psi.ArgumentList.Add("-ss");
                psi.ArgumentList.Add(startSeconds.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture));
            }
            foreach (var a in new[] { "-i", url, "-map", $"0:a:{Math.Max(0, audioIndex)}?", "-vn", "-sn", "-dn", "-ac", "2", "-ar", "16000", "-f", "f32le", "pipe:1" })
                psi.ArgumentList.Add(a);
            var p = Process.Start(psi) ?? throw new InvalidOperationException("FFmpeg didn't start.");
            try { p.PriorityClass = ProcessPriorityClass.BelowNormal; } catch { }
            return p;
        }
    }
}
