using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Threading;
using Caption.Services;
using Caption.Whisper;

namespace Caption.Captioning
{
    public sealed class PipelineOptions
    {
        public string Language = "auto";
        /// <summary>Whisper's own translate task (to English). Skipped automatically when the speech is already English.</summary>
        public bool TranslateToEnglish;
        /// <summary>Also produce the source-language text for finished lines (an extra pass).</summary>
        public bool KeepOriginal;
        public int UpdateIntervalMs = 300;
        public int PauseToEndMs = 900;
        public int MaxLineSeconds = 8;
        public float VadThreshold = 0.5f;
        public bool ShowSoundLabels;
        public bool FilterHallucinations = true;
        /// <summary>Beam search width (1 = greedy). Beams make each read steadier and more accurate.</summary>
        public int BeamSize = 1;
        /// <summary>Film-style subtitles: one sentence per subtitle, at most two lines of MaxLineChars.</summary>
        public bool SubtitleMode;
        public int MaxLineChars = 42;
    }

    public sealed class FinishedLine
    {
        public string Text = "";
        public string? Original;
        public string Language = "";
        public TimeSpan Start, End;
    }

    /// <summary>
    /// Streaming captions with "local agreement" (the whisper_streaming approach): the audio since the last finished
    /// sentence is re-read every UpdateIntervalMs with the recent text as context, and words are confirmed once two
    /// reads in a row agree on them. Confirmed words show solid right away; the newest, still-changing words show
    /// dimmed. Whisper always hears whole sentences (not fragments cut at every short pause), which is what makes it
    /// accurate, while the screen stays only one read behind the speaker.
    /// </summary>
    public sealed class CaptionPipeline : IDisposable
    {
        private const int Rate = 16000;
        private const int Frame = 512;             // 32 ms, Silero's frame
        private const int PrerollFrames = 8;       // 256 ms kept from before speech starts
        private const int VadContext = Rate;
        private const int MaxBufferSeconds = 18;   // whisper's window is 30 s; keep well inside it
        private const int KeepAfterPause = 4;      // seconds of audio kept as context after a pause

        private sealed class Word
        {
            public string Text = "";
            public string Key = "";
            public long Start;   // absolute sample index (approximate, from its segment)
            public long End;
            public bool SentenceEnd;
        }

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

        // recent audio (absolute indexes) for the optional "original" pass
        private readonly float[] _ring = new float[Rate * 60];

        // speech state
        private readonly Queue<float[]> _preroll = new();
        private bool _active;
        private int _speechRun;
        private int _silenceRun;
        private float[] _buf = new float[Rate * 32];  // audio not yet trimmed away
        private int _bufCount;
        private long _bufStart;                     // absolute sample index of _buf[0]
        private int _sinceInference;
        private long _pausedAt;

        // agreement state
        private List<Word> _prev = new();           // the previous read's words for the buffer
        private int _committed;                     // how many of them are confirmed
        private readonly List<Word> _line = new();  // confirmed words of the line on screen
        private string _context = "";               // recent finished text, fed back as the prompt
        private string? _language;                  // language locked for this stretch of speech
        private string _lastStable = "", _lastTentative = "";
        // per segment of the last read: word count, end time (10 ms units), ends a sentence
        private List<(int Words, long T1, bool Ends)> _segs = new();

        // energy VAD fallback
        private float _noiseFloor = 0.003f;

        /// <summary>The line being spoken: confirmed words, and newer words that may still change. Both "" clears.</summary>
        public event Action<string, string>? Partial;
        public event Action<FinishedLine>? Finished;
        public event Action<Exception>? Failed;

        /// <summary>How long the last read took (GPU/CPU time).</summary>
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
                        Reset(clearScreen: true);
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
            // While idle, Silero is asked every ~96 ms of audio at most.
            if (_useSilero && frames < 3 && !_active) return;

            float[] probs = DetectSpeech(frames);
            for (int f = 0; f < frames; f++)
            {
                var frame = new float[Frame];
                Array.Copy(_pending, f * Frame, frame, 0, Frame);
                for (int i = 0; i < Frame; i++) _ring[(_consumed + i) % _ring.Length] = frame[i];
                OnFrame(frame, probs[f] >= _o.VadThreshold);
                _consumed += Frame;
            }
            int used = frames * Frame;
            Array.Copy(_pending, used, _pending, 0, _pendingCount - used);
            _pendingCount -= used;

