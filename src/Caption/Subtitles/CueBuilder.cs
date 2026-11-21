using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using Caption.Captioning;
using Caption.Views;

namespace Caption.Subtitles
{
    public sealed class Cue
    {
        public double Start;
        public double End;
        /// <summary>Up to two lines, joined with '\n'.</summary>
        public string Text = "";
    }

    public sealed class TimedWord
    {
        public string Text = "";
        public double Start;
        public double End;
        public float Prob;
        /// <summary>Layout: a sentence ends here and the next one goes on the second line.</summary>
        public bool SentenceBreak;
    }

    /// <summary>
    /// Turns timed words into subtitles the way professional subtitlers lay them out: one sentence (or clause)
    /// per subtitle, at most two balanced lines of 42 characters, on screen from the first word to just after the
    /// last, never shorter than a second, never overlapping the next.
    /// </summary>
    public static class CueBuilder
    {
        public const int MaxLine = 42;
        private const int MaxChars = MaxLine * 2;
        private const double MaxDuration = 6.5;
        private const double PauseBreak = 0.8;

        public static List<Cue> Build(IReadOnlyList<TimedWord> words)
        {
            var groups = new List<List<TimedWord>>();
            var cur = new List<TimedWord>();
            int chars = 0;
            for (int i = 0; i < words.Count; i++)
            {
                var w = words[i];
                if (cur.Count > 0)
                {
                    double gap = w.Start - cur[^1].End;
                    bool full = chars + 1 + w.Text.Length > MaxChars;
                    bool tooLong = w.End - cur[0].Start > MaxDuration;
                    if (gap > PauseBreak || full || tooLong)
                    {
                        // Full: break after the last comma instead of mid-phrase, if there is one.
                        int k = -1;
                        if (gap <= PauseBreak)
                            for (int c = cur.Count - 2; c >= 1; c--)
                                if (cur[c].Text[^1] is ',' or ';' or ':' or '—' or '，' or '、') { k = c; break; }
                        if (k > 0)
                        {
                            groups.Add(cur.GetRange(0, k + 1));
                            cur = cur.GetRange(k + 1, cur.Count - k - 1);
                        }
                        else
                        {
                            groups.Add(cur);
                            cur = new List<TimedWord>();
                        }
                        chars = cur.Sum(x => x.Text.Length) + Math.Max(0, cur.Count - 1);
                    }
                }
                cur.Add(w);
                chars += (chars > 0 ? 1 : 0) + w.Text.Length;

                char last = w.Text[^1];
                bool sentenceEnd = IsSentenceEnd(w.Text);
                bool clause = last is ',' or ';' or ':' or '—' or '、' or '，';
                var next = i + 1 < words.Count ? words[i + 1] : null;
                if (sentenceEnd)
                {
                    // A short sentence followed straight away by another short one ("Are you ready? Of course you're
                    // ready.") shares a subtitle, one sentence per line, so quick exchanges don't flash past.
                    bool merge = false;
                    if (next != null && chars < 24 && next.Start - w.End < 0.4 && !cur.Any(x => x.SentenceBreak))
                    {
                        int nchars = 0; double nEnd = next.End;
                        for (int k = i + 1; k < words.Count; k++)
                        {
                            nchars += (nchars > 0 ? 1 : 0) + words[k].Text.Length;
                            nEnd = words[k].End;
                            if (IsSentenceEnd(words[k].Text) || (k + 1 < words.Count && words[k + 1].Start - words[k].End > PauseBreak)) break;
                        }
                        merge = nchars <= MaxLine && chars + 1 + nchars <= 64 && nEnd - cur[0].Start <= 4.5;
                    }
                    if (merge) w.SentenceBreak = true;
                    else { groups.Add(cur); cur = new List<TimedWord>(); chars = 0; }
                }
                else if (clause && chars >= 50)
                {
                    groups.Add(cur); cur = new List<TimedWord>(); chars = 0;
                }
            }
            if (cur.Count > 0) groups.Add(cur);

            var cues = new List<Cue>();
            foreach (var g in groups)
            {
                string text = TextFilter.CollapseRepeats(string.Join(' ', g.Select(x => x.Text)));
                if (!text.Any(char.IsLetterOrDigit)) continue;
                // Shown a touch before the voice (the eye needs a moment), held a little after it, and long enough
                // to read (17 characters a second).
                double start = Math.Max(0, g[0].Start - 0.1);
                double minByReading = Math.Max(1.0, text.Length / 17.0);
                double end = Math.Max(g[^1].End + 0.4, start + minByReading);
                int sb = g.FindIndex(x => x.SentenceBreak);
                string laid = sb >= 0 && sb < g.Count - 1
                    ? string.Join(' ', g.Take(sb + 1).Select(x => x.Text)) + "\n" + string.Join(' ', g.Skip(sb + 1).Select(x => x.Text))
                    : Layout(text);
                if (laid.Split('\n').Any(l => l.Length > MaxLine)) laid = Layout(text);
                cues.Add(new Cue { Start = start, End = Math.Min(end, start + 7.0), Text = laid });
            }
            FixTiming(cues);
            return cues;
        }

