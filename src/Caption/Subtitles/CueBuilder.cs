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
                        // When the subtitle is full, move a dangling short clause start to the next one.
                        groups.Add(cur);
                        cur = new List<TimedWord>();
                        chars = 0;
                    }
                }
                cur.Add(w);
                chars += (chars > 0 ? 1 : 0) + w.Text.Length;

                char last = w.Text[^1];
                bool sentenceEnd = last is '.' or '?' or '!' or '…' or '。' or '？' or '！' || w.Text.EndsWith("\"") && w.Text.Length > 1 && w.Text[^2] is '.' or '?' or '!';
                bool clause = last is ',' or ';' or ':' or '—' or '、' or '，';
                var next = i + 1 < words.Count ? words[i + 1] : null;
                if (sentenceEnd)
                {
                    // Very short exchanges ("Yes." "No.") said back to back can share a subtitle.
                    bool shortAndQuick = chars < 16 && next != null && next.Start - w.End < 0.3 && next.End - cur[0].Start < 2.5;
                    if (!shortAndQuick) { groups.Add(cur); cur = new List<TimedWord>(); chars = 0; }
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
                double start = Math.Max(0, g[0].Start - 0.05);
                double minByReading = Math.Max(1.0, text.Length / 21.0);
                double end = Math.Max(g[^1].End + 0.35, start + minByReading);
                cues.Add(new Cue { Start = start, End = Math.Min(end, start + 7.0), Text = CaptionBarWindow.FormatSubtitle(text, int.MaxValue, MaxLine).Text });
            }
            FixTiming(cues);
            return cues;
        }

        /// <summary>No overlaps: each subtitle ends two frames before the next starts.</summary>
        public static void FixTiming(List<Cue> cues)
        {
            cues.Sort((a, b) => a.Start.CompareTo(b.Start));
            for (int i = 0; i + 1 < cues.Count; i++)
            {
                double limit = cues[i + 1].Start - 0.084;
                if (cues[i].End > limit) cues[i].End = Math.Max(cues[i].Start + 0.3, limit);
            }
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
