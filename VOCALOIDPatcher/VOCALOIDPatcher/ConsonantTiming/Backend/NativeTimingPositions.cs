using System;
using System.Runtime.InteropServices;
using Yamaha.VOCALOID.VSM;

namespace VOCALOIDPatcher.ConsonantTiming;

// Validation must read VSM's own positions. The public managed accessor can
// legitimately return our display projection, which is not a native edit.
internal static unsafe class NativeTimingPositions
{
    internal static int[] Read(WIVSMNote note, bool original)
    {
        LocalTimingNative.VerifyImages();
        var count = (delegate* unmanaged[Cdecl]<nint, int>)NativeLibrary.GetExport(LocalTimingNative.Vsm,
            original ? "VIS_VSM_WIVSMNote_numOriginalPhonemePosition" : "VIS_VSM_WIVSMNote_numPhonemePosition");
        var at = (delegate* unmanaged[Cdecl]<nint, int, int>)NativeLibrary.GetExport(LocalTimingNative.Vsm,
            original ? "VIS_VSM_WIVSMNote_originalPhonemePosition" : "VIS_VSM_WIVSMNote_phonemePosition");
        int length = count((nint)note);
        if (length is < 0 or > 61) throw new NotSupportedException("Native phoneme count exceeds its bound.");
        var result = new int[length];
        for (int i = 0; i < result.Length; i++) result[i] = at((nint)note, i);
        return result;
    }
}
