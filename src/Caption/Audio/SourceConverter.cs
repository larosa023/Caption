using System;
using NAudio.Dsp;
using NAudio.Wave;

namespace Caption.Audio
{
    /// <summary>
    /// Turns a source's device audio into the 16 kHz mono Whisper hears. With "focus on dialogue" on:
    /// surround audio uses the center channel (where films put dialogue); stereo goes through a center
    /// extractor that keeps what's the same in both channels (voices) and turns down what isn't
    /// (wide music, effects, ambience); and rumble below 90 Hz is cut.
    /// </summary>
    public sealed class SourceConverter
    {
        private readonly WaveFormat _format;
        private readonly bool _focus;
        private readonly int _channels;
        private readonly Resampler _mono;
        private readonly Resampler? _left, _right;
        private readonly CenterExtractor? _center;
        private readonly HighPass? _highPass;

        public SourceConverter(WaveFormat format, bool dialogueFocus)
        {
            _format = format;
            _focus = dialogueFocus;
            _channels = Math.Max(1, format.Channels);
            _mono = new Resampler(format.SampleRate);
            if (_focus && _channels == 2)
            {
                _left = new Resampler(format.SampleRate);
                _right = new Resampler(format.SampleRate);
                _center = new CenterExtractor();
            }
            if (_focus) _highPass = new HighPass(90, 16000);
        }

        public float[] Process(byte[] buffer, int bytes)
        {
            var ch = AudioSource.Deinterleave(buffer, bytes, _format);
            if (ch.Length == 0 || ch[0].Length == 0) return Array.Empty<float>();
            float[] output;
            if (_center != null && ch.Length == 2)
            {
                var l = _left!.Process(ch[0]);
                var r = _right!.Process(ch[1]);
                int n = Math.Min(l.Length, r.Length);
                output = _center.Process(l, r, n);
            }
            else
            {
                int frames = ch[0].Length;
                var mono = new float[frames];
                if (_focus && ch.Length >= 6)
                {
                    // 5.1 / 7.1: front-left, front-right, center, LFE, ... Dialogue lives in the center.
                    for (int i = 0; i < frames; i++) mono[i] = ch[2][i] + 0.2f * (ch[0][i] + ch[1][i]) * 0.5f;
                }
                else
                {
                    float scale = 1f / ch.Length;
                    for (int c = 0; c < ch.Length; c++)
                        for (int i = 0; i < frames; i++) mono[i] += ch[c][i] * scale;
                }
                output = _mono.Process(mono);
            }
            _highPass?.Process(output);
            return output;
        }
    }

    /// <summary>
    /// Short-time Fourier "center extraction": per frequency bin, keep the part of the sound that's identical
    /// in left and right. Identical channels pass through unchanged; uncorrelated or hard-panned sound is
    /// reduced by up to 20 dB. 24 ms of latency.
    /// </summary>
    public sealed class CenterExtractor
    {
        private const int N = 512, Hop = 128, Bins = N / 2 + 1, Log2N = 9;
        /// <summary>How many samples the output trails the input.</summary>
        public const int Latency = N - Hop;
        private readonly float[] _window = new float[N];
        private readonly float[] _inL = new float[N], _inR = new float[N];
        private readonly float[] _out = new float[N];
        private readonly float[] _gain = new float[Bins];
        private readonly Complex[] _fl = new Complex[N], _fr = new Complex[N];
        private int _fill = N - Hop;

        public CenterExtractor()
        {
            for (int i = 0; i < N; i++) _window[i] = (float)Math.Sqrt(0.5 - 0.5 * Math.Cos(2 * Math.PI * i / N));
            for (int k = 0; k < Bins; k++) _gain[k] = 1f;
        }

        public float[] Process(float[] left, float[] right, int count)
        {
            var result = new float[(count / Hop + 2) * Hop];
            int produced = 0;
            for (int i = 0; i < count; i++)
            {
                _inL[_fill] = left[i];
                _inR[_fill] = right[i];
                if (++_fill < N) continue;

                for (int j = 0; j < N; j++)
                {
                    _fl[j].X = _inL[j] * _window[j]; _fl[j].Y = 0;
                    _fr[j].X = _inR[j] * _window[j]; _fr[j].Y = 0;
                }
                FastFourierTransform.FFT(true, Log2N, _fl);
                FastFourierTransform.FFT(true, Log2N, _fr);
                for (int k = 0; k < Bins; k++)
                {
                    float lx = _fl[k].X, ly = _fl[k].Y, rx = _fr[k].X, ry = _fr[k].Y;
                    float power = lx * lx + ly * ly + rx * rx + ry * ry + 1e-20f;
                    float sim = 2f * (lx * rx + ly * ry) / power;          // 1 = same in both channels
                    sim = Math.Clamp(sim, 0f, 1f);
                    float g = Math.Max(sim * sim, 0.1f);
                    _gain[k] = 0.5f * _gain[k] + 0.5f * g;               // smooth over time (less "musical noise")
                    float gk = _gain[k] * 0.5f;
                    float mx = (lx + rx) * gk, my = (ly + ry) * gk;
                    _fl[k].X = mx; _fl[k].Y = my;
                    if (k > 0 && k < N / 2) { _fl[N - k].X = mx; _fl[N - k].Y = -my; }
                }
                FastFourierTransform.FFT(false, Log2N, _fl);
                for (int j = 0; j < N; j++) _out[j] += _fl[j].X * _window[j] * 0.5f;

                Array.Copy(_out, 0, result, produced, Hop);
                produced += Hop;
                Array.Copy(_out, Hop, _out, 0, N - Hop);
                Array.Clear(_out, N - Hop, Hop);
                Array.Copy(_inL, Hop, _inL, 0, N - Hop);
                Array.Copy(_inR, Hop, _inR, 0, N - Hop);
                _fill = N - Hop;
            }
            if (produced == result.Length) return result;
            var trimmed = new float[produced];
            Array.Copy(result, trimmed, produced);
            return trimmed;
        }
    }

    /// <summary>Second-order high-pass (Butterworth) for cutting rumble and LFE spill.</summary>
    public sealed class HighPass
    {
        private readonly float _b0, _b1, _b2, _a1, _a2;
        private float _x1, _x2, _y1, _y2;

        public HighPass(double cutoff, double rate)
        {
            double w = 2 * Math.PI * cutoff / rate, cos = Math.Cos(w), alpha = Math.Sin(w) / (2 * Math.Sqrt(0.5));
            double a0 = 1 + alpha;
            _b0 = (float)((1 + cos) / 2 / a0);
            _b1 = (float)(-(1 + cos) / a0);
            _b2 = _b0;
            _a1 = (float)(-2 * cos / a0);
            _a2 = (float)((1 - alpha) / a0);
        }

        public void Process(float[] data)
        {
            for (int i = 0; i < data.Length; i++)
            {
                float x = data[i];
                float y = _b0 * x + _b1 * _x1 + _b2 * _x2 - _a1 * _y1 - _a2 * _y2;
                _x2 = _x1; _x1 = x; _y2 = _y1; _y1 = y;
                data[i] = y;
            }
        }
    }
}
