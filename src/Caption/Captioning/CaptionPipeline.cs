using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using Caption.Services;
using Caption.Whisper;

namespace Caption.Captioning
{
    public sealed class PipelineOptions
    {
        public string Language = "auto";
        /// <summary>Whisper's own translate task (to English).</summary>
        public bool TranslateToEnglish;
        /// <summary>Also produce the source-language text for finished lines (an extra pass).</summary>
        public bool KeepOriginal;
        public int UpdateIntervalMs = 400;
        public int PauseToEndMs = 600;
        public int MaxLineSeconds = 8;
        public float VadThreshold = 0.5f;
        public bool ShowSoundLabels;
        public bool FilterHallucinations = true;
    }

    public sealed class FinishedLine
    {
        public string Text = "";
        public string? Original;
        public string Language = "";
        public TimeSpan Start, End;
    }

    /// <summary>
    /// Turns a 16 kHz audio stream into captions. Voice activity detection finds speech; while someone talks,
    /// the line so far is re-read every UpdateIntervalMs and shown in place (and corrected as more audio arrives);
    /// a pause (or a long line) finishes it with a final, steadier pass.
    /// </summary>
    public sealed class CaptionPipeline : IDisposable
    {
        private const int Rate = 16000;
        private const int Frame = 512;             // 32 ms, Silero's frame
        private const int PrerollFrames = 10;      // 320 ms kept from before speech starts
        private const int VadContext = Rate;       // Silero re-reads the last second each time (its state resets per call)

        private readonly WorkerClient _worker;
        private readonly bool _useSilero;
        private PipelineOptions _o;
        private readonly ConcurrentQueue<(float[] Data, long Ticks)> _queue = new();
        private readonly AutoResetEvent _signal = new(false);
        private readonly Thread _thread;
        private volatile bool _stop;
        private volatile bool _paused;
        private volatile bool _resetRequested;

        // audio bookkeeping
        private float[] _pending = new float[Rate];
        private int _pendingCount;
        private long _consumed;                     // samples consumed by VAD since start
        private long _lastSampleTicks;              // wall time the newest sample arrived
        private readonly float[] _vadHistory = new float[VadContext];
        private int _vadHistoryCount;

        // speech state
        private readonly Queue<float[]> _preroll = new();
        private bool _active;
        private int _speechRun;
        private int _silenceRun;
        private float[] _utt = new float[Rate * 30];
        private int _uttCount;
        private long _uttStart;                     // sample index where the current line's audio starts
        private int _sinceInference;
        private string? _lockedLanguage;
        private string _lastText = "";
        private string _lastPartial = "";

        // energy VAD fallback
        private float _noiseFloor = 0.003f;

        public event Action<string>? Partial;             // "" clears
        public event Action<FinishedLine>? Finished;
        public event Action<Exception>? Failed;

        /// <summary>How long the last read of the line took (GPU/CPU time).</summary>
        public int LastInferenceMs { get; private set; }
        /// <summary>From the newest audio arriving to its text being shown.</summary>
        public int LastDelayMs { get; private set; }
        public string DetectedLanguage { get; private set; } = "";
        public bool Speaking => _active;
        public bool UsesSilero => _useSilero;

        public CaptionPipeline(WorkerClient worker, bool hasVad, PipelineOptions options)
        {
            _worker = worker;
            _useSilero = hasVad;
            _o = options;
            _thread = new Thread(Loop) { IsBackground = true, Name = "Caption pipeline", Priority = ThreadPriority.AboveNormal };
            _thread.Start();
        }

        public void UpdateOptions(PipelineOptions options) => _o = options;

        public bool Paused
        {
            get => _paused;
            set
            {
                _paused = value;
                if (value) _resetRequested = true;
            }
        }

        /// <summary>Audio in (any thread).</summary>
        public void Feed(float[] samples)
        {
            if (_stop) return;
            _queue.Enqueue((samples, Stopwatch.GetTimestamp()));
            _signal.Set();
        }

        /// <summary>For tests: true once all fed audio has been processed.</summary>
        public bool Idle => _queue.IsEmpty && _idle;
        private volatile bool _idle = true;

