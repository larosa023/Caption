using System;
using System.Runtime.InteropServices;
using System.Threading;
using Caption.Services;
using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;
using NAudio.Wave;

namespace Caption.Audio
{
    /// <summary>Something Caption listens to. Raises 16 kHz mono float blocks.</summary>
    public abstract class AudioSource : IDisposable
    {
        public string Name { get; protected set; } = "";

        /// <summary>16 kHz mono samples. Raised on a capture thread.</summary>
        public event Action<AudioSource, float[]>? Samples;

        /// <summary>The source stopped on its own (device unplugged, app closed...).</summary>
        public event Action<AudioSource, Exception?>? Stopped;

        public abstract void Start();
        public abstract void Dispose();

        protected void RaiseSamples(float[] s) { if (s.Length > 0) Samples?.Invoke(this, s); }
        protected void RaiseStopped(Exception? ex) => Stopped?.Invoke(this, ex);

        // ---------------------------------------------------------------- format conversion

        private static readonly Guid IeeeFloat = new("00000003-0000-0010-8000-00aa00389b71");

        /// <summary>Interleaved device samples (float/16/24/32-bit) → one float array per channel.</summary>
        public static float[][] Deinterleave(byte[] buffer, int bytes, WaveFormat format)
        {
            int channels = Math.Max(1, format.Channels);
            int bits = format.BitsPerSample;
            bool isFloat = format.Encoding == WaveFormatEncoding.IeeeFloat ||
                           (format is WaveFormatExtensible ext && ext.SubFormat == IeeeFloat);
            int bytesPerSample = bits / 8;
            if (bytesPerSample <= 0) return Array.Empty<float[]>();
            int frames = bytes / (bytesPerSample * channels);
            var result = new float[channels][];
            for (int c = 0; c < channels; c++) result[c] = new float[frames];
            int idx = 0;
            for (int f = 0; f < frames; f++)
            {
                for (int c = 0; c < channels; c++)
                {
                    float v;
                    if (isFloat && bits == 32) v = BitConverter.ToSingle(buffer, idx);
                    else if (bits == 16) v = BitConverter.ToInt16(buffer, idx) / 32768f;
                    else if (bits == 24) v = ((buffer[idx] << 8 | buffer[idx + 1] << 16 | buffer[idx + 2] << 24) >> 8) / 8388608f;
                    else if (bits == 32) v = BitConverter.ToInt32(buffer, idx) / 2147483648f;
                    else v = 0;
                    result[c][f] = v;
                    idx += bytesPerSample;
                }
            }
            return result;
        }

        /// <summary>Interleaved device samples (float/16/24/32-bit) → mono float.</summary>
        public static float[] ToMono(byte[] buffer, int bytes, WaveFormat format)
        {
            int channels = Math.Max(1, format.Channels);
            int bits = format.BitsPerSample;
            bool isFloat = format.Encoding == WaveFormatEncoding.IeeeFloat ||
                           (format is WaveFormatExtensible ext && ext.SubFormat == IeeeFloat);
            int bytesPerSample = bits / 8;
            if (bytesPerSample <= 0) return Array.Empty<float>();
            int frames = bytes / (bytesPerSample * channels);
            var mono = new float[frames];
            float scale = 1f / channels;
            int idx = 0;
            for (int f = 0; f < frames; f++)
            {
                float sum = 0;
                for (int c = 0; c < channels; c++)
                {
                    float v;
                    if (isFloat && bits == 32) v = BitConverter.ToSingle(buffer, idx);
                    else if (bits == 16) v = BitConverter.ToInt16(buffer, idx) / 32768f;
                    else if (bits == 24) v = ((buffer[idx] << 8 | buffer[idx + 1] << 16 | buffer[idx + 2] << 24) >> 8) / 8388608f;
                    else if (bits == 32) v = BitConverter.ToInt32(buffer, idx) / 2147483648f;
                    else v = 0;
                    sum += v;
                    idx += bytesPerSample;
                }
                mono[f] = sum * scale;
            }
            return mono;
        }
    }

    /// <summary>WASAPI loopback (an output device: everything it plays) or a microphone.</summary>
    public sealed class DeviceSource : AudioSource
    {
        private readonly MMDevice _device;
        private readonly WasapiCapture _capture;
        private readonly bool _focus;
        private SourceConverter? _converter;
        private bool _disposed;

        public string DeviceId { get; }
        public bool IsLoopback { get; }

