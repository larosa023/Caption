using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using Caption.Audio;
using Caption.Captioning;
using Caption.Services;
using Caption.Whisper;

namespace Caption.Subtitles
{
    public sealed class JobRequest
    {
        public string Url { get; set; } = "";
        /// <summary>Extra HTTP headers for the stream, "Name: value" lines.</summary>
        public Dictionary<string, string>? Headers { get; set; }
        public string Title { get; set; } = "";
        /// <summary>Where playback is, in seconds.</summary>
        public double Position { get; set; }
        /// <summary>Subtitle language wanted ("en", "eng", "" = whatever is spoken).</summary>
        public string Language { get; set; } = "";
        /// <summary>Which audio track (0 = first).</summary>
        public int AudioIndex { get; set; }
    }

    /// <summary>
    /// Makes subtitles for one stream, ahead of where it's playing: FFmpeg reads the audio from the playhead on,
    /// voice detection finds the speech, Whisper reads each stretch of speech with up to ~25 seconds of context
    /// (and the text before it), word times come from Whisper's attention (DTW), and the words are laid out
    /// as proper subtitles. It stays a few minutes ahead of playback and follows seeks.
    /// </summary>
    public sealed class SubtitleJob : IDisposable
    {
        private const int Rate = 16000;
        private const double AheadSeconds = 8 * 60;
        // Chunk length grows with how far ahead of playback we are: short while playback is close behind
        // (so subtitles keep appearing), long once there's a lead (fewer, better-context reads).
        private const double FirstChunk = 12, Chunk = 75;
        private double ChunkTarget(double chunkStart, bool first)
        {
            if (first) return FirstChunk;
            double lead = chunkStart - _playhead;
            return lead < 30 ? 15 : lead < 90 ? 30 : lead < 180 ? 50 : Chunk;
        }

        private readonly WorkerClient _worker;
        private readonly bool _hasVad;
        private readonly int _beam;
        private readonly JobRequest _req;
        private readonly Thread _thread;
        private readonly object _gate = new();
        private readonly List<Cue> _cues = new();
        private readonly List<(double From, double To)> _covered = new();
        private volatile bool _stop;
        private double _playhead;
        private double? _restartAt;
        private Process? _ffmpeg;
        private string? _language;
        private readonly Dictionary<string, int> _languageVotes = new();
        private int _version;

        public string Id { get; } = Guid.NewGuid().ToString("N").Substring(0, 12);
        public DateTime LastSeen { get; private set; } = DateTime.UtcNow;
        public string State { get; private set; } = "starting";
        public string Error { get; private set; } = "";
        /// <summary>The end of the audio already turned into subtitles, from the current reading position.</summary>
        public double DoneUntil { get; private set; }
        public string Language => _language ?? "";
        public string Title => _req.Title;
        public int Version => _version;
        public bool Translating { get; private set; }

        public event Action? Changed;

        public SubtitleJob(JobRequest req, WorkerClient worker, bool hasVad, int beam)
        {
            _req = req;
            _worker = worker;
            _hasVad = hasVad;
            _beam = beam;
            _playhead = Math.Max(0, req.Position);
            _restartAt = Math.Max(0, req.Position - 1);
            _thread = new Thread(Run) { IsBackground = true, Name = "Subtitle job", Priority = ThreadPriority.BelowNormal };
            _thread.Start();
        }

        public int CueCount { get { lock (_gate) return _cues.Count; } }

        /// <summary>The app is still there.</summary>
        public void Touch() => LastSeen = DateTime.UtcNow;

        public string Srt()
        {
            lock (_gate) return CueBuilder.ToSrt(_cues);
        }

        public List<Cue> Cues(double from, double to)
        {
            lock (_gate) return _cues.Where(c => c.End >= from && c.Start <= to).Select(c => new Cue { Start = c.Start, End = c.End, Text = c.Text }).ToList();
        }

