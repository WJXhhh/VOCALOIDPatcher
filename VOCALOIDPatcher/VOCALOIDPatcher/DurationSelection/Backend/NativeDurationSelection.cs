using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using VOCALOIDPatcher.ConsonantTiming;
using Yamaha.VOCALOID.VSM;

namespace VOCALOIDPatcher.DurationSelection;

internal static unsafe class NativeDurationSelection
{
    [StructLayout(LayoutKind.Sequential)]
    internal struct Note
    {
        internal long Begin, End;
        internal float Pitch, Ratio;
        internal uint Policy, Identity;
    }
    [StructLayout(LayoutKind.Sequential)]
    internal struct TimeEvent
    {
        internal ulong Section;
        internal fixed byte Key[32];
        internal int Kind, Count, Gap, Index;
        internal fixed int Halves[64];
    }
    [StructLayout(LayoutKind.Sequential)]
    internal struct Status
    {
        internal ulong Scopes, Scores, Changed, Failures, Contracts;
        internal int LastOwner, LastBegin, LastTarget, LastWork, LastStart, LastEnd, Reason, Reserved;
        public override string ToString() => $"scopes={Scopes}, scores={Scores}, changed={Changed}, failures={Failures}, contracts={Contracts}, reason={Reason}, owner={LastOwner}, note={LastBegin}/{LastTarget}, work={LastWork}, score={LastStart}..{LastEnd}";
    }
    private static readonly object Sync = new();
    private static nint _library;
    internal static bool IsInitialized => _library != 0;
    private static delegate* unmanaged[Cdecl]<Note*, int, int> _begin;
    private static delegate* unmanaged[Cdecl]<int> _count;
    private static delegate* unmanaged[Cdecl]<TimeEvent*, int, int*, Status*, int> _end;
    private static delegate* unmanaged[Cdecl]<void> _cancel;
    private static delegate* unmanaged[Cdecl]<ulong, ulong, Note*, int, TimeEvent*, int, int> _set;
    private static delegate* unmanaged[Cdecl]<ulong, Status*, int> _status;
    private static delegate* unmanaged[Cdecl]<ulong, void> _remove;

    internal static void Initialize(string path)
    {
        lock (Sync)
        {
            if (_library != 0) return;
            LocalTimingNative.VerifyImages();
            NativeSampledTiming.Initialize(path);
            // Retain for process lifetime: partially installed native entries
            // must never point into an unloaded library after an exception.
            nint library = NativeLibrary.Load(Path.GetFullPath(path));
            LocalTimingNative.VerifyBridge(library);
            uint Read(string name) => ((delegate* unmanaged[Cdecl]<uint>)NativeLibrary.GetExport(library, name))();
            if (Read("v6_duration_abi_version") != 1 || Read("v6_duration_note_size") != sizeof(Note) ||
                Read("v6_duration_event_size") != sizeof(TimeEvent) || Read("v6_duration_status_size") != sizeof(Status))
                throw new NotSupportedException("Unsupported independent duration ABI.");
            _begin = (delegate* unmanaged[Cdecl]<Note*, int, int>)NativeLibrary.GetExport(library, "v6_duration_query_begin");
            _count = (delegate* unmanaged[Cdecl]<int>)NativeLibrary.GetExport(library, "v6_duration_query_count");
            _end = (delegate* unmanaged[Cdecl]<TimeEvent*, int, int*, Status*, int>)NativeLibrary.GetExport(library, "v6_duration_query_end");
            _cancel = (delegate* unmanaged[Cdecl]<void>)NativeLibrary.GetExport(library, "v6_duration_query_cancel");
            _set = (delegate* unmanaged[Cdecl]<ulong, ulong, Note*, int, TimeEvent*, int, int>)NativeLibrary.GetExport(library, "v6_duration_set_part");
            _status = (delegate* unmanaged[Cdecl]<ulong, Status*, int>)NativeLibrary.GetExport(library, "v6_duration_status");
            _remove = (delegate* unmanaged[Cdecl]<ulong, void>)NativeLibrary.GetExport(library, "v6_duration_remove_part");
            int install = ((delegate* unmanaged[Cdecl]<int>)NativeLibrary.GetExport(library, "v6_dse_install"))();
            if (install < 0) throw new NotSupportedException("DSE render routing unavailable: " + install);
            install = ((delegate* unmanaged[Cdecl]<nint, int>)NativeLibrary.GetExport(library, "v6_duration_install"))(LocalTimingNative.Dse);
            if (install < 0) throw new NotSupportedException("Duration entry installation failed: " + install);
            _library = library;
        }
    }