        private void Loop()
        {
            try
            {
                while (!_stop)
                {
                    _signal.WaitOne(60);
                    if (_stop) break;
                    _idle = false;
                    if (_resetRequested)
                    {
                        _resetRequested = false;
                        ResetLine(clearPartial: true);
                    }
                    bool any = false;
                    while (_queue.TryDequeue(out var item))
                    {
                        any = true;
                        if (_paused) continue;
                        Append(item.Data);
                        _lastSampleTicks = item.Ticks;
                    }
                    if (any && !_paused) Process();
                    _idle = _queue.IsEmpty;
                }
            }
            catch (Exception ex) when (!_stop)
            {
                Log.Error("Caption pipeline", ex);
                Failed?.Invoke(ex);
            }
        }

        private void Append(float[] data)
        {
            if (_pendingCount + data.Length > _pending.Length)
                Array.Resize(ref _pending, Math.Max(_pending.Length * 2, _pendingCount + data.Length));
            Array.Copy(data, 0, _pending, _pendingCount, data.Length);
            _pendingCount += data.Length;
        }

        private void Process()
        {
            int frames = _pendingCount / Frame;
            if (frames == 0) return;
            // Silero is asked every ~96 ms of audio at most.
            if (_useSilero && frames < 3 && _active == false && _pendingCount < Frame * 3) return;

            float[] probs = DetectSpeech(frames);
            for (int f = 0; f < frames; f++)
            {
                var frame = new float[Frame];
                Array.Copy(_pending, f * Frame, frame, 0, Frame);
                OnFrame(frame, probs[f] >= _o.VadThreshold);
                _consumed += Frame;
            }
            int used = frames * Frame;
            Array.Copy(_pending, used, _pending, 0, _pendingCount - used);
            _pendingCount -= used;

            if (_active) MaybeInfer();
        }

        private float[] DetectSpeech(int frames)
        {
            int n = frames * Frame;
            var result = new float[frames];
            if (_useSilero)
            {
                // Window = up to half a second of earlier audio (Silero restarts each call and needs a little
                // context) + every new frame, so a backlog of any length gets real probabilities.
                int ctx = Math.Min(_vadHistoryCount, Rate / 2) / Frame * Frame;
                var window = new float[ctx + n];
                Array.Copy(_vadHistory, _vadHistoryCount - ctx, window, 0, ctx);
                Array.Copy(_pending, 0, window, ctx, n);
                float[]? p = null;
                try { p = _worker.DetectSpeech(window, window.Length); }
                catch (WorkerException ex) { Log.WarnOnce("VAD", ex); }
                if (p != null && p.Length > 0)
                {
                    for (int f = 0; f < frames; f++)
                    {
                        int idx = p.Length - frames + f;
                        result[f] = idx >= 0 ? p[idx] : 0f;
                    }
                    RememberForVad(n);
                    return result;
                }
            }
            RememberForVad(n);

            // Energy fallback with an adaptive noise floor.
            for (int f = 0; f < frames; f++)
            {
                float rms = Rms(_pending, f * Frame, Frame);
                bool speech = rms > Math.Max(_noiseFloor * 3.2f, 0.006f);
                if (!speech) _noiseFloor = _noiseFloor * 0.98f + rms * 0.02f;
                else _noiseFloor = Math.Min(_noiseFloor * 1.0005f, 0.05f);
                result[f] = speech ? 1f : 0f;
            }
            return result;
        }

        private void RememberForVad(int n)
        {
            if (n >= VadContext)
            {
                Array.Copy(_pending, n - VadContext, _vadHistory, 0, VadContext);
                _vadHistoryCount = VadContext;
                return;
            }
            int keep = Math.Min(_vadHistoryCount, VadContext - n);
            Array.Copy(_vadHistory, _vadHistoryCount - keep, _vadHistory, 0, keep);
            Array.Copy(_pending, 0, _vadHistory, keep, n);
            _vadHistoryCount = keep + n;
        }

