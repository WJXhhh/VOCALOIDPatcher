using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;

namespace VOCALOIDPatcher.VoiceComp;

// Bounded streaming compositor. No native wrappers or Dispatcher access here.
internal static class CompWave
{
    internal sealed record Format(int Rate, int Channels, int Bits, int Tag, long Offset, long Frames)
    { internal int Align => checked(Channels * (Bits / 8)); }
    internal static Format Inspect(string path)
    {
        using var stream = File.OpenRead(path); return Inspect(stream);
    }
    private static Format Inspect(Stream stream)
    {
        using var reader = new BinaryReader(stream, System.Text.Encoding.ASCII, true);
        if (stream.Length < 12 || reader.ReadUInt32() != 0x46464952) throw new InvalidDataException("Expected RIFF audio.");
        long limit = checked(reader.ReadUInt32() + 8L);
        if (limit > stream.Length || reader.ReadUInt32() != 0x45564157) throw new InvalidDataException("Invalid WAVE container.");
        int rate = 0, channels = 0, bits = 0, tag = 0, align = 0; long offset = -1, bytes = 0;
        while (stream.Position + 8 <= limit)
        {
            uint id = reader.ReadUInt32(), size = reader.ReadUInt32(); long start = stream.Position;
            long next = checked(start + size + (size & 1));
            if (start + size > limit) throw new InvalidDataException("Truncated audio chunk.");
            if (id == 0x20746d66)
            {
                if (size < 16) throw new InvalidDataException("Invalid audio format.");
                tag = reader.ReadUInt16(); channels = reader.ReadUInt16(); rate = reader.ReadInt32();
                _ = reader.ReadUInt32(); align = reader.ReadUInt16(); bits = reader.ReadUInt16();
            }
            else if (id == 0x61746164)
            {
                if (offset >= 0) throw new InvalidDataException("Ambiguous audio data.");
                offset = start; bytes = size;
            }
            stream.Position = next;
        }
        if (channels is < 1 or > 2 || rate is < 8000 or > 192000 ||
            !(tag == 1 && bits is 16 or 24 or 32 || tag == 3 && bits == 32) ||
            align != channels * (bits / 8) || offset < 0 || bytes % align != 0)
            throw new InvalidDataException("Unsupported or malformed PCM audio.");
        return new(rate, channels, bits, tag, offset, bytes / align);
    }

    internal static double Decode(ReadOnlySpan<byte> bytes, Format format)
    {
        double value = format.Tag == 3 ? BitConverter.Int32BitsToSingle(BinaryPrimitives.ReadInt32LittleEndian(bytes)) :
            format.Bits switch
            {
                16 => BinaryPrimitives.ReadInt16LittleEndian(bytes) / 32768d,
                24 => ((bytes[0] | bytes[1] << 8 | bytes[2] << 16) << 8 >> 8) / 8388608d,
                32 => BinaryPrimitives.ReadInt32LittleEndian(bytes) / 2147483648d,
                _ => throw new InvalidDataException("Unsupported PCM depth.")
            };
        return double.IsFinite(value) ? value : throw new InvalidDataException("Non-finite source PCM.");
    }

    internal static double Envelope(long sample, long length, int fadeIn, int fadeOut)
    {
        double gain = 1;
        if (fadeIn > 0 && sample < fadeIn) gain *= .5 - .5 * Math.Cos(Math.PI * (sample + .5) / fadeIn);
        if (fadeOut > 0 && length - sample <= fadeOut) gain *= .5 - .5 * Math.Cos(Math.PI * (length - sample - .5) / fadeOut);
        return gain;
    }

    internal static long Mix(string destination, CompRenderPlan plan, CancellationToken cancellation = default)
        => MixCore(destination, plan, null, cancellation);

    internal static (long Clipped, float[] Peaks) MixWithPeaks(string destination, CompRenderPlan plan,
        CancellationToken cancellation = default, int bins = 1024)
    {
        if (bins is < 1 or > 4096) throw new ArgumentOutOfRangeException(nameof(bins));
        var peaks = new float[bins];
        return (MixCore(destination, plan, peaks, cancellation), peaks);
    }