    internal static Note[] Notes(WIVSMMidiPart part, IReadOnlyDictionary<nint, (float Ratio, uint Policy)> controls, bool presend)
    {
        var source = part.Sequence ?? throw new InvalidOperationException("Missing duration source.");
        if (part.IsAi || !string.IsNullOrEmpty(part.SecondaryVoiceBankID) ||
            source.GetSamplingRate() != VSMSamplingRate._44100 || source.NumSampleInFrame != 256)
            throw new NotSupportedException("Independent duration requires a single sampled bank at 44100/256.");
        double offset = presend ? source.PresendTimeSec : 0;
        return part.NotesEnumerator.Select((n, i) => new Note
        {
            Begin = (long)Math.Round((offset + source.GetTimeFromTick(part.AbsBeginTick, n.AbsPosTick)) * 44100 / 256),
            End = (long)Math.Round((offset + source.GetTimeFromTick(part.AbsBeginTick, n.AbsEndTick)) * 44100 / 256),
            Pitch = (n.NoteNumber - 69) * 100,
            Ratio = controls.TryGetValue((nint)n, out var value) ? value.Ratio : 1,
            Policy = value.Policy,
            Identity = checked((uint)i)
        }).ToArray();
    }
    internal static void ValidateBudget(WIVSMMidiPart part, WIVSMNote target, DurationSelectionPlan? plan)
    {
        var source = part.Sequence ?? throw new InvalidOperationException("Missing duration source.");
        if ((nint)target.Parent! != (nint)part) throw new ArgumentException("Duration target belongs to another Part.");
        long begin = (long)Math.Round((source.PresendTimeSec + source.GetTimeFromTick(part.AbsBeginTick, target.AbsPosTick)) * 44100 / 256);
        long end = (long)Math.Round((source.PresendTimeSec + source.GetTimeFromTick(part.AbsBeginTick, target.AbsEndTick)) * 44100 / 256);
        if (end <= begin || (end - begin) * (double)(plan?.Ratio ?? 1) > 4096)
            throw new NotSupportedException("Virtual note budget exceeds the reviewed 4096-frame planner bound.");
    }
    internal static void BeginCapture(Note[] notes)
    {
        if (_library == 0) throw new InvalidOperationException("Duration native adapter is not initialized.");
        fixed (Note* input = notes)
            if (_begin(input, notes.Length) != 0) throw new InvalidOperationException("Cannot begin duration planning.");
    }
    internal static TimeEvent[] EndCapture()
    {
        int size = _count();
        if (size is < 0 or > 32768) { _cancel(); throw new InvalidOperationException("Invalid duration contract size."); }
        var result = new TimeEvent[size];
        Status status = default; int count;
        fixed (TimeEvent* output = result)
        {
            int code = _end(output, result.Length, &count, &status);
            if (code != 0 || count != size) throw new InvalidOperationException($"Duration capture failed ({code}; {status}).");
        }
        return result;
    }
    internal static void CancelCapture() { if (_library != 0) _cancel(); }
    internal static void Publish(nint part, ulong epoch, Note[] notes, TimeEvent[] plan)
    {
        fixed (Note* input = notes)
        fixed (TimeEvent* events = plan)
            if (_set((ulong)part, epoch, input, notes.Length, events, plan.Length) != 0)
                throw new InvalidOperationException("Cannot publish independent duration snapshot.");
    }
    internal static Status ReadStatus(nint part)
    {
        Status result = default;
        if (_library == 0 || _status((ulong)part, &result) != 0) throw new InvalidOperationException("No duration render status.");
        return result;
    }
    internal static void Remove(nint part) { if (_library != 0) _remove((ulong)part); }
}