        private void OnFrame(float[] frame, bool speech)
        {
            if (!_active)
            {
                _preroll.Enqueue(frame);
                while (_preroll.Count > PrerollFrames) _preroll.Dequeue();
                _speechRun = speech ? _speechRun + 1 : 0;
                if (_speechRun >= 2)
                {
                    _active = true;
                    _silenceRun = 0;
                    _uttCount = 0;
                    _uttStart = _consumed - (long)(_preroll.Count - 1) * Frame;
                    foreach (var f in _preroll) AppendUtt(f);
                    _preroll.Clear();
                    _sinceInference = _uttCount;
                }
                return;
            }

            AppendUtt(frame);
            _sinceInference += Frame;
            _silenceRun = speech ? 0 : _silenceRun + 1;

            if (_silenceRun * 32 >= _o.PauseToEndMs)
            {
                // Pause: finish the line (keep ~200 ms of the trailing silence for natural word endings).
                int trailing = Math.Max(0, (_silenceRun - 6) * Frame);
                FinishLine(_uttCount - trailing, all: true);
                _active = false;
                _speechRun = 0;
            }
            else if (_uttCount >= _o.MaxLineSeconds * Rate)
            {
                // Long unbroken speech (or catching up on a backlog): split here.
                FinishLine(_uttCount, all: false);
            }
        }

        private void AppendUtt(float[] frame)
        {
            if (_uttCount + frame.Length > _utt.Length) Array.Resize(ref _utt, _utt.Length * 2);
            Array.Copy(frame, 0, _utt, _uttCount, frame.Length);
            _uttCount += frame.Length;
        }

        private void MaybeInfer()
        {
            if (!_active) return;
            double seconds = _uttCount / (double)Rate;
            if (seconds >= _o.MaxLineSeconds)
            {
                FinishLine(_uttCount, all: false);
                return;
            }
            if (seconds >= 0.6 && _sinceInference >= _o.UpdateIntervalMs * Rate / 1000)
            {
                _sinceInference = 0;
                var res = Run(_uttCount, final: false, translate: _o.TranslateToEnglish);
                if (res == null) return;
                string text = Join(res.Segments, _uttCount);
                if (_o.FilterHallucinations && TextFilter.IsLikelyInvented(text, MinNoSpeech(res), seconds, Rms(_utt, 0, _uttCount)))
                    text = "";
                if (text != _lastPartial)
                {
                    _lastPartial = text;
                    LastDelayMs = (int)((Stopwatch.GetTimestamp() - _lastSampleTicks) * 1000 / Stopwatch.Frequency) + 100;
                    Partial?.Invoke(text);
                }
            }
        }

        /// <summary>
        /// Finishes the line. With all=false (line too long), everything except the last sentence is committed and
        /// the rest keeps going as the next line.
        /// </summary>
        private void FinishLine(int count, bool all)
        {
            count = Math.Clamp(count, 0, _uttCount);
            if (count < Rate / 4)
            {
                ResetLine(clearPartial: true);
                return;
            }
            var res = Run(count, final: true, translate: _o.TranslateToEnglish);
            if (res == null) { ResetLine(clearPartial: true); return; }

            int commitSamples = count;
            List<Segment> segs = res.Segments;
            if (!all && segs.Count > 1)
            {
                // Keep the last segment for the next line; it may still be mid-sentence.
                var last = segs[^1];
                int cut = (int)Math.Clamp(last.T0 * Rate / 100, Rate / 2, count);
                segs = segs.Take(segs.Count - 1).ToList();
                commitSamples = cut;
            }

            string text = Join(segs, commitSamples);
            double seconds = commitSamples / (double)Rate;
            if (_o.FilterHallucinations && TextFilter.IsLikelyInvented(text, MinNoSpeech(res), seconds, Rms(_utt, 0, commitSamples)))
                text = "";

            if (text.Length > 0)
            {
                string? original = null;
                if (_o.TranslateToEnglish && _o.KeepOriginal && !string.Equals(DetectedLanguage, "en", StringComparison.Ordinal))
                {
                    var orig = Run(commitSamples, final: true, translate: false);
                    if (orig != null) original = Join(orig.Segments, commitSamples);
                    if (string.IsNullOrWhiteSpace(original) || original == text) original = null;
                }
                var line = new FinishedLine
                {
                    Text = text,
                    Original = original,
                    Language = DetectedLanguage,
                    Start = TimeSpan.FromSeconds(_uttStart / (double)Rate),
                    End = TimeSpan.FromSeconds((_uttStart + commitSamples) / (double)Rate),
                };
                if (!_o.TranslateToEnglish) _lastText = text;
                LastDelayMs = (int)((Stopwatch.GetTimestamp() - _lastSampleTicks) * 1000 / Stopwatch.Frequency) + 100;
                _lastPartial = "";
                Finished?.Invoke(line);
            }

            if (all)
            {
                ResetLine(clearPartial: text.Length == 0);
            }
            else if (commitSamples >= _uttCount)
            {
                // Long line committed whole while the speaker keeps going: start the next line right here.
                _uttStart += _uttCount;
                _uttCount = 0;
                _sinceInference = 0;
                if (text.Length == 0 && _lastPartial.Length > 0) { _lastPartial = ""; Partial?.Invoke(""); }
            }
            else
            {
                // Carry the unfinished tail into the next line.
                int rest = _uttCount - commitSamples;
                Array.Copy(_utt, commitSamples, _utt, 0, rest);
                _uttCount = rest;
                _uttStart += commitSamples;
                _sinceInference = _o.UpdateIntervalMs * Rate / 1000; // re-read the tail right away
                if (text.Length == 0) { _lastPartial = ""; Partial?.Invoke(""); }
            }
        }

