using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Caption.Services;

namespace Caption.Whisper
{
    /// <summary>
    /// Speech recognition runs in a child process ("Caption.exe --worker"). That keeps the GPU's memory
    /// completely free whenever captions are off (the process simply exits), and a crash in native code
    /// can't take the app down. The two halves talk over the child's stdin/stdout.
    /// </summary>
    internal static class WorkerProtocol
    {
        public const byte Load = 1, Transcribe = 2, Vad = 3, Quit = 9;
        public const byte Ok = 0x80, Error = 0x81;

        public static void WriteFloats(BinaryWriter w, float[] data, int count)
        {
            w.Write(count);
            w.Write(MemoryMarshal.AsBytes(data.AsSpan(0, count)));
        }

        public static float[] ReadFloats(BinaryReader r)
        {
            int n = r.ReadInt32();
            if (n < 0 || n > 16000 * 120) throw new InvalidDataException("bad sample count");
            var data = new float[n];
            var bytes = MemoryMarshal.AsBytes(data.AsSpan());
            int read = 0;
            while (read < bytes.Length)
            {
                int got = r.BaseStream.Read(bytes.Slice(read));
                if (got <= 0) throw new EndOfStreamException();
                read += got;
            }
            return data;
        }
    }

    /// <summary>The child side: loads the engine and answers requests until told to quit or the parent goes away.</summary>
    internal static class WorkerHost
    {
        public static int Run()
        {
            Log.Prefix = "[worker] ";
            var input = new BinaryReader(Console.OpenStandardInput());
            var output = new BinaryWriter(Console.OpenStandardOutput());
            WhisperEngine? engine = null;
            try
            {
                while (true)
                {
                    byte type;
                    try { type = input.ReadByte(); }
                    catch (EndOfStreamException) { break; }

                    try
                    {
                        switch (type)
                        {
                            case WorkerProtocol.Load:
                            {
                                string engineDir = input.ReadString();
                                string model = input.ReadString();
                                bool gpu = input.ReadBoolean();
                                string vad = input.ReadString();
                                int threads = input.ReadInt32();
                                engine?.Dispose();
                                engine = new WhisperEngine(engineDir, model, gpu, vad, threads);
                                Log.Info($"Loaded {Path.GetFileName(model)} (gpu: {engine.GpuActive}, vad: {engine.HasVad})");
                                output.Write(WorkerProtocol.Ok);
                                output.Write(engine.GpuActive);
                                output.Write(engine.HasVad);
                                output.Write(engine.Multilingual);
                                output.Write(engine.SystemInfo);
                                break;
                            }
                            case WorkerProtocol.Transcribe:
                            {
                                var req = new TranscribeRequest
                                {
                                    Language = input.ReadString(),
                                    Translate = input.ReadBoolean(),
                                    Final = input.ReadBoolean(),
                                    SuppressNonSpeech = input.ReadBoolean(),
                                    Prompt = input.ReadString(),
                                    BeamSize = input.ReadInt32(),
                                };
                                req.Samples = WorkerProtocol.ReadFloats(input);
                                req.Count = req.Samples.Length;
                                if (engine == null) throw new InvalidOperationException("No model loaded.");
                                var res = engine.Transcribe(req);
                                output.Write(WorkerProtocol.Ok);
                                output.Write(res.Language);
                                output.Write(res.Milliseconds);
                                output.Write(res.Segments.Count);
                                foreach (var s in res.Segments)
                                {
                                    output.Write(s.T0);
                                    output.Write(s.T1);
                                    output.Write(s.NoSpeech);
                                    output.Write(s.Text);
                                }
                                break;
                            }
                            case WorkerProtocol.Vad:
                            {
                                var samples = WorkerProtocol.ReadFloats(input);
                                var probs = engine?.DetectSpeech(samples, samples.Length);
                                output.Write(WorkerProtocol.Ok);
                                if (probs == null) output.Write(-1);
                                else WorkerProtocol.WriteFloats(output, probs, probs.Length);
                                break;
                            }
                            case WorkerProtocol.Quit:
                                output.Write(WorkerProtocol.Ok);
                                output.Flush();
                                return 0;
                            default:
                                throw new InvalidDataException("Unknown request " + type);
                        }
                    }
                    catch (Exception ex) when (ex is not EndOfStreamException and not IOException)
                    {
                        Log.Error("Worker request", ex);
                        output.Write(WorkerProtocol.Error);
                        output.Write(ex.Message);
                    }
                    output.Flush();
                }
            }
            catch (IOException)
            {
                // Parent went away.
            }
            finally
            {
                try { engine?.Dispose(); } catch { }
            }
            return 0;
        }
    }

    public sealed class WorkerLoadInfo
    {
        public bool GpuActive;
        public bool HasVad;
        public bool Multilingual;
        public string SystemInfo = "";
    }