            // One read per batch of audio: however far behind we were, the text catches up in one step.
            if (_active && _bufCount >= Rate / 2 && _sinceInference >= _o.UpdateIntervalMs * Rate / 1000)
                Step();
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
                if (_bufCount > 0)
                {
                    // Right after a pause the audio is kept, so the next sentence is heard in context.
                    AppendBuf(frame);
                    if (_consumed - _pausedAt > KeepAfterPause * Rate || _bufCount >= MaxBufferSeconds * Rate)
                        ResetBuffer();
                }
                _speechRun = speech ? _speechRun + 1 : 0;
                if (_speechRun >= 2)
                {
                    _active = true;
                    _silenceRun = 0;
                    if (_bufCount == 0)
                    {
                        _bufStart = _consumed - (long)(_preroll.Count - 1) * Frame;
                        foreach (var f in _preroll) AppendBuf(f);
                    }
                    _preroll.Clear();
                    _sinceInference = _o.UpdateIntervalMs * Rate / 1000 - 3 * Frame; // first read ~100 ms from now
                }
                return;
            }

            AppendBuf(frame);
            _sinceInference += Frame;
            _silenceRun = speech ? 0 : _silenceRun + 1;

            if (_silenceRun * 32 >= _o.PauseToEndMs)
            {
                // The speaker paused: settle everything heard so far and end the line on screen.
                Flush(endLine: true);
                _active = false;
                _speechRun = 0;
                _pausedAt = _consumed;
            }
            else if (_bufCount >= MaxBufferSeconds * Rate)
            {
                // A long stretch Whisper never put a sentence end in: settle it and make room.
                Flush(endLine: false);
                if (_bufCount >= (MaxBufferSeconds - 2) * Rate) ResetBuffer();
            }
        }

        private void AppendBuf(float[] frame)
        {
            if (_bufCount + frame.Length > _buf.Length) Array.Resize(ref _buf, _buf.Length * 2);
            Array.Copy(frame, 0, _buf, _bufCount, frame.Length);
            _bufCount += frame.Length;
        }

        // ---------------------------------------------------------------- agreement

        /// <summary>
        /// Where the confirmed words end in a new read. Usually the same index as before, but a re-read can merge or
        /// split a word earlier on, so look a few words either side for the last confirmed words.
        /// </summary>
        private int Boundary(List<Word> words)
        {
            if (_committed == 0) return 0;
            string last = _prev[_committed - 1].Key;
            string? before = _committed >= 2 ? _prev[_committed - 2].Key : null;
            for (int d = 0; d <= 3; d++)
            {
                foreach (int j in new[] { _committed + d, _committed - d })
                {
                    if (j < 1 || j > words.Count) continue;
                    if (words[j - 1].Key != last) continue;
                    if (before != null && j >= 2 && words[j - 2].Key != before) continue;
                    return j;
                }
            }
            return Math.Min(_committed, words.Count);
        }

        /// <summary>Re-read the buffer, confirm the words two reads agree on, show the rest as tentative.</summary>
        private void Step()
        {
            _sinceInference = 0;
            var res = Run(_bufCount, final: false);
            if (res == null) return;
            var words = ToWords(res, _bufCount);

            int b = Boundary(words);
            int agree = 0;
            while (b + agree < words.Count && _committed + agree < _prev.Count &&
                   words[b + agree].Key == _prev[_committed + agree].Key) agree++;
            for (int i = b; i < b + agree; i++) Confirm(words[i]);
            _prev = words;
            _committed = b + agree;
            TrimBuffer();
            ShowProgress(words.Skip(_committed));
        }

        /// <summary>Pause or full buffer: one final, careful read; everything it says is confirmed.</summary>
        private void Flush(bool endLine)
        {
            int count = _bufCount;
            // Leave out most of the trailing silence (keep ~200 ms for word endings).
            int trailing = Math.Max(0, (_silenceRun - 6) * Frame);
            count = Math.Max(0, count - trailing);
            if (count >= Rate / 4)
            {
                var res = Run(count, final: true);
                if (res != null)
                {
                    var words = ToWords(res, count);
                    int b = Boundary(words);
                    for (int i = b; i < words.Count; i++) Confirm(words[i]);
                    _prev = words;
                    _committed = words.Count;
                    TrimBuffer();
                }
            }
            if (endLine) FinishLine();
            ShowProgress(Enumerable.Empty<Word>());
        }

        /// <summary>Text whose audio has left the buffer becomes the prompt for later reads (never text still in the buffer).</summary>
        private void AddContext(IEnumerable<Word> words)
        {
            string t = string.Join(' ', words.Select(w => w.Text));
            if (t.Length == 0) return;
            _context = (_context + " " + t).Trim();
            if (_context.Length > 220) _context = _context.Substring(_context.Length - 220);
        }

        private void ResetBuffer()
        {
            AddContext(_prev.Take(_committed));
            _bufCount = 0;
            _prev = new List<Word>();
            _committed = 0;
            _language = null;
        }

        private void Confirm(Word w)
        {
            _line.Add(w);
            int chars = _line.Sum(x => x.Text.Length + 1) - 1;
            double seconds = (w.End - _line[0].Start) / (double)Rate;
            bool end;
            if (_o.SubtitleMode)
            {
                // Subtitles: each sentence is its own subtitle; long ones split at a comma or when two lines are full.
                int full = _o.MaxLineChars * 2;
                bool comma = w.Text.EndsWith(',') || w.Text.EndsWith(';') || w.Text.EndsWith(':') || w.Text.EndsWith('—');
                end = w.SentenceEnd || chars >= full - 6 || (comma && chars >= full * 2 / 3) || seconds >= 7;
            }
            else
            {
                // Lines end at sentence ends once they're a reasonable length, or at a word boundary if they run long.
                end = (w.SentenceEnd && (chars >= 40 || seconds >= 3)) || chars >= 150 || seconds >= _o.MaxLineSeconds + 4;
            }
            if (end) FinishLine();
        }

        private void FinishLine()
        {
            if (_line.Count == 0) return;
            string text = TextFilter.CollapseRepeats(string.Join(' ', _line.Select(w => w.Text)));
            long start = _line[0].Start, end = Math.Max(_line[^1].End, start + Rate / 2);
            _line.Clear();
            if (!text.Any(char.IsLetterOrDigit)) return;
            string? original = null;
            if (_o.TranslateToEnglish && _o.KeepOriginal && DetectedLanguage != "en") original = Original(start, end);
            LastDelayMs = Delay();
            Finished?.Invoke(new FinishedLine
            {
                Text = text,
                Original = original,
                Language = DetectedLanguage,
                Start = TimeSpan.FromSeconds(start / (double)Rate),
                End = TimeSpan.FromSeconds(end / (double)Rate),
            });
            _lastStable = "";
        }

        /// <summary>
        /// Drop audio Whisper is done with: everything up to the end of the last confirmed segment that ends a sentence
        /// (or, on a long buffer, any fully confirmed segment). Its text becomes context for the next reads.
        /// </summary>
        private void TrimBuffer()
        {
            int wordIndex = 0, cutWords = -1;
            long cutT1 = 0;
            bool longBuffer = _bufCount > 10 * Rate;
            foreach (var seg in _segs)
            {
                wordIndex += seg.Words;
                if (wordIndex > _committed) break;
                if (seg.Ends || longBuffer) { cutWords = wordIndex; cutT1 = seg.T1; }
            }
            if (cutWords <= 0) return;
            int cut = (int)Math.Clamp(cutT1 * Rate / 100, 0, _bufCount);
            if (cut < Rate / 2 || cut > _bufCount - Rate / 4) return; // leave a little audio, and don't trim tiny bits
            Array.Copy(_buf, cut, _buf, 0, _bufCount - cut);
            _bufCount -= cut;
            _bufStart += cut;
            AddContext(_prev.Take(cutWords));
            _prev = _prev.Skip(cutWords).ToList();
            _committed = Math.Max(0, _committed - cutWords);
        }

        private void ShowProgress(IEnumerable<Word> tentative)
        {
            string stable = string.Join(' ', _line.Select(w => w.Text));
            string tent = string.Join(' ', tentative.Select(w => w.Text));
            if (stable == _lastStable && tent == _lastTentative) return;
            _lastStable = stable;
            _lastTentative = tent;
            LastDelayMs = Delay();
            Partial?.Invoke(stable, tent);
        }

        private void Reset(bool clearScreen)
        {
            _active = false;
            ResetBuffer();
            _speechRun = 0;
            _silenceRun = 0;
            if (clearScreen) _line.Clear();
            if (_lastStable.Length > 0 || _lastTentative.Length > 0 || clearScreen)
            {
                _lastStable = "";
                _lastTentative = "";
                Partial?.Invoke("", "");
            }
        }

        private int Delay() => (int)((Stopwatch.GetTimestamp() - _lastSampleTicks) * 1000 / Stopwatch.Frequency) + 100;

        // ---------------------------------------------------------------- whisper

        private TranscribeResult? Run(int count, bool final)
        {
            // Whisper does better with a little padding on very short audio.
            int padded = Math.Max(count, Rate + Rate / 10);
            var samples = new float[padded];
            Array.Copy(_buf, samples, count);
            string lang = _o.Language != "auto" ? _o.Language : _language ?? "auto";
            // Translate only speech that isn't English already (and translating English hurts accuracy).
            string known = lang != "auto" ? lang : DetectedLanguage;
            bool translate = _o.TranslateToEnglish && known.Length > 0 && known != "en";
            return RunOn(samples, padded, lang, translate, final);
        }

        private TranscribeResult? RunOn(float[] samples, int count, string lang, bool translate, bool final)
        {
            var req = new TranscribeRequest
            {
                Language = lang,
                Translate = translate,
                Final = final,
                SuppressNonSpeech = !_o.ShowSoundLabels,
                BeamSize = _o.BeamSize,
                // Recent finished text as context: helps names, casing and punctuation stay consistent.
                Prompt = translate ? "" : _context,
                Samples = samples,
                Count = count,
            };
            try
            {
                var res = _worker.Transcribe(req);
                LastInferenceMs = res.Milliseconds;
                if (!string.IsNullOrEmpty(res.Language))
                {
                    if (lang == "auto") DetectedLanguage = res.Language;
                    else DetectedLanguage = lang;
                    // Once there's a second and a half of speech, keep its language steady until the next pause.
                    if (_language == null && count >= Rate * 3 / 2 && _o.Language == "auto") _language = res.Language;
                }
                return res;
            }
            catch (WorkerException ex)
            {
                Log.WarnOnce("Transcribe", ex);
                return null;
            }
        }

        private string? Original(long start, long end)
        {
            int n = (int)Math.Min(end - start, _ring.Length);
            if (n <= Rate / 4 || _consumed - start > _ring.Length) return null;
            var samples = new float[Math.Max(n, Rate + Rate / 10)];
            for (int i = 0; i < n; i++) samples[i] = _ring[(start + i) % _ring.Length];
            var res = RunOn(samples, samples.Length, DetectedLanguage.Length > 0 ? DetectedLanguage : "auto", false, true);
            string? text = res == null ? null : Clean(res.Text);
            return string.IsNullOrWhiteSpace(text) ? null : text;
        }

        private string Clean(string text) => TextFilter.Clean(text, _o.ShowSoundLabels);

        private static List<string> Split(string s) => s.Split(' ', StringSplitOptions.RemoveEmptyEntries).ToList();

        /// <summary>The read as words, dropping invented lines and anything Whisper placed in the padding.</summary>
        private List<Word> ToWords(TranscribeResult res, int count)
        {
            var list = new List<Word>();
            _segs = new List<(int, long, bool)>();
            double limit = count / (double)Rate * 100 + 30;
            float rms = Rms(_buf, 0, count);
            foreach (var seg in res.Segments)
            {
                if (seg.T0 > limit) continue;
                string text = Clean(seg.Text);
                if (text.Length == 0) continue;
                double secs = Math.Max(0.1, (seg.T1 - seg.T0) / 100.0);
                if (_o.FilterHallucinations && TextFilter.IsLikelyInvented(text, seg.NoSpeech, secs, rms)) continue;
                var ws = Split(text);
                if (ws.Count == 0) continue;
                char endChar = text[^1];
                _segs.Add((ws.Count, seg.T1, endChar is '.' or '?' or '!' or '。' or '？' or '！'));
                long s0 = _bufStart + seg.T0 * Rate / 100, s1 = _bufStart + seg.T1 * Rate / 100;
                for (int i = 0; i < ws.Count; i++)
                {
                    // Spread the segment's time across its words (good enough for .srt timing).
                    long a = s0 + (s1 - s0) * i / ws.Count, b = s0 + (s1 - s0) * (i + 1) / ws.Count;
                    string w = ws[i];
                    char last = w[^1];
                    list.Add(new Word
                    {
                        Text = w,
                        Key = Key(w),
                        Start = a,
                        End = b,
                        SentenceEnd = last is '.' or '?' or '!' or '。' or '？' or '！',
                    });
                }
            }
            return list;
        }

        /// <summary>Compare words ignoring case and punctuation, so "you," and "you" agree.</summary>
        private static string Key(string w)
        {
            var sb = new StringBuilder(w.Length);
            foreach (char c in w) if (char.IsLetterOrDigit(c)) sb.Append(char.ToLowerInvariant(c));
            return sb.Length > 0 ? sb.ToString() : w;
        }

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
