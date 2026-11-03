using System;
using System.Buffers.Binary;
using System.IO;
using System.Threading;

namespace VOCALOIDPatcher.ParameterEnergy;

internal sealed record EnergyEnvelope(double StepSeconds, double DurationSeconds, float[] Decibels, float[] PeakDecibels,
    int WeightingPercent = 0)
{
    internal double GetDecibels(double beginSeconds, double endSeconds)
        => GetDecibels(Decibels, beginSeconds, endSeconds, power: true);

    internal double GetPeakDecibels(double beginSeconds, double endSeconds)
        => GetDecibels(PeakDecibels, beginSeconds, endSeconds, power: false);

    private double GetDecibels(float[] values, double beginSeconds, double endSeconds, bool power)
    {
        if (values.Length == 0 || endSeconds < 0 || beginSeconds >= DurationSeconds) return -120;
        // At high zoom interpolate between time-centred samples in amplitude /
        // power, instead of holding each analysis value as a rectangular step.
        if (endSeconds - beginSeconds <= StepSeconds)
        {
            double index = Math.Clamp((beginSeconds + endSeconds) / (2 * StepSeconds) - .5, 0, values.Length - 1);
            int firstIndex = (int)index, nextIndex = Math.Min(firstIndex + 1, values.Length - 1);
            double divisor = power ? 10 : 20;
            double a = Math.Pow(10, values[firstIndex] / divisor), b = Math.Pow(10, values[nextIndex] / divisor);
            return divisor * Math.Log10(Math.Max(power ? 1e-12 : 1e-6, a + (b - a) * (index - firstIndex)));
        }
        int first = (int)Math.Clamp(Math.Floor(beginSeconds / StepSeconds), 0, values.Length - 1);
        // The end is exclusive: borrowing the next pixel's first bucket spreads
        // a transient into neighbouring intervals and hides brief valleys.
        int last = (int)Math.Clamp(Math.Ceiling(endSeconds / StepSeconds) - 1, first, values.Length - 1);
        double result = -120;
        for (int i = first; i <= last; i++) result = Math.Max(result, values[i]);
        return result;
    }

    // Stream the audio instead of keeping a second PCM copy in the editor.
    // 1 ms analysis hops, 20 ms centred RMS and 10 ms centred peak windows.
    // Decode once, then reuse small power/peak buckets for overlapping windows.
    // RMS averages power across channels (no stereo phase cancellation).
    internal static EnergyEnvelope Read(Stream stream, CancellationToken cancellation = default, int weightingPercent = 0)
    {
        using var reader = new BinaryReader(stream, System.Text.Encoding.UTF8, leaveOpen: true);
        if (stream.Length < 12 || reader.ReadUInt32() != 0x46464952)
            throw new InvalidDataException("Expected RIFF audio.");
        long limit = reader.ReadUInt32() + 8L;
        if (limit > stream.Length || limit < 12 || reader.ReadUInt32() != 0x45564157)
            throw new InvalidDataException("Invalid WAVE length.");
        int tag = 0, channels = 0, rate = 0, bits = 0, align = 0;
        long data = -1, length = 0;
        while (stream.Position + 8 <= limit)
        {
            cancellation.ThrowIfCancellationRequested();
            uint id = reader.ReadUInt32(), size = reader.ReadUInt32();
            long start = stream.Position, next = start + size + (size & 1);
            if (next > limit) throw new InvalidDataException("Truncated WAVE chunk.");
            if (id == 0x20746d66)
            {
                if (size < 16) throw new InvalidDataException("Invalid WAVE format.");
                tag = reader.ReadUInt16(); channels = reader.ReadUInt16(); rate = reader.ReadInt32();
                reader.ReadUInt32(); align = reader.ReadUInt16(); bits = reader.ReadUInt16();
                if (tag == 0xfffe)
                {
                    if (size < 40 || reader.ReadUInt16() < 22 || reader.ReadUInt16() != bits)
                        throw new InvalidDataException("Invalid extensible WAVE.");
                    reader.ReadUInt32();
                    var subtype = new Guid(reader.ReadBytes(16));
                    tag = subtype == new Guid("00000001-0000-0010-8000-00aa00389b71") ? 1 :
                        subtype == new Guid("00000003-0000-0010-8000-00aa00389b71") ? 3 : 0;
                }
            }
            else if (id == 0x61746164)
            {
                if (data >= 0) throw new InvalidDataException("Multiple WAVE data chunks.");
                data = start; length = size;
            }
            stream.Position = next;
        }
        if (data < 0 || channels is < 1 or > 8 || rate is < 8000 or > 384000 ||
            bits is not (16 or 24 or 32) || align != channels * (bits / 8) || length % align != 0 ||
            (tag != 1 && tag != 3) || (tag == 3 && bits != 32))
            throw new InvalidDataException("Unsupported energy WAVE layout.");
        long frames = length / align;
        if (frames > rate * 7200L) throw new InvalidDataException("Energy WAVE is too long.");
        weightingPercent = Math.Clamp(weightingPercent, 0, 300);
        var weighting = weightingPercent == 0 ? null : new EnergyFrequencyWeighting(rate, channels, weightingPercent);
        // Keep long Parts bounded too: at most one million points per series.
        int hop = (int)Math.Max((rate + 999) / 1000, (frames + 999999) / 1000000);
        int countBuckets = (int)((frames + hop - 1) / hop);
        var powers = new double[countBuckets];
        var rawPeaks = new float[countBuckets];
        // Decode bounded blocks. BinaryReader's per-sample reads otherwise
        // cross the Stream boundary millions of times for an ordinary Part.
        var bytes = new byte[65536 / align * align];
        int available = 0, position = 0, sampleBytes = bits / 8;
        long remaining = length;
        stream.Position = data;
        for (int bucket = 0; bucket < countBuckets; bucket++)
        {
            cancellation.ThrowIfCancellationRequested();
            int count = (int)Math.Min(hop, frames - (long)bucket * hop);
            double power = 0, peak = 0;
            for (int i = 0; i < count * channels; i++)
            {
                if (position == available)
                {
                    available = (int)Math.Min(bytes.Length, remaining);
                    stream.ReadExactly(bytes.AsSpan(0, available));
                    remaining -= available;
                    position = 0;
                }
                var pcm = bytes.AsSpan(position, sampleBytes);
                double sample = (tag, bits) switch
                {
                    (3, 32) => BitConverter.Int32BitsToSingle(BinaryPrimitives.ReadInt32LittleEndian(pcm)),
                    (1, 16) => BinaryPrimitives.ReadInt16LittleEndian(pcm) / 32768d,
                    (1, 24) => ((pcm[0] | pcm[1] << 8 | pcm[2] << 16) << 8 >> 8) / 8388608d,
                    (1, 32) => BinaryPrimitives.ReadInt32LittleEndian(pcm) / 2147483648d,
                    _ => 0
                };
                position += sampleBytes;
                if (!double.IsFinite(sample)) sample = 0;
                if (weighting != null) sample = weighting.Process(sample, i % channels);
                power += sample * sample;
                peak = Math.Max(peak, Math.Abs(sample));
            }
            powers[bucket] = power;
            rawPeaks[bucket] = (float)peak;
        }
        double step = hop / (double)rate;
        int rmsWindow = Math.Max(1, (int)Math.Round(.020 / step));
        int peakWindow = Math.Max(1, (int)Math.Round(.010 / step));
        var values = new float[countBuckets];
        var peaks = new float[countBuckets];
        int begin = 0, end = 0;
        double rollingPower = 0;
        for (int i = 0; i < countBuckets; i++)
        {
            if ((i & 1023) == 0) cancellation.ThrowIfCancellationRequested();
            int left = Math.Max(0, i - (rmsWindow - 1) / 2);
            int right = Math.Min(countBuckets, i + rmsWindow / 2 + 1);
            while (end < right) rollingPower += powers[end++];
            while (begin < left) rollingPower -= powers[begin++];
            long samples = Math.Min(frames, (long)right * hop) - (long)left * hop;
            values[i] = (float)(10 * Math.Log10(Math.Max(1e-12, rollingPower / (samples * channels))));
            double peak = 0;
            int peakEnd = Math.Min(countBuckets, i + peakWindow / 2 + 1);
            for (int j = Math.Max(0, i - (peakWindow - 1) / 2); j < peakEnd; j++) peak = Math.Max(peak, rawPeaks[j]);
            peaks[i] = (float)(20 * Math.Log10(Math.Max(1e-6, peak)));
        }
        return new(step, frames / (double)rate, values, peaks, weightingPercent);
    }
}
