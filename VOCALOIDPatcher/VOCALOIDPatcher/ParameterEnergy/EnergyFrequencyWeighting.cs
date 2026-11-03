using System;

namespace VOCALOIDPatcher.ParameterEnergy;

// Display-only, deliberately mild weighting, not an A/K weighting or LUFS meter.
// At 100%: -6 dB low shelf at 150 Hz, +4 dB high shelf at 2 kHz.
// Both gains scale in dB with strength; normalize the cascade at 1 kHz.
internal sealed class EnergyFrequencyWeighting
{
    private readonly Coefficients _low;
    private readonly Coefficients _high;
    private readonly double _gain;
    private readonly State[] _lowStates;
    private readonly State[] _highStates;

    internal EnergyFrequencyWeighting(int sampleRate, int channels, int percent)
    {
        if (sampleRate is < 8000 or > 384000) throw new ArgumentOutOfRangeException(nameof(sampleRate));
        if (channels is < 1 or > 8) throw new ArgumentOutOfRangeException(nameof(channels));
        double strength = Math.Clamp(percent, 0, 300) / 100d;
        _low = Coefficients.Shelf(sampleRate, 150, -6 * strength, high: false);
        _high = Coefficients.Shelf(sampleRate, 2000, 4 * strength, high: true);
        _gain = 1 / (_low.Magnitude(sampleRate, 1000) * _high.Magnitude(sampleRate, 1000));
        _lowStates = new State[channels];
        _highStates = new State[channels];
    }

    internal double Process(double sample, int channel)
        => _highStates[channel].Process(_lowStates[channel].Process(sample, _low), _high) * _gain;

    private struct State
    {
        private double _z1, _z2;

        internal double Process(double sample, Coefficients coefficients)
        {
            double result = coefficients.B0 * sample + _z1;
            _z1 = coefficients.B1 * sample - coefficients.A1 * result + _z2;
            _z2 = coefficients.B2 * sample - coefficients.A2 * result;
            // Avoid denormal arithmetic after a long silent tail.
            if (Math.Abs(_z1) < 1e-30) _z1 = 0;
            if (Math.Abs(_z2) < 1e-30) _z2 = 0;
            return result;
        }
    }

    private readonly record struct Coefficients(double B0, double B1, double B2, double A1, double A2)
    {
        // RBJ shelf equations, slope S=1, independently calculated for each rate:
        // https://www.w3.org/TR/audio-eq-cookbook/#formulae
        internal static Coefficients Shelf(int rate, double frequency, double db, bool high)
        {
            double a = Math.Pow(10, db / 40), omega = 2 * Math.PI * frequency / rate;
            double c = Math.Cos(omega), t = Math.Sin(omega) * Math.Sqrt(2 * a);
            double a0, a1, a2, b0, b1, b2;
            if (high)
            {
                b0 = a * ((a + 1) + (a - 1) * c + t);
                b1 = -2 * a * ((a - 1) + (a + 1) * c);
                b2 = a * ((a + 1) + (a - 1) * c - t);
                a0 = (a + 1) - (a - 1) * c + t;
                a1 = 2 * ((a - 1) - (a + 1) * c);
                a2 = (a + 1) - (a - 1) * c - t;
            }
            else
            {
                b0 = a * ((a + 1) - (a - 1) * c + t);
                b1 = 2 * a * ((a - 1) - (a + 1) * c);
                b2 = a * ((a + 1) - (a - 1) * c - t);
                a0 = (a + 1) + (a - 1) * c + t;
                a1 = -2 * ((a - 1) + (a + 1) * c);
                a2 = (a + 1) + (a - 1) * c - t;
            }
            return new(b0 / a0, b1 / a0, b2 / a0, a1 / a0, a2 / a0);
        }

        internal double Magnitude(int rate, double frequency)
        {
            double w = 2 * Math.PI * frequency / rate;
            double nr = B0 + B1 * Math.Cos(w) + B2 * Math.Cos(2 * w);
            double ni = B1 * Math.Sin(w) + B2 * Math.Sin(2 * w);
            double dr = 1 + A1 * Math.Cos(w) + A2 * Math.Cos(2 * w);
            double di = A1 * Math.Sin(w) + A2 * Math.Sin(2 * w);
            return Math.Sqrt((nr * nr + ni * ni) / (dr * dr + di * di));
        }
    }
}