        /// <summary>Playback moved (or just progressed). Far jumps restart reading there.</summary>
        public void SetPosition(double seconds)
        {
            LastSeen = DateTime.UtcNow;
            double old = _playhead;
            _playhead = Math.Max(0, seconds);
            bool covered;
            lock (_gate) covered = _covered.Any(r => seconds >= r.From - 1 && seconds < r.To - 2);
            bool insideReading = _readFrom <= seconds + 1 && seconds <= _readCursor + 15;
            if (!covered && !insideReading && Math.Abs(seconds - old) > 3)
            {
                _restartAt = Math.Max(0, seconds - 1);
                KillFfmpeg();
            }
        }

        private double _readFrom, _readCursor;

        // ---------------------------------------------------------------- the work

        private void Run()
        {
            try
            {
                while (!_stop)
                {
                    double start = _restartAt ?? -1;
                    if (start < 0)
                    {
                        // Finished (end of stream) or waiting for playback to come closer.
                        Thread.Sleep(250);
                        continue;
                    }
                    _restartAt = null;
                    start = SkipCovered(start);
                    ReadFrom(start);
                }
            }
            catch (Exception ex) when (!_stop)
            {
                Log.Error("Subtitle job", ex);
                Error = ex.Message;
                State = "error";
                Changed?.Invoke();
            }
            finally
            {
                KillFfmpeg();
            }
        }

        private double SkipCovered(double t)
        {
            lock (_gate)
            {
                foreach (var r in _covered.OrderBy(r => r.From))
                    if (t >= r.From - 0.5 && t < r.To) t = r.To;
            }
            return t;
        }

        private void ReadFrom(double start)
        {
            string headers = "";
            string? ua = null;
            if (_req.Headers != null)
                foreach (var (k, v) in _req.Headers)
                {
                    if (string.Equals(k, "User-Agent", StringComparison.OrdinalIgnoreCase)) ua = v;
                    else headers += $"{k}: {v}\r\n";
                }
            State = "working";
            Changed?.Invoke();
            var p = Ffmpeg.StartAudio(_req.Url, headers.Length > 0 ? headers : null, ua, start, _req.AudioIndex);
            _ffmpeg = p;
            string err = "";
            p.ErrorDataReceived += (_, e) => { if (!string.IsNullOrWhiteSpace(e.Data) && err.Length < 2000) err += e.Data + "\n"; };
            p.BeginErrorReadLine();
            var stdout = p.StandardOutput.BaseStream;

            var center = new CenterExtractor();
            var mono = new List<float>(Rate * 120);
            double chunkStart = start;     // time of mono[0]
            _readFrom = start;
            _readCursor = start;
            bool first = true;
            bool keepContext = false;
            var raw = new byte[Rate * 8 * 2]; // ~2 s of stereo float
            int carry = 0;
            bool eof = false;
            var readClock = System.Diagnostics.Stopwatch.StartNew();
            int chunks = 0;

            while (!_stop && _restartAt == null)
            {
                // Stay a few minutes ahead of playback, no more (saves bandwidth on torrents).
                if (chunkStart > _playhead + AheadSeconds)
                {
                    State = "ahead";
                    Thread.Sleep(500);
                    readClock.Restart();
                    continue;
                }

                int n;
                try { n = stdout.Read(raw, carry, raw.Length - carry); }
                catch (Exception) { n = 0; }
                if (n <= 0) { eof = true; }
                else
                {
                    int total = carry + n;
                    int frames = total / 8;
                    var l = new float[frames];
                    var r = new float[frames];
                    var f = MemoryMarshal.Cast<byte, float>(raw.AsSpan(0, frames * 8));
                    for (int i = 0; i < frames; i++) { l[i] = f[2 * i]; r[i] = f[2 * i + 1]; }
                    carry = total - frames * 8;
                    if (carry > 0) Array.Copy(raw, frames * 8, raw, 0, carry);
                    mono.AddRange(center.Process(l, r, frames));
                    _readCursor = chunkStart + mono.Count / (double)Rate;
                }

                // Another job already covered what's next (seeked back earlier): jump past it.
                double target = ChunkTarget(chunkStart, first);
                if (mono.Count >= target * Rate || (eof && mono.Count > Rate / 2))
                {
                    int cut = eof ? mono.Count : QuietCut(mono, (int)(target * Rate));
                    var chunk = mono.GetRange(0, cut).ToArray();
                    mono.RemoveRange(0, cut);
                    double readSec = readClock.Elapsed.TotalSeconds;
                    var tx = System.Diagnostics.Stopwatch.StartNew();
                    Transcribe(chunk, chunkStart, keepContext);
                    double txSec = tx.Elapsed.TotalSeconds;
                    keepContext = true;
                    double len = cut / (double)Rate;
                    double leadAfter = chunkStart + len - _playhead;
                    // Every chunk while starting, then every 10th, plus any time playback caught up.
                    if (chunks < 6 || chunks % 10 == 0 || leadAfter < 5)
                        Log.Info($"Subtitles to {Fmt(chunkStart + len)}: {len:0} s of audio downloaded in {readSec:0.0} s, transcribed in {txSec:0.0} s; {leadAfter:0} s ahead of playback");
                    chunks++;
                    readClock.Restart();
                    lock (_gate) AddCovered(chunkStart, chunkStart + cut / (double)Rate);
                    chunkStart += cut / (double)Rate;
                    DoneUntil = chunkStart;
                    first = false;
                    Changed?.Invoke();

                    double skipped = SkipCovered(chunkStart);
                    if (skipped > chunkStart + 1)
                    {
                        _restartAt = skipped;
                        break;
                    }
                }
                if (eof) break;
            }

            KillFfmpeg();
            if (eof && _restartAt == null && !_stop)
            {
                if (CueCount == 0 && err.Length > 0)
                {
                    Error = "Couldn't read the stream's audio: " + err.Split('\n')[0];
                    State = "error";
                    Log.Info("FFmpeg: " + err.Trim());
                }
                else
                {
                    State = "done";
                }
                Changed?.Invoke();
            }
        }

