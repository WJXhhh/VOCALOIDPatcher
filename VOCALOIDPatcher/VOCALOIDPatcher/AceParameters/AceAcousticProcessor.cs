using System;
using System.Numerics;
using System.Threading;

namespace VOCALOIDPatcher.AceParameters;

// Offline, phase-preserving harmonic source transformation. The original
// waveform is the identity path; only the overlap-added spectral DIFFERENCE
// is applied. Neutral curves, silence and unvoiced source frames stay intact.
internal static class AceAcousticProcessor
{
    private const int Size = 2048;
    private const int Hop = Size / 4;
    private const int Models = 48;
    private const int Harmonics = 128;
    private static readonly double[,] SourceLog = BuildSourceTable();

    internal static float[] Process(float[] input, int sampleRate, AceAcousticControls controls,
        CancellationToken cancellation = default)
    {
        if (sampleRate is < 16000 or > 192000) throw new ArgumentOutOfRangeException(nameof(sampleRate));
        foreach (float value in input)
            if (!float.IsFinite(value)) throw new ArgumentException("Non-finite source PCM.");
        var output = (float[])input.Clone();
        if (controls.IsNeutral || input.Length == 0) return output;
        if (controls.HasSourceChanges)
        {
            var difference = new double[input.Length];
            var weight = new double[input.Length];
            var frame = new double[Size];
            var window = new double[Size];
            var spectrum = new Complex[Size];
            var original = new Complex[Size];
            for (int i = 0; i < Size; i++) window[i] = .5 - .5 * Math.Cos(2 * Math.PI * i / Size);
            // Padding at both ends prevents gain errors at the first/last frame.
            for (int start = -Size + Hop; start < input.Length; start += Hop)
            {
                cancellation.ThrowIfCancellationRequested();
                double energy = 0;
                for (int i = 0; i < Size; i++)
                {
                    int at = start + i;
                    frame[i] = at >= 0 && at < input.Length ? input[at] : 0;
                    energy += frame[i] * frame[i];
                    spectrum[i] = new Complex(frame[i] * window[i], 0);
                    if (at >= 0 && at < input.Length) weight[at] += window[i] * window[i];
                }
                double seconds = (start + Size / 2d) / sampleRate;
                double tension = controls.Tension.At(seconds), air = controls.Air.At(seconds), falsetto = controls.Falsetto.At(seconds);
                if (energy < Size * 1e-12 || (tension == 0 && air == 0 && falsetto == 0)) continue;
                double f0 = EstimatePitch(frame, sampleRate, out double voiced);
                if (f0 == 0) continue; // Preserve consonants and attacks rather than invent a pitch.
                Transform(spectrum, inverse: false);
                spectrum.CopyTo(original, 0);
                double rd = EstimateRd(frame, spectrum, sampleRate, f0);
                double targetRd = tension >= 0 ? rd + tension * (LfGlottalModel.MinimumRd - rd) * .8
                    : rd - tension * (LfGlottalModel.MaximumRd - rd) * .8;
                ApplySource(spectrum, sampleRate, f0, voiced, rd, targetRd, falsetto, air, start);
                // Separate source shape/HNR from Energy. Keep weighted frame
                // energy constant before the independent sample-domain gain.
                double before = 0, after = 0;
                for (int i = 0; i < Size; i++)
                {
                    before += original[i].Magnitude * original[i].Magnitude;
                    after += spectrum[i].Magnitude * spectrum[i].Magnitude;
                }
                double normalize = after > 1e-20 ? Math.Clamp(Math.Sqrt(before / after), .25, 4) : 1;
                for (int i = 0; i < Size; i++) spectrum[i] = spectrum[i] * normalize - original[i];
                Transform(spectrum, inverse: true);
                for (int i = 0; i < Size; i++)
                {
                    int at = start + i;
                    if (at >= 0 && at < input.Length) difference[at] += spectrum[i].Real * window[i];
                }
            }
            for (int i = 0; i < output.Length; i++)
            {
                double t = i / (double)sampleRate;
                double amount = Math.Max(Math.Abs(controls.Air.At(t)),
                    Math.Max(Math.Abs(controls.Tension.At(t)), Math.Abs(controls.Falsetto.At(t))));
                // A frame straddling the neutral boundary must not alter the
                // next note. Fade the spectral delta to the exact identity path.
                double blend = Math.Min(1, amount / .02);
                if (weight[i] > 1e-12 && blend > 0)
                    output[i] = (float)(input[i] + difference[i] / weight[i] * blend);
            }
        }
        for (int i = 0; i < output.Length; i++)
        {
            if ((i & 4095) == 0) cancellation.ThrowIfCancellationRequested();
            double gain = Math.Pow(10, controls.Energy.At(i / (double)sampleRate) * 18 / 20);
            output[i] = (float)(output[i] * gain);
            if (!float.IsFinite(output[i])) throw new InvalidOperationException("Non-finite acoustic output.");
        }
        return output;
    }

