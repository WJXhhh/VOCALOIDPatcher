using System;
using System.Collections.Generic;
using System.IO;

namespace VOCALOIDPatcher.BreathVolume;

internal readonly record struct BreathGainRegion(long BeginSample, long EndSample, byte Value);

internal static class BreathWaveProcessor
{
    private const int RiffHeaderSize = 12;

    public static void CreateAdjustedWave(
        string sourcePath,
        string destinationPath,
        IReadOnlyList<BreathGainRegion> regions,
        double fadeMilliseconds = 5.0)
    {
        ArgumentException.ThrowIfNullOrEmpty(sourcePath);
        ArgumentException.ThrowIfNullOrEmpty(destinationPath);

        var bytes = File.ReadAllBytes(sourcePath);
        var format = ParseWave(bytes);
        var fadeSamples = Math.Max(1L, (long)Math.Round(format.SampleRate * fadeMilliseconds / 1000.0));

        foreach (var region in regions)
            ApplyRegion(bytes, format, region, fadeSamples);

        var directory = Path.GetDirectoryName(destinationPath);
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);

        var temporaryPath = destinationPath + ".tmp";
        try
        {
            File.WriteAllBytes(temporaryPath, bytes);
            File.Move(temporaryPath, destinationPath, true);
        }
        finally
        {
            try
            {
                if (File.Exists(temporaryPath))
                    File.Delete(temporaryPath);
            }
            catch
            {
                // The caller will fall back to the original WAVE; temp cleanup is best effort.
            }
        }
    }

    public static unsafe void ApplyFloatBuffer(
        IntPtr samples,
        long numSamples,
        long bufferBeginSample,
        long processFromSample,
        IReadOnlyList<BreathGainRegion> regions,
        int sampleRate = 44100,
        double fadeMilliseconds = 5.0)
    {
        if (samples == IntPtr.Zero || numSamples <= 0 || numSamples > int.MaxValue)
            return;

        var fadeSamples = Math.Max(1L, (long)Math.Round(sampleRate * fadeMilliseconds / 1000.0));
        var firstLocalSample = Math.Clamp(processFromSample - bufferBeginSample, 0L, numSamples);
        var span = new Span<float>((void*)samples, checked((int)numSamples));

        for (var localSample = firstLocalSample; localSample < numSamples; localSample++)
        {
            var absoluteSample = bufferBeginSample + localSample;
            var gain = GainAt(absoluteSample, regions, fadeSamples);
            if (gain < 1.0)
                span[checked((int)localSample)] *= (float)gain;
        }
    }

    private static WaveFormat ParseWave(byte[] bytes)
    {
        if (bytes.Length < RiffHeaderSize ||
            !Matches(bytes, 0, "RIFF") ||
            !Matches(bytes, 8, "WAVE"))
            throw new InvalidDataException("Only RIFF/WAVE audio is supported.");

        ushort formatTag = 0;
        ushort channels = 0;
        uint sampleRate = 0;
        ushort blockAlign = 0;
        ushort bitsPerSample = 0;
        var dataOffset = -1;
        var dataLength = 0;

        for (var offset = RiffHeaderSize; offset + 8 <= bytes.Length;)
        {
            var chunkSize = checked((int)ReadUInt32(bytes, offset + 4));
            var chunkData = offset + 8;
            if (chunkData > bytes.Length || chunkSize > bytes.Length - chunkData)
                throw new InvalidDataException("The WAVE file contains a truncated chunk.");

            if (Matches(bytes, offset, "fmt "))
            {
                if (chunkSize < 16)
                    throw new InvalidDataException("The WAVE format chunk is invalid.");

                formatTag = ReadUInt16(bytes, chunkData);
                channels = ReadUInt16(bytes, chunkData + 2);
                sampleRate = ReadUInt32(bytes, chunkData + 4);
                blockAlign = ReadUInt16(bytes, chunkData + 12);
                bitsPerSample = ReadUInt16(bytes, chunkData + 14);

                if (formatTag == 0xfffe && chunkSize >= 40)
                    formatTag = ReadUInt16(bytes, chunkData + 24);
            }
            else if (Matches(bytes, offset, "data") && dataOffset < 0)
            {
                dataOffset = chunkData;
                dataLength = chunkSize;
            }

            offset = checked(chunkData + chunkSize + (chunkSize & 1));
        }

        if (dataOffset < 0 || channels == 0 || sampleRate == 0 || blockAlign == 0)
            throw new InvalidDataException("The WAVE file is missing required format or data chunks.");

        var bytesPerSample = (bitsPerSample + 7) / 8;
        if (bytesPerSample == 0 || blockAlign != channels * bytesPerSample)
            throw new InvalidDataException("The WAVE sample layout is unsupported.");

        if (formatTag == 1 && bitsPerSample is not (8 or 16 or 24 or 32) ||
            formatTag == 3 && bitsPerSample != 32 ||
            formatTag is not (1 or 3))
            throw new InvalidDataException($"WAVE format {formatTag}/{bitsPerSample}-bit is unsupported.");

        return new WaveFormat(formatTag, channels, checked((int)sampleRate), blockAlign,
            bitsPerSample, dataOffset, dataLength);
    }

    private static void ApplyRegion(byte[] bytes, WaveFormat format, BreathGainRegion region, long fadeSamples)
    {
        var frameCount = format.DataLength / format.BlockAlign;
        var begin = Math.Clamp(region.BeginSample, 0L, frameCount);
        var end = Math.Clamp(region.EndSample, begin, frameCount);
        if (begin >= end || region.Value >= 127)
            return;

        for (var frame = begin; frame < end; frame++)
        {
            var gain = GainAt(frame, region, fadeSamples);
            var frameOffset = checked(format.DataOffset + (int)(frame * format.BlockAlign));
            for (var channel = 0; channel < format.Channels; channel++)
            {
                var sampleOffset = frameOffset + channel * format.BytesPerSample;
                if (format.FormatTag == 3)
                    WriteSingle(bytes, sampleOffset, ReadSingle(bytes, sampleOffset) * (float)gain);
                else
                    ApplyIntegerGain(bytes, sampleOffset, format.BitsPerSample, gain);
            }
        }
    }

    private static double GainAt(long sample, IReadOnlyList<BreathGainRegion> regions, long fadeSamples)
    {
        var gain = 1.0;
        foreach (var region in regions)
        {
            if (sample < region.BeginSample || sample >= region.EndSample || region.Value >= 127)
                continue;

            gain = Math.Min(gain, GainAt(sample, region, fadeSamples));
        }

        return gain;
    }

    private static double GainAt(long sample, BreathGainRegion region, long fadeSamples)
    {
        var target = region.Value / 127.0;
        var fadeInEnd = Math.Min(region.EndSample, region.BeginSample + fadeSamples);
        var fadeOutBegin = Math.Max(region.BeginSample, region.EndSample - fadeSamples);

        if (sample < fadeInEnd)
        {
            var position = (sample - region.BeginSample + 1.0) / Math.Max(1.0, fadeInEnd - region.BeginSample);
            return 1.0 + (target - 1.0) * position;
        }

        if (sample >= fadeOutBegin)
        {
            var position = (region.EndSample - sample) / Math.Max(1.0, region.EndSample - fadeOutBegin);
            return 1.0 + (target - 1.0) * position;
        }

        return target;
    }

    private static void ApplyIntegerGain(byte[] bytes, int offset, int bitsPerSample, double gain)
    {
        switch (bitsPerSample)
        {
            case 8:
            {
                var signed = bytes[offset] - 128;
                bytes[offset] = (byte)Math.Clamp((int)Math.Round(signed * gain) + 128, 0, 255);
                break;
            }
            case 16:
            {
                var sample = (short)(bytes[offset] | bytes[offset + 1] << 8);
                WriteInt16(bytes, offset, (short)Math.Clamp((int)Math.Round(sample * gain), short.MinValue, short.MaxValue));
                break;
            }
            case 24:
            {
                var sample = bytes[offset] | bytes[offset + 1] << 8 | bytes[offset + 2] << 16;
                if ((sample & 0x800000) != 0)
                    sample |= unchecked((int)0xff000000);
                var adjusted = Math.Clamp((long)Math.Round(sample * gain), -8388608L, 8388607L);
                bytes[offset] = (byte)adjusted;
                bytes[offset + 1] = (byte)(adjusted >> 8);
                bytes[offset + 2] = (byte)(adjusted >> 16);
                break;
            }
            case 32:
            {
                var sample = ReadInt32(bytes, offset);
                var adjusted = Math.Clamp((long)Math.Round(sample * gain), int.MinValue, int.MaxValue);
                WriteInt32(bytes, offset, (int)adjusted);
                break;
            }
        }
    }

    private static bool Matches(byte[] bytes, int offset, string value)
        => offset >= 0 && offset + value.Length <= bytes.Length &&
           bytes.AsSpan(offset, value.Length).SequenceEqual(System.Text.Encoding.ASCII.GetBytes(value));

    private static ushort ReadUInt16(byte[] bytes, int offset)
        => (ushort)(bytes[offset] | bytes[offset + 1] << 8);

    private static uint ReadUInt32(byte[] bytes, int offset)
        => (uint)(bytes[offset] | bytes[offset + 1] << 8 | bytes[offset + 2] << 16 | bytes[offset + 3] << 24);

    private static int ReadInt32(byte[] bytes, int offset)
        => bytes[offset] | bytes[offset + 1] << 8 | bytes[offset + 2] << 16 | bytes[offset + 3] << 24;

    private static float ReadSingle(byte[] bytes, int offset)
        => BitConverter.Int32BitsToSingle(ReadInt32(bytes, offset));

    private static void WriteInt16(byte[] bytes, int offset, short value)
    {
        bytes[offset] = (byte)value;
        bytes[offset + 1] = (byte)(value >> 8);
    }

    private static void WriteInt32(byte[] bytes, int offset, int value)
    {
        bytes[offset] = (byte)value;
        bytes[offset + 1] = (byte)(value >> 8);
        bytes[offset + 2] = (byte)(value >> 16);
        bytes[offset + 3] = (byte)(value >> 24);
    }

    private static void WriteSingle(byte[] bytes, int offset, float value)
        => WriteInt32(bytes, offset, BitConverter.SingleToInt32Bits(value));

    private readonly record struct WaveFormat(
        ushort FormatTag,
        ushort Channels,
        int SampleRate,
        ushort BlockAlign,
        ushort BitsPerSample,
        int DataOffset,
        int DataLength)
    {
        public int BytesPerSample => (BitsPerSample + 7) / 8;
    }
}