        /// <summary>Cut near the target length at the quietest moment of the last 8 seconds, so no word is split.</summary>
        private static string Fmt(double t) => TimeSpan.FromSeconds(Math.Max(0, t)).ToString(t >= 3600 ? @"h\:mm\:ss" : @"m\:ss");

        private static int QuietCut(List<float> mono, int target)
        {
            int win = Rate / 5; // 200 ms
            int from = Math.Max(win, target - 8 * Rate);
            int best = target;
            double bestE = double.MaxValue;
            for (int s = from; s + win <= Math.Min(mono.Count, target); s += win / 2)
            {
                double e = 0;
                for (int i = s; i < s + win; i++) e += mono[i] * mono[i];
                if (e < bestE) { bestE = e; best = s + win / 2; }
            }
            return Math.Clamp(best, Rate, mono.Count);
        }

        private void AddCovered(double from, double to)
        {
            _covered.Add((from, to));
            _covered.Sort((a, b) => a.From.CompareTo(b.From));
            for (int i = 0; i + 1 < _covered.Count;)
            {
                if (_covered[i + 1].From <= _covered[i].To + 0.5)
                {
                    _covered[i] = (_covered[i].From, Math.Max(_covered[i].To, _covered[i + 1].To));
                    _covered.RemoveAt(i + 1);
                }
                else i++;
            }
        }

        // ---------------------------------------------------------------- recognition

