using System;
using System.Runtime.InteropServices;
using Yamaha.VOCALOID.VSM;

namespace VOCALOIDPatcher.ConsonantTiming;

internal static class ConsonantTimingInputEncoder
{
    internal static unsafe byte[] EncodeNote(WIVSMNote note, int sampleOffset, int[] tempoPairs)
    {
        if (sampleOffset is < 0 or >= 256 || note.Phonemes.Length > 60 ||
            tempoPairs.Length < 2 || tempoPairs.Length % 2 != 0)
            throw new ArgumentException("Invalid bounded timing input.");
        nint library = LocalTimingNative.Vsm;
        nint storage = Marshal.AllocHGlobal(32768);
        try
        {
            long* vector = stackalloc long[3];
            vector[0] = storage;
            vector[1] = storage;
            vector[2] = storage + 32768;
            byte channel = 0, result = 0;
            ushort presend = 500;
            byte* message = stackalloc byte[24];
            byte* style = stackalloc byte[32];
            new Span<byte>(message, 24).Clear();
            new Span<byte>(style, 32).Clear();
            *(int*)message = sampleOffset;
            fixed (int* tempo = tempoPairs)
            {
                long* timeMap = stackalloc long[3];
                timeMap[0] = (long)tempo; timeMap[1] = (long)(tempo + tempoPairs.Length); timeMap[2] = timeMap[1];
                ((delegate* unmanaged<nint, nint, nint, nint, nint, nint, nint, nint, void>)(library + LocalTimingNative.Profile.VsmEncodeNote))(
                    (nint)(&channel), (nint)message, (nint)(&presend), (nint)note, (nint)style,
                    (nint)(&result), (nint)timeMap, (nint)vector);
            }
            if (vector[0] != storage || vector[1] < storage || vector[1] > storage + 32768)
                throw new InvalidOperationException("Native note input exceeds bounded buffer.");
            byte[] bytes = new byte[checked((int)(vector[1] - storage))];
            Marshal.Copy(storage, bytes, 0, bytes.Length);
            if (bytes.Length == 0) throw new InvalidOperationException("Native note encoder produced no input.");
            return bytes;
        }
        finally { Marshal.FreeHGlobal(storage); }
    }

    internal static unsafe byte[] EncodeSinger(int languageId, int singerId)
    {
        nint storage = Marshal.AllocHGlobal(4096);
        try
        {
            long* vector = stackalloc long[3];
            vector[0] = storage; vector[1] = storage; vector[2] = storage + 4096;
            byte channel = 0;
            int offset = 0;
            ushort presend = 500;
            byte result = ((delegate* unmanaged<nint, nint, nint, int, int, int, nint, byte>)(
                LocalTimingNative.Vsm + LocalTimingNative.Profile.VsmEncodeSinger))(
                (nint)(&channel), (nint)(&offset), (nint)(&presend), languageId, singerId, -1, (nint)vector);
            if (result == 0 || vector[0] != storage || vector[1] < storage || vector[1] > storage + 4096)
                throw new InvalidOperationException("Native singer input encoding failed.");
            byte[] bytes = new byte[checked((int)(vector[1] - storage))];
            Marshal.Copy(storage, bytes, 0, bytes.Length);
            return bytes;
        }
        finally { Marshal.FreeHGlobal(storage); }
    }
}
