using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Caption.Services;

namespace Caption.Subtitles
{
    /// <summary>
    /// A tiny HTTP API on 127.0.0.1 (never reachable from the network) that lets apps on this PC, like Ripple,
    /// ask for subtitles for what they're playing.
    ///
    ///   GET    /v1/health                     → { app, version, ready, model }
    ///   POST   /v1/jobs                       { url, headers?, title?, position?, language?, audioIndex? } → { id, srt, vtt }
    ///   GET    /v1/jobs/{id}                  → { state, error, doneUntil, cues, version, language }
    ///   GET    /v1/jobs/{id}.srt              → the subtitles so far
    ///   POST   /v1/jobs/{id}/audio            { audioIndex } → starts over on another audio track (same .srt address)
    ///   GET    /v1/jobs/{id}/cues?from=&amp;to=   → JSON cues in a time range
    ///   POST   /v1/jobs/{id}/position         { position } (call every few seconds while playing, and on seeks)
    ///   DELETE /v1/jobs/{id}
    /// </summary>
    public sealed class SubtitleServer : IDisposable
    {
        public const int Port = 46277;
        private readonly SubtitleService _service;
        private TcpListener? _listener;
        private CancellationTokenSource? _cts;

        public SubtitleServer(SubtitleService service) => _service = service;

        public bool Running => _listener != null;
        public string Error { get; private set; } = "";

        public void Start()
        {
            if (_listener != null) return;
            try
            {
                _cts = new CancellationTokenSource();
                _listener = new TcpListener(IPAddress.Loopback, Port);
                _listener.Start();
                _ = AcceptLoop(_listener, _cts.Token);
                Error = "";
                Log.Info($"Subtitle API listening on 127.0.0.1:{Port}");
            }
            catch (Exception ex)
            {
                _listener = null;
                Error = ex is SocketException ? $"Port {Port} is in use by another app." : ex.Message;
                Log.Error("Subtitle API", ex);
            }
        }

        public void Stop()
        {
            _cts?.Cancel();
            try { _listener?.Stop(); } catch { }
            _listener = null;
        }

        private async Task AcceptLoop(TcpListener listener, CancellationToken ct)
        {
            while (!ct.IsCancellationRequested)
            {
                TcpClient client;
                try { client = await listener.AcceptTcpClientAsync(ct); }
                catch { break; }
                _ = Task.Run(() => Handle(client, ct), ct);
            }
        }

        private sealed class Request
        {
            public string Method = "";
            public string Path = "";
            public Dictionary<string, string> Query = new(StringComparer.OrdinalIgnoreCase);
            public Dictionary<string, string> Headers = new(StringComparer.OrdinalIgnoreCase);
            public string Body = "";
        }

        private async Task Handle(TcpClient client, CancellationToken ct)
        {
            using var _ = client;
            client.ReceiveTimeout = 10000;
            var stream = client.GetStream();
            try
            {
                var req = await ReadRequest(stream, ct);
                if (req == null) return;
                var (status, type, body) = await Route(req, ct);
                await Write(stream, status, type, body);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                try { await Write(stream, 500, "application/json", Json(new JsonObject { ["error"] = ex.Message })); } catch { }
            }
        }

