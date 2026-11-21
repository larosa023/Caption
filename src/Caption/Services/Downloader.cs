using System;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;

namespace Caption.Services
{
    public readonly record struct DownloadProgress(long Received, long Total, double BytesPerSecond)
    {
        public double Fraction => Total > 0 ? Math.Clamp((double)Received / Total, 0, 1) : 0;

        public string Text
        {
            get
            {
                string s = Total > 0 ? $"{Format(Received)} of {Format(Total)}" : Format(Received);
                if (BytesPerSecond > 1)
                {
                    s += $"  ·  {Format((long)BytesPerSecond)}/s";
                    if (Total > 0)
                    {
                        double left = (Total - Received) / BytesPerSecond;
                        if (left > 1) s += left > 90 ? $"  ·  {Math.Ceiling(left / 60)} min left" : $"  ·  {Math.Ceiling(left)} s left";
                    }
                }
                return s;
            }
        }

        public static string Format(long bytes) => bytes switch
        {
            >= 1L << 30 => $"{bytes / (double)(1L << 30):0.0} GB",
            >= 1L << 20 => $"{bytes / (double)(1L << 20):0} MB",
            >= 1L << 10 => $"{bytes / (double)(1L << 10):0} KB",
            _ => $"{bytes} B"
        };
    }

    public sealed class DownloadResult
    {
        public string Path = "";
        public string Sha256 = "";
        /// <summary>The sha256 the server vouched for (Hugging Face's X-Linked-Etag), if any.</summary>
        public string? ServerSha256;
        public long Length;
    }

    /// <summary>HTTP downloads with progress, resume (".partial" files) and SHA-256.</summary>
    public static class Downloader
    {
        private static readonly Lazy<HttpClient> LazyClient = new(() =>
        {
            var handler = new SocketsHttpHandler
            {
                AutomaticDecompression = DecompressionMethods.None,
                PooledConnectionIdleTimeout = TimeSpan.FromSeconds(30),
                ConnectTimeout = TimeSpan.FromSeconds(20),
            };
            var c = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
            c.DefaultRequestHeaders.UserAgent.ParseAdd($"Caption/{UpdateService.AppVersion} (+https://github.com/larosa023/Caption)");
            return c;
        });

        public static HttpClient Client => LazyClient.Value;

        /// <summary>
        /// Downloads url to destination. Keeps a ".partial" file so a broken download resumes where it stopped.
        /// Throws on failure or cancel (the partial file is kept for next time).
        /// </summary>
        public static async Task<DownloadResult> DownloadAsync(string url, string destination,
            IProgress<DownloadProgress>? progress, CancellationToken ct, long expectedSize = 0)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            string partial = destination + ".partial";
            long existing = File.Exists(partial) ? new FileInfo(partial).Length : 0;
            if (expectedSize > 0 && existing > expectedSize) { File.Delete(partial); existing = 0; }

            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            if (existing > 0) request.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(existing, null);
            using var response = await Client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);

            if (response.StatusCode == HttpStatusCode.RequestedRangeNotSatisfiable)
            {
                // Probably complete already (or the file changed); start over.
                File.Delete(partial);
                return await DownloadAsync(url, destination, progress, ct, expectedSize).ConfigureAwait(false);
            }
            response.EnsureSuccessStatusCode();

            bool resumed = response.StatusCode == HttpStatusCode.PartialContent && existing > 0;
            if (!resumed) existing = 0;

            string? serverSha = null;
            foreach (var h in new[] { "X-Linked-Etag", "x-linked-etag" })
            {
                if (response.Headers.TryGetValues(h, out var vals))
                {
                    foreach (var v in vals)
                    {
                        string s = v.Trim().Trim('"');
                        if (s.StartsWith("W/")) s = s.Substring(2).Trim('"');
                        if (s.Length == 64) serverSha = s.ToLowerInvariant();
                    }
                }
            }

            long total = response.Content.Headers.ContentLength is long len ? len + existing : expectedSize;
            using (var input = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false))
            using (var output = new FileStream(partial, resumed ? FileMode.Append : FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16))
            {
                var buffer = new byte[1 << 16];
                long received = existing;
                var clock = Stopwatch.StartNew();
                long lastReport = 0, speedMark = received;
                double speed = 0;
                var speedClock = Stopwatch.StartNew();
                int read;
                while ((read = await input.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
                {
                    await output.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
                    received += read;
                    if (clock.ElapsedMilliseconds - lastReport >= 200)
                    {
                        lastReport = clock.ElapsedMilliseconds;
                        if (speedClock.ElapsedMilliseconds >= 1000)
                        {
                            double inst = (received - speedMark) / speedClock.Elapsed.TotalSeconds;
                            speed = speed <= 0 ? inst : speed * 0.6 + inst * 0.4;
                            speedMark = received;
                            speedClock.Restart();
                        }
                        progress?.Report(new DownloadProgress(received, total, speed));
                    }
                }
                progress?.Report(new DownloadProgress(received, total, speed));
            }

            var result = new DownloadResult
            {
                Path = destination,
                ServerSha256 = serverSha,
                Length = new FileInfo(partial).Length,
            };
            if (expectedSize > 0 && result.Length != expectedSize)
            {
                File.Delete(partial);
                throw new InvalidDataException($"Download was {result.Length} bytes, expected {expectedSize}.");
            }
            result.Sha256 = await Sha256OfFileAsync(partial, ct).ConfigureAwait(false);
            File.Move(partial, destination, overwrite: true);
            return result;
        }

        private static readonly Lazy<HttpClient> LazyNoRedirect = new(() =>
        {
            var c = new HttpClient(new SocketsHttpHandler { AllowAutoRedirect = false, ConnectTimeout = TimeSpan.FromSeconds(15) })
            { Timeout = TimeSpan.FromSeconds(30) };
            c.DefaultRequestHeaders.UserAgent.ParseAdd($"Caption/{UpdateService.AppVersion}");
            return c;
        });

        /// <summary>
        /// Asks Hugging Face what a file should be: its LFS sha256 and size come back on the redirect
        /// (X-Linked-Etag / X-Linked-Size) before the CDN takes over. Nulls if unknown.
        /// </summary>
        public static async Task<(string? Sha256, long Size)> ProbeAsync(string url, CancellationToken ct)
        {
            try
            {
                using var req = new HttpRequestMessage(HttpMethod.Head, url);
                using var resp = await LazyNoRedirect.Value.SendAsync(req, ct).ConfigureAwait(false);
                string? sha = null;
                long size = 0;
                if (resp.Headers.TryGetValues("X-Linked-Etag", out var e))
                    foreach (var v in e)
                    {
                        string s = v.Trim();
                        if (s.StartsWith("W/")) s = s.Substring(2);
                        s = s.Trim('"');
                        if (s.Length == 64) sha = s.ToLowerInvariant();
                    }
                if (resp.Headers.TryGetValues("X-Linked-Size", out var z))
                    foreach (var v in z) long.TryParse(v, out size);
                return (sha, size);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                Log.WarnOnce("Probing " + url, ex);
                return (null, 0);
            }
        }

        public static async Task<string> Sha256OfFileAsync(string path, CancellationToken ct = default)
        {
            using var sha = SHA256.Create();
            await using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20, FileOptions.SequentialScan);
            var hash = await sha.ComputeHashAsync(fs, ct).ConfigureAwait(false);
            return Convert.ToHexString(hash).ToLowerInvariant();
        }
    }
}