    private static void ApplySource(Complex[] spectrum, int sampleRate, double f0, double voiced,
        double rd, double targetRd, double falsetto, double air, int start)
    {
        var mask = new double[Size / 2 + 1];
        double binHz = sampleRate / (double)Size;
        double width = Math.Min(f0 * .22, binHz * 1.5);
        for (int k = 1; k < Size / 2; k++)
        {
            double frequency = k * binHz, harmonic = frequency / f0;
            double nearest = Math.Max(1, Math.Round(harmonic));
            mask[k] = voiced * Math.Exp(-.5 * Math.Pow((frequency - nearest * f0) / width, 2));
            if (frequency > 10000) mask[k] *= Math.Max(0, 1 - (frequency - 10000) / 4000);
            // Ratios alter the source while retaining the input tract resonances
            // and phase; gains are relative to harmonic number, not fixed Hz.
            double logRatio = Lookup(targetRd, harmonic) - Lookup(rd, harmonic);
            double thinningDb = -12 * falsetto * Math.Log2(Math.Max(1, harmonic / 2));
            double gain = Math.Exp(Math.Clamp(logRatio + thinningDb * Math.Log(10) / 20, -2.07, 2.07));
            spectrum[k] *= 1 + mask[k] * (gain - 1);
        }
        if (air > 0)
            ApplyAspiration(spectrum, sampleRate, f0, voiced, air, start);
        else if (air < 0)
        {
            double nyquist = sampleRate / 2d;
            // Noise control uses the documented ERB indexing, not a hardcoded
            // 2.3..5.1 kHz claim (the original article's limits disagree).
            for (int band = 12; band <= 16; band++)
            {
                double Boundary(int b) => 228.8 * (Math.Pow(1 + nyquist / 228.8, b / 24d) - 1);
                int first = Math.Clamp((int)Math.Ceiling(Boundary(band) / binHz), 1, Size / 2 - 1);
                int last = Math.Clamp((int)Math.Ceiling(Boundary(band + 1) / binHz), first + 1, Size / 2);
                double periodic = 0, noise = 0;
                for (int k = first; k < last; k++)
                {
                    double power = spectrum[k].Magnitude * spectrum[k].Magnitude;
                    periodic += power * mask[k]; noise += power * (1 - mask[k]);
                }
                double total = periodic + noise;
                if (total < 1e-18) continue;
                double fraction = noise / total;
                double target = fraction * (1 + air * .95);
                double periodicGain = Math.Sqrt((1 - target) / Math.Max(1e-8, 1 - fraction));
                double noiseGain = Math.Min(2, Math.Sqrt(target / Math.Max(1e-8, fraction)));
                for (int k = first; k < last; k++)
                    spectrum[k] *= mask[k] * periodicGain + (1 - mask[k]) * noiseGain;
            }
        }
        for (int k = 1; k < Size / 2; k++) spectrum[Size - k] = Complex.Conjugate(spectrum[k]);
    }

