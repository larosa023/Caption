using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using Caption.Services;
using Caption.Subtitles;

namespace Caption
{
    /// <summary>
    /// "Caption.exe --subeval &lt;engineDir&gt; &lt;model&gt; &lt;media&gt; &lt;reference.srt&gt; &lt;outDir&gt;": makes subtitles for a
    /// whole file exactly the way Ripple's jobs do, then scores them against professional subtitles:
    /// lines caught, start/end timing, and layout. Used by CI to measure changes instead of guessing.
    /// Writes outDir/report.txt and outDir/ai.srt.
    /// </summary>
    internal static class SubEval
    {
        public static int Run(string[] args)
        {
            int i = Array.FindIndex(args, a => a == "--subeval");
            if (args.Length < i + 6) return 2;
            string engine = args[i + 1], model = args[i + 2], media = args[i + 3], refPath = args[i + 4], outDir = args[i + 5];
            Directory.CreateDirectory(outDir);
            var report = new StringBuilder();
            void Line(string s) { report.AppendLine(s); Console.WriteLine(s); File.WriteAllText(Path.Combine(outDir, "report.txt"), report.ToString()); }

            try
            {
                var settings = new AppSettings();
                settings.Normalize();
                using var subs = new SubtitleService(settings) { TestOverride = (engine, model) };
                var sw = Stopwatch.StartNew();
                var job = subs.StartAsync(new JobRequest { Url = media, Title = "eval", Position = 0, Language = "en" }).Result;
                // Keep "playback" just behind what's done so the job never waits.
                while (job.State != "done" && job.State != "error" && sw.Elapsed < TimeSpan.FromHours(3))
                {
                    Thread.Sleep(1000);
                    job.SetPosition(Math.Max(0, job.DoneUntil - 5));
                }
                Line($"job: {job.State} {job.Error} in {sw.Elapsed.TotalSeconds:0} s, done to {job.DoneUntil:0} s");
                var ai = job.Cues(0, 1e9);
                File.WriteAllText(Path.Combine(outDir, "ai.srt"), CueBuilder.ToSrt(ai));
                var refs = ParseSrt(File.ReadAllText(refPath));
                Score(refs, ai, Line);
                // Exact timing from the isolated dialogue: full mix minus the music-and-effects mix.
                int d = Array.FindIndex(args, a => a == "--dialogue");
                if (d >= 0 && args.Length > d + 2)
                    ScoreAgainstDialogue(ReadMono(args[d + 1]), ReadMono(args[d + 2]), ai, Line);
                return 0;
            }
            catch (Exception ex)
            {
                Line("ERROR: " + ex);
                return 1;
            }
        }

        private static float[] ReadMono(string path)
        {
            if (!Ffmpeg.IsInstalled) Ffmpeg.EnsureAsync(null, default).Wait();
            var p = Ffmpeg.StartAudio(path, null, null, 0, 0);
            p.BeginErrorReadLine();
            var s = p.StandardOutput.BaseStream;
            var ms = new MemoryStream();
            s.CopyTo(ms);
            p.WaitForExit();
            var f = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, float>(ms.GetBuffer().AsSpan(0, (int)(ms.Length / 8 * 8)));
            var mono = new float[f.Length / 2];
            for (int i = 0; i < mono.Length; i++) mono[i] = (f[2 * i] + f[2 * i + 1]) * 0.5f;
            return mono;
        }

