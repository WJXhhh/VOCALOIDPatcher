using System;
using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Text;

namespace VOCALOIDPatcher.BreathVolume;

internal static class BreathPhonemeClassifier
{
    public static bool IsNativeBreathPhoneme(string? phoneme)
        => phoneme != null &&
           (phoneme.Equals("br", StringComparison.OrdinalIgnoreCase) ||
            phoneme.Equals("SilBreath", StringComparison.OrdinalIgnoreCase) ||
            phoneme.Equals("SilBreath+", StringComparison.OrdinalIgnoreCase));
}

internal static class NativePhonemeInspector
{
    private const int ProbeSize = 96;
    private const int MaxTextLength = 32;
    private static readonly IntPtr CurrentProcess = new(-1);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ReadProcessMemory(
        IntPtr process,
        IntPtr address,
        [Out] byte[] buffer,
        nuint size,
        out nuint bytesRead);

    public static string ReadName(IntPtr pointer)
    {
        if (!TryRead(pointer, ProbeSize, out var bytes))
            return string.Empty;

        if (TryReadAscii(bytes, 0, MaxTextLength, out var direct))
            return direct;

        string? fallback = null;
        for (var offset = 0; offset + 32 <= bytes.Length; offset += 8)
        {
            if (!TryReadMsvcString(bytes, offset, out var candidate))
                continue;
            if (BreathPhonemeClassifier.IsNativeBreathPhoneme(candidate))
                return candidate;
            fallback ??= candidate;
        }
        for (var offset = 0; offset + sizeof(long) <= 64; offset += sizeof(long))
        {
            var nestedAddress = BinaryPrimitives.ReadInt64LittleEndian(bytes.AsSpan(offset, sizeof(long)));
            if (!IsProbablePointer(nestedAddress) ||
                !TryRead(new IntPtr(nestedAddress), MaxTextLength + 1, out var nested) ||
                !TryReadAscii(nested, 0, MaxTextLength, out var candidate))
                continue;
            if (BreathPhonemeClassifier.IsNativeBreathPhoneme(candidate))
                return candidate;
            fallback ??= candidate;
        }

        return fallback ?? string.Empty;
    }

    private static bool TryReadMsvcString(byte[] bytes, int offset, out string text)
    {
        text = string.Empty;
        var lengthValue = BinaryPrimitives.ReadUInt64LittleEndian(bytes.AsSpan(offset + 16, sizeof(ulong)));
        var capacity = BinaryPrimitives.ReadUInt64LittleEndian(bytes.AsSpan(offset + 24, sizeof(ulong)));
        if (lengthValue is 0 or > MaxTextLength || capacity < lengthValue || capacity > 0x100000)
            return false;

        var length = (int)lengthValue;
        if (capacity <= 15)
            return TryReadAscii(bytes, offset, length, requireTerminator: true, out text);

        var address = BinaryPrimitives.ReadInt64LittleEndian(bytes.AsSpan(offset, sizeof(long)));
        return IsProbablePointer(address) &&
               TryRead(new IntPtr(address), length + 1, out var external) &&
               TryReadAscii(external, 0, length, requireTerminator: true, out text);
    }

    private static bool TryReadAscii(byte[] bytes, int offset, int maximum, out string text)
    {
        text = string.Empty;
        var available = Math.Min(maximum, bytes.Length - offset);
        if (available <= 0)
            return false;
        var length = Array.IndexOf(bytes, (byte)0, offset, available);
        if (length < 0)
            return false;
        length -= offset;
        return TryReadAscii(bytes, offset, length, requireTerminator: true, out text);
    }

    private static bool TryReadAscii(
        byte[] bytes,
        int offset,
        int length,
        bool requireTerminator,
        out string text)
    {
        text = string.Empty;
        if (length <= 0 || length > MaxTextLength || offset < 0 || offset + length > bytes.Length ||
            requireTerminator && (offset + length >= bytes.Length || bytes[offset + length] != 0))
            return false;

        for (var index = 0; index < length; index++)
            if (bytes[offset + index] is < 0x20 or > 0x7e)
                return false;

        text = Encoding.ASCII.GetString(bytes, offset, length);
        return true;
    }

    private static bool TryRead(IntPtr address, int size, out byte[] bytes)
    {
        bytes = new byte[size];
        if (address == IntPtr.Zero ||
            !ReadProcessMemory(CurrentProcess, address, bytes, (nuint)size, out var bytesRead) ||
            bytesRead == 0)
            return false;
        if (bytesRead < (nuint)size)
            Array.Resize(ref bytes, checked((int)bytesRead));
        return true;
    }

    private static bool IsProbablePointer(long value)
        => value >= 0x10000 && value <= 0x00007fff_ffff_ffff;

}
