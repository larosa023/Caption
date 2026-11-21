using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.IO;
using System.Text;
using System.Threading;
using Caption.Audio;
using Caption.Captioning;
using Caption.Services;
using Caption.Whisper;

namespace Caption
{
    /// <summary>
    /// "Caption.exe --selftest &lt;engineDir&gt; &lt;model&gt; &lt;wav&gt; &lt;out.txt&gt; [vadModel]": used by CI to check the
    /// native interop, the worker process and the streaming pipeline end to end on Windows.
    /// Writes results to out.txt and returns 0 on success.
    /// </summary>
    internal static class SelfTest
    {
        public static int Run(string[] args)
        {
            int i = Array.FindIndex(args, a => a == "--selftest");
            if (args.Length < i + 5) return 2;
            string engine = args[i + 1], model = args[i + 2], wav = args[i + 3], outFile = args[i + 4];
            string vad = args.Length > i + 5 ? args[i + 5] : "";
            var report = new StringBuilder();
            int failures = 0;
            void Line(string s) { report.AppendLine(s); File.WriteAllText(outFile, report.ToString()); }

            try
            {
                var audio = ReadWav(wav);
                Line($"audio: {audio.Length / 16000.0:0.00} s");

                // 1) In process.
                var sw = Stopwatch.StartNew();
                using (var e = new WhisperEngine(engine, model, false, vad, 4))
                {
                    Line($"inproc load: {sw.ElapsedMilliseconds} ms, vad: {e.HasVad}, multilingual: {e.Multilingual}");
                    var r = e.Transcribe(new TranscribeRequest { Samples = audio, Count = audio.Length, Final = true });
                    Line($"inproc [{r.Language}] {r.Milliseconds} ms: {r.Text}");
                    if (!r.Text.Contains("country", StringComparison.OrdinalIgnoreCase)) failures++;
                    var probs = e.DetectSpeech(audio, audio.Length);
                    if (probs != null)
                    {
                        int speech = 0;
                        foreach (var p in probs) if (p > 0.5f) speech++;
                        Line($"vad: {probs.Length} frames, {speech} speech");
                        if (speech == 0) failures++;
                    }
                }

                // 1b) Dialogue focus on stereo audio must leave centered speech intact.
                {
                    var fmt = new NAudio.Wave.WaveFormat(16000, 16, 2);
                    var bytes = new byte[audio.Length * 4];
                    var rng = new Random(1);
                    for (int k = 0; k < audio.Length; k++)
                    {
                        // Speech in the center plus some wide, uncorrelated noise.
                        short l = (short)Math.Clamp((audio[k] + (float)(rng.NextDouble() - 0.5) * 0.15f) * 32767, -32768, 32767);
                        short r = (short)Math.Clamp((audio[k] + (float)(rng.NextDouble() - 0.5) * 0.15f) * 32767, -32768, 32767);
                        BitConverter.GetBytes(l).CopyTo(bytes, k * 4);
                        BitConverter.GetBytes(r).CopyTo(bytes, k * 4 + 2);
                    }
                    var conv = new SourceConverter(fmt, dialogueFocus: true);
                    var focused = conv.Process(bytes, bytes.Length);
                    using var e2 = new WhisperEngine(engine, model, false, vad, 4);
                    var fr = e2.Transcribe(new TranscribeRequest { Samples = focused, Count = focused.Length, Final = true });
                    Line($"dialogue focus [{fr.Language}]: {fr.Text}");
                    if (!fr.Text.Contains("country", StringComparison.OrdinalIgnoreCase)) failures++;
                }

                // 2) Through the worker process, then 3) the streaming pipeline.
                using var worker = new WorkerClient();
                var info = worker.Load(engine, model, false, vad, 4);
                var wr = worker.Transcribe(new TranscribeRequest { Samples = audio, Count = audio.Length, Final = true });
                Line($"worker [{wr.Language}] {wr.Milliseconds} ms: {wr.Text}");
                if (!wr.Text.Contains("country", StringComparison.OrdinalIgnoreCase)) failures++;

                var finished = new StringBuilder();
                int partials = 0;
                using (var pipeline = new CaptionPipeline(worker, info.HasVad, new PipelineOptions { SubtitleMode = true }))
                {
                    pipeline.Partial += (a, b) => { if (a.Length + b.Length > 0) Interlocked.Increment(ref partials); };
                    pipeline.Finished += f => { lock (finished) finished.Append('[').Append(f.Start.TotalSeconds.ToString("0.0")).Append("s] ").Append(f.Text).Append(' '); };
                    var stream = new float[audio.Length + 16000 * 2]; // + 2 s of silence to end the line
                    Array.Copy(audio, stream, audio.Length);
                    for (int pos = 0; pos < stream.Length; pos += 640)
                    {
                        var chunk = new float[Math.Min(640, stream.Length - pos)];
                        Array.Copy(stream, pos, chunk, 0, chunk.Length);
                        pipeline.Feed(chunk);
                        Thread.Sleep(20); // twice real time, so the reads overlap like they do live
                    }
                    var wait = Stopwatch.StartNew();
                    var idleFor = Stopwatch.StartNew();
                    while (wait.Elapsed < TimeSpan.FromSeconds(180))
                    {
                        Thread.Sleep(100);
                        if (!pipeline.Idle) idleFor.Restart();
                        else if (idleFor.ElapsedMilliseconds > 2500) break;
                    }
                }
                Line($"pipeline: {partials} partial updates; lines: {finished}");
                if (!finished.ToString().Contains("country", StringComparison.OrdinalIgnoreCase)) failures++;

                // 3b) Subtitles for apps, end to end over the local API: a 4x JFK "episode" with pauses.
                {
                    string media = Path.Combine(Path.GetDirectoryName(outFile)!, "episode.wav");
                    const int Gap = 3;
                    var starts = new List<double>();
                    var episode = new List<float>();
                    for (int k = 0; k < 4; k++)
                    {
                        starts.Add(episode.Count / 16000.0);
                        episode.AddRange(audio);
                        episode.AddRange(new float[16000 * Gap]);
                    }
                    WriteWav(media, episode.ToArray());
                    var settings = new AppSettings();
                    settings.Normalize();
                    using var subs = new Caption.Subtitles.SubtitleService(settings) { TestOverride = (engine, model) };
                    using var server = new Caption.Subtitles.SubtitleServer(subs);
                    server.Start();
                    using var http = new System.Net.Http.HttpClient { Timeout = TimeSpan.FromMinutes(5) };
                    string api = $"http://127.0.0.1:{Caption.Subtitles.SubtitleServer.Port}/v1";
                    Line("api health: " + http.GetStringAsync(api + "/health").Result);
                    var post = http.PostAsync(api + "/jobs", new System.Net.Http.StringContent(
                        System.Text.Json.JsonSerializer.Serialize(new { url = media, title = "Self-test", position = 0, language = "en" }),
                        Encoding.UTF8, "application/json")).Result;
                    string created = post.Content.ReadAsStringAsync().Result;
                    Line("api job: " + created);
                    string id = System.Text.Json.Nodes.JsonNode.Parse(created)?["id"]?.GetValue<string>() ?? "";
                    var sw2 = Stopwatch.StartNew();
                    string status = "";
                    while (sw2.Elapsed < TimeSpan.FromMinutes(4))
                    {
                        Thread.Sleep(1000);
                        status = http.GetStringAsync($"{api}/jobs/{id}").Result;
                        if (status.Contains("\"state\":\"done\"") || status.Contains("\"state\":\"error\"")) break;
                    }
                    Line($"api status after {sw2.Elapsed.TotalSeconds:0} s: {status}");
                    string srt = http.GetStringAsync($"{api}/jobs/{id}.srt").Result;
                    Line("srt:\n" + srt);
                    int country = 0;
                    foreach (var line in srt.Split('\n')) if (line.Contains("country", StringComparison.OrdinalIgnoreCase)) country++;
                    if (country < 4) { failures++; Line($"FAIL: expected 'country' in all 4 repeats, got {country}"); }
                    // Timing: each repeat's first subtitle must start within 0.6 s of the speech (JFK starts ~0.3 s in).
                    var cueStarts = new List<double>();
                    foreach (var line in srt.Split('\n'))
                        if (line.Contains("-->")) cueStarts.Add(TimeSpan.ParseExact(line.Split(" --> ")[0].Trim(), @"hh\:mm\:ss\,fff", System.Globalization.CultureInfo.InvariantCulture).TotalSeconds);
                    foreach (var st in starts)
                    {
                        double nearest = cueStarts.Count == 0 ? 99 : cueStarts.Min(c => Math.Abs(c - (st + 0.3)));
                        Line($"repeat at {st:0.0}s: nearest subtitle start off by {nearest:0.00}s");
                        if (nearest > 0.6) failures++;
                    }
                    http.DeleteAsync($"{api}/jobs/{id}").Wait();
                }

                // 4) Resampler + filters sanity.
                var rs = new Resampler(48000);
                int n = rs.Process(new float[48000]).Length;
                Line($"resampler 48k→16k: {n} samples");
                if (Math.Abs(n - 16000) > 40) failures++;
                string clean = TextFilter.Clean(" [Music] Hello hello hello hello hello world ", false);
                Line($"filter: '{clean}'");
                Line(UpdateService.CompareVersions("0.2.0", "0.2.0-beta.1") > 0 && UpdateService.CompareVersions("0.1.10", "0.1.9") > 0 ? "versions: ok" : "versions: FAIL");
            }
            catch (Exception ex)
            {
                failures++;
                Line("ERROR: " + ex);
            }
            Line(failures == 0 ? "SELFTEST PASSED" : $"SELFTEST FAILED ({failures})");
            return failures == 0 ? 0 : 1;
        }

