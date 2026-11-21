using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace Caption.Captioning
{
    /// <summary>
    /// Cleans what Whisper returns: drops the phrases it invents over silence or music ("Thanks for watching!"),
    /// optional sound labels like [Music], and runaway repetition loops.
    /// </summary>
    public static class TextFilter
    {
        private static readonly Regex Labels = new(@"\[[^\]]*\]|\([^\)]*\)|\*[^\*]*\*|[♪♫]+", RegexOptions.Compiled);
        private static readonly Regex Blank = new(@"\[\s*(BLANK_AUDIO|blank_audio|SILENCE|silence|NO_SPEECH|inaudible)\s*\]", RegexOptions.Compiled);
        private static readonly Regex Spaces = new(@"\s{2,}", RegexOptions.Compiled);

        // Whisper's well-known inventions, compared after lower-casing and dropping punctuation.
        private static readonly HashSet<string> Inventions = new(StringComparer.Ordinal)
        {
            "thank you", "thank you very much", "thank you so much", "thanks", "thanks for watching",
            "thank you for watching", "thank you so much for watching", "thanks for watching and see you next time",
            "please subscribe", "subscribe", "like and subscribe", "please like and subscribe", "you", "bye", "bye bye",
            "so", "okay", "oh", "the end", "music", "applause", "silence", "see you next time", "see you in the next video",
            "subtitles by the amara org community", "transcription by castingwords", "transcribed by https otter ai",
            "thank you for listening", "i ll see you next time", "ご視聴ありがとうございました", "おやすみなさい",
            "字幕由amara org社区提供", "продолжение следует", "субтитры сделал dimatorzok", "untertitel im auftrag des zdf für funk 2017",
            "untertitel der amara org community", "sous titrage société radio canada", "sous titres réalisés para la communauté d amara org",
            "gracias por ver", "gracias", "obrigado", "merci", "danke", "감사합니다", "시청해주셔서 감사합니다", "谢谢观看", "謝謝觀看",
        };

        public static string Clean(string text, bool keepSoundLabels)
        {
            if (string.IsNullOrEmpty(text)) return "";
            string s = Blank.Replace(text, " ");
            if (!keepSoundLabels) s = Labels.Replace(s, " ");
            s = s.Replace(">>", " ").Replace('\n', ' ');
            s = Spaces.Replace(s, " ").Trim();
            s = CollapseRepeats(s);
            // Leftover punctuation with no words ("...", "-") isn't worth showing.
            if (!s.Any(char.IsLetterOrDigit)) return "";
            return s;
        }

        /// <summary>True for a line that's almost certainly invented (only checked for short / quiet / unsure lines).</summary>
        public static bool IsLikelyInvented(string text, float noSpeechProb, double seconds, float rms)
        {
            string key = Normalize(text);
            if (key.Length == 0) return true;
            if (Inventions.Contains(key))
                return noSpeechProb > 0.15f || rms < 0.012f || seconds < 1.0;
            // Anything Whisper is very sure isn't speech, over quiet audio.
            return noSpeechProb > 0.85f && rms < 0.02f;
        }

        public static string Normalize(string text)
        {
            var sb = new StringBuilder(text.Length);
            foreach (char c in text.ToLowerInvariant())
                sb.Append(char.IsLetterOrDigit(c) ? c : ' ');
            return Spaces.Replace(sb.ToString(), " ").Trim();
        }

        /// <summary>"no no no no no no" → "no no"; also catches repeated phrases up to 8 words.</summary>
        public static string CollapseRepeats(string s)
        {
            var words = s.Split(' ', StringSplitOptions.RemoveEmptyEntries).ToList();
            if (words.Count < 6) return s;
            bool changed = false;
            for (int n = 1; n <= 8; n++)
            {
                for (int i = 0; i + n * 3 <= words.Count; i++)
                {
                    int reps = 1;
                    while (i + (reps + 1) * n <= words.Count && Same(words, i, i + reps * n, n)) reps++;
                    if (reps >= 3)
                    {
                        words.RemoveRange(i + 2 * n, (reps - 2) * n);
                        changed = true;
                    }
                }
            }
            return changed ? string.Join(' ', words) : s;
        }

        private static bool Same(List<string> w, int a, int b, int n)
        {
            for (int k = 0; k < n; k++)
                if (!string.Equals(Strip(w[a + k]), Strip(w[b + k]), StringComparison.OrdinalIgnoreCase)) return false;
            return true;
        }

        private static string Strip(string w) => w.Trim(',', '.', '!', '?', ';', ':', '"', '\'');
    }
}
