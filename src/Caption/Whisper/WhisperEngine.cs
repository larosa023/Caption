using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Caption.Services;

namespace Caption.Whisper
{
    public sealed class Segment
    {
        public long T0;          // in 10 ms units, relative to the audio passed in
        public long T1;
        public float NoSpeech;
        public string Text = "";
    }

    public sealed class TranscribeResult
    {
        public string Language = "";
        public int Milliseconds;
        public List<Segment> Segments = new();

        public string Text
        {
            get
            {
                var sb = new StringBuilder();
                foreach (var s in Segments) sb.Append(s.Text);
                return sb.ToString().Trim();
            }
        }
    }

    public sealed class TranscribeRequest
    {
        public string Language = "auto";
        public bool Translate;
        /// <summary>Allow temperature fallback (slower, steadier); used for finished lines.</summary>
        public bool Final;
        public bool SuppressNonSpeech = true;
        /// <summary>1 = greedy; 2+ = beam search with that many beams (steadier, more accurate).</summary>
        public int BeamSize = 1;
        public string Prompt = "";
        public float[] Samples = Array.Empty<float>();
        public int Count;
    }

    /// <summary>One loaded whisper.cpp model (and optional Silero VAD). Not thread-safe: one caller at a time.</summary>
    public sealed unsafe class WhisperEngine : IDisposable
    {
        private IntPtr _ctx;
        private IntPtr _vad;
        private readonly int _threads;
        private static WhisperNative.LogCallback? _logCallback; // kept alive for the native side
        private static bool _sawCuda;
        private static readonly StringBuilder RecentLog = new();

        public bool GpuActive { get; }
        public bool HasVad => _vad != IntPtr.Zero;
        public bool Multilingual { get; }
        public string SystemInfo { get; }

        public WhisperEngine(string engineDir, string modelPath, bool useGpu, string? vadModelPath, int threads)
        {
            WhisperNative.Load(engineDir);
            HookLog();
            _threads = Math.Max(1, threads);

            IntPtr defaults = WhisperNative.whisper_context_default_params_by_ref();
            var cp = new WhisperNative.ContextParams();
            Buffer.MemoryCopy((void*)defaults, cp.Data, WhisperNative.ContextParamsSize, WhisperNative.ContextParamsSize);
            WhisperNative.whisper_free_context_params(defaults);
            cp.Data[WhisperNative.OffUseGpu] = (byte)(useGpu ? 1 : 0);
            cp.Data[WhisperNative.OffFlashAttn] = (byte)(useGpu ? 1 : 0);
            *(int*)(cp.Data + WhisperNative.OffGpuDevice) = 0;

            _sawCuda = false;
            byte[] path = Utf8Z(modelPath);
            fixed (byte* p = path)
                _ctx = WhisperNative.whisper_init_from_file_with_params(p, cp);
            if (_ctx == IntPtr.Zero)
                throw new InvalidOperationException("The model couldn't be loaded. " + LastLogLines());

            GpuActive = useGpu && _sawCuda;
            Multilingual = WhisperNative.whisper_is_multilingual(_ctx) != 0;
            SystemInfo = WhisperNative.Utf8(WhisperNative.whisper_print_system_info()) ?? "";

            if (!string.IsNullOrEmpty(vadModelPath) && System.IO.File.Exists(vadModelPath))
            {
                try
                {
                    var vp = new WhisperNative.VadContextParams { NThreads = 2, UseGpu = 0, GpuDevice = 0 };
                    byte[] vpath = Utf8Z(vadModelPath);
                    fixed (byte* p = vpath)
                        _vad = WhisperNative.whisper_vad_init_from_file_with_params(p, vp);
                }
                catch (Exception ex)
                {
                    Log.WarnOnce("VAD model", ex);
                    _vad = IntPtr.Zero;
                }
            }
        }

        private static void HookLog()
        {
            if (_logCallback != null) return;
            _logCallback = (level, text, _) =>
            {
                string s = WhisperNative.Utf8(text) ?? "";
                if (s.Contains("CUDA", StringComparison.Ordinal) && (s.Contains("using CUDA") || s.Contains("CUDA0")))
                    _sawCuda = true;
                lock (RecentLog)
                {
                    RecentLog.Append(s);
                    if (RecentLog.Length > 4000) RecentLog.Remove(0, RecentLog.Length - 4000);
                }
                // 3 = warn, 4 = error
                if (level >= 3 && s.Trim().Length > 0) Log.Info("whisper: " + s.Trim());
            };
            WhisperNative.whisper_log_set(Marshal.GetFunctionPointerForDelegate(_logCallback), IntPtr.Zero);
        }

        public static string LastLogLines()
        {
            lock (RecentLog)
            {
                var lines = RecentLog.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries);
                int from = Math.Max(0, lines.Length - 4);
                return string.Join(" ", lines, from, lines.Length - from).Trim();
            }
        }

