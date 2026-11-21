using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Text;
using Caption.ViewModels;

namespace Caption.Captioning
{
    /// <summary>One finished caption line.</summary>
    public sealed class CaptionLine : ObservableObject
    {
        private string _text = "";
        private string? _translation;

        public int Number { get; init; }
        public DateTime Wall { get; init; }
        /// <summary>From the start of the session (for .srt).</summary>
        public TimeSpan Start { get; init; }
        public TimeSpan End { get; init; }
        public string Language { get; init; } = "";
        /// <summary>The source-language text when the main text is an English translation.</summary>
        public string? Original { get; init; }

        public string Text { get => _text; set { if (SetProperty(ref _text, value)) OnPropertyChanged(nameof(Display)); } }

        /// <summary>Ollama translation, filled in a moment after the line is committed.</summary>
        public string? Translation
        {
            get => _translation;
            set
            {
                if (SetProperty(ref _translation, value))
                {
                    OnPropertyChanged(nameof(Display));
                    OnPropertyChanged(nameof(Secondary));
                }
            }
        }

        public string TimeText => Wall.ToString("HH:mm:ss");

        /// <summary>What the caption bar shows as the main line.</summary>
        public string Display => Translation ?? Text;

        /// <summary>Smaller line under the main one in the transcript (original text when translated).</summary>
        public string? Secondary => Translation != null ? Text : Original;

        public string LanguageName => Languages.Name(Language);
    }

    /// <summary>The running transcript (UI thread only) plus .txt / .srt export.</summary>
    public sealed class Transcript
    {
        public ObservableCollection<CaptionLine> Lines { get; } = new();

        public DateTime StartedAt { get; private set; } = DateTime.Now;

        public void Clear()
        {
            Lines.Clear();
            StartedAt = DateTime.Now;
        }

        public void Add(CaptionLine line)
        {
            if (Lines.Count == 0) StartedAt = line.Wall - line.End;
            Lines.Add(line);
            // Keep memory bounded on all-day sessions.
            while (Lines.Count > 20000) Lines.RemoveAt(0);
        }

        public static string ToText(System.Collections.Generic.IEnumerable<CaptionLine> lines, bool timestamps = true)
        {
            var sb = new StringBuilder();
            foreach (var l in lines)
            {
                if (timestamps) sb.Append('[').Append(l.TimeText).Append("] ");
                sb.AppendLine(l.Display);
                if (l.Secondary != null) sb.Append(timestamps ? "           " : "").AppendLine("(" + l.Secondary + ")");
            }
            return sb.ToString();
        }

        public static string ToSrt(System.Collections.Generic.IEnumerable<CaptionLine> lines)
        {
            var sb = new StringBuilder();
            int n = 1;
            TimeSpan lastEnd = TimeSpan.Zero;
            foreach (var l in lines)
            {
                var start = l.Start < lastEnd ? lastEnd : l.Start;
                var end = l.End <= start ? start + TimeSpan.FromSeconds(1.5) : l.End;
                lastEnd = end;
                sb.AppendLine((n++).ToString());
                sb.Append(Srt(start)).Append(" --> ").AppendLine(Srt(end));
                sb.AppendLine(l.Display);
                if (l.Secondary != null) sb.AppendLine(l.Secondary);
                sb.AppendLine();
            }
            return sb.ToString();
        }

        private static string Srt(TimeSpan t) =>
            $"{(int)t.TotalHours:00}:{t.Minutes:00}:{t.Seconds:00},{t.Milliseconds:000}";

        public void Save(string path)
        {
            string text = path.EndsWith(".srt", StringComparison.OrdinalIgnoreCase) ? ToSrt(Lines) : ToText(Lines);
            File.WriteAllText(path, text, new UTF8Encoding(true));
        }
    }

    public static class Languages
    {
        // Whisper's language codes → names (the common ones; others show their code).
        private static readonly (string Code, string Name)[] Table =
        {
            ("en", "English"), ("zh", "Chinese"), ("de", "German"), ("es", "Spanish"), ("ru", "Russian"), ("ko", "Korean"),
            ("fr", "French"), ("ja", "Japanese"), ("pt", "Portuguese"), ("tr", "Turkish"), ("pl", "Polish"), ("ca", "Catalan"),
            ("nl", "Dutch"), ("ar", "Arabic"), ("sv", "Swedish"), ("it", "Italian"), ("id", "Indonesian"), ("hi", "Hindi"),
            ("fi", "Finnish"), ("vi", "Vietnamese"), ("he", "Hebrew"), ("uk", "Ukrainian"), ("el", "Greek"), ("ms", "Malay"),
            ("cs", "Czech"), ("ro", "Romanian"), ("da", "Danish"), ("hu", "Hungarian"), ("ta", "Tamil"), ("no", "Norwegian"),
            ("th", "Thai"), ("ur", "Urdu"), ("hr", "Croatian"), ("bg", "Bulgarian"), ("lt", "Lithuanian"), ("la", "Latin"),
            ("cy", "Welsh"), ("sk", "Slovak"), ("te", "Telugu"), ("fa", "Persian"), ("lv", "Latvian"), ("bn", "Bengali"),
            ("sr", "Serbian"), ("az", "Azerbaijani"), ("sl", "Slovenian"), ("kn", "Kannada"), ("et", "Estonian"),
            ("mk", "Macedonian"), ("is", "Icelandic"), ("hy", "Armenian"), ("ne", "Nepali"), ("mn", "Mongolian"),
            ("bs", "Bosnian"), ("kk", "Kazakh"), ("sq", "Albanian"), ("sw", "Swahili"), ("gl", "Galician"), ("mr", "Marathi"),
            ("pa", "Punjabi"), ("tl", "Tagalog"), ("af", "Afrikaans"), ("ka", "Georgian"), ("be", "Belarusian"),
            ("yue", "Cantonese"), ("ml", "Malayalam"), ("tg", "Tajik"), ("uz", "Uzbek"), ("fil", "Filipino"),
        };

        public static string Name(string code)
        {
            if (string.IsNullOrEmpty(code)) return "";
            foreach (var (c, n) in Table) if (c == code) return n;
            return code;
        }

        /// <summary>Spoken-language choices for profiles: Auto first, then by name.</summary>
        public static (string Code, string Name)[] SpokenChoices { get; } =
            new[] { ("auto", "Detect automatically") }.Concat(Table.OrderBy(t => t.Name)).ToArray();

        /// <summary>Target languages for the Ollama step.</summary>
        public static string[] TranslationTargets { get; } =
            Table.Select(t => t.Name).Where(n => n != "Latin").OrderBy(n => n)
                .Concat(new[] { "Chinese (Traditional)", "Brazilian Portuguese" }).OrderBy(n => n).ToArray();

        public static string? CodeForName(string name)
        {
            foreach (var (c, n) in Table) if (string.Equals(n, name, StringComparison.OrdinalIgnoreCase)) return c;
            return null;
        }
    }
}