        private static async Task<Request?> ReadRequest(NetworkStream stream, CancellationToken ct)
        {
            var buffer = new byte[65536];
            int total = 0, headerEnd = -1;
            while (headerEnd < 0)
            {
                int n = await stream.ReadAsync(buffer.AsMemory(total, buffer.Length - total), ct);
                if (n <= 0) return null;
                total += n;
                for (int i = 3; i < total; i++)
                    if (buffer[i - 3] == '\r' && buffer[i - 2] == '\n' && buffer[i - 1] == '\r' && buffer[i] == '\n') { headerEnd = i + 1; break; }
                if (total == buffer.Length) return null;
            }
            string head = Encoding.ASCII.GetString(buffer, 0, headerEnd);
            var lines = head.Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
            var first = lines[0].Split(' ');
            if (first.Length < 2) return null;
            var req = new Request { Method = first[0].ToUpperInvariant() };
            string target = first[1];
            int q = target.IndexOf('?');
            req.Path = Uri.UnescapeDataString(q >= 0 ? target.Substring(0, q) : target);
            if (q >= 0)
                foreach (var kv in target.Substring(q + 1).Split('&', StringSplitOptions.RemoveEmptyEntries))
                {
                    int e = kv.IndexOf('=');
                    if (e > 0) req.Query[Uri.UnescapeDataString(kv.Substring(0, e))] = Uri.UnescapeDataString(kv.Substring(e + 1));
                }
            foreach (var l in lines.Skip(1))
            {
                int c = l.IndexOf(':');
                if (c > 0) req.Headers[l.Substring(0, c).Trim()] = l.Substring(c + 1).Trim();
            }
            int length = req.Headers.TryGetValue("Content-Length", out var cl) && int.TryParse(cl, out int v) ? Math.Clamp(v, 0, 1 << 20) : 0;
            var body = new byte[length];
            int have = Math.Min(length, total - headerEnd);
            Array.Copy(buffer, headerEnd, body, 0, have);
            while (have < length)
            {
                int n = await stream.ReadAsync(body.AsMemory(have, length - have), ct);
                if (n <= 0) break;
                have += n;
            }
            req.Body = Encoding.UTF8.GetString(body, 0, have);
            return req;
        }

        private static async Task Write(NetworkStream stream, int status, string type, string body)
        {
            var bytes = Encoding.UTF8.GetBytes(body);
            string reason = status switch { 200 => "OK", 204 => "No Content", 400 => "Bad Request", 403 => "Forbidden", 404 => "Not Found", 409 => "Conflict", 503 => "Service Unavailable", _ => "Error" };
            var head = new StringBuilder()
                .Append($"HTTP/1.1 {status} {reason}\r\n")
                .Append($"Content-Type: {type}\r\n")
                .Append($"Content-Length: {bytes.Length}\r\n")
                .Append("Cache-Control: no-store\r\n")
                .Append("Access-Control-Allow-Origin: *\r\n")
                .Append("Access-Control-Allow-Methods: GET, POST, DELETE, OPTIONS\r\n")
                .Append("Access-Control-Allow-Headers: Content-Type\r\n")
                .Append("Access-Control-Allow-Private-Network: true\r\n")
                .Append("Connection: close\r\n\r\n");
            var h = Encoding.ASCII.GetBytes(head.ToString());
            await stream.WriteAsync(h);
            await stream.WriteAsync(bytes);
            await stream.FlushAsync();
        }

        private static readonly HashSet<string> AllowedOrigins = new(StringComparer.OrdinalIgnoreCase)
        {
            "tauri://localhost", "http://tauri.localhost", "https://tauri.localhost",
        };

        private static string Json(JsonNode node) => node.ToJsonString();

        private static string Num(double d) => d.ToString("0.###", CultureInfo.InvariantCulture);

        private async Task<(int, string, string)> Route(Request req, CancellationToken ct)
        {
            const string J = "application/json; charset=utf-8";
            // Websites in a browser must not be able to use this: only native apps (no Origin) and Ripple's
            // own webview may call it.
            if (req.Headers.TryGetValue("Origin", out var origin) && !AllowedOrigins.Contains(origin))
                return (403, J, Json(new JsonObject { ["error"] = "origin not allowed" }));
            if (req.Method == "OPTIONS") return (204, "text/plain", "");
            var parts = req.Path.Trim('/').Split('/');
            if (parts.Length < 2 || parts[0] != "v1") return (404, J, Json(new JsonObject { ["error"] = "not found" }));

            if (parts[1] == "health" && req.Method == "GET")
            {
                var model = _service.PickModel();
                return (200, J, Json(new JsonObject
                {
                    ["app"] = "Caption",
                    ["version"] = UpdateService.AppVersion,
                    ["ready"] = model != null,
                    ["model"] = model?.Name ?? "",
                    ["ffmpeg"] = Ffmpeg.IsInstalled,
                }));
            }

            if (parts[1] != "jobs") return (404, J, Json(new JsonObject { ["error"] = "not found" }));

            if (parts.Length == 2 && req.Method == "POST")
            {
                JobRequest? jr;
                try { jr = JsonSerializer.Deserialize<JobRequest>(req.Body, new JsonSerializerOptions { PropertyNameCaseInsensitive = true }); }
                catch { jr = null; }
                if (jr == null || string.IsNullOrWhiteSpace(jr.Url)) return (400, J, Json(new JsonObject { ["error"] = "url is required" }));
                bool web = jr.Url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || jr.Url.StartsWith("https://", StringComparison.OrdinalIgnoreCase);
                bool localFile = !web && Path.IsPathFullyQualified(jr.Url) && File.Exists(jr.Url);
                if (!web && !localFile) return (400, J, Json(new JsonObject { ["error"] = "url must be http(s) or a local file" }));
                try
                {
                    var job = await _service.StartAsync(jr, ct);
                    string root = $"http://127.0.0.1:{Port}/v1/jobs/{job.Id}";
                    return (200, J, Json(new JsonObject { ["id"] = job.Id, ["srt"] = root + ".srt", ["status"] = root }));
                }
                catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or IOException or InvalidDataException or System.Net.Http.HttpRequestException)
                {
                    Log.Error("Starting subtitle job", ex);
                    return (503, J, Json(new JsonObject { ["error"] = ex.Message }));
                }
            }

