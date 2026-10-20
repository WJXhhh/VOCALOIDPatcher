using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace VOCALOIDPatcher.ConsonantTiming.Smoothing;

/// <summary>
/// File- and buffer-level WAVE audio processor for applying transition smoothing
/// directly to rendered audio files without modifying VOCALOID internal engine state.
/// Supports 16-bit, 24-bit, 32-bit integer PCM and 32-bit float WAVE formats.
/// </summary>
public static class WaveJunctionProcessor
{
    private const int RiffHeaderSize = 12;

    private readonly record struct WaveFormat(
        ushort Tag,
        int Channels,
        int SampleRate,
        int BlockAlign,
        int BitsPerSample,
        long DataOffset,
        long DataLength);

    /// <summary>
    /// Information describing a transition junction inside a WAVE file.
    /// </summary>
    public readonly record struct JunctionInfo(
        long JunctionSample,
        double? LeftNominalDb,
        double? RightNominalDb);

    /// <summary>
    /// Smooths a list of transition junctions inside a WAVE file and writes the result atomically.
    /// </summary>
    public static IReadOnlyList<SmoothingResult> ProcessWaveFile(
        string sourcePath,
        string destinationPath,
        IReadOnlyList<JunctionInfo> junctions,
        TransitionSmootherConfig? config = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(sourcePath);
        ArgumentException.ThrowIfNullOrEmpty(destinationPath);

        byte[] bytes = File.ReadAllBytes(sourcePath);
        var format = ParseWaveFormat(bytes);

        long totalFrames = format.DataLength / format.BlockAlign;
        var validJunctions = (junctions ?? [])
            .Where(j => j.JunctionSample > 0 && j.JunctionSample < totalFrames)
            .OrderBy(j => j.JunctionSample)
            .ToList();

        if (validJunctions.Count == 0)
        {
            if (!Path.GetFullPath(sourcePath).Equals(Path.GetFullPath(destinationPath), StringComparison.OrdinalIgnoreCase))
                File.Copy(sourcePath, destinationPath, true);

            return [];
        }

        // Decode audio to float channels
        float[][] channels = DecodeFrames(bytes, format, 0, (int)totalFrames);

        var results = new List<SmoothingResult>(validJunctions.Count);

        // Process each junction across all channels
        for (int j = 0; j < validJunctions.Count; j++)
        {
            var junction = validJunctions[j];
            SmoothingResult junctionResult = default!;

            for (int ch = 0; ch < format.Channels; ch++)
            {
                var r = TransitionSmoother.SmoothJunction(
                    channels[ch].AsSpan(),
                    (int)junction.JunctionSample,
                    format.SampleRate,
                    config,
                    junction.LeftNominalDb,
                    junction.RightNominalDb);

                if (ch == 0) junctionResult = r;
            }

            results.Add(junctionResult);
        }

        // Encode back to bytes
        EncodeFrames(channels, bytes, format, 0, (int)totalFrames);

        // Atomic write
        string? dir = Path.GetDirectoryName(destinationPath);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

        string tempPath = destinationPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllBytes(tempPath, bytes);
            File.Move(tempPath, destinationPath, true);
        }
        finally
        {
            if (File.Exists(tempPath))
            {
                try { File.Delete(tempPath); }
                catch { /* Best effort cleanup */ }
            }
        }