        private void Transcribe(float[] audio, double offset, bool keepContext)
        {
            var windows = SpeechWindows(audio);
            var words = new List<TimedWord>();
            foreach (var (from, to) in windows)
            {
                if (_stop || _restartAt != null) return;
                int n = to - from;
                var samples = new float[Math.Max(n, Rate + Rate / 10)];
                Array.Copy(audio, from, samples, 0, n);
                string lang = Normalize(_req.Language);
                string speech = _language ?? "auto";
                bool translate = lang == "en" && _language != null && _language != "en";
                Translating = translate;
                TranscribeResult res;
                try
                {
                    res = _worker.Transcribe(new TranscribeRequest
                    {
                        Samples = samples,
                        Count = samples.Length,
                        Language = speech,
                        Translate = translate,
                        Final = true,
                        BeamSize = _beam,
                        KeepContext = keepContext,
                        WordTimes = true,
                        SuppressNonSpeech = true,
                    });
                }
                catch (WorkerException ex)
                {
                    Log.WarnOnce("Subtitle transcribe", ex);
                    continue;
                }
                keepContext = true;
                if (_language == null && !string.IsNullOrEmpty(res.Language) && n > Rate * 3)
                {
                    _languageVotes[res.Language] = _languageVotes.GetValueOrDefault(res.Language) + (n > Rate * 10 ? 2 : 1);
                    var top = _languageVotes.OrderByDescending(kv => kv.Value).First();
                    if (top.Value >= 2) _language = top.Key;
                }

                double baseTime = offset + from / (double)Rate;
                float rms = Rms(samples, 0, n);
                if (Debug) Log.Info($"[subs] window {baseTime:0.00}–{baseTime + n / (double)Rate:0.00} rms {rms:0.000}: {res.Segments.Count} segments");
                foreach (var seg in res.Segments)
                {
                    string why = "";
                    if (seg.Words == null || seg.Words.Count == 0) why = "no words";
                    else if (seg.T0 * Rate / 100 > n + Rate / 5) why = "in padding";
                    else if (TextFilter.Clean(seg.Text, false).Length == 0) why = "empty after cleaning";
                    else if (TextFilter.IsLikelyInvented(TextFilter.Clean(seg.Text, false), seg.NoSpeech, (seg.T1 - seg.T0) / 100.0, rms)) why = "looks invented";
                    if (Debug) Log.Info($"[subs]   {baseTime + seg.T0 / 100.0:0.00}–{baseTime + seg.T1 / 100.0:0.00} nsp {seg.NoSpeech:0.00} {(why.Length > 0 ? "DROPPED (" + why + ") " : "")}{seg.Text.Trim()}");
                    if (why.Length > 0) continue;
                    string text = TextFilter.Clean(seg.Text, false);
                    foreach (var w in seg.Words)
                    {
                        string wt = TextFilter.Clean(w.Text, false);
                        if (wt.Length == 0) continue;
                        words.Add(new TimedWord { Text = wt, Start = baseTime + w.Start / 100.0, End = baseTime + w.End / 100.0, Prob = w.Prob });
                    }
                }
            }
            if (words.Count == 0) return;
            var cues = CueBuilder.Build(words);
            lock (_gate)
            {
                // Replace anything already there for this stretch (a re-read after a seek).
                double a = offset, b = offset + audio.Length / (double)Rate;
                _cues.RemoveAll(c => c.Start >= a - 0.01 && c.Start < b);
                _cues.AddRange(cues);
                CueBuilder.FixTiming(_cues);
                _version++;
            }
        }

        /// <summary>
        /// Groups detected speech into windows of up to ~25 s (Whisper reads 30 s at a time), split only in pauses,
        /// with a little padding. Music and silence between them are never sent to Whisper.
        /// </summary>
        private static readonly bool Debug = Environment.GetEnvironmentVariable("CAPTION_DEBUG") == "1";

