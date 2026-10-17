using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using Yamaha.VOCALOID.VSM;

namespace VOCALOIDPatcher.ConsonantTiming;

internal static unsafe class NativeSampledTiming
{
    [StructLayout(LayoutKind.Sequential)]
    private struct Note { internal long Begin, End; internal float Pitch, Coefficient; }
    [StructLayout(LayoutKind.Sequential)]
    internal struct Status { internal ulong Epoch, Scopes, Applied, Failures, CacheKeys; }
    private static nint _library;
    internal static bool IsInitialized => _library != 0;
    private static delegate* unmanaged[Cdecl]<ulong, ulong, Note*, int, int> _set;
    private static delegate* unmanaged[Cdecl]<ulong, ulong, Note*, int, NativePhonemeTiming.Point*, int, int> _setPhonemes;
    private static delegate* unmanaged[Cdecl]<ulong, void> _remove;
    private static delegate* unmanaged[Cdecl]<ulong, Status*, int> _status;
    private static delegate* unmanaged[Cdecl]<ulong, NativePhonemeTiming.Status*, int> _phonemeStatus;
    private static delegate* unmanaged[Cdecl]<ulong> _peak;
    private static delegate* unmanaged[Cdecl]<ulong> _scheduleReuses;
    private static bool _failed;

    internal static void Initialize(string path)
    {
        if (_library != 0) return;
        if (_failed) throw new NotSupportedException("Native timing initialization previously failed.");
        LocalTimingNative.VerifyImages();
        nint library = NativeLibrary.Load(Path.GetFullPath(path));
        try
        {
            LocalTimingNative.VerifyBridge(library);
            uint Read(string name) => ((delegate* unmanaged[Cdecl]<uint>)NativeLibrary.GetExport(library, name))();
            if (Read("v6_clock_abi_version") != 14 || Read("v6_timing_abi_version") != 3 ||
                Read("v6_timing_note_size") != sizeof(Note) || Read("v6_timing_status_size") != sizeof(Status))
                throw new NotSupportedException("Unsupported sampled timing ABI.");
            var install = (delegate* unmanaged[Cdecl]<int>)NativeLibrary.GetExport(library, "v6_timing_install");
            _set = (delegate* unmanaged[Cdecl]<ulong, ulong, Note*, int, int>)NativeLibrary.GetExport(library, "v6_timing_set_part");
            _setPhonemes = (delegate* unmanaged[Cdecl]<ulong, ulong, Note*, int, NativePhonemeTiming.Point*, int, int>)NativeLibrary.GetExport(library,"v6_timing_set_part_phonemes");
            _remove = (delegate* unmanaged[Cdecl]<ulong, void>)NativeLibrary.GetExport(library, "v6_timing_remove_part");
            _status = (delegate* unmanaged[Cdecl]<ulong, Status*, int>)NativeLibrary.GetExport(library, "v6_timing_status");
            _phonemeStatus = (delegate* unmanaged[Cdecl]<ulong, NativePhonemeTiming.Status*, int>)NativeLibrary.GetExport(library, "v6_timing_phoneme_status");
            _peak = (delegate* unmanaged[Cdecl]<ulong>)NativeLibrary.GetExport(library, "v6_timing_peak_scopes");
            _scheduleReuses = (delegate* unmanaged[Cdecl]<ulong>)NativeLibrary.GetExport(library, "v6_timing_schedule_reuses");
            // Installation can leave safe partial detours. Keep the module
            // loaded for process lifetime even when an installation fails.
            nint retained = library; library = 0;
            if (install() < 0) { _failed = true; throw new NotSupportedException("Native sampled timing routing is unavailable."); }
            _library = retained;
        }
        finally { if (library != 0) NativeLibrary.Free(library); }
    }

    internal static void Publish(WIVSMMidiPart part, ulong epoch, IReadOnlyDictionary<nint, float> coefficients, NativePhonemeTiming.Point[]? points = null,
        NativeTransitionGain.Gain[]? transitionGains = null)
    {
        if (_library == 0) throw new InvalidOperationException("Native timing routing is not initialized.");
        var sequence = part.Sequence!;
        if (part.IsAi || !string.IsNullOrEmpty(part.SecondaryVoiceBankID) ||
            sequence.GetSamplingRate() != VSMSamplingRate._44100 || sequence.NumSampleInFrame != 256 ||
            part.NumNotes > 100000) throw new NotSupportedException("Unsupported timing render source.");
        var values = part.NotesEnumerator.Select(n => new Note {
            Begin = (long)Math.Round((sequence.PresendTimeSec + sequence.GetTimeFromTick(part.AbsBeginTick, n.AbsPosTick)) * 44100 / 256),
            End = (long)Math.Round((sequence.PresendTimeSec + sequence.GetTimeFromTick(part.AbsBeginTick, n.AbsEndTick)) * 44100 / 256),
            Pitch = (n.NoteNumber - 69) * 100,
            Coefficient = coefficients.TryGetValue((nint)n, out float c) ? c : -1
        }).ToArray();
        fixed (Note* notes = values)
        fixed (NativePhonemeTiming.Point* anchors = points)
        {
            if (transitionGains != null)
            {
                if (points == null) throw new ArgumentException("Transition gains require the verified timing anchors.");
                NativeTransitionGain.Set((ulong)(nint)part, epoch, (nint)notes, values.Length, anchors, points.Length, transitionGains);
                return;
            }
            if ((points == null ? _set((ulong)(nint)part, epoch, notes, values.Length) :
                _setPhonemes((ulong)(nint)part, epoch, notes, values.Length, anchors, points.Length)) != 0)
                throw new InvalidOperationException("Cannot publish native timing snapshot.");
        }
    }

    internal static void Remove(nint part) { if (_library != 0) _remove((ulong)part); }
    internal static ulong PeakScopes => _library == 0 ? 0 : _peak();
    internal static ulong ScheduleReuses => _library == 0 ? 0 : _scheduleReuses();
    internal static Status ReadStatus(nint part)
    {
        Status value = default;
        if (_library != 0 && _status((ulong)part, &value) != 0) throw new InvalidOperationException("Cannot read timing status.");
        return value;
    }
    internal static string PhonemeDiagnostic(nint part)
    {
        NativePhonemeTiming.Status value = default;
        if (_library == 0 || _phonemeStatus((ulong)part, &value) != 0) return "no phoneme cursor";
        return $"calls={value.Calls}, changed={value.Changed}, failures={value.Failures}, last={value.Left}/{value.Right}, actual={value.Actual}, displacement={value.Displacement}, reason={value.Reason}, trace={string.Join(',',new ReadOnlySpan<int>(value.Trace,24).ToArray())}";
    }
}