        public static bool IsSentenceEnd(string t) =>
            t.Length > 0 && (t[^1] is '.' or '?' or '!' or '…' or '。' or '？' or '！' || t.EndsWith("\"") && t.Length > 1 && t[^2] is '.' or '?' or '!');

        /// <summary>
        /// No overlaps: each subtitle ends two frames before the next starts. A short gap (under 0.6 s) is closed
        /// the same way, so the screen doesn't blink empty between lines of the same conversation.
        /// </summary>
        public static void FixTiming(List<Cue> cues)
        {
            cues.Sort((a, b) => a.Start.CompareTo(b.Start));
            for (int i = 0; i + 1 < cues.Count; i++)
            {
                double limit = cues[i + 1].Start - 0.084;
                if (cues[i].End > limit) cues[i].End = Math.Max(cues[i].Start + 0.3, limit);
                else if (limit - cues[i].End < 0.6 && limit - cues[i].Start <= 7.0) cues[i].End = limit;
            }
        }

        // Words a line shouldn't end on (the phrase continues on the next line), and words it's good to start one with.
        private static readonly HashSet<string> NoEnd = new(StringComparer.OrdinalIgnoreCase)
        {
            "a", "an", "the", "to", "of", "in", "on", "at", "for", "with", "from", "by", "into", "about", "as",
            "my", "your", "his", "her", "its", "our", "their", "this", "these", "those", "that's",
            "i", "i'm", "i've", "i'll", "i'd", "you're", "we're", "they're", "is", "are", "was", "were", "be", "been",
            "and", "but", "or", "not", "no", "very", "so", "if", "than", "mr.", "mrs.", "ms.", "dr.",
        };
        private static readonly HashSet<string> GoodStart = new(StringComparer.OrdinalIgnoreCase)
        {
            "and", "but", "or", "so", "because", "that", "which", "who", "when", "where", "while", "if", "unless",
            "to", "for", "with", "about", "from", "you're", "i'm", "we", "they", "he", "she", "it's",
        };

        /// <summary>
        /// One line if it fits in 42 characters; otherwise two, broken where a subtitler would: after punctuation,
        /// before a conjunction or preposition, never after "the", "a", "my"…, and with the lines close in length.
        /// </summary>
        public static string Layout(string text)
        {
            var words = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (text.Length <= MaxLine || words.Length < 2) return text;
            int best = -1;
            double bestScore = double.MaxValue;
            for (int b = 1; b < words.Length; b++)
            {
                int left = string.Join(' ', words, 0, b).Length, right = text.Length - left - 1;
                if (left > MaxLine || right > MaxLine) continue;
                string last = words[b - 1], first = words[b];
                double score = Math.Abs(left - right);
                if (IsSentenceEnd(last)) score -= 24;
                else if (last[^1] is ',' or ';' or ':' or '—') score -= 14;
                if (NoEnd.Contains(last.TrimEnd(',', '.', '!', '?'))) score += 16;
                if (GoodStart.Contains(first)) score -= 5;
                if (left > right) score += 2; // slightly bottom-heavy reads better
                if (score < bestScore) { bestScore = score; best = b; }
            }
            if (best < 0) return CaptionBarWindow.FormatSubtitle(text, int.MaxValue, MaxLine).Text;
            return string.Join(' ', words, 0, best) + "\n" + string.Join(' ', words, best, words.Length - best);
        }

        public static string ToSrt(IEnumerable<Cue> cues)
        {
            var sb = new StringBuilder();
            int n = 1;
            foreach (var c in cues)
            {
                sb.Append(n++).Append('\n');
                sb.Append(Time(c.Start)).Append(" --> ").Append(Time(c.End)).Append('\n');
                sb.Append(c.Text.Replace("\r", "")).Append("\n\n");
            }
            return sb.ToString();
        }

        private static string Time(double s)
        {
            var t = TimeSpan.FromMilliseconds(Math.Round(Math.Max(0, s) * 1000));
            return string.Format(CultureInfo.InvariantCulture, "{0:00}:{1:00}:{2:00},{3:000}", (int)t.TotalHours, t.Minutes, t.Seconds, t.Milliseconds);
        }
    }
}