        /// <summary>
        /// Speech on/off from the dialogue stem (10 ms frames), then each AI subtitle's start against the nearest
        /// speech onset and its end against the nearest speech end; plus how much speech has no subtitle at all.
        /// </summary>
        private static void ScoreAgainstDialogue(float[] mix, float[] musicFx, List<Cue> ai, Action<string> Line)
        {
            const int Rate = 16000, Hop = 160;
            int len = Math.Min(mix.Length, musicFx.Length);
            // Line the two up (they should already be): best lag within ±50 ms.
            int bestLag = 0; double bestE = double.MaxValue;
            for (int lag = -800; lag <= 800; lag += 8)
            {
                double e = 0;
                for (int i = Rate * 60; i < Rate * 90 && i + lag < len && i + lag >= 0; i += 4) { double v = mix[i] - musicFx[i + lag]; e += v * v; }
                if (e < bestE) { bestE = e; bestLag = lag; }
            }
            var dia = new float[len];
            for (int i = 0; i < len; i++) { int j = i + bestLag; dia[i] = mix[i] - (j >= 0 && j < len ? musicFx[j] : 0); }
            double Energy(float[] a) { double e = 0; foreach (var v in a) e += v * v; return Math.Sqrt(e / Math.Max(1, a.Length)); }
            Line($"\ndialogue stem: lag {bestLag / 16.0:0.0} ms, rms mix {20 * Math.Log10(Energy(mix) + 1e-9):0.0} dB, music+fx {20 * Math.Log10(Energy(musicFx) + 1e-9):0.0} dB, dialogue {20 * Math.Log10(Energy(dia) + 1e-9):0.0} dB");

            int frames = len / Hop;
            var db = new double[frames];
            for (int f = 0; f < frames; f++)
            {
                double e = 0;
                for (int i = f * Hop; i < f * Hop + 2 * Hop && i < len; i++) e += dia[i] * dia[i];
                db[f] = 10 * Math.Log10(e / (2 * Hop) + 1e-12);
            }
            var sorted = db.OrderBy(x => x).ToArray();
            double floor = sorted[(int)(frames * 0.3)], peak = sorted[(int)(frames * 0.99)];
            double thr = Math.Max(floor + 12, peak - 40);
            // Speech mask with 150 ms hangover and runs shorter than 80 ms dropped.
            var on = new bool[frames];
            for (int f = 0; f < frames; f++) if (db[f] > thr) for (int k = 0; k < 15 && f + k < frames; k++) on[f + k] = true;
            var runs = new List<(double A, double B)>();
            for (int f = 0; f < frames;)
            {
                if (!on[f]) { f++; continue; }
                int g = f; while (g < frames && on[g]) g++;
                double a = f * 0.01, b = (g - 15) * 0.01 + 0.02;
                if (b - a >= 0.08)
                {
                    if (runs.Count > 0 && a - runs[^1].B < 0.25) runs[^1] = (runs[^1].A, b); else runs.Add((a, b));
                }
                f = g;
            }
            Line($"dialogue: threshold {thr:0.0} dB (floor {floor:0.0}, peak {peak:0.0}), {runs.Count} speech runs, {runs.Sum(r => r.B - r.A):0} s of speech");

            var startErr = new List<double>();
            var endErr = new List<double>();
            foreach (var c in ai)
            {
                var onset = runs.Select(r => r.A).Where(t => Math.Abs(t - c.Start) < 1.5).OrderBy(t => Math.Abs(t - c.Start)).Cast<double?>().FirstOrDefault();
                if (onset is double o) startErr.Add(c.Start - o);
                // The speech end this subtitle covers: last run end inside it, or the first after its start.
                var ends = runs.Select(r => r.B).Where(t => t > c.Start + 0.2 && t < c.End + 2.0).ToList();
                var inside = ends.Where(t => t <= c.End + 0.2).ToList();
                double? e = inside.Count > 0 ? inside.Max() : ends.Count > 0 ? ends.Min() : null;
                if (e is double ee) endErr.Add(c.End - ee);
            }
            Line($"start vs speech onset ({startErr.Count}): {Stats(startErr)}");
            Line($"end vs speech end ({endErr.Count}): {Stats(endErr)}  (subtitles normally stay ~0.3–0.7 s past the voice)");

            // Speech with no subtitle on screen.
            double total = 0, uncovered = 0;
            var gaps = new List<string>();
            foreach (var r in runs)
            {
                if (r.B - r.A < 0.3) continue;
                total += r.B - r.A;
                double covered = 0;
                foreach (var c in ai) covered += Math.Max(0, Math.Min(r.B, c.End) - Math.Max(r.A, c.Start));
                double miss = Math.Max(0, r.B - r.A - covered);
                uncovered += miss;
                if (miss > 0.5 * (r.B - r.A) && r.B - r.A >= 0.4) gaps.Add($"  {Fmt(r.A)}–{Fmt(r.B)} ({r.B - r.A:0.0} s)");
            }
            Line($"speech with no subtitle: {uncovered:0.0} s of {total:0.0} s ({100 * uncovered / Math.Max(1, total):0.0}%), {gaps.Count} runs mostly uncovered:");
            foreach (var g in gaps) Line(g);
            // Subtitles up with nobody talking.
            double idle = 0;
            foreach (var c in ai)
            {
                double sp = runs.Sum(r => Math.Max(0, Math.Min(r.B, c.End) - Math.Max(r.A, c.Start)));
                idle += Math.Max(0, c.End - c.Start - sp);
            }
            Line($"subtitle time with no speech under it: {idle:0.0} s of {ai.Sum(c => c.End - c.Start):0.0} s");
        }