    private static long MixCore(string destination, CompRenderPlan plan, float[]? peaks, CancellationToken cancellation)
    {
        if (plan.SampleRate is < 8000 or > 192000 || plan.Samples < 1 || plan.Slices.IsDefaultOrEmpty ||
            plan.Samples > (uint.MaxValue - 36L) / 4) throw new InvalidDataException("Invalid composition length.");
        var opened = new Dictionary<string, (FileStream Stream, Format Format)>(StringComparer.OrdinalIgnoreCase);
        string temporary = destination + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            int channels = 0;
            foreach (var slice in plan.Slices)
            {
                slice.Validate();
                if (Path.GetFullPath(slice.Path).Equals(Path.GetFullPath(destination), StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("Composition must not overwrite a source.");
                if (!opened.TryGetValue(slice.Path, out var source))
                {
                    var stream = new FileStream(slice.Path, FileMode.Open, FileAccess.Read, FileShare.Read);
                    try { source = (stream, Inspect(stream)); opened.Add(slice.Path, source); }
                    catch { stream.Dispose(); throw; }
                }
                channels = channels == 0 ? source.Format.Channels : channels;
                if (source.Format.Rate != plan.SampleRate || source.Format.Channels != channels ||
                    slice.SourceBegin + slice.Length > source.Format.Frames || slice.OutputEnd > plan.Samples)
                    throw new InvalidDataException("Composition source or coordinates mismatch.");
            }
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(destination))!);
            using (var output = new BinaryWriter(File.Create(temporary)))
            {
                uint dataBytes = checked((uint)(plan.Samples * channels * 2));
                output.Write(0x46464952u); output.Write(checked(dataBytes + 36)); output.Write(0x45564157u);
                output.Write(0x20746d66u); output.Write(16u); output.Write((ushort)1); output.Write((ushort)channels);
                output.Write(plan.SampleRate); output.Write(plan.SampleRate * channels * 2);
                output.Write((ushort)(channels * 2)); output.Write((ushort)16); output.Write(0x61746164u); output.Write(dataBytes);
                var mix = new double[4096 * channels]; var bytes = new byte[4096 * channels * 4];
                long clipped = 0;
                for (long at = 0; at < plan.Samples; at += 4096)
                {
                    cancellation.ThrowIfCancellationRequested();
                    int count = (int)Math.Min(4096, plan.Samples - at); Array.Clear(mix);
                    foreach (var slice in plan.Slices)
                    {
                        long begin = Math.Max(at, slice.OutputBegin), end = Math.Min(at + count, slice.OutputEnd);
                        if (end <= begin) continue;
                        var source = opened[slice.Path]; var format = source.Format;
                        int frames = checked((int)(end - begin));
                        source.Stream.Position = checked(format.Offset + (slice.SourceBegin + begin - slice.OutputBegin) * format.Align);
                        source.Stream.ReadExactly(bytes.AsSpan(0, frames * format.Align));
                        for (int frame = 0; frame < frames; frame++)
                        {
                            double gain = Envelope(begin - slice.OutputBegin + frame, slice.Length, slice.FadeIn, slice.FadeOut);
                            for (int channel = 0; channel < channels; channel++)
                                mix[(begin - at + frame) * channels + channel] +=
                                    Decode(bytes.AsSpan(frame * format.Align + channel * (format.Bits / 8)), format) * gain;
                        }
                    }
                    for (int i = 0; i < count * channels; i++)
                    {
                        double value = Math.Round(mix[i] * 32768);
                        if (value < short.MinValue || value > short.MaxValue) clipped++;
                        short sample = (short)Math.Clamp(value, short.MinValue, short.MaxValue);
                        BinaryPrimitives.WriteInt16LittleEndian(bytes.AsSpan(i * 2), sample);
                    }
                    if (peaks != null)
                    {
                        // Visit each bin's portion of this quantized block.
                        // Integer division is needed per bin, not per sample.
                        long frame = at;
                        while (frame < at + count)
                        {
                            int bin = (int)(frame * peaks.Length / plan.Samples);
                            long end = Math.Min(at + count, ((bin + 1L) * plan.Samples + peaks.Length - 1) / peaks.Length);
                            float peak = peaks[bin];
                            for (int i = (int)(frame - at) * channels; i < (end - at) * channels; i++)
                            {
                                // Match Peaks on rounded and clipped PCM16.
                                short sample = BinaryPrimitives.ReadInt16LittleEndian(bytes.AsSpan(i * 2));
                                peak = Math.Max(peak, (float)Math.Abs(sample / 32768d));
                            }
                            peaks[bin] = peak; frame = end;
                        }
                    }
                    output.Write(bytes, 0, count * channels * 2);
                }
                output.Flush();
                output.Dispose(); cancellation.ThrowIfCancellationRequested();
                File.Move(temporary, destination, true); return clipped;
            }
        }
        finally
        {
            foreach (var source in opened.Values) source.Stream.Dispose();
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    internal static float[] Peaks(string path, int bins = 1024)
    {
        if (bins is < 1 or > 4096) throw new ArgumentOutOfRangeException(nameof(bins));
        using var stream = File.OpenRead(path); var format = Inspect(stream); stream.Position = format.Offset;
        var result = new float[bins]; var bytes = new byte[4096 * format.Align];
        for (long at = 0; at < format.Frames; at += 4096)
        {
            int frames = (int)Math.Min(4096, format.Frames - at); stream.ReadExactly(bytes.AsSpan(0, frames * format.Align));
            for (int i = 0; i < frames; i++)
                for (int c = 0; c < format.Channels; c++)
                {
                    int bin = (int)((at + i) * bins / Math.Max(1, format.Frames));
                    result[bin] = Math.Max(result[bin], (float)Math.Abs(Decode(bytes.AsSpan(i * format.Align + c * (format.Bits / 8)), format)));
                }
        }
        return result;
    }
}