        private List<(int From, int To)> SpeechWindows(float[] audio)
        {
            const int Frame = 512;
            float[]? probs = null;
            if (_hasVad)
            {
                try { probs = _worker.DetectSpeech(audio, audio.Length); }
                catch (WorkerException ex) { Log.WarnOnce("Subtitle VAD", ex); }
            }
            int frames = audio.Length / Frame;
            var speech = new bool[frames];
            if (probs != null && probs.Length > 0)
            {
                // Hysteresis: start at 0.5, keep going down to 0.3.
                bool on = false;
                for (int i = 0; i < frames; i++)
                {
                    float p = i < probs.Length ? probs[i] : 0;
                    on = on ? p >= 0.3f : p >= 0.5f;
                    speech[i] = on;
                }
            }
            else
            {
                for (int i = 0; i < frames; i++) speech[i] = Rms(audio, i * Frame, Frame) > 0.01f;
            }

            // Regions, padded 0.25 s, merged across short gaps.
            var regions = new List<(int A, int B)>();
            int pad = Rate / 4;
            for (int i = 0; i < frames;)
            {
                if (!speech[i]) { i++; continue; }
                int j = i;
                while (j < frames && speech[j]) j++;
                int a = Math.Max(0, i * Frame - pad), b = Math.Min(audio.Length, j * Frame + pad);
                if (b - a >= Rate / 5)
                {
                    if (regions.Count > 0 && a - regions[^1].B < Rate * 6 / 10) regions[^1] = (regions[^1].A, b);
                    else regions.Add((a, b));
                }
                i = j;
            }

            // Pack into windows: consecutive regions while the window stays under 25 s and gaps under 2.5 s.
            var windows = new List<(int, int)>();
            int max = Rate * 25;
            foreach (var r in regions)
            {
                int a = r.A, b = r.B;
                // A single region longer than the window: split it at quiet points.
                while (b - a > max)
                {
                    int cut = a + QuietSplit(audio, a, max);
                    AddWindow(windows, a, cut, max);
                    a = cut;
                }
                AddWindow(windows, a, b, max);
            }
            return windows;
        }

        private static void AddWindow(List<(int From, int To)> windows, int a, int b, int max)
        {
            if (windows.Count > 0)
            {
                var last = windows[^1];
                if (b - last.From <= max && a - last.To < Rate * 5 / 2)
                {
                    windows[^1] = (last.From, b);
                    return;
                }
            }
            windows.Add((a, b));
        }

        private static int QuietSplit(float[] audio, int start, int max)
        {
            int win = Rate / 5, best = max, from = Math.Max(win, max - 6 * Rate);
            double bestE = double.MaxValue;
            for (int s = from; s + win <= max; s += win / 2)
            {
                double e = 0;
                for (int i = start + s; i < start + s + win && i < audio.Length; i++) e += audio[i] * audio[i];
                if (e < bestE) { bestE = e; best = s + win / 2; }
            }
            return best;
        }

        private static string Normalize(string lang)
        {
            lang = (lang ?? "").Trim().ToLowerInvariant();
            return lang switch
            {
                "eng" => "en", "spa" => "es", "fre" or "fra" => "fr", "ger" or "deu" => "de", "ita" => "it", "por" or "pob" => "pt",
                "jpn" => "ja", "kor" => "ko", "chi" or "zho" => "zh", "rus" => "ru", "ara" => "ar", "hin" => "hi", "dut" or "nld" => "nl",
                _ => lang.Length > 2 && lang[2] == '-' ? lang.Substring(0, 2) : lang,
            };
        }

        private static float Rms(float[] a, int offset, int count)
        {
            double sum = 0;
            int end = Math.Min(a.Length, offset + count);
            for (int i = offset; i < end; i++) sum += a[i] * a[i];
            return (float)Math.Sqrt(sum / Math.Max(1, end - offset));
        }

        private void KillFfmpeg()
        {
            var p = _ffmpeg;
            _ffmpeg = null;
            if (p == null) return;
            try { if (!p.HasExited) p.Kill(true); } catch { }
            try { p.Dispose(); } catch { }
        }

        public void Dispose()
        {
            _stop = true;
            KillFfmpeg();
            try { _thread.Join(3000); } catch { }
        }
    }
}