        public static List<Cue> ParseSrt(string text)
        {
            var cues = new List<Cue>();
            var blocks = Regex.Split(text.Replace("\r", ""), @"\n\s*\n");
            foreach (var b in blocks)
            {
                var lines = b.Split('\n').Where(l => l.Length > 0).ToList();
                int t = lines.FindIndex(l => l.Contains("-->"));
                if (t < 0) continue;
                var parts = lines[t].Split("-->");
                cues.Add(new Cue
                {
                    Start = ParseTime(parts[0]),
                    End = ParseTime(parts[1]),
                    Text = Regex.Replace(string.Join("\n", lines.Skip(t + 1)), "<[^>]+>|\\{[^}]+\\}", ""),
                });
            }
            return cues.OrderBy(c => c.Start).ToList();
        }

        private static double ParseTime(string s)
        {
            var m = Regex.Match(s, @"(\d+):(\d+):(\d+)[,.](\d+)");
            if (!m.Success) return 0;
            return int.Parse(m.Groups[1].Value) * 3600 + int.Parse(m.Groups[2].Value) * 60 + int.Parse(m.Groups[3].Value)
                + double.Parse("0." + m.Groups[4].Value, CultureInfo.InvariantCulture);
        }

        private static string[] Words(string s) =>
            Regex.Matches(s.ToLowerInvariant().Replace("’", "'"), @"[a-z0-9']+").Select(m => m.Value.Trim('\'')).Where(w => w.Length > 0).ToArray();

        private sealed class W { public string T = ""; public double Time; public int Cue; }

        private static List<W> Flatten(List<Cue> cues)
        {
            var list = new List<W>();
            for (int c = 0; c < cues.Count; c++)
            {
                var ws = Words(cues[c].Text);
                for (int k = 0; k < ws.Length; k++)
                {
                    // Spread words through the cue; only the first word's time is exact.
                    double t = cues[c].Start + (cues[c].End - cues[c].Start) * k / Math.Max(1, ws.Length);
                    list.Add(new W { T = ws[k], Time = t, Cue = c });
                }
            }
            return list;
        }