        public TranscribeResult Transcribe(TranscribeRequest req)
        {
            if (_ctx == IntPtr.Zero) throw new ObjectDisposedException(nameof(WhisperEngine));
            var sw = Stopwatch.StartNew();
            IntPtr defaults = WhisperNative.whisper_full_default_params_by_ref(req.BeamSize > 1 ? 1 /* beam search */ : 0 /* greedy */);
            var fp = new WhisperNative.FullParams();
            Buffer.MemoryCopy((void*)defaults, fp.Data, WhisperNative.FullParamsSize, WhisperNative.FullParamsSize);
            WhisperNative.whisper_free_params(defaults);

            byte* d = fp.Data;
            *(int*)(d + WhisperNative.OffNThreads) = _threads;
            d[WhisperNative.OffTranslate] = (byte)(req.Translate ? 1 : 0);
            d[WhisperNative.OffNoContext] = 1;
            d[WhisperNative.OffNoTimestamps] = 0;
            d[WhisperNative.OffSingleSegment] = 0;
            d[WhisperNative.OffPrintSpecial] = 0;
            d[WhisperNative.OffPrintProgress] = 0;
            d[WhisperNative.OffPrintRealtime] = 0;
            d[WhisperNative.OffPrintTimestamps] = 0;
            d[WhisperNative.OffTokenTimestamps] = 0;
            d[WhisperNative.OffDetectLanguage] = 0;
            d[WhisperNative.OffSuppressBlank] = 1;
            d[WhisperNative.OffSuppressNst] = (byte)(req.SuppressNonSpeech ? 1 : 0);
            *(float*)(d + WhisperNative.OffTemperature) = 0f;
            *(float*)(d + WhisperNative.OffTemperatureInc) = req.Final ? 0.2f : 0f;
            d[WhisperNative.OffVad] = 0;
            if (req.BeamSize > 1) *(int*)(d + WhisperNative.OffBeamSize) = Math.Min(req.BeamSize, 8);

            byte[] lang = Utf8Z(string.IsNullOrEmpty(req.Language) ? "auto" : req.Language);
            byte[]? prompt = string.IsNullOrWhiteSpace(req.Prompt) ? null : Utf8Z(req.Prompt);
            int rc;
            fixed (byte* pl = lang)
            fixed (byte* pp = prompt)
            fixed (float* ps = req.Samples)
            {
                *(IntPtr*)(d + WhisperNative.OffLanguage) = (IntPtr)pl;
                *(IntPtr*)(d + WhisperNative.OffInitialPrompt) = prompt != null ? (IntPtr)pp : IntPtr.Zero;
                rc = WhisperNative.whisper_full(_ctx, fp, ps, req.Count);
            }
            if (rc != 0) throw new InvalidOperationException($"whisper_full failed ({rc}). " + LastLogLines());

            var result = new TranscribeResult();
            int n = WhisperNative.whisper_full_n_segments(_ctx);
            for (int i = 0; i < n; i++)
            {
                result.Segments.Add(new Segment
                {
                    T0 = WhisperNative.whisper_full_get_segment_t0(_ctx, i),
                    T1 = WhisperNative.whisper_full_get_segment_t1(_ctx, i),
                    NoSpeech = WhisperNative.whisper_full_get_segment_no_speech_prob(_ctx, i),
                    Text = WhisperNative.Utf8(WhisperNative.whisper_full_get_segment_text(_ctx, i)) ?? ""
                });
            }
            int langId = WhisperNative.whisper_full_lang_id(_ctx);
            result.Language = langId >= 0 ? WhisperNative.Utf8(WhisperNative.whisper_lang_str(langId)) ?? "" : "";
            result.Milliseconds = (int)sw.ElapsedMilliseconds;
            return result;
        }

        /// <summary>Speech probability for each 512-sample (32 ms) frame, or null if there's no VAD model.</summary>
        public float[]? DetectSpeech(float[] samples, int count)
        {
            if (_vad == IntPtr.Zero || count < 512) return null;
            fixed (float* p = samples)
            {
                if (!WhisperNative.whisper_vad_detect_speech(_vad, p, count)) return null;
            }
            int n = WhisperNative.whisper_vad_n_probs(_vad);
            float* probs = WhisperNative.whisper_vad_probs(_vad);
            if (n <= 0 || probs == null) return null;
            var result = new float[n];
            for (int i = 0; i < n; i++) result[i] = probs[i];
            return result;
        }

        private static byte[] Utf8Z(string s)
        {
            int len = Encoding.UTF8.GetByteCount(s);
            var b = new byte[len + 1];
            Encoding.UTF8.GetBytes(s, 0, s.Length, b, 0);
            return b;
        }

        public void Dispose()
        {
            if (_vad != IntPtr.Zero) { WhisperNative.whisper_vad_free(_vad); _vad = IntPtr.Zero; }
            if (_ctx != IntPtr.Zero) { WhisperNative.whisper_free(_ctx); _ctx = IntPtr.Zero; }
        }
    }
}