    private static void ApplyAspiration(Complex[] spectrum, int sampleRate, double f0, double voiced,
        double air, int start)
    {
        var noise = new Complex[Size];
        var cumulativePower = new double[Size / 2 + 1];
        double sourcePower = 0;
        for (int k = 1; k < Size / 2; k++)
        {
            double power = spectrum[k].Magnitude * spectrum[k].Magnitude;
            sourcePower += power;
            cumulativePower[k + 1] = sourcePower;
        }
        if (sourcePower < 1e-18) return;
        for (int i = 0; i < Size; i++)
        {
            // The same absolute sample has the same random value in overlapping
            // frames. Independent random phases per frame would partly cancel
            // on reconstruction and weaken the actual aspiration level.
            uint seed = unchecked((uint)(start + i) + 0x9e3779b9u);
            seed ^= seed >> 16; seed *= 2246822519u; seed ^= seed >> 13;
            seed *= 3266489917u; seed ^= seed >> 16;
            double value = seed / (double)uint.MaxValue * 2 - 1;
            noise[i] = new Complex(value * (.5 - .5 * Math.Cos(2 * Math.PI * i / Size)), 0);
        }
        Transform(noise, inverse: false);
        double binHz = sampleRate / (double)Size;
        double average = sourcePower / (Size / 2 - 1), noisePower = 0;
        for (int k = 1; k < Size / 2; k++)
        {
            double frequency = k * binHz;
            // Smooth over multiple harmonics instead of imprinting a periodic
            // comb onto the noise. A small broadband floor avoids the original
            // empty-band failure on dark/soft vowels.
            int radius = Math.Max(4, (int)Math.Ceiling(Math.Max(f0 * 1.5, frequency * .15) / binHz));
            int first = Math.Max(1, k - radius), end = Math.Min(Size / 2, k + radius + 1);
            double envelope = (cumulativePower[end] - cumulativePower[first]) / (end - first);
            double highPass = frequency * frequency / (frequency * frequency + 500 * 500);
            double lowPass = 1 / Math.Sqrt(1 + Math.Pow(frequency / 8000, 6));
            double shape = Math.Sqrt(envelope + average * .025) * highPass * lowPass * Math.Sqrt(frequency / 1000);
            noise[k] *= shape;
            noisePower += noise[k].Magnitude * noise[k].Magnitude;
        }
        if (noisePower < 1e-20) return;
        // This is a synthesis amount relative to the WHOLE voiced frame, not
        // the analysis band's existing noise. Full scale reserves up to 40%
        // of frame power for aspiration before final RMS normalization.
        double fraction = .4 * Math.Pow(air, 1.3) * voiced;
        double dryGain = Math.Sqrt(1 - fraction);
        double noiseGain = Math.Sqrt(sourcePower * fraction / noisePower);
        for (int k = 1; k < Size / 2; k++) spectrum[k] = spectrum[k] * dryGain + noise[k] * noiseGain;
    }

    internal static double EstimatePitch(double[] samples, int sampleRate, out double confidence)
    {
        // Decimated normalized autocorrelation; select the first strong local
        // peak to avoid a systematic octave-down choice on periodic vowels.
        int stride = Math.Max(1, sampleRate / 12000);
        int length = samples.Length / stride;
        int min = Math.Max(2, (int)(sampleRate / (1200d * stride)));
        int max = Math.Min(length / 2 - 1, (int)(sampleRate / (65d * stride)));
        var correlation = new double[max + 2];
        double best = 0;
        for (int lag = min; lag <= max; lag++)
        {
            double xy = 0, xx = 0, yy = 0;
            for (int i = 0; i < length - max; i++)
            {
                double x = samples[i * stride], y = samples[(i + lag) * stride];
                xy += x * y; xx += x * x; yy += y * y;
            }
            correlation[lag] = xy / Math.Max(1e-20, Math.Sqrt(xx * yy));
            best = Math.Max(best, correlation[lag]);
        }
        confidence = Math.Clamp((best - .55) / .35, 0, 1);
        if (best < .7) return 0;
        for (int lag = min + 1; lag < max; lag++)
        {
            double middle = correlation[lag];
            if (middle < best * .92 || middle < correlation[lag - 1] || middle < correlation[lag + 1]) continue;
            double divisor = correlation[lag - 1] - 2 * middle + correlation[lag + 1];
            double correction = Math.Abs(divisor) > 1e-10
                ? Math.Clamp(.5 * (correlation[lag - 1] - correlation[lag + 1]) / divisor, -.5, .5) : 0;
            return sampleRate / ((lag + correction) * stride);
        }
        confidence = 0;
        return 0;
    }