        private void ResetLine(bool clearPartial)
        {
            _active = false;
            _uttCount = 0;
            _speechRun = 0;
            _silenceRun = 0;
            _lockedLanguage = null;
            if (clearPartial && _lastPartial.Length > 0)
            {
                _lastPartial = "";
                Partial?.Invoke("");
            }
        }

        private TranscribeResult? Run(int count, bool final, bool translate)
        {
            // Whisper does better with a little padding on very short audio.
            int padded = Math.Max(count, Rate + Rate / 10);
            var samples = new float[padded];
            Array.Copy(_utt, samples, count);
            string lang = _o.Language != "auto" ? _o.Language : _lockedLanguage ?? "auto";
            var req = new TranscribeRequest
            {
                Language = lang,
                Translate = translate,
                Final = final,
                SuppressNonSpeech = !_o.ShowSoundLabels,
                // Carry the previous sentence as context, which helps casing and punctuation.
                Prompt = !translate && _lastText.Length > 0 && (lang == "auto" || lang == DetectedLanguage)
                    ? (_lastText.Length > 160 ? _lastText.Substring(_lastText.Length - 160) : _lastText)
                    : "",
                Samples = samples,
                Count = padded,
            };
            try
            {
                var res = _worker.Transcribe(req);
                LastInferenceMs = res.Milliseconds;
                if (!string.IsNullOrEmpty(res.Language))
                {
                    DetectedLanguage = res.Language;
                    // Once a line has a second and a half of audio, keep its language steady.
                    if (_lockedLanguage == null && count >= Rate * 3 / 2 && _o.Language == "auto") _lockedLanguage = res.Language;
                }
                return res;
            }
            catch (WorkerException ex)
            {
                Log.WarnOnce("Transcribe", ex);
                return null;
            }
        }

        private string Join(List<Segment> segs, int samples)
        {
            double limit = samples / (double)Rate * 100 + 50; // ignore segments Whisper placed in the padding
            var parts = segs.Where(s => s.T0 <= limit).Select(s => s.Text);
            return TextFilter.Clean(string.Concat(parts), _o.ShowSoundLabels);
        }

        private static float MinNoSpeech(TranscribeResult r) => r.Segments.Count == 0 ? 1f : r.Segments.Min(s => s.NoSpeech);

        private static float Rms(float[] a, int offset, int count)
        {
            if (count <= 0) return 0;
            double sum = 0;
            int end = Math.Min(a.Length, offset + count);
            for (int i = offset; i < end; i++) sum += a[i] * a[i];
            return (float)Math.Sqrt(sum / Math.Max(1, end - offset));
        }

        public void Dispose()
        {
            _stop = true;
            _signal.Set();
            try { _thread.Join(3000); } catch { }
            _signal.Dispose();
        }
    }
}