    /// <summary>The parent side. Calls are synchronous and serialized (the caption pipeline is one thread).</summary>
    public sealed class WorkerClient : IDisposable
    {
        private readonly Process _process;
        private readonly BinaryWriter _out;
        private readonly BinaryReader _in;
        private readonly object _gate = new();
        private bool _disposed;

        public bool IsAlive => !_disposed && !_process.HasExited;

        /// <summary>Starts a worker. <paramref name="exe"/> defaults to this exe.</summary>
        public WorkerClient(string? exe = null)
        {
            var psi = new ProcessStartInfo(exe ?? Paths.ExePath)
            {
                UseShellExecute = false,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                WorkingDirectory = Paths.ExeDir,
            };
            psi.ArgumentList.Add("--worker");
            _process = Process.Start(psi) ?? throw new InvalidOperationException("Couldn't start the speech worker.");
            try { _process.PriorityClass = ProcessPriorityClass.AboveNormal; } catch { }
            _out = new BinaryWriter(_process.StandardInput.BaseStream);
            _in = new BinaryReader(_process.StandardOutput.BaseStream);
            // Native libraries sometimes print to stderr; keep the last bits for diagnostics.
            _process.ErrorDataReceived += (_, e) =>
            {
                if (!string.IsNullOrWhiteSpace(e.Data) && e.Data.Length < 400 &&
                    (e.Data.Contains("error", StringComparison.OrdinalIgnoreCase) || e.Data.Contains("CUDA")))
                    Log.Info("[worker stderr] " + e.Data);
            };
            _process.BeginErrorReadLine();
        }

        private void ReadStatus()
        {
            byte status = _in.ReadByte();
            if (status == WorkerProtocol.Error) throw new WorkerException(_in.ReadString());
            if (status != WorkerProtocol.Ok) throw new InvalidDataException("Unexpected worker reply.");
        }

        public WorkerLoadInfo Load(string engineDir, string modelPath, bool gpu, string vadModel, int threads)
        {
            lock (_gate)
            {
                _out.Write(WorkerProtocol.Load);
                _out.Write(engineDir);
                _out.Write(modelPath);
                _out.Write(gpu);
                _out.Write(vadModel ?? "");
                _out.Write(threads);
                _out.Flush();
                ReadStatus();
                return new WorkerLoadInfo
                {
                    GpuActive = _in.ReadBoolean(),
                    HasVad = _in.ReadBoolean(),
                    Multilingual = _in.ReadBoolean(),
                    SystemInfo = _in.ReadString(),
                };
            }
        }

        public TranscribeResult Transcribe(TranscribeRequest req)
        {
            lock (_gate)
            {
                _out.Write(WorkerProtocol.Transcribe);
                _out.Write(req.Language ?? "auto");
                _out.Write(req.Translate);
                _out.Write(req.Final);
                _out.Write(req.SuppressNonSpeech);
                _out.Write(req.Prompt ?? "");
                _out.Write(req.BeamSize);
                WorkerProtocol.WriteFloats(_out, req.Samples, req.Count);
                _out.Flush();
                ReadStatus();
                var res = new TranscribeResult
                {
                    Language = _in.ReadString(),
                    Milliseconds = _in.ReadInt32(),
                };
                int n = _in.ReadInt32();
                for (int i = 0; i < n; i++)
                {
                    res.Segments.Add(new Segment
                    {
                        T0 = _in.ReadInt64(),
                        T1 = _in.ReadInt64(),
                        NoSpeech = _in.ReadSingle(),
                        Text = _in.ReadString(),
                    });
                }
                return res;
            }
        }

        /// <summary>Per-32 ms-frame speech probabilities, or null when the worker has no VAD model.</summary>
        public float[]? DetectSpeech(float[] samples, int count)
        {
            lock (_gate)
            {
                _out.Write(WorkerProtocol.Vad);
                WorkerProtocol.WriteFloats(_out, samples, count);
                _out.Flush();
                ReadStatus();
                int n = _in.ReadInt32();
                if (n < 0) return null;
                var probs = new float[n];
                for (int i = 0; i < n; i++) probs[i] = _in.ReadSingle();
                return probs;
            }
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            try
            {
                if (!_process.HasExited)
                {
                    lock (_gate)
                    {
                        _out.Write(WorkerProtocol.Quit);
                        _out.Flush();
                    }
                    if (!_process.WaitForExit(3000)) _process.Kill(true);
                }
            }
            catch
            {
                try { if (!_process.HasExited) _process.Kill(true); } catch { }
            }
            _process.Dispose();
        }
    }

    public sealed class WorkerException : Exception
    {
        public WorkerException(string message) : base(message) { }
    }
}