            if (parts.Length < 3) return (404, J, Json(new JsonObject { ["error"] = "not found" }));
            string id = parts[2];
            bool srt = id.EndsWith(".srt", StringComparison.OrdinalIgnoreCase);
            if (srt) id = id.Substring(0, id.Length - 4);
            var j = _service.Find(id);
            if (j == null) return (404, J, Json(new JsonObject { ["error"] = "no such job" }));
            j.Touch();

            if (srt && req.Method == "GET") return (200, "application/x-subrip; charset=utf-8", j.Srt());

            if (parts.Length == 3 && req.Method == "GET")
                return (200, J, Json(new JsonObject
                {
                    ["id"] = j.Id,
                    ["state"] = j.State,
                    ["error"] = j.Error,
                    ["doneUntil"] = Math.Round(j.ReadyUntil, 2),
                    ["cues"] = j.CueCount,
                    ["version"] = j.Version,
                    ["language"] = j.Language,
                    ["translating"] = j.Translating,
                }));

            if (parts.Length == 3 && req.Method == "DELETE")
            {
                _service.Stop(id);
                return (200, J, "{}");
            }

            if (parts.Length == 4 && parts[3] == "position" && req.Method == "POST")
            {
                double pos = 0;
                try { pos = JsonNode.Parse(req.Body)?["position"]?.GetValue<double>() ?? 0; } catch { }
                j.SetPosition(pos);
                return (200, J, Json(new JsonObject { ["doneUntil"] = Math.Round(j.ReadyUntil, 2), ["version"] = j.Version }));
            }

            if (parts.Length == 4 && parts[3] == "audio" && req.Method == "POST")
            {
                int idx = -1;
                try { idx = JsonNode.Parse(req.Body)?["audioIndex"]?.GetValue<int>() ?? -1; } catch { }
                if (idx < 0) return (400, J, Json(new JsonObject { ["error"] = "audioIndex is required" }));
                j.SetAudio(idx);
                return (200, J, Json(new JsonObject { ["version"] = j.Version }));
            }

            if (parts.Length == 4 && parts[3] == "cues" && req.Method == "GET")
            {
                double from = req.Query.TryGetValue("from", out var f) && double.TryParse(f, NumberStyles.Float, CultureInfo.InvariantCulture, out var fv) ? fv : 0;
                double to = req.Query.TryGetValue("to", out var t) && double.TryParse(t, NumberStyles.Float, CultureInfo.InvariantCulture, out var tv) ? tv : double.MaxValue;
                var arr = new JsonArray();
                foreach (var c in j.Cues(from, to))
                    arr.Add(new JsonObject { ["start"] = Math.Round(c.Start, 3), ["end"] = Math.Round(c.End, 3), ["text"] = c.Text });
                return (200, J, Json(new JsonObject { ["cues"] = arr, ["version"] = j.Version }));
            }

            return (404, J, Json(new JsonObject { ["error"] = "not found" }));
        }

        public void Dispose() => Stop();
    }
}
