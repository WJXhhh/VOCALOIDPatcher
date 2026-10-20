using System;
using System.Buffers.Binary;
using System.IO;
using System.Threading;

namespace VOCALOIDPatcher.AceParameters;

internal static class AceAcousticWave
{
    // A bounded offline operation. Reject oversized/malformed files before
    // allocating decoded channels; the host keeps its original audio on error.
    private const long MaximumBytes = 128L * 1024 * 1024;

    internal static void Create(string source, string destination, AceAcousticControls controls,
        CancellationToken cancellation = default, bool compatiblePcm16 = false)
    {
        if (Path.GetFullPath(source).Equals(Path.GetFullPath(destination), StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Acoustic processing requires a separate output.");
        cancellation.ThrowIfCancellationRequested();
        var info = new FileInfo(source);
        if (info.Length > MaximumBytes) throw new InvalidDataException("Acoustic input exceeds the processing limit.");
        byte[] bytes = File.ReadAllBytes(source);
        var (channels, rate, bits, tag, offset, frames) = ReadFormat(bytes);
        if ((long)frames * channels > 16_000_000)
            throw new InvalidDataException("Decoded acoustic input exceeds the processing limit.");
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(destination))!);
        string temporary = destination + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            if (controls.IsNeutral) File.WriteAllBytes(temporary, bytes);
            else
            {
                var processed = new float[channels][];
                for (int c = 0; c < channels; c++)
                {
                    var pcm = new float[frames];
                    for (int f = 0; f < frames; f++)
                    {
                        int at = offset + (f * channels + c) * (bits / 8);
                        pcm[f] = (tag, bits) switch
                        {
                            (3, 32) => BitConverter.Int32BitsToSingle(BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(at))),
                            (1, 16) => BinaryPrimitives.ReadInt16LittleEndian(bytes.AsSpan(at)) / 32768f,
                            (1, 24) => ((bytes[at] | bytes[at + 1] << 8 | bytes[at + 2] << 16) << 8 >> 8) / 8388608f,
                            (1, 32) => (float)(BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(at)) / 2147483648d),
                            _ => throw new InvalidDataException("Unsupported acoustic input PCM.")
                        };
                    }
                    processed[c] = AceAcousticProcessor.Process(pcm, rate, controls, cancellation);
                }
                using var writer = new BinaryWriter(File.Create(temporary));
                int sampleBytes = compatiblePcm16 ? 2 : 4;
                int length = checked(frames * channels * sampleBytes);
                // VOCALOID's managed WaveFile reader accepts only PCM16. The
                // worker's float path keeps headroom for standalone analysis.
                writer.Write(0x46464952); writer.Write(length + 36); writer.Write(0x45564157);
                writer.Write(0x20746d66); writer.Write(16); writer.Write((short)(compatiblePcm16 ? 1 : 3)); writer.Write((short)channels);
                writer.Write(rate); writer.Write(rate * channels * sampleBytes); writer.Write((short)(channels * sampleBytes)); writer.Write((short)(sampleBytes * 8));
                writer.Write(0x61746164); writer.Write(length);
                for (int f = 0; f < frames; f++)
                {
                    if ((f & 4095) == 0) cancellation.ThrowIfCancellationRequested();
                    for (int c = 0; c < channels; c++)
                    {
                        double sample = processed[c][f];
                        if (compatiblePcm16)
                        {
                            // A continuous soft knee protects integer output
                            // when Energy consumes the available peak headroom.
                            if (Math.Abs(sample) > .9)
                                sample = Math.Sign(sample) * (.9 + .1 * Math.Tanh((Math.Abs(sample) - .9) / .1));
                            writer.Write((short)Math.Clamp(Math.Round(sample * 32768), -32768, 32767));
                        }
                        else writer.Write((float)sample);
                    }
                }
            }
            cancellation.ThrowIfCancellationRequested();
            File.Move(temporary, destination, false);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    private static (int Channels, int Rate, int Bits, int Tag, int Offset, int Frames) ReadFormat(byte[] bytes)
    {
        using var reader = new BinaryReader(new MemoryStream(bytes));
        if (bytes.Length < 12 || reader.ReadUInt32() != 0x46464952) throw new InvalidDataException("Expected RIFF.");
        long limit = reader.ReadUInt32() + 8L;
        if (limit > bytes.Length || limit < 12 || reader.ReadUInt32() != 0x45564157) throw new InvalidDataException("Invalid WAVE.");
        int tag = 0, channels = 0, rate = 0, bits = 0, align = 0, data = -1, length = 0;
        while (reader.BaseStream.Position + 8 <= limit)
        {
            uint id = reader.ReadUInt32(), size = reader.ReadUInt32();
            long start = reader.BaseStream.Position, next = start + size + (size & 1);
            if (next > limit) throw new InvalidDataException("Truncated WAVE chunk.");
            if (id == 0x20746d66)
            {
                if (size < 16) throw new InvalidDataException("Invalid WAVE format.");
                tag = reader.ReadUInt16(); channels = reader.ReadUInt16(); rate = reader.ReadInt32();
                reader.ReadUInt32(); align = reader.ReadUInt16(); bits = reader.ReadUInt16();
                if (tag == 0xfffe)
                {
                    if (size < 40 || reader.ReadUInt16() < 22) throw new InvalidDataException("Invalid extensible WAVE.");
                    int validBits = reader.ReadUInt16(); reader.ReadUInt32();
                    var subtype = new Guid(reader.ReadBytes(16));
                    if (validBits != bits) throw new InvalidDataException("Packed valid bits are unsupported.");
                    tag = subtype == new Guid("00000001-0000-0010-8000-00aa00389b71") ? 1 :
                        subtype == new Guid("00000003-0000-0010-8000-00aa00389b71") ? 3 : 0;
                }
            }
            else if (id == 0x61746164)
            {
                if (data >= 0) throw new InvalidDataException("Multiple WAVE data chunks.");
                data = checked((int)start); length = checked((int)size);
            }
            reader.BaseStream.Position = next;
        }
        if (data < 0 || channels is < 1 or > 2 || rate is < 16000 or > 192000 ||
            bits is not (16 or 24 or 32) || align != channels * (bits / 8) || length % align != 0 ||
            (tag != 1 && tag != 3) || (tag == 3 && bits != 32))
            throw new InvalidDataException("Unsupported acoustic WAVE layout.");
        return (channels, rate, bits, tag, data, length / align);
    }
}
