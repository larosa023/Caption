using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Caption.Services;
using Microsoft.Win32;

namespace Caption.Whisper
{
    public sealed class EngineVariant
    {
        public string Id = "";
        public string Name = "";
        public string Url = "";
        public string Sha256 = "";
        public long Size;
        public string[] Files = Array.Empty<string>();
        public bool Gpu;

        public string Dir => Path.Combine(Paths.EngineDir, $"{Id}-{EngineManager.WhisperVersion}");
        public bool IsInstalled => File.Exists(Path.Combine(Dir, ".ok")) && Files.All(f => File.Exists(Path.Combine(Dir, f)));
        public string SizeText => DownloadProgress.Format(Size);
    }

    /// <summary>
    /// The whisper.cpp engine, downloaded once from the official ggml-org release and pinned by SHA-256:
    /// the CUDA 12.4 build for NVIDIA GPUs (includes the CUDA runtime and cuBLAS, so no CUDA install is needed)
    /// or the CPU build.
    /// </summary>
    public static class EngineManager
    {
        public const string WhisperVersion = "1.8.2";
        private const string Base = "https://github.com/ggml-org/whisper.cpp/releases/download/v" + WhisperVersion + "/";

        private static readonly string[] CoreFiles = { "whisper.dll", "ggml.dll", "ggml-base.dll", "ggml-cpu.dll" };

        public static readonly EngineVariant Cuda = new()
        {
            Id = "cuda",
            Name = "NVIDIA GPU (CUDA)",
            Url = Base + "whisper-cublas-12.4.0-bin-x64.zip",
            Sha256 = "54a3c012e6c567e2e8bed874cdf49c55f018ec8d90bb5d5271568aa573291b2d",
            Size = 457274520,
            Files = CoreFiles.Concat(new[] { "ggml-cuda.dll", "cudart64_12.dll", "cublas64_12.dll", "cublasLt64_12.dll" }).ToArray(),
            Gpu = true,
        };

        public static readonly EngineVariant Cpu = new()
        {
            Id = "cpu",
            Name = "CPU",
            Url = Base + "whisper-bin-x64.zip",
            Sha256 = "b1514ebc099765e39fa37eb780b92a140a94c86bb0b3b3d98226b38825979732",
            Size = 3832432,
            Files = CoreFiles,
            Gpu = false,
        };

        /// <summary>The variant to use for the Processor setting ("auto", "gpu", "cpu").</summary>
        public static EngineVariant Pick(string processor) => processor switch
        {
            "cpu" => Cpu,
            "gpu" => Cuda,
            _ => GpuInfo.Detect().HasNvidia ? Cuda : Cpu,
        };

