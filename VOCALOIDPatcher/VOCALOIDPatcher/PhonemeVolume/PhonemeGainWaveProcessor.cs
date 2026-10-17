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
    private readonly record struct Range(long Begin, long End, double Gain);

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
        var bytes = new byte[checked(4096 * format.Align)];
        for (long at = begin; at < end;)
        {
            int count = (int)Math.Min(4096, end - at), length = count * format.Align;
            long offset = checked(format.Offset + at * format.Align);
            stream.Position = offset;
            stream.ReadExactly(bytes.AsSpan(0, length));
            for (int frame = 0; frame < count; frame++)
            {
                long sample = at + frame;
                double gain = 1;
                foreach (var range in ranges)
                {
                    if (sample < range.Begin || sample >= range.End) continue;
                    // Smooth strictly inside the selected phoneme. The 5 ms
                    // ramp shortens for short phonemes and reaches its target.
                    double width = Math.Min(fade, Math.Max(.5, (range.End - range.Begin - 1) / 2d));
                    double edge = Math.Min(sample - range.Begin + .5, range.End - sample - .5);
                    double weight = .5 - .5 * Math.Cos(Math.PI * Math.Clamp(edge / width, 0, 1));
                    gain *= 1 + (range.Gain - 1) * weight;
                }
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