        private static void WriteWav(string path, float[] samples)
        {
            using var w = new BinaryWriter(File.Create(path));
            int bytes = samples.Length * 2;
            w.Write(Encoding.ASCII.GetBytes("RIFF")); w.Write(36 + bytes); w.Write(Encoding.ASCII.GetBytes("WAVE"));
            w.Write(Encoding.ASCII.GetBytes("fmt ")); w.Write(16); w.Write((short)1); w.Write((short)1); w.Write(16000); w.Write(32000); w.Write((short)2); w.Write((short)16);
            w.Write(Encoding.ASCII.GetBytes("data")); w.Write(bytes);
            foreach (var f in samples) w.Write((short)Math.Clamp(f * 32767, -32768, 32767));
        }

        /// <summary>16-bit PCM WAV → 16 kHz mono float.</summary>
        public static float[] ReadWav(string path)
        {
            using var r = new BinaryReader(File.OpenRead(path));
            if (new string(r.ReadChars(4)) != "RIFF") throw new InvalidDataException("not a WAV");
            r.ReadInt32();
            r.ReadChars(4);
            int channels = 1, rate = 16000, bits = 16;
            while (r.BaseStream.Position < r.BaseStream.Length)
            {
                string id = new string(r.ReadChars(4));
                int size = r.ReadInt32();
                if (id == "fmt ")
                {
                    r.ReadInt16();
                    channels = r.ReadInt16();
                    rate = r.ReadInt32();
                    r.ReadInt32();
                    r.ReadInt16();
                    bits = r.ReadInt16();
                    if (size > 16) r.ReadBytes(size - 16);
                }
                else if (id == "data")
                {
                    var bytes = r.ReadBytes(size);
                    var fmt = new NAudio.Wave.WaveFormat(rate, bits, channels);
                    var mono = AudioSource.ToMono(bytes, bytes.Length, fmt);
                    return new Resampler(rate).Process(mono);
                }
                else
                {
                    r.ReadBytes(size + (size & 1));
                }
            }
            throw new InvalidDataException("no data chunk");
        }
    }
}