        public DeviceSource(MMDevice device, bool loopback, bool dialogueFocus = false)
        {
            _focus = dialogueFocus && loopback;
            _device = device;
            DeviceId = device.ID;
            IsLoopback = loopback;
            Name = device.FriendlyName;
            _capture = loopback ? new WasapiLoopbackCapture(device) : new WasapiCapture(device, true, 50);
            _capture.DataAvailable += OnData;
            _capture.RecordingStopped += (_, e) => { if (!_disposed) RaiseStopped(e.Exception); };
        }

        public override void Start()
        {
            _converter = new SourceConverter(_capture.WaveFormat, _focus);
            _capture.StartRecording();
        }

        private void OnData(object? sender, WaveInEventArgs e)
        {
            if (_disposed || e.BytesRecorded <= 0 || _converter == null) return;
            try
            {
                RaiseSamples(_converter.Process(e.Buffer, e.BytesRecorded));
            }
            catch (Exception ex)
            {
                Log.WarnOnce("Audio conversion", ex);
            }
        }

        public override void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            try { _capture.StopRecording(); } catch { }
            try { _capture.Dispose(); } catch { }
            try { _device.Dispose(); } catch { }
        }
    }

    /// <summary>
    /// One app's audio (and its child processes) via process loopback, Windows 10 2004+ / Windows 11.
    /// Works even when the app plays to a different device than the default.
    /// </summary>
    public sealed class ProcessSource : AudioSource
    {
        private readonly uint _pid;
        private Thread? _thread;
        private volatile bool _stop;
        private AudioClient? _client;

        public uint ProcessId => _pid;

        private readonly bool _focus;

        public ProcessSource(uint pid, string name, bool dialogueFocus = false)
        {
            _pid = pid;
            Name = name;
            _focus = dialogueFocus;
        }

        public override void Start()
        {
            // Activation must not happen on the UI (STA) thread; do everything on our capture thread.
            var ready = new ManualResetEventSlim();
            Exception? startError = null;
            _thread = new Thread(() => Run(ready, e => startError = e)) { IsBackground = true, Name = "Process loopback " + Name, Priority = ThreadPriority.AboveNormal };
            _thread.SetApartmentState(ApartmentState.MTA);
            _thread.Start();
            ready.Wait(5000);
            if (startError != null) throw startError;
        }

        private void Run(ManualResetEventSlim ready, Action<Exception> fail)
        {
            var format = new WaveFormat(48000, 16, 2);
            EventWaitHandle? evt = null;
            try
            {
                _client = ProcessLoopback.Activate(_pid);
                // AUTOCONVERTPCM lets Windows hand us the app's audio in our format whatever it plays at.
                var flags = AudioClientStreamFlags.Loopback | AudioClientStreamFlags.EventCallback |
                            AudioClientStreamFlags.AutoConvertPcm | AudioClientStreamFlags.SrcDefaultQuality;
                _client.Initialize(AudioClientShareMode.Shared, flags, 200_0000 /* 200 ms */, 0, format, Guid.Empty);
                evt = new EventWaitHandle(false, EventResetMode.AutoReset);
                _client.SetEventHandle(evt.SafeWaitHandle.DangerousGetHandle());
                var capture = _client.AudioCaptureClient;
                _client.Start();
                ready.Set();

                var converter = new SourceConverter(format, _focus);
                byte[] buffer = new byte[format.AverageBytesPerSecond];
                int frameBytes = format.BlockAlign;
                while (!_stop)
                {
                    evt.WaitOne(100);
                    int packet = capture.GetNextPacketSize();
                    while (packet > 0 && !_stop)
                    {
                        IntPtr data = capture.GetBuffer(out int frames, out AudioClientBufferFlags bflags);
                        int bytes = frames * frameBytes;
                        if (bytes > buffer.Length) buffer = new byte[bytes];
                        if ((bflags & AudioClientBufferFlags.Silent) != 0) Array.Clear(buffer, 0, bytes);
                        else Marshal.Copy(data, buffer, 0, bytes);
                        capture.ReleaseBuffer(frames);
                        RaiseSamples(converter.Process(buffer, bytes));
                        packet = capture.GetNextPacketSize();
                    }
                }
            }
            catch (Exception ex)
            {
                if (!ready.IsSet) { fail(ex); ready.Set(); }
                else if (!_stop) RaiseStopped(ex);
            }
            finally
            {
                try { _client?.Stop(); } catch { }
                try { _client?.Dispose(); } catch { }
                evt?.Dispose();
            }
        }

        public override void Dispose()
        {
            _stop = true;
            try { _thread?.Join(1000); } catch { }
        }
    }

    /// <summary>ActivateAudioInterfaceAsync with AUDIOCLIENT_ACTIVATION_TYPE_PROCESS_LOOPBACK.</summary>
    internal static class ProcessLoopback
    {
        private const string VirtualDevice = "VAD\\Process_Loopback";
        private static readonly Guid IID_IAudioClient = new("1CB9AD4C-DBFA-4c32-B178-C2F568A703B2");

        [StructLayout(LayoutKind.Sequential)]
        private struct AudioClientActivationParams
        {
            public int ActivationType;     // 1 = PROCESS_LOOPBACK
            public uint TargetProcessId;
            public int ProcessLoopbackMode; // 0 = include target process tree
        }

        [StructLayout(LayoutKind.Explicit)]
        private struct PropVariantBlob
        {
            [FieldOffset(0)] public ushort vt;      // VT_BLOB = 0x41
            [FieldOffset(8)] public uint cbSize;
            [FieldOffset(16)] public IntPtr pBlobData;
        }

        [ComImport, Guid("72A22D78-CDE4-431D-B8CC-843A71199B6D"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        internal interface IActivateAudioInterfaceAsyncOperation
        {
            void GetActivateResult([MarshalAs(UnmanagedType.Error)] out int activateResult,
                [MarshalAs(UnmanagedType.IUnknown)] out object activatedInterface);
        }

        [ComImport, Guid("41D949AB-9862-444A-80F6-C261334DA5EB"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        internal interface IActivateAudioInterfaceCompletionHandler
        {
            void ActivateCompleted(IActivateAudioInterfaceAsyncOperation operation);
        }

        [ComImport, Guid("94ea2b94-e9cc-49e0-c0ff-ee64ca8f5b90"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        internal interface IAgileObject
        {
        }

        [DllImport("Mmdevapi.dll", ExactSpelling = true, PreserveSig = false)]
        private static extern void ActivateAudioInterfaceAsync(
            [MarshalAs(UnmanagedType.LPWStr)] string deviceInterfacePath,
            [MarshalAs(UnmanagedType.LPStruct)] Guid riid,
            IntPtr activationParams,
            IActivateAudioInterfaceCompletionHandler completionHandler,
            out IActivateAudioInterfaceAsyncOperation activationOperation);

        [ComVisible(true)]
        internal sealed class Handler : IActivateAudioInterfaceCompletionHandler, IAgileObject
        {
            public readonly ManualResetEventSlim Done = new();
            public int Result;
            public object? Interface;

            public void ActivateCompleted(IActivateAudioInterfaceAsyncOperation operation)
            {
                try
                {
                    operation.GetActivateResult(out Result, out object obj);
                    Interface = obj;
                }
                catch (Exception ex)
                {
                    Result = ex.HResult;
                }
                finally
                {
                    Done.Set();
                }
            }
        }

        public static AudioClient Activate(uint pid)
        {
            var p = new AudioClientActivationParams { ActivationType = 1, TargetProcessId = pid, ProcessLoopbackMode = 0 };
            IntPtr pParams = Marshal.AllocHGlobal(Marshal.SizeOf<AudioClientActivationParams>());
            IntPtr pVariant = Marshal.AllocHGlobal(Marshal.SizeOf<PropVariantBlob>());
            try
            {
                Marshal.StructureToPtr(p, pParams, false);
                var v = new PropVariantBlob { vt = 0x41, cbSize = (uint)Marshal.SizeOf<AudioClientActivationParams>(), pBlobData = pParams };
                Marshal.StructureToPtr(v, pVariant, false);
                var handler = new Handler();
                ActivateAudioInterfaceAsync(VirtualDevice, IID_IAudioClient, pVariant, handler, out var op);
                if (!handler.Done.Wait(5000)) throw new TimeoutException("Windows didn't start app audio capture.");
                GC.KeepAlive(op);
                if (handler.Result != 0 || handler.Interface == null)
                    throw new COMException("App audio capture isn't available (needs Windows 10 2004 or later).", handler.Result);
                return new AudioClient((IAudioClient)handler.Interface);
            }
            finally
            {
                Marshal.FreeHGlobal(pVariant);
                Marshal.FreeHGlobal(pParams);
            }
        }
    }
}
