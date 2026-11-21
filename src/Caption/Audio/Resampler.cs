using System;

namespace Caption.Audio
{
    /// <summary>
    /// Streaming mono resampler to 16 kHz (what Whisper wants): a windowed-sinc low-pass interpolator,
    /// so 44.1/48 kHz audio comes down without aliasing. Keeps state between blocks.
    /// </summary>
    public sealed class Resampler
    {
        private const int HalfTaps = 16;
        private readonly double _step;     // input samples per output sample
        private readonly double _cutoff;   // relative to input Nyquist
        private float[] _history = new float[0];
        private double _pos;               // position of the next output sample, in history coordinates

        public int InputRate { get; }
        public int OutputRate { get; }

        public Resampler(int inputRate, int outputRate = 16000)
        {
            InputRate = inputRate;
            OutputRate = outputRate;
            _step = (double)inputRate / outputRate;
            _cutoff = Math.Min(1.0, (double)outputRate / inputRate) * 0.92;
            _history = new float[HalfTaps * 2];
            _pos = HalfTaps;
        }

        /// <summary>Resamples a block of mono samples; returns the 16 kHz output for it.</summary>
        public float[] Process(ReadOnlySpan<float> input)
        {
            if (InputRate == OutputRate)
                return input.ToArray();

            // history = tail of previous input (2*HalfTaps samples) + new input
            int keep = _history.Length;
            var buf = new float[keep + input.Length];
            Array.Copy(_history, buf, keep);
            input.CopyTo(buf.AsSpan(keep));

            int maxOut = (int)((buf.Length - _pos - HalfTaps) / _step) + 2;
            var output = new float[Math.Max(0, maxOut)];
            int count = 0;
            while (_pos + HalfTaps < buf.Length && count < output.Length)
            {
                int center = (int)Math.Floor(_pos);
                double frac = _pos - center;
                double acc = 0, norm = 0;
                for (int k = -HalfTaps + 1; k <= HalfTaps; k++)
                {
                    int idx = center + k;
                    if (idx < 0 || idx >= buf.Length) continue;
                    double x = k - frac;
                    double w = Kernel(x);
                    acc += buf[idx] * w;
                    norm += w;
                }
                output[count++] = (float)(norm != 0 ? acc / norm : 0);
                _pos += _step;
            }

            // Keep the last 2*HalfTaps input samples as history; shift the position accordingly.
            int newKeep = Math.Min(buf.Length, HalfTaps * 2);
            int drop = buf.Length - newKeep;
            _history = new float[newKeep];
            Array.Copy(buf, drop, _history, 0, newKeep);
            _pos -= drop;
            if (_pos < HalfTaps - 1) _pos = HalfTaps - 1;

            if (count == output.Length) return output;
            var trimmed = new float[count];
            Array.Copy(output, trimmed, count);
            return trimmed;
        }

        private double Kernel(double x)
        {
            double c = _cutoff;
            double sinc = x == 0 ? c : Math.Sin(Math.PI * c * x) / (Math.PI * x);
            double wx = x / HalfTaps;
            if (Math.Abs(wx) >= 1) return 0;
            double window = 0.42 + 0.5 * Math.Cos(Math.PI * wx) + 0.08 * Math.Cos(2 * Math.PI * wx); // Blackman
            return sinc * window;
        }
    }
}