        return results;
    }

    /// <summary>
    /// Smooths a transition junction inside a WAVE file and writes the result atomically.
    /// </summary>
    public static SmoothingResult ProcessWaveFile(
        string sourcePath,
        string destinationPath,
        long junctionSample,
        TransitionSmootherConfig? config = null,
        double? leftNominalDb = null,
        double? rightNominalDb = null)
    {
        var list = ProcessWaveFile(
            sourcePath,
            destinationPath,
            [new JunctionInfo(junctionSample, leftNominalDb, rightNominalDb)],
            config);

        return list.Count > 0 ? list[0] : new SmoothingResult
        {
            Intervened = false,
            SpliceOffsetSamples = 0,
            SpliceOffsetMs = 0.0,
            CrossfadeDurationMs = 0.0,
            LeftMeasuredDb = -120f,
            RightMeasuredDb = -120f,
            VolumeDifferenceDb = 0f,
            PhaseAligned = false
        };
    }

    private static WaveFormat ParseWaveFormat(byte[] bytes)
    {
        if (bytes.Length < RiffHeaderSize ||
            bytes[0] != (byte)'R' || bytes[1] != (byte)'I' || bytes[2] != (byte)'F' || bytes[3] != (byte)'F' ||
            bytes[8] != (byte)'W' || bytes[9] != (byte)'A' || bytes[10] != (byte)'V' || bytes[11] != (byte)'E')
        {
            throw new InvalidDataException("Only RIFF/WAVE audio is supported.");
        }

        ushort tag = 0, channels = 0, bits = 0, align = 0;
        int rate = 0;
        long dataOffset = -1, dataLength = 0;

        for (int offset = RiffHeaderSize; offset + 8 <= bytes.Length;)
        {
            uint chunkSize = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(offset + 4));
            int chunkData = offset + 8;
            if (chunkData > bytes.Length || chunkSize > (uint)(bytes.Length - chunkData))
                throw new InvalidDataException("Truncated WAVE chunk.");

            bool isFmt = bytes[offset] == (byte)'f' && bytes[offset + 1] == (byte)'m' && bytes[offset + 2] == (byte)'t' && bytes[offset + 3] == (byte)' ';
            bool isData = bytes[offset] == (byte)'d' && bytes[offset + 1] == (byte)'a' && bytes[offset + 2] == (byte)'t' && bytes[offset + 3] == (byte)'a';

            if (isFmt)
            {
                if (chunkSize < 16) throw new InvalidDataException("Invalid fmt chunk size.");
                tag = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(chunkData));
                channels = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(chunkData + 2));
                rate = (int)BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(chunkData + 4));
                align = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(chunkData + 12));
                bits = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(chunkData + 14));

                if (tag == 0xfffe && chunkSize >= 40)
                    tag = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(chunkData + 24));
            }
            else if (isData && dataOffset < 0)
            {
                dataOffset = chunkData;
                dataLength = chunkSize;
            }

            offset = checked(chunkData + (int)chunkSize + ((int)chunkSize & 1));
        }

        if (dataOffset < 0 || channels == 0 || rate == 0 || align == 0)
            throw new InvalidDataException("WAVE missing required format or data chunks.");

        return new WaveFormat(tag, channels, rate, align, bits, dataOffset, dataLength);
    }

    private static float[][] DecodeFrames(byte[] bytes, WaveFormat format, int startFrame, int frameCount)
    {
        var channels = new float[format.Channels][];
        for (int ch = 0; ch < format.Channels; ch++)
            channels[ch] = new float[frameCount];

        int bytesPerSample = format.BitsPerSample / 8;
        int frameSize = format.BlockAlign;

        for (int f = 0; f < frameCount; f++)
        {
            long frameOffset = format.DataOffset + (long)(startFrame + f) * frameSize;
            for (int ch = 0; ch < format.Channels; ch++)
            {
                int sampleOffset = (int)(frameOffset + ch * bytesPerSample);
                channels[ch][f] = DecodeSample(bytes, sampleOffset, format);
            }
        }

        return channels;
    }

    private static float DecodeSample(byte[] bytes, int offset, WaveFormat format)
    {
        if (format.Tag == 3)
            return BitConverter.Int32BitsToSingle(BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(offset)));
        return format.BitsPerSample switch
        {
            16 => BinaryPrimitives.ReadInt16LittleEndian(bytes.AsSpan(offset)) / 32768f,
            24 => ((bytes[offset] | bytes[offset + 1] << 8 | bytes[offset + 2] << 16) << 8 >> 8) / 8388608f,
            32 => (float)(BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(offset)) / 2147483648.0),
            _ => throw new InvalidDataException("Unsupported PCM sample size.")
        };
    }

    private static void EncodeFrames(float[][] channels, byte[] bytes, WaveFormat format, int startFrame, int frameCount)
    {
        int bytesPerSample = format.BitsPerSample / 8;
        int frameSize = format.BlockAlign;

        for (int f = 0; f < frameCount; f++)
        {
            long frameOffset = format.DataOffset + (long)(startFrame + f) * frameSize;
            for (int ch = 0; ch < format.Channels; ch++)
            {
                int sampleOffset = (int)(frameOffset + ch * bytesPerSample);
                float val = channels[ch][f];
                // Preserve unmodified PCM bytes, including 32-bit integers
                // whose low bits cannot be represented by the float buffer.
                if (val.Equals(DecodeSample(bytes, sampleOffset, format))) continue;

                if (format.Tag == 3) // IEEE float
                {
                    BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(sampleOffset), BitConverter.SingleToInt32Bits(val));
                }
                else switch (format.BitsPerSample)
                {
                    case 16:
                        short s16 = (short)Math.Clamp(Math.Round(val * 32768.0), short.MinValue, short.MaxValue);
                        BinaryPrimitives.WriteInt16LittleEndian(bytes.AsSpan(sampleOffset), s16);
                        break;
                    case 24:
                        int s24 = (int)Math.Clamp(Math.Round(val * 8388608.0), -8388608, 8388607);
                        bytes[sampleOffset] = (byte)s24;
                        bytes[sampleOffset + 1] = (byte)(s24 >> 8);
                        bytes[sampleOffset + 2] = (byte)(s24 >> 16);
                        break;
                    case 32:
                        int s32 = (int)Math.Clamp(Math.Round(val * 2147483648.0), int.MinValue, int.MaxValue);
                        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(sampleOffset), s32);
                        break;
                }
            }
        }
    }
}