        public static void Score(List<Cue> refs, List<Cue> ai, Action<string> Line)
        {
            var rw = Flatten(refs);
            var aw = Flatten(ai);

            // Word alignment (LCS restricted to words within 10 s of each other).
            int n = rw.Count, m = aw.Count;
            var dp = new int[n + 1, m + 1];
            for (int x = n - 1; x >= 0; x--)
                for (int y = m - 1; y >= 0; y--)
                    dp[x, y] = rw[x].T == aw[y].T && Math.Abs(rw[x].Time - aw[y].Time) < 10
                        ? dp[x + 1, y + 1] + 1
                        : Math.Max(dp[x + 1, y], dp[x, y + 1]);
            var match = new int[n];
            Array.Fill(match, -1);
            for (int x = 0, y = 0; x < n && y < m;)
            {
                if (rw[x].T == aw[y].T && Math.Abs(rw[x].Time - aw[y].Time) < 10 && dp[x, y] == dp[x + 1, y + 1] + 1) { match[x] = y; x++; y++; }
                else if (dp[x + 1, y] >= dp[x, y + 1]) x++;
                else y++;
            }
            int matched = match.Count(v => v >= 0);
            Line($"words: reference {n}, ai {m}, matched {matched} → recall {100.0 * matched / Math.Max(1, n):0.0}%, precision {100.0 * matched / Math.Max(1, m):0.0}%");

            // Per reference line: caught if at least half its words were matched.
            int caught = 0;
            var startErr = new List<double>();
            var endErr = new List<double>();
            var missed = new List<string>();
            for (int c = 0; c < refs.Count; c++)
            {
                var idx = Enumerable.Range(0, n).Where(x => rw[x].Cue == c).ToList();
                if (idx.Count == 0) continue;
                int hit = idx.Count(x => match[x] >= 0);
                if (hit * 2 >= idx.Count) caught++;
                else missed.Add($"  {Fmt(refs[c].Start)} {refs[c].Text.Replace("\n", " ")}");
                // Start timing: the line's first word, if the AI has it as the first word of one of its subtitles.
                int f = idx[0];
                if (match[f] >= 0)
                {
                    var a = aw[match[f]];
                    if (a.Cue >= 0 && aw.FindIndex(w => w.Cue == a.Cue) == match[f]) startErr.Add(ai[a.Cue].Start - refs[c].Start);
                }
                int l = idx[^1];
                if (match[l] >= 0)
                {
                    var a = aw[match[l]];
                    if (aw.FindLastIndex(w => w.Cue == a.Cue) == match[l]) endErr.Add(ai[a.Cue].End - refs[c].End);
                }
            }
            int lines = refs.Count(r => Words(r.Text).Length > 0);
            Line($"lines: reference {lines}, caught {caught} ({100.0 * caught / Math.Max(1, lines):0.0}%), ai subtitles {ai.Count}");
            Line($"start timing (ai − reference, {startErr.Count} aligned): {Stats(startErr)}");
            Line($"end timing (ai − reference, {endErr.Count} aligned): {Stats(endErr)}");

            // Layout.
            var len = ai.Select(c => c.Text.Replace("\n", " ").Length).ToList();
            var dur = ai.Select(c => c.End - c.Start).ToList();
            var cps = ai.Select(c => c.Text.Replace("\n", "").Length / Math.Max(0.1, c.End - c.Start)).ToList();
            var rlen = refs.Select(c => c.Text.Replace("\n", " ").Length).ToList();
            Line($"ai layout: chars avg {Avg(len):0} (reference {Avg(rlen):0}), duration avg {Avg(dur):0.0} s (reference {Avg(refs.Select(c => c.End - c.Start)):0.0} s), " +
                 $"two-line {100.0 * ai.Count(c => c.Text.Contains('\n')) / Math.Max(1, ai.Count):0}%, over 20 cps {(ai.Count == 0 ? 0 : 100.0 * cps.Count(v => v > 20) / ai.Count):0}%, " +
                 $"over 7 s {dur.Count(d => d > 7)}, under 1 s {dur.Count(d => d < 0.99)}");
            Line($"missed lines ({missed.Count}):");
            foreach (var s in missed) Line(s);

            // Side by side, for reading.
            Line("\nside by side (reference | ai):");
            int ai0 = 0;
            foreach (var r in refs)
            {
                var near = ai.Where(a => a.End > r.Start - 0.5 && a.Start < r.End + 0.5).ToList();
                string aiText = string.Join("  ||  ", near.Select(a => $"[{Fmt(a.Start)}-{Fmt(a.End)}] {a.Text.Replace("\n", " / ")}"));
                Line($"[{Fmt(r.Start)}-{Fmt(r.End)}] {r.Text.Replace("\n", " / ")}\n        AI: {aiText}");
                ai0++;
            }
        }

        private static double Avg(IEnumerable<double> v) { var l = v.ToList(); return l.Count == 0 ? 0 : l.Average(); }
        private static double Avg(IEnumerable<int> v) { var l = v.ToList(); return l.Count == 0 ? 0 : l.Average(); }

        private static string Stats(List<double> v)
        {
            if (v.Count == 0) return "n/a";
            var abs = v.Select(Math.Abs).OrderBy(x => x).ToList();
            var s = v.OrderBy(x => x).ToList();
            return $"median {s[s.Count / 2]:+0.00;-0.00} s, mean |err| {abs.Average():0.00} s, " +
                   $"within 0.25 s {100.0 * abs.Count(x => x <= 0.25) / v.Count:0}%, within 0.5 s {100.0 * abs.Count(x => x <= 0.5) / v.Count:0}%, worst {abs[^1]:0.00} s";
        }

        private static string Fmt(double s) => TimeSpan.FromSeconds(Math.Max(0, s)).ToString(@"m\:ss\.f");
    }
}
