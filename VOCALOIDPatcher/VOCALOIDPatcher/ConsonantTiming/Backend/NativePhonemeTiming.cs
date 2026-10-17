using System;
using System.IO;
using System.Runtime.InteropServices;

namespace VOCALOIDPatcher.ConsonantTiming;

internal static unsafe class NativePhonemeTiming
{
    [StructLayout(LayoutKind.Sequential)]
    internal readonly record struct Point(int From, int To);
    [StructLayout(LayoutKind.Sequential)]
    internal struct Status { internal ulong Calls, Changed, Failures; internal int Left, Right, Actual, Displacement, Reason, Reserved; internal fixed int Trace[24]; }
    private static nint _library;
    internal static bool IsInitialized => _library != 0;
    private static bool _failed;
    private static delegate* unmanaged[Cdecl]<Point*, int, int> _begin;
    private static delegate* unmanaged[Cdecl]<Status*, int> _end;

    internal static void Initialize(string path)
    {
        if (_library != 0) return;
        if (_failed) throw new NotSupportedException("Native phoneme timing initialization previously failed.");
        LocalTimingNative.VerifyImages();
        nint library = NativeLibrary.Load(Path.GetFullPath(path));
        try
        {
            LocalTimingNative.VerifyBridge(library);
            uint Read(string name) => ((delegate* unmanaged[Cdecl]<uint>)NativeLibrary.GetExport(library, name))();
            if (Read("v6_phoneme_abi_version") != 1 || Read("v6_phoneme_point_size") != sizeof(Point) || Read("v6_phoneme_status_size") != sizeof(Status))
                throw new NotSupportedException("Unsupported phoneme timing ABI.");
            _begin = (delegate* unmanaged[Cdecl]<Point*, int, int>)NativeLibrary.GetExport(library, "v6_phoneme_begin");
            _end = (delegate* unmanaged[Cdecl]<Status*, int>)NativeLibrary.GetExport(library, "v6_phoneme_end");
            var install = (delegate* unmanaged[Cdecl]<nint, int>)NativeLibrary.GetExport(library, "v6_phoneme_install");
            // Retain a module containing detours, including partial installs.
            nint retained = library; library = 0;
            if (install(LocalTimingNative.Dse) != 0) { _failed = true; throw new NotSupportedException("Native phoneme mapper unavailable."); }
            _library = retained;
        }
        finally { if (library != 0) NativeLibrary.Free(library); }
    }

    internal static void Begin(Point[] points)
    {
        if (_library == 0) throw new NotSupportedException("Native phoneme timing is not initialized.");
        fixed (Point* p = points)
            if (_begin(p, points.Length) != 0) throw new InvalidOperationException("Invalid or nested phoneme timing scope.");
    }

    internal static Status End()
    {
        Status status = default;
        if (_library == 0 || _end(&status) != 0)
            throw new NotSupportedException($"Native phoneme mapping failed ({status.Calls}/{status.Changed}/{status.Failures}; {status.Left}/{status.Right}/{status.Actual}/{status.Displacement}/{status.Reason}; {string.Join(',', new ReadOnlySpan<int>(status.Trace, 24).ToArray())}).");
        return status;
    }
}
