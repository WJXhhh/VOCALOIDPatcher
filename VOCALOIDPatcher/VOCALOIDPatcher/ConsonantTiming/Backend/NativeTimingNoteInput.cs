using System;
using System.Runtime.InteropServices;

namespace VOCALOIDPatcher.ConsonantTiming;

// One owned NOTE message from the native encoder, never arbitrary live MIDI.
// The same commit boundary is used by the isolated render adapter and planner.
internal static unsafe class NativeTimingNoteInput
{
    internal static void Feed(nint state, int frame, ReadOnlySpan<byte> bytes, float coefficient)
    {
        if (!float.IsFinite(coefficient) || coefficient is < 0 or > 1 ||
            frame < 0 || bytes.Length == 0 || bytes.Length > 32768 || bytes.Length % 8 != 0)
            throw new ArgumentException("Invalid prepared timing input.");
        int msb = -1, lsb = -1, commit = -1, count = 0;
        for (int offset = 0; offset < bytes.Length; offset += 8)
        {
            if (bytes[offset + 4] != 0xb0) continue;
            int controller = bytes[offset + 5], value = bytes[offset + 6];
            if (controller == 99) { msb = value; lsb = -1; }
            else if (controller == 98) lsb = value;
            else if (controller == 6 && msb == 0x50 && lsb == 0x7f) { commit = offset; count++; }
        }
        if (count != 1 || commit <= 0) throw new NotSupportedException("Expected one complete native NOTE.");
        Parse(state, frame, bytes[..commit]);
        SetCoefficient(state, coefficient);
        Parse(state, frame, bytes[commit..]);
    }

    internal static void SetCoefficient(nint state, float coefficient)
    {
        if (!float.IsFinite(coefficient) || coefficient is < 0 or > 1) throw new ArgumentOutOfRangeException(nameof(coefficient));
        int slot = Marshal.ReadInt32(state + LocalTimingNative.Profile.StateParser, LocalTimingNative.Profile.ParserSlot);
        if (slot is < 0 or > 1) throw new InvalidOperationException("Invalid timing parser slot.");
        *(float*)(state + LocalTimingNative.Profile.StateParser + LocalTimingNative.Profile.ParserNotes + slot * LocalTimingNative.Profile.NoteStride + LocalTimingNative.Profile.NoteCoefficient) = coefficient;
    }

    private static void Parse(nint state, int frame, ReadOnlySpan<byte> bytes)
    {
        fixed (byte* payload = bytes)
        {
            long* input = stackalloc long[2]; input[0] = bytes.Length / 8; input[1] = (long)payload;
            ((delegate* unmanaged<nint, int, nint, void>)(LocalTimingNative.Dse + LocalTimingNative.Profile.DseParse))(
                state + LocalTimingNative.Profile.StateParser, frame, (nint)input);
        }
    }
}
