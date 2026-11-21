using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace Caption.Services
{
    /// <summary>
    /// The optional second translation step, through the Ollama server on this PC (default localhost:11434).
    /// Nothing leaves the machine: Caption only talks to the address in Settings.
    /// </summary>
    public sealed class OllamaClient
    {
        private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(60) };
        private readonly string _baseUrl;

        public OllamaClient(string baseUrl) => _baseUrl = baseUrl.TrimEnd('/');

        /// <summary>Installed models, or null if Ollama isn't running.</summary>
        public async Task<List<string>?> ListModelsAsync(CancellationToken ct = default)
        {
            try
            {
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                cts.CancelAfter(TimeSpan.FromSeconds(3));
                string json = await Http.GetStringAsync(_baseUrl + "/api/tags", cts.Token).ConfigureAwait(false);
                var root = JsonNode.Parse(json);
                return root?["models"]?.AsArray()
                    .Select(m => m?["name"]?.GetValue<string>() ?? "")
                    .Where(n => n.Length > 0 && !n.Contains("embed", StringComparison.OrdinalIgnoreCase))
                    .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
                    .ToList() ?? new List<string>();
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                return null;
            }
        }

        /// <summary>Translates one caption line. <paramref name="context"/> is the previous line or two, for consistency.</summary>
        public async Task<string?> TranslateAsync(string model, string text, string targetLanguage, string? sourceLanguage,
            string? context, CancellationToken ct)
        {
            string system =
                $"You translate live captions into {targetLanguage}. Reply with only the {targetLanguage} translation " +
                "of the caption, nothing else: no quotes, notes, explanations or romanization. Keep names as they are. " +
                $"If the caption is already in {targetLanguage}, repeat it unchanged.";
            var sb = new StringBuilder();
            if (!string.IsNullOrWhiteSpace(context))
                sb.Append("Earlier captions, for context only (do not translate them):\n").Append(context.Trim()).Append("\n\n");
            if (!string.IsNullOrWhiteSpace(sourceLanguage))
                sb.Append("Caption (").Append(sourceLanguage).Append("):\n");
            else
                sb.Append("Caption:\n");
            sb.Append(text);

            var body = new JsonObject
            {
                ["model"] = model,
                ["stream"] = false,
                ["keep_alive"] = "15m",
                ["think"] = false,
                ["options"] = new JsonObject { ["temperature"] = 0.1, ["num_predict"] = 256 },
                ["messages"] = new JsonArray
                {
                    new JsonObject { ["role"] = "system", ["content"] = system },
                    new JsonObject { ["role"] = "user", ["content"] = sb.ToString() },
                },
            };
            using var content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");
            using var resp = await Http.PostAsync(_baseUrl + "/api/chat", content, ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
            {
                // Older Ollama versions reject "think"; retry without it.
                if ((int)resp.StatusCode == 400)
                {
                    body.Remove("think");
                    using var retry = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");
                    using var resp2 = await Http.PostAsync(_baseUrl + "/api/chat", retry, ct).ConfigureAwait(false);
                    if (!resp2.IsSuccessStatusCode) return null;
                    return Extract(await resp2.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
                }
                return null;
            }
            return Extract(await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
        }

        private static string? Extract(string json)
        {
            var root = JsonNode.Parse(json);
            string? s = root?["message"]?["content"]?.GetValue<string>();
            if (s == null) return null;
            // Some models still wrap their reasoning; drop it.
            int end = s.LastIndexOf("</think>", StringComparison.OrdinalIgnoreCase);
            if (end >= 0) s = s.Substring(end + 8);
            s = s.Trim().Trim('"', '“', '”').Trim();
            return s.Length == 0 ? null : s;
        }
    }
}
