using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace VOCALOIDPatcher.PhonemeVolume;

// Pure file processing. No VSM wrappers, synthesis or UI calls on the worker.
// Copy the original once, then touch only the controlled sample windows.
internal static class PhonemeGainWaveProcessor
{
    private readonly record struct Format(ushort Tag, int Channels, int Rate, int Align, int Bits, long Offset, long Frames);
    private readonly record struct Range(long Begin, long End, double Gain, double FadeIn = 0, double FadeOut = 0);
    private readonly record struct Junction(long Sample, double LeftGain, double RightGain, double Width);

    internal static void Create(string sourcePath, string destinationPath, IReadOnlyList<PhonemeGainRegion> regions)
    {
        if (Path.GetFullPath(sourcePath).Equals(Path.GetFullPath(destinationPath), StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Phoneme gain requires a separate output file.");
        foreach (var region in regions)
            if (!double.IsFinite(region.BeginSeconds) || !double.IsFinite(region.EndSeconds) ||
                region.EndSeconds <= region.BeginSeconds || !float.IsFinite(region.Decibels) ||
                region.Decibels < PhonemeVolumePlan.Minimum || region.Decibels > PhonemeVolumePlan.Maximum)
                throw new InvalidDataException("Invalid phoneme gain region.");
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(destinationPath))!);
        string temporary = destinationPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.Copy(sourcePath, temporary, false);
            using (var stream = new FileStream(temporary, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            {
                var format = ReadFormat(stream);
                var ranges = regions.Where(r => r.Decibels != 0).Select(r => new Range(
                        ToSample(r.BeginSeconds, format), ToSample(r.EndSeconds, format), PhonemeVolumePlan.LinearGain(r.Decibels)))
                    .Where(r => r.End > r.Begin).OrderBy(r => r.Begin).ToArray();
                long fade = Math.Max(1, (long)Math.Round(format.Rate * .005));
                int index = 0;
                while (index < ranges.Length)
                {
                    long begin = ranges[index].Begin, end = ranges[index].End;
                    int first = index++;
                    while (index < ranges.Length && ranges[index].Begin <= end)
                        end = Math.Max(end, ranges[index++].End);
                    ProcessWindow(stream, format, begin, end, ranges.AsSpan(first, index - first), fade);
                }
            }
            File.Move(temporary, destinationPath, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    private static long ToSample(double seconds, Format format) =>
        (long)Math.Round(Math.Clamp(seconds * format.Rate, 0, format.Frames));

    private static void ProcessWindow(FileStream stream, Format format, long begin, long end, ReadOnlySpan<Range> ranges, long fade)
    {
        // A shared phoneme boundary is a change between two gain targets,
        // not two separate fades back to unity. Returning to unity creates
        // a short loudness burst even when both neighbours are attenuated.
        var envelopes = ranges.ToArray();
        var starts = envelopes.GroupBy(r => r.Begin).ToDictionary(g => g.Key, g => g.ToArray());
        var ends = envelopes.GroupBy(r => r.End).ToDictionary(g => g.Key, g => g.ToArray());
        double Width(Range range) => Math.Min(fade, Math.Max(.5, (range.End - range.Begin - 1) / 2d));
        var junctions = starts.Keys.Intersect(ends.Keys).OrderBy(s => s).Select(sample =>
            new Junction(sample, ends[sample].Aggregate(1d, (gain, r) => gain * r.Gain),
                starts[sample].Aggregate(1d, (gain, r) => gain * r.Gain),
                starts[sample].Concat(ends[sample]).Min(Width))).ToArray();
        for (int i = 0; i < junctions.Length; i++)
        {
            double width = junctions[i].Width;
            if (i > 0) width = Math.Min(width, (junctions[i].Sample - junctions[i - 1].Sample) / 2d);
            if (i + 1 < junctions.Length) width = Math.Min(width, (junctions[i + 1].Sample - junctions[i].Sample) / 2d);
            junctions[i] = junctions[i] with { Width = width };
        }
        var widths = junctions.ToDictionary(j => j.Sample, j => j.Width);
        for (int i = 0; i < envelopes.Length; i++)
            envelopes[i] = envelopes[i] with
            {
                FadeIn = widths.GetValueOrDefault(envelopes[i].Begin, Width(envelopes[i])),
                FadeOut = widths.GetValueOrDefault(envelopes[i].End, Width(envelopes[i]))
            };
        static double Weight(double t) => t >= 1 ? 1 : .5 - .5 * Math.Cos(Math.PI * t);
        var bytes = new byte[checked(4096 * format.Align)];
        var active = new List<Range>();
        int next = 0, junctionIndex = 0;
        for (long at = begin; at < end;)
        {
            int count = (int)Math.Min(4096, end - at), length = count * format.Align;
            long offset = checked(format.Offset + at * format.Align);
            stream.Position = offset;
            stream.ReadExactly(bytes.AsSpan(0, length));
            for (int frame = 0; frame < count; frame++)
            {
                long sample = at + frame;
                while (next < envelopes.Length && envelopes[next].Begin <= sample)
                    active.Add(envelopes[next++]);
                // End points need not be sorted: nested / overlapping phonemes
                // can finish before an earlier range. Preserve multiplication
                // order while dropping only expired intervals.
                for (int i = active.Count - 1; i >= 0; i--)
                    if (active[i].End <= sample) active.RemoveAt(i);
                double centre = sample + .5;
                while (junctionIndex < junctions.Length && centre >= junctions[junctionIndex].Sample + junctions[junctionIndex].Width)
                    junctionIndex++;
                Junction? junction = junctionIndex < junctions.Length && centre >= junctions[junctionIndex].Sample - junctions[junctionIndex].Width
                    ? junctions[junctionIndex] : null;
                double gain = 1;
                foreach (var range in active)
                {
                    if (junction is { } shared && (range.Begin == shared.Sample || range.End == shared.Sample)) continue;
                    // Smooth strictly inside the selected phoneme. The 5 ms
                    // ramp shortens for short phonemes and reaches its target.
                    double weight = Math.Min(Weight((centre - range.Begin) / range.FadeIn),
                        Weight((range.End - centre) / range.FadeOut));
                    gain *= 1 + (range.Gain - 1) * weight;
                }
                if (junction is { } bridge)
                    gain *= bridge.LeftGain + (bridge.RightGain - bridge.LeftGain) *
                        Weight((centre - bridge.Sample + bridge.Width) / (2 * bridge.Width));
                for (int channel = 0; channel < format.Channels; channel++)
                    Apply(bytes.AsSpan(frame * format.Align + channel * (format.Bits / 8)), format, gain);
            }
            stream.Position = offset;
            stream.Write(bytes, 0, length);
            at += count;
        }
    }

    private static void Apply(Span<byte> bytes, Format format, double gain)
    {
        if (format.Tag == 3)
        {
            float sample = BitConverter.Int32BitsToSingle(BinaryPrimitives.ReadInt32LittleEndian(bytes));
            if (!float.IsFinite(sample)) throw new InvalidDataException("Non-finite source PCM.");
            float adjusted = (float)(sample * gain);
            if (!float.IsFinite(adjusted)) throw new InvalidDataException("Non-finite gain output.");
            BinaryPrimitives.WriteInt32LittleEndian(bytes, BitConverter.SingleToInt32Bits(adjusted));
            return;
        }
        switch (format.Bits)
        {
            case 8:
                bytes[0] = (byte)Math.Clamp(Math.Round((bytes[0] - 128) * gain) + 128, 0, 255);
                break;
            case 16:
                BinaryPrimitives.WriteInt16LittleEndian(bytes,
                    (short)Math.Clamp(Math.Round(BinaryPrimitives.ReadInt16LittleEndian(bytes) * gain), short.MinValue, short.MaxValue));
                break;
            case 24:
                int sample = (bytes[0] | bytes[1] << 8 | bytes[2] << 16) << 8 >> 8;
                int adjusted = (int)Math.Clamp(Math.Round(sample * gain), -8388608, 8388607);
                bytes[0] = (byte)adjusted; bytes[1] = (byte)(adjusted >> 8); bytes[2] = (byte)(adjusted >> 16);
                break;
            case 32:
                BinaryPrimitives.WriteInt32LittleEndian(bytes,
                    (int)Math.Clamp(Math.Round(BinaryPrimitives.ReadInt32LittleEndian(bytes) * gain), int.MinValue, int.MaxValue));
                break;
        }
    }

    private static Format ReadFormat(Stream stream)
    {
        using var reader = new BinaryReader(stream, System.Text.Encoding.ASCII, true);
        if (stream.Length < 12 || reader.ReadUInt32() != 0x46464952) throw new InvalidDataException("Expected RIFF.");
        long limit = reader.ReadUInt32() + 8L;
        if (limit > stream.Length || reader.ReadUInt32() != 0x45564157) throw new InvalidDataException("Invalid WAVE.");
        ushort tag = 0, channels = 0, align = 0, bits = 0;
        int rate = 0;
        long dataOffset = -1, dataLength = 0;
        while (stream.Position + 8 <= limit)
        {
            uint id = reader.ReadUInt32(), size = reader.ReadUInt32();
            long start = stream.Position, next = checked(start + size + (size & 1));
            if (next > limit) throw new InvalidDataException("Truncated WAVE chunk.");
            if (id == 0x20746d66)
            {
                if (size < 16) throw new InvalidDataException("Invalid WAVE format.");
                tag = reader.ReadUInt16(); channels = reader.ReadUInt16(); rate = reader.ReadInt32();
                reader.ReadUInt32(); align = reader.ReadUInt16(); bits = reader.ReadUInt16();
                if (tag == 0xfffe)
                {
                    if (size < 40) throw new InvalidDataException("Invalid extensible WAVE.");
                    stream.Position = start + 24;
                    tag = reader.ReadUInt16();
                }
            }
            else if (id == 0x61746164)
            {
                if (dataOffset >= 0) throw new InvalidDataException("Multiple WAVE data chunks.");
                dataOffset = start; dataLength = size;
            }
            stream.Position = next;
        }
        if (dataOffset < 0 || channels is < 1 or > 64 || rate is < 8000 or > 384000 ||
            align != channels * (bits / 8) || align == 0 || dataLength % align != 0 ||
            (tag == 1 ? bits is not (8 or 16 or 24 or 32) : tag != 3 || bits != 32))
            throw new InvalidDataException("Unsupported PCM format.");
        return new(tag, channels, rate, align, bits, dataOffset, dataLength / align);
    }
}
