using System;
using System.Linq;
using System.Runtime.InteropServices;

namespace VOCALOIDPatcher.ConsonantTiming;

// Explicit opt-in only; not registered at startup or attached to any editor UI.
internal static unsafe class NativeTransitionGain
{
    [StructLayout(LayoutKind.Sequential)]
    internal struct Gain
    {
        internal int Begin, End, From, To, Stage, Reverse;
        internal float Multiplier;
        internal uint Reserved;
    }
    [StructLayout(LayoutKind.Sequential)]
    internal struct Status { internal ulong Epoch, Calls, Applied, Failures, Primary, Auxiliary; }
    private static readonly object Gate = new();
    private static nint _library;
    private static delegate* unmanaged[Cdecl]<ulong, ulong, nint, int, NativePhonemeTiming.Point*, int, Gain*, int, int> _set;
    private static delegate* unmanaged[Cdecl]<ulong, Status*, int> _status;
    private static delegate* unmanaged[Cdecl]<ulong, ulong, int> _remove;

    internal static void Initialize(string path)
    {
        lock (Gate)
        {
            if (_library != 0) return;
            NativePhonemeTiming.Initialize(path); NativeSampledTiming.Initialize(path);
            nint library = NativeLibrary.Load(System.IO.Path.GetFullPath(path));
            try
            {
                LocalTimingNative.VerifyBridge(library);
                uint Read(string name) => ((delegate* unmanaged[Cdecl]<uint>)NativeLibrary.GetExport(library, name))();
                if (Read("v6_transition_abi_version") != 1 || Read("v6_transition_gain_size") != sizeof(Gain) || Read("v6_transition_status_size") != sizeof(Status))
                    throw new NotSupportedException("Unsupported transition gain ABI.");
                _set = (delegate* unmanaged[Cdecl]<ulong, ulong, nint, int, NativePhonemeTiming.Point*, int, Gain*, int, int>)NativeLibrary.GetExport(library, "v6_transition_set_part");
                _status = (delegate* unmanaged[Cdecl]<ulong, Status*, int>)NativeLibrary.GetExport(library, "v6_transition_status");
                _remove = (delegate* unmanaged[Cdecl]<ulong, ulong, int>)NativeLibrary.GetExport(library, "v6_transition_remove_part");
                var install = (delegate* unmanaged[Cdecl]<nint, int>)NativeLibrary.GetExport(library, "v6_transition_install");
                // A partial native installation also retains its code module.
                nint retained = library; library = 0;
                if (install(LocalTimingNative.Dse) != 0) throw new NotSupportedException("Native transition synthesis entry unavailable.");
                _library = retained;
            }
            finally { if (library != 0) NativeLibrary.Free(library); }
        }
    }

    internal static Gain[] Gains(TransitionExecutionPlan result)
    {
        var rows = result.Segments.Where(s => s.To != null).ToArray();
        return result.Controls.Gains.Select(edit =>
        {
            int index = result.Layout.Edges.IndexOf(result.Layout.Edges.Single(e => e.Identity == edit.Edge));
            if (index < 0 || index >= rows.Length) throw new InvalidOperationException("Missing verified transition gain target.");
            var row = rows[index];
            return new Gain { Begin = row.Begin, End = row.End, From = row.FromId, To = row.ToId,
                Stage = row.Stage, Reverse = row.Reverse ? 1 : 0,
                Multiplier = edit.Decibels <= -60 ? 0 : MathF.Pow(10, edit.Decibels / 20) };
        }).ToArray();
    }
    internal static void Set(ulong part, ulong epoch, nint notes, int count, NativePhonemeTiming.Point* points, int numPoints, Gain[] gains)
    {
        if (_library == 0) throw new InvalidOperationException("Native transition gain is not initialized.");
        fixed (Gain* values = gains)
            if (_set(part, epoch, notes, count, points, numPoints, values, gains.Length) != 0)
                throw new InvalidOperationException("Cannot publish the transition/timing render snapshot.");
    }
    internal static Status ReadStatus(nint part)
    {
        Status result = default;
        if (_library == 0 || _status((ulong)part, &result) != 0) throw new InvalidOperationException("Transition diagnostics unavailable.");
        return result;
    }
    internal static void Remove(nint part, ulong epoch) { if (_library != 0) _remove((ulong)part, epoch); }
}
