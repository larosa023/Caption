using System;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;

namespace Caption.Whisper
{
    /// <summary>
    /// P/Invoke for whisper.cpp v1.8.2 (the pinned engine, see EngineManager). The large parameter structs are
    /// passed as opaque byte blocks: Caption gets the library's own defaults (whisper_*_default_params_by_ref),
    /// copies them, and changes only the fields below at offsets taken from that version's whisper.h
    /// (x64 layout, checked with offsetof).
    /// </summary>
    internal static unsafe class WhisperNative
    {
        public const string Lib = "whisper";

        // ----- whisper_full_params (304 bytes) -----
        public const int FullParamsSize = 304;
        public const int OffNThreads = 4;
        public const int OffNMaxTextCtx = 8;
        public const int OffTranslate = 20;
        public const int OffNoContext = 21;
        public const int OffNoTimestamps = 22;
        public const int OffSingleSegment = 23;
        public const int OffPrintSpecial = 24;
        public const int OffPrintProgress = 25;
        public const int OffPrintRealtime = 26;
        public const int OffPrintTimestamps = 27;
        public const int OffTokenTimestamps = 28;
        public const int OffInitialPrompt = 72;
        public const int OffLanguage = 104;
        public const int OffDetectLanguage = 112;
        public const int OffSuppressBlank = 113;
        public const int OffSuppressNst = 114;
        public const int OffTemperature = 116;
        public const int OffTemperatureInc = 128;
        public const int OffNoSpeechThold = 140;
        public const int OffBeamSize = 148;
        public const int OffVad = 268;

        // ----- whisper_context_params (48 bytes) -----
        public const int ContextParamsSize = 48;
        public const int OffUseGpu = 0;
        public const int OffFlashAttn = 1;
        public const int OffGpuDevice = 4;

        [StructLayout(LayoutKind.Sequential, Size = FullParamsSize)]
        public struct FullParams { public fixed byte Data[FullParamsSize]; }

        [StructLayout(LayoutKind.Sequential, Size = ContextParamsSize)]
        public struct ContextParams { public fixed byte Data[ContextParamsSize]; }

        [StructLayout(LayoutKind.Sequential)]
        public struct VadContextParams
        {
            public int NThreads;
            public byte UseGpu;
            public int GpuDevice;
        }

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        public delegate void LogCallback(int level, IntPtr text, IntPtr userData);

        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
        public static extern IntPtr whisper_context_default_params_by_ref();

        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
        public static extern void whisper_free_context_params(IntPtr p);

        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
        public static extern IntPtr whisper_full_default_params_by_ref(int strategy);

        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
        public static extern void whisper_free_params(IntPtr p);

        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
        public static extern IntPtr whisper_init_from_file_with_params(byte* path, ContextParams p);

        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
        public static extern void whisper_free(IntPtr ctx);

        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
        public static extern int whisper_full(IntPtr ctx, FullParams p, float* samples, int n);

        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
        public static extern int whisper_full_n_segments(IntPtr ctx);

        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
        public static extern long whisper_full_get_segment_t0(IntPtr ctx, int i);

        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
        public static extern long whisper_full_get_segment_t1(IntPtr ctx, int i);

        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
        public static extern IntPtr whisper_full_get_segment_text(IntPtr ctx, int i);

        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
        public static extern float whisper_full_get_segment_no_speech_prob(IntPtr ctx, int i);

        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
        public static extern int whisper_full_lang_id(IntPtr ctx);

        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
        public static extern IntPtr whisper_lang_str(int id);

        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
        public static extern int whisper_is_multilingual(IntPtr ctx);

        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
        public static extern IntPtr whisper_print_system_info();

        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
        public static extern IntPtr whisper_version();

        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
        public static extern void whisper_log_set(IntPtr callback, IntPtr userData);

        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
        public static extern IntPtr whisper_vad_init_from_file_with_params(byte* path, VadContextParams p);

        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
        [return: MarshalAs(UnmanagedType.U1)]
        public static extern bool whisper_vad_detect_speech(IntPtr vctx, float* samples, int n);

        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
        public static extern int whisper_vad_n_probs(IntPtr vctx);

        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
        public static extern float* whisper_vad_probs(IntPtr vctx);

        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
        public static extern void whisper_vad_free(IntPtr vctx);

        // ---------------------------------------------------------------- loading

        private static IntPtr _handle;
        private static bool _resolverSet;

        /// <summary>
        /// Loads whisper.dll (and with it ggml*.dll, CUDA DLLs) from an engine folder. Once per process:
        /// the worker process is restarted to switch engines.
        /// </summary>
        public static void Load(string engineDir)
        {
            if (_handle != IntPtr.Zero) return;
            string dll = Path.Combine(engineDir, "whisper.dll");
            if (!File.Exists(dll)) throw new FileNotFoundException("The speech engine isn't installed.", dll);
            // Let the loader find ggml*.dll, the CUDA libraries and the C++ runtime next to whisper.dll.
            Caption.Services.Native.SetDllDirectory(engineDir);
            _handle = NativeLibrary.Load(dll);
            if (!_resolverSet)
            {
                _resolverSet = true;
                NativeLibrary.SetDllImportResolver(typeof(WhisperNative).Assembly, Resolve);
            }
        }

        private static IntPtr Resolve(string name, Assembly assembly, DllImportSearchPath? path) =>
            name == Lib ? _handle : IntPtr.Zero;

        public static string? Utf8(IntPtr p) => p == IntPtr.Zero ? null : Marshal.PtrToStringUTF8(p);
    }
}