    private static double EstimateRd(double[] frame, Complex[] spectrum, int sampleRate, double f0)
    {
        // Regularized pre-emphasized LPC approximates the tract envelope.
        // It is an estimate, not an exact inverse filter on arbitrary singing.
        int order = Math.Clamp(2 + sampleRate / 1000, 18, 48);
        var emphasized = new double[Size];
        for (int i = 1; i < Size; i++) emphasized[i] = (frame[i] - .97 * frame[i - 1]) *
            (.5 - .5 * Math.Cos(2 * Math.PI * i / Size));
        var r = new double[order + 1];
        for (int lag = 0; lag <= order; lag++)
            for (int i = lag; i < Size; i++) r[lag] += emphasized[i] * emphasized[i - lag];
        var a = new double[order + 1]; a[0] = 1;
        double error = Math.Max(1e-12, r[0] * 1.001);
        for (int i = 1; i <= order; i++)
        {
            double sum = r[i];
            for (int j = 1; j < i; j++) sum += a[j] * r[i - j];
            double reflection = Math.Clamp(-sum / error, -.98, .98);
            var previous = (double[])a.Clone();
            for (int j = 1; j < i; j++) a[j] = previous[j] + reflection * previous[i - j];
            a[i] = reflection;
            error = Math.Max(1e-12, error * (1 - reflection * reflection));
        }
        int count = Math.Clamp((int)(8000 / f0), 4, 40);
        var measured = new double[count];
        for (int h = 1; h <= count; h++)
        {
            double bin = h * f0 * Size / sampleRate;
            int at = Math.Clamp((int)Math.Round(bin), 1, Size / 2 - 2);
            double amplitude = Math.Max(spectrum[at].Magnitude, Math.Max(spectrum[at - 1].Magnitude, spectrum[at + 1].Magnitude));
            Complex inverse = Complex.One;
            for (int j = 1; j <= order; j++) inverse += Complex.FromPolarCoordinates(a[j], -2 * Math.PI * h * f0 * j / sampleRate);
            measured[h - 1] = Math.Log(Math.Max(1e-12, amplitude * inverse.Magnitude));
        }
        int best = 0; double bestError = double.PositiveInfinity;
        for (int model = 0; model < Models; model++)
        {
            double mean = 0;
            for (int h = 1; h <= count; h++) mean += measured[h - 1] - SourceLog[model, h];
            mean /= count;
            double cost = 0;
            for (int h = 1; h <= count; h++) cost += Math.Pow(measured[h - 1] - SourceLog[model, h] - mean, 2);
            if (cost < bestError) { bestError = cost; best = model; }
        }
        // LPC can absorb part of the source tilt. Keep modest bidirectional
        // headroom instead of letting that ambiguous estimate pin a control
        // against a physical model endpoint (and silently disable half its range).
        return Math.Clamp(LfGlottalModel.MinimumRd +
            (LfGlottalModel.MaximumRd - LfGlottalModel.MinimumRd) * best / (Models - 1d), .75, 2.45);
    }

    private static double[,] BuildSourceTable()
    {
        var table = new double[Models, Harmonics + 1];
        for (int m = 0; m < Models; m++)
        {
            var model = new LfGlottalModel(LfGlottalModel.MinimumRd +
                (LfGlottalModel.MaximumRd - LfGlottalModel.MinimumRd) * m / (Models - 1d));
            double first = Math.Max(1e-12, model.Spectrum(1).Magnitude);
            for (int h = 1; h <= Harmonics; h++) table[m, h] = Math.Log(Math.Max(1e-12, model.Spectrum(h).Magnitude) / first);
        }
        return table;
    }

    private static double Lookup(double rd, double harmonic)
    {
        double row = Math.Clamp((rd - LfGlottalModel.MinimumRd) / (LfGlottalModel.MaximumRd - LfGlottalModel.MinimumRd) * (Models - 1), 0, Models - 1);
        int a = (int)row, b = Math.Min(Models - 1, a + 1);
        double h = Math.Clamp(harmonic, 1, Harmonics);
        int x = (int)h, y = Math.Min(Harmonics, x + 1);
        double left = SourceLog[a, x] + (SourceLog[a, y] - SourceLog[a, x]) * (h - x);
        double right = SourceLog[b, x] + (SourceLog[b, y] - SourceLog[b, x]) * (h - x);
        return left + (right - left) * (row - a);
    }

    internal static void Transform(Complex[] data, bool inverse)
    {
        int size = data.Length;
        if (size < 2 || (size & (size - 1)) != 0) throw new ArgumentException("Invalid FFT size.");
        for (int i = 1, j = 0; i < size; i++)
        {
            int bit = size >> 1;
            for (; (j & bit) != 0; bit >>= 1) j ^= bit;
            j ^= bit;
            if (i < j) (data[i], data[j]) = (data[j], data[i]);
        }
        for (int width = 2; width <= size; width <<= 1)
        {
            Complex step = Complex.FromPolarCoordinates(1, (inverse ? 2 : -2) * Math.PI / width);
            for (int start = 0; start < size; start += width)
            {
                Complex twiddle = Complex.One;
                for (int j = 0; j < width / 2; j++)
                {
                    Complex a = data[start + j], b = twiddle * data[start + j + width / 2];
                    data[start + j] = a + b; data[start + j + width / 2] = a - b;
                    twiddle *= step;
                }
            }
        }
        if (inverse) for (int i = 0; i < size; i++) data[i] /= size;
    }
}