        public static async Task InstallAsync(EngineVariant v, IProgress<DownloadProgress>? progress, CancellationToken ct)
        {
            if (v.IsInstalled) return;
            string dl = Path.Combine(Paths.EngineDir, "downloads", Path.GetFileName(new Uri(v.Url).LocalPath));
            var result = await Downloader.DownloadAsync(v.Url, dl, progress, ct, v.Size).ConfigureAwait(false);
            if (!string.Equals(result.Sha256, v.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                File.Delete(dl);
                throw new InvalidDataException("The speech engine download is damaged (checksum mismatch). Try again.");
            }
            await Task.Run(() =>
            {
                string tmp = v.Dir + ".tmp";
                if (Directory.Exists(tmp)) Directory.Delete(tmp, true);
                Directory.CreateDirectory(tmp);
                using (var zip = ZipFile.OpenRead(dl))
                {
                    foreach (var name in v.Files)
                    {
                        var entry = zip.Entries.FirstOrDefault(e => string.Equals(e.Name, name, StringComparison.OrdinalIgnoreCase))
                                    ?? throw new InvalidDataException($"{name} is missing from the engine package.");
                        entry.ExtractToFile(Path.Combine(tmp, name), true);
                    }
                }
                ExtractRuntime(tmp);
                File.WriteAllText(Path.Combine(tmp, ".ok"), v.Sha256);
                if (Directory.Exists(v.Dir)) Directory.Delete(v.Dir, true);
                Directory.Move(tmp, v.Dir);
            }, ct).ConfigureAwait(false);
            try { File.Delete(dl); } catch { }
            // Older engine versions are no longer needed.
            foreach (var dir in Directory.GetDirectories(Paths.EngineDir, v.Id + "-*"))
                if (!string.Equals(dir, v.Dir, StringComparison.OrdinalIgnoreCase))
                    try { Directory.Delete(dir, true); } catch { }
            Log.Info($"Engine {v.Id} {WhisperVersion} installed");
        }

        /// <summary>
        /// Copies the Microsoft C++ runtime DLLs bundled in Caption.exe next to the engine, so it works on PCs
        /// without the Visual C++ redistributable.
        /// </summary>
        private static void ExtractRuntime(string dir)
        {
            var asm = Assembly.GetExecutingAssembly();
            foreach (var res in asm.GetManifestResourceNames().Where(n => n.StartsWith("native/", StringComparison.Ordinal)))
            {
                string file = Path.Combine(dir, res.Substring("native/".Length));
                using var s = asm.GetManifestResourceStream(res);
                if (s == null) continue;
                using var f = File.Create(file);
                s.CopyTo(f);
            }
        }

        public static long InstalledBytes()
        {
            try
            {
                return Directory.Exists(Paths.EngineDir)
                    ? new DirectoryInfo(Paths.EngineDir).EnumerateFiles("*", SearchOption.AllDirectories).Sum(f => f.Length)
                    : 0;
            }
            catch
            {
                return 0;
            }
        }

        public static void Remove(EngineVariant v)
        {
            try { if (Directory.Exists(v.Dir)) Directory.Delete(v.Dir, true); } catch (Exception ex) { Log.Error("Removing engine", ex); }
        }
    }

    public sealed class GpuInfo
    {
        public string Name = "";
        public long VramBytes;
        public bool HasNvidia;

        private static GpuInfo? _cached;

        public string Summary => HasNvidia
            ? (VramBytes > 0 ? $"{Name} · {VramBytes / (1L << 30)} GB" : Name)
            : string.IsNullOrEmpty(Name) ? "No NVIDIA GPU found" : $"{Name} (no NVIDIA GPU, using the CPU)";

        /// <summary>Reads the display adapters from the registry (no WMI, no admin).</summary>
        public static GpuInfo Detect()
        {
            if (_cached != null) return _cached;
            var best = new GpuInfo();
            try
            {
                using var cls = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}");
                if (cls != null)
                {
                    foreach (var sub in cls.GetSubKeyNames().Where(n => n.Length == 4 && n.All(char.IsDigit)))
                    {
                        try
                        {
                            using var k = cls.OpenSubKey(sub);
                            if (k == null) continue;
                            string desc = k.GetValue("DriverDesc") as string ?? "";
                            string match = (k.GetValue("MatchingDeviceId") as string ?? "").ToLowerInvariant();
                            string provider = k.GetValue("ProviderName") as string ?? "";
                            long vram = 0;
                            object? q = k.GetValue("HardwareInformation.qwMemorySize");
                            if (q is long l) vram = l;
                            else if (q is byte[] qb && qb.Length >= 8) vram = BitConverter.ToInt64(qb, 0);
                            if (vram == 0)
                            {
                                object? m = k.GetValue("HardwareInformation.MemorySize");
                                if (m is int i) vram = (uint)i;
                                else if (m is byte[] mb && mb.Length >= 4) vram = BitConverter.ToUInt32(mb, 0);
                            }
                            bool nvidia = match.Contains("ven_10de") || provider.Contains("NVIDIA", StringComparison.OrdinalIgnoreCase);
                            if (string.IsNullOrEmpty(desc)) continue;
                            bool better = (nvidia && !best.HasNvidia) || (nvidia == best.HasNvidia && vram > best.VramBytes) || best.Name.Length == 0;
                            if (better) best = new GpuInfo { Name = desc, VramBytes = vram, HasNvidia = nvidia };
                        }
                        catch
                        {
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Log.WarnOnce("GPU detection", ex);
            }
            // The CUDA engine also needs the NVIDIA driver's CUDA library.
            if (best.HasNvidia && !File.Exists(Path.Combine(Environment.SystemDirectory, "nvcuda.dll")))
                best.HasNvidia = false;
            _cached = best;
            return best;
        }
    }
}
