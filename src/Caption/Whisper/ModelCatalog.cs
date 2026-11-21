using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Caption.Services;

namespace Caption.Whisper
{
    public sealed class ModelInfo
    {
        public string Id = "";
        public string Name = "";
        public string File = "";
        public long ApproxSize;
        public string Blurb = "";
        /// <summary>1-5, for the little dot ratings.</summary>
        public int Speed;
        public int Accuracy;
        /// <summary>Rough time to re-read a 5 second line.</summary>
        public string GpuSpeed = "";
        public string CpuSpeed = "";
        public bool TranslatesWell = true;
        public bool Recommended;

        public string Url => $"https://huggingface.co/ggerganov/whisper.cpp/resolve/main/{File}";
        public string Path => System.IO.Path.Combine(Paths.ModelsDir, File);
        public bool IsInstalled => System.IO.File.Exists(Path);
        public string SizeText => DownloadProgress.Format(ApproxSize);
    }

    /// <summary>The Whisper models Caption offers (ggml format, from the whisper.cpp model repo on Hugging Face).</summary>
    public static class ModelCatalog
    {
        public static readonly IReadOnlyList<ModelInfo> Models = new List<ModelInfo>
        {
            new()
            {
                Id = "small", Name = "Small", File = "ggml-small.bin", ApproxSize = 487_601_967,
                Blurb = "Fastest. Good for clear speech, and the best pick without an NVIDIA GPU.",
                Speed = 5, Accuracy = 3, GpuSpeed = "~0.05 s per update", CpuSpeed = "~1 s per update",
            },
            new()
            {
                Id = "medium", Name = "Medium", File = "ggml-medium.bin", ApproxSize = 1_533_763_059,
                Blurb = "More accurate than Small and translates to English well. Slower.",
                Speed = 3, Accuracy = 4, GpuSpeed = "~0.2 s per update", CpuSpeed = "~4 s per update",
            },
            new()
            {
                Id = "large-v3-turbo", Name = "Large v3 Turbo", File = "ggml-large-v3-turbo.bin", ApproxSize = 1_624_555_275,
                Blurb = "Near large-model accuracy at a fraction of the cost. Best for live captions on a GPU. " +
                        "Not trained to translate: pick Medium or Large v3 for English translation, or use Ollama.",
                Speed = 4, Accuracy = 5, GpuSpeed = "~0.12 s per update", CpuSpeed = "~4 s per update",
                TranslatesWell = false, Recommended = true,
            },
            new()
            {
                Id = "large-v3", Name = "Large v3", File = "ggml-large-v3.bin", ApproxSize = 3_095_033_483,
                Blurb = "The most accurate, and the best at translating to English. Needs a strong GPU.",
                Speed = 2, Accuracy = 5, GpuSpeed = "~0.35 s per update", CpuSpeed = "~10 s per update",
            },
        };

        public static ModelInfo? Find(string id) => Models.FirstOrDefault(m => m.Id == id);

        /// <summary>The model Caption suggests for this PC.</summary>
        public static ModelInfo Suggested(bool gpu) => Find(gpu ? "large-v3-turbo" : "small")!;

        // Silero voice activity detection (about 1 MB), so silence never turns into text.
        public const string VadFile = "ggml-silero-v5.1.2.bin";
        public const string VadUrl = "https://huggingface.co/ggml-org/whisper-vad/resolve/main/" + VadFile;
        public static string VadPath => System.IO.Path.Combine(Paths.ModelsDir, VadFile);

        public static async Task DownloadAsync(ModelInfo model, IProgress<DownloadProgress>? progress, CancellationToken ct)
        {
            await DownloadVerifiedAsync(model.Url, model.Path, progress, ct).ConfigureAwait(false);
            await EnsureVadAsync(ct).ConfigureAwait(false);
            Log.Info($"Model {model.Id} downloaded");
        }

        public static async Task EnsureVadAsync(CancellationToken ct)
        {
            if (File.Exists(VadPath)) return;
            try
            {
                await DownloadVerifiedAsync(VadUrl, VadPath, null, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Optional: Caption falls back to its own (energy-based) voice detection.
                Log.Error("VAD model download", ex);
            }
        }

        /// <summary>Downloads a Hugging Face file and checks it against the SHA-256 Hugging Face publishes for it.</summary>
        private static async Task DownloadVerifiedAsync(string url, string path, IProgress<DownloadProgress>? progress, CancellationToken ct)
        {
            var (sha, size) = await Downloader.ProbeAsync(url, ct).ConfigureAwait(false);
            string tmp = path + ".download";
            var result = await Downloader.DownloadAsync(url, tmp, progress, ct, size).ConfigureAwait(false);
            string expected = sha ?? result.ServerSha256 ?? "";
            if (expected.Length == 64 && !string.Equals(expected, result.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                File.Delete(tmp);
                throw new InvalidDataException("The download is damaged (checksum mismatch). Try again.");
            }
            if (expected.Length != 64) Log.Info($"No published checksum for {url}; size {result.Length} accepted");
            File.Move(tmp, path, overwrite: true);
        }

        public static void Delete(ModelInfo model)
        {
            try { File.Delete(model.Path); } catch (Exception ex) { Log.Error("Deleting model", ex); }
        }
    }
}
