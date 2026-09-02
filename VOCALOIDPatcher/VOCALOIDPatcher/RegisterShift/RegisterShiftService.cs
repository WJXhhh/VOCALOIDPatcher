using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using VOCALOIDPatcher.BreathVolume;
using VOCALOIDPatcher.Config;
using VOCALOIDPatcher.Patch.Patches;
using Yamaha.VOCALOID;
using Yamaha.VOCALOID.MusicalEditor;
using Yamaha.VOCALOID.VSM;

namespace VOCALOIDPatcher.RegisterShift;

internal enum RegisterShiftStatus
{
    Unavailable,
    Loading,
    Installed,
    Unsupported
}

internal static class RegisterShiftService
{
    public const int MinValue = -24;
    public const int MaxValue = 24;
    public const int DefaultValue = 0;
    public const int DisplayOffset = -MinValue;
    public static readonly ControlParameterTypeEnum ParameterType = (ControlParameterTypeEnum)0x524547;

    private static readonly object Sync = new();
    private static readonly Dictionary<IntPtr, long> Generations = new();
    private static readonly Dictionary<NoteKey, sbyte> Values = new();
    private static readonly HashSet<IntPtr> Selection = new();
    private static readonly Dictionary<IntPtr, int> RebuildGenerations = new();
    private static readonly Dictionary<IntPtr, ulong> RenderEpochs = new();
    private static readonly Dictionary<IntPtr, ulong> RenderFingerprints = new();
    private static long _nextGeneration;
    private static long _nextRenderEpoch;
    [ThreadStatic] private static RegisterShiftProjectLoadState? _pendingProjectLoad;

    public static event Action<WIVSMMidiPart?>? Changed;
    public static event Action? ValuesChanged;
    public static event Action<WIVSMMidiPart, int, bool>? RebuildCompleted;
    public static RegisterShiftStatus NativeStatus => NativeRegisterShift.Status;
    public static RegisterShiftStatus NativeStatusForPart(WIVSMMidiPart? part)
        => NativeRegisterShift.StatusForPart(part);

    public static bool IsActive(ControlParameterTypeEnum type)
        => Settings.RegisterShift && type.Equals(ParameterType);

    public static bool IsSupported => NativeStatus != RegisterShiftStatus.Unsupported;

    public static bool IsSupportedForPart(WIVSMMidiPart? part)
        => NativeStatusForPart(part) != RegisterShiftStatus.Unsupported;

    public static IReadOnlyList<BreathRegion> GetRegions(WIVSMMidiPart? part)
    {
        if (part == null)
            return Array.Empty<BreathRegion>();
        var result = new List<BreathRegion>();
        for (ulong index = 0; index < part.NumNotes; index++)
        {
            if (part.GetNote(index) is not { } note)
                continue;
            Register(note);
            result.Add(new BreathRegion(0, 0, note.AbsPosTick.Value, note.AbsEndTick.Value,
                note.CppObjPtr));
        }
        return result;
    }

    public static int GetValue(IntPtr handle)
    {
        lock (Sync)
            return TryKey(handle, out var key) && Values.TryGetValue(key, out var value)
                ? value : DefaultValue;
    }

    public static IReadOnlyCollection<IntPtr> GetSelection()
    {
        lock (Sync) return Selection.ToArray();
    }

    public static bool IsSelected(IntPtr handle)
    {
        lock (Sync) return Selection.Contains(handle);
    }

    public static void ClearSelection()
    {
        lock (Sync) Selection.Clear();
        Notify(null);
    }

    public static void SetSelection(IEnumerable<IntPtr> handles, bool additive = false)
    {
        lock (Sync)
        {
            if (!additive) Selection.Clear();
            foreach (var handle in handles.Where(handle => handle != IntPtr.Zero))
                Selection.Add(handle);
        }
        Notify(null);
    }

    public static void ToggleSelection(IntPtr handle)
    {
        lock (Sync)
        {
            if (!Selection.Remove(handle)) Selection.Add(handle);
        }
        Notify(null);
    }

    public static Dictionary<IntPtr, byte> Snapshot(IEnumerable<IntPtr> handles)
        => handles.Distinct().Where(handle => handle != IntPtr.Zero)
            .ToDictionary(handle => handle,
                handle => unchecked((byte)(GetValue(handle) + DisplayOffset)));

    public static void SetPreviewValues(IEnumerable<KeyValuePair<IntPtr, byte>> values)
    {
        lock (Sync)
            foreach (var pair in values)
                SetCore(pair.Key, pair.Value - DisplayOffset);
        Notify(null);
    }

    public static void CommitValues(WIVSMSequence sequence, WIVSMMidiPart part,
        IReadOnlyDictionary<IntPtr, byte> before)
    {
        var beforeValues = before.ToDictionary(pair => pair.Key, pair => pair.Value - DisplayOffset);
        var afterValues = before.Keys.ToDictionary(handle => handle, GetValue);
        if (beforeValues.All(pair => afterValues.TryGetValue(pair.Key, out var value) && value == pair.Value))
            return;

        RegisterShiftDiagnosticsLog.Write(
            $"history push part=0x{((IntPtr)part).ToInt64():X} " +
            $"before=[{FormatSnapshot(beforeValues)}] after=[{FormatSnapshot(afterValues)}]");
        CustomParameterHistoryCoordinator.Push(sequence,
            new RegisterShiftHistoryEdit(sequence, part, beforeValues, afterValues));
        Notify(part);
        ValuesChanged?.Invoke();
        CommandManager.InvalidateRequerySuggested();
        PublishAndRender(sequence, part);
    }

    public static void SetValues(WIVSMSequence sequence, WIVSMMidiPart part,
        IEnumerable<IntPtr> handles, int value)
    {
        var target = handles.Distinct().Where(handle => handle != IntPtr.Zero).ToArray();
        if (target.Length == 0) return;
        var before = Snapshot(target);
        lock (Sync)
            foreach (var handle in target) SetCore(handle, Math.Clamp(value, MinValue, MaxValue));
        CommitValues(sequence, part, before);
    }

    public static void ResetSelected(WIVSMSequence sequence, WIVSMMidiPart part)
        => SetValues(sequence, part, GetSelection(), DefaultValue);

    internal static void CompleteExternalMutation(WIVSMSequence sequence, IEnumerable<WIVSMMidiPart> parts)
    {
        WIVSMMidiPart[] targets = parts.Distinct().ToArray();
        Notify(null);
        ValuesChanged?.Invoke();
        CommandManager.InvalidateRequerySuggested();
        foreach (WIVSMMidiPart part in targets)
            PublishAndRender(sequence, part);
    }

    public static void CopyNoteValue(WIVSMNote? source, WIVSMNote? target)
    {
        if (source == null || target == null) return;
        Register(source); Register(target);
        lock (Sync) SetCore(target.CppObjPtr, GetValue(source.CppObjPtr));
    }

    public static void CopyPartValues(WIVSMMidiPart? source, WIVSMMidiPart? target)
    {
        if (source == null || target == null) return;
        var count = Math.Min(source.NumNotes, target.NumNotes);
        for (ulong index = 0; index < count; index++)
            CopyNoteValue(source.GetNote(index), target.GetNote(index));
    }

    public static void CopyTrackValues(WIVSMTrack? source, WIVSMTrack? target)
    {
        if (source is not WIVSMMidiTrack sourceTrack || target is not WIVSMMidiTrack targetTrack)
            return;
        var count = Math.Min(sourceTrack.NumParts, targetTrack.NumParts);
        for (ulong index = 0; index < count; index++)
            CopyPartValues(sourceTrack.GetPart(index) as WIVSMMidiPart,
                targetTrack.GetPart(index) as WIVSMMidiPart);
    }

    public static void CopySequenceValues(WIVSMSequence? source, WIVSMSequence? target)
    {
        if (source == null || target == null) return;
        var count = Math.Min(source.NumTrack, target.NumTrack);
        for (ulong index = 0; index < count; index++)
            CopyTrackValues(source.GetTrack(index), target.GetTrack(index));
    }

    public static void ReleaseNoteHandles(IEnumerable<IntPtr> handles)
    {
        foreach (var handle in handles) ReleaseNote(handle);
    }

    public static void ReleaseMissingPartNotes(WIVSMMidiPart part, IEnumerable<IntPtr> previous)
    {
        var live = BreathVolumeService.CapturePartNoteHandles(part).ToHashSet();
        ReleaseNoteHandles(previous.Where(handle => !live.Contains(handle)));
    }

    public static void ReleasePart(BreathNativeObjectHandles handles)
    {
        ReleaseNoteHandles(handles.NoteHandles);
        foreach (var part in handles.PartHandles)
        {
            NativeRegisterShift.RemovePart(unchecked((ulong)part.ToInt64()));
            lock (Sync)
            {
                RenderEpochs.Remove(part);
                RenderFingerprints.Remove(part);
                RebuildGenerations.Remove(part);
            }
        }
    }

    public static void ReleaseNote(IntPtr handle)
    {
        lock (Sync)
        {
            if (TryKey(handle, out var key)) Values.Remove(key);
            Generations.Remove(handle);
            Selection.Remove(handle);
        }
    }

    public static RegisterShiftProjectData BuildProjectData(WIVSMSequence sequence)
    {
        var data = new RegisterShiftProjectData();
        for (ulong trackIndex = 0; trackIndex < sequence.NumTrack; trackIndex++)
        {
            if (sequence.GetTrack(trackIndex) is not WIVSMMidiTrack track) continue;
            for (ulong partIndex = 0; partIndex < track.NumParts; partIndex++)
            {
                if (track.GetPart(partIndex) is not WIVSMMidiPart part) continue;
                var occurrences = new Dictionary<(long, int), int>();
                for (ulong noteIndex = 0; noteIndex < part.NumNotes; noteIndex++)
                {
                    if (part.GetNote(noteIndex) is not { } note) continue;
                    Register(note);
                    var value = GetValue(note.CppObjPtr);
                    var identity = (note.RelPosTick.Value, note.NoteNumber);
                    occurrences.TryGetValue(identity, out var occurrence);
                    occurrences[identity] = occurrence + 1;
                    if (value == 0) continue;
                    data.Entries.Add(new RegisterShiftProjectEntry
                    {
                        Track = checked((int)trackIndex), Part = checked((int)partIndex),
                        Note = checked((int)noteIndex), RelPosTick = note.RelPosTick.Value,
                        NoteNumber = note.NoteNumber, Occurrence = occurrence, Value = value
                    });
                }
            }
        }
        return data;
    }

    public static void LoadProjectData(WIVSMSequence sequence, RegisterShiftProjectData? data)
        => ApplyProjectData(sequence, data, notify: true);

    public static void PublishAll(WIVSMSequence sequence)
    {
        if (!Settings.RegisterShift)
        {
            NativeRegisterShift.Clear();
            return;
        }
        for (ulong trackIndex = 0; trackIndex < sequence.NumTrack; trackIndex++)
            if (sequence.GetTrack(trackIndex) is WIVSMMidiTrack track)
                for (ulong partIndex = 0; partIndex < track.NumParts; partIndex++)
                    if (track.GetPart(partIndex) is WIVSMMidiPart part)
                        PublishPart(sequence, part);
    }

    public static void PublishPart(WIVSMSequence sequence, WIVSMMidiPart part)
    {
        if (!Settings.RegisterShift)
        {
            NativeRegisterShift.RemovePart(unchecked((ulong)((IntPtr)part).ToInt64()));
            return;
        }
        var values = GetNoteValues(part);
        var handle = (IntPtr)part;
        var fingerprint = ComputeRenderFingerprint(sequence, part, values);
        if (values.Values.All(value => value == 0))
        {
            lock (Sync)
                if (RenderFingerprints.TryGetValue(handle, out var previous) && previous == fingerprint &&
                    RenderEpochs.TryGetValue(handle, out var existingEpoch) && existingEpoch == 0)
                    return;
            NativeRegisterShift.RemovePart(unchecked((ulong)handle.ToInt64()));
            lock (Sync)
            {
                RenderEpochs[handle] = 0;
                RenderFingerprints[handle] = fingerprint;
            }
            RegisterShiftDiagnosticsLog.Write(
                $"publish cleared part=0x{handle.ToInt64():X} notes={values.Count}");
            RegisterShiftDiagnosticsLog.WriteStatus("publish-clear-status", handle);
            return;
        }
        NativeRegisterShift.Initialize();
        lock (Sync)
            if (RenderFingerprints.TryGetValue(handle, out var previous) && previous == fingerprint &&
                RenderEpochs.TryGetValue(handle, out var existingEpoch))
                return;
        var epoch = unchecked((ulong)Interlocked.Increment(ref _nextRenderEpoch));
        if (epoch == 0)
            epoch = unchecked((ulong)Interlocked.Increment(ref _nextRenderEpoch));
        var result = NativeRegisterShift.SetPart(sequence, part, epoch, values);
        if (result == 0)
            lock (Sync)
            {
                RenderEpochs[handle] = epoch;
                RenderFingerprints[handle] = fingerprint;
            }
        var majorVersion = part.VoiceBank()?.MajorVersion;
        RegisterShiftDiagnosticsLog.Write(
            $"publish part=0x{handle.ToInt64():X} epoch={epoch} result={result} " +
            $"isAi={part.IsAi} voiceMajor={majorVersion?.ToString() ?? "?"} " +
            $"notes={values.Count} nonzero={values.Values.Count(value => value != 0)}");
        RegisterShiftDiagnosticsLog.WriteStatus("publish-status", handle);
    }

    public static void LogNativeStatus(string boundary, WIVSMMidiPart? part)
    {
        var handle = part == null ? IntPtr.Zero : (IntPtr)part;
        if (part != null)
        {
            ulong epoch;
            lock (Sync) RenderEpochs.TryGetValue(handle, out epoch);
            RegisterShiftDiagnosticsLog.WriteRenderedFlags(boundary, part, epoch);
        }
        RegisterShiftDiagnosticsLog.WriteStatus(boundary, handle);
    }

    public static void DisableNative()
    {
        NativeRegisterShift.Clear();
        lock (Sync)
        {
            RenderEpochs.Clear();
            RenderFingerprints.Clear();
        }
        Notify(null);
    }

    public static void RefreshUi(WIVSMMidiPart? part = null)
        => Notify(part);

    private static Dictionary<IntPtr, int> GetNoteValues(WIVSMMidiPart part)
        => GetRegions(part).ToDictionary(region => region.NoteHandle, region => GetValue(region.NoteHandle));

    private static ulong ComputeRenderFingerprint(WIVSMSequence sequence, WIVSMMidiPart part,
        IReadOnlyDictionary<IntPtr, int> values)
    {
        const ulong offset = 14695981039346656037;
        const ulong prime = 1099511628211;
        var hash = offset;
        void Add(ulong value)
        {
            for (var index = 0; index < 8; index++)
            {
                hash ^= (byte)value;
                hash *= prime;
                value >>= 8;
            }
        }
        Add(part.IsAi ? 1UL : 0UL);
        Add(part.NumNotes);
        for (ulong index = 0; index < part.NumNotes; index++)
        {
            if (part.GetNote(index) is not { } note)
            {
                Add(ulong.MaxValue);
                continue;
            }
            var beginSeconds = sequence.PresendTimeSec +
                               sequence.GetTimeFromTick(part.AbsBeginTick, note.AbsPosTick);
            var endSeconds = sequence.PresendTimeSec +
                             sequence.GetTimeFromTick(part.AbsBeginTick, note.AbsEndTick);
            Add(index);
            Add(unchecked((ulong)BitConverter.DoubleToInt64Bits(beginSeconds)));
            Add(unchecked((ulong)BitConverter.DoubleToInt64Bits(endSeconds)));
            Add(unchecked((ulong)(uint)note.NoteNumber));
            Add(unchecked((ulong)(uint)(values.TryGetValue(note.CppObjPtr, out var value)
                ? value : DefaultValue)));
        }
        return hash;
    }

    internal static RegisterShiftProjectLoadState BeginProjectLoad(RegisterShiftProjectData? data)
    {
        var state = new RegisterShiftProjectLoadState(data, _pendingProjectLoad);
        _pendingProjectLoad = state;
        return state;
    }

    internal static void PublishBeforeRendering(WIVSMSequence sequence)
    {
        if (_pendingProjectLoad is { Applied: false } pending)
        {
            ApplyProjectData(sequence, pending.Data, notify: false);
            pending.Applied = true;
        }
        PublishAll(sequence);
    }

    internal static void CompleteProjectLoad(WIVSMSequence sequence,
        RegisterShiftProjectLoadState? state)
    {
        if (state == null) return;
        if (!state.Applied)
        {
            ApplyProjectData(sequence, state.Data, notify: false);
            state.Applied = true;
        }
        Notify(null);
    }

    internal static void EndProjectLoad(RegisterShiftProjectLoadState? state)
    {
        if (state != null && ReferenceEquals(_pendingProjectLoad, state))
            _pendingProjectLoad = state.Previous;
    }

    private static void ApplyProjectData(WIVSMSequence sequence, RegisterShiftProjectData? data,
        bool notify)
    {
        lock (Sync)
        {
            Values.Clear();
            Generations.Clear();
            Selection.Clear();
            RebuildGenerations.Clear();
            RenderEpochs.Clear();
            RenderFingerprints.Clear();
        }
        NativeRegisterShift.Clear();
        if (data != null)
            foreach (var entry in data.Entries)
            {
                var note = FindNote(sequence, entry);
                if (note == null) continue;
                Register(note);
                lock (Sync) SetCore(note.CppObjPtr, entry.Value);
            }
        PublishAll(sequence);
        if (notify) Notify(null);
    }

    private static void PublishAndRender(WIVSMSequence sequence, WIVSMMidiPart part)
    {
        int generation;
        lock (Sync)
        {
            RebuildGenerations.TryGetValue((IntPtr)part, out generation);
            RebuildGenerations[(IntPtr)part] = ++generation;
        }
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(250).ConfigureAwait(false);
                lock (Sync)
                    if (!RebuildGenerations.TryGetValue((IntPtr)part, out var current) || current != generation)
                        return;
                var dispatcher = Application.Current?.Dispatcher;
                if (dispatcher == null)
                    return;
                await dispatcher.InvokeAsync(() =>
                {
                    try
                    {
                        PublishPart(sequence, part);
                        RenderedWaveCachePatch.InvalidateAndRefreshPart(part);
                        ForceNativeRender(sequence, part);
                    }
                    catch (Exception exception)
                    {
                        RegisterShiftDiagnosticsLog.Write(
                            $"render request failed part=0x{((IntPtr)part).ToInt64():X}: " +
                            $"{exception.GetType().Name}: {exception.Message}");
                    }
                }).Task;
            }
            finally
            {
                bool latest;
                lock (Sync) latest = RebuildGenerations.TryGetValue((IntPtr)part, out var current) && current == generation;
                RebuildCompleted?.Invoke(part, generation, latest);
            }
        });
    }

    private static void ForceNativeRender(WIVSMSequence sequence, WIVSMMidiPart part)
    {
        ulong epoch;
        lock (Sync) RenderEpochs.TryGetValue((IntPtr)part, out epoch);
        RegisterShiftDiagnosticsLog.WriteRenderedFlags("render-before", part, epoch);
        var duration = checked((int)Math.Clamp(part.DurationTick.Value, 0, int.MaxValue));
        var began = part.BeginScoreEdit();
        var updated = false;
        var invalidated = false;
        var ended = false;
        var committed = false;
        var invalidationNotes = new List<(WIVSMNote Note, int Velocity)>();
        try
        {
            if (began)
            {
                // Score-edit range notifications alone do not invalidate an already rendered
                // native score. Every note can own a separately cached synthesis unit, so
                // touching only note zero leaves later units reusable even when the selector is
                // called again. Touch every note and restore all values before ending the edit;
                // the project remains unchanged while the complete Part is dirtied.
                invalidated = part.NumNotes > 0;
                for (ulong index = 0; index < part.NumNotes; index++)
                {
                    if (part.GetNote(index) is not { } invalidationNote)
                    {
                        invalidated = false;
                        break;
                    }
                    var originalVelocity = invalidationNote.NoteVelocity;
                    invalidationNotes.Add((invalidationNote, originalVelocity));
                    var temporaryVelocity = originalVelocity < WIVSMNote.MaxVelocity
                        ? originalVelocity + 1
                        : originalVelocity - 1;
                    invalidationNote.NoteVelocity = temporaryVelocity;
                    if (invalidationNote.NoteVelocity != temporaryVelocity)
                    {
                        invalidated = false;
                        break;
                    }
                }
                updated = invalidated && part.UpdateScoreEdit(false, 0, duration);
                invalidated &= RestoreInvalidationNotes(invalidationNotes);
                updated &= invalidated && part.UpdateScoreEdit(false, 0, duration);
                ended = updated && part.EndScoreEdit();
                if (!ended)
                    part.CancelScoreEdit();
            }
            if (ended && sequence.IsStaged)
                committed = sequence.Commit(updateHistory: false);
            if (sequence.CanAsyncRendering())
                sequence.StartAsyncRendering();
            RegisterShiftDiagnosticsLog.Write(
                $"render request part=0x{((IntPtr)part).ToInt64():X} epoch={epoch} " +
                $"begin={began} invalidate={invalidated} invalidatedNotes={invalidationNotes.Count} " +
                $"update={updated} end={ended} commit={committed} " +
                $"staged={sequence.IsStaged} async={sequence.CanAsyncRendering()}");
            RegisterShiftDiagnosticsLog.WriteRenderedFlags("render-after", part, epoch);
        }
        catch
        {
            RestoreInvalidationNotes(invalidationNotes);
            if (began && !ended)
                part.CancelScoreEdit();
            throw;
        }
        finally
        {
            // The temporary velocity invalidation above emits native model-change
            // notifications after the REG value notification has already refreshed
            // the custom panel. Reassert the custom surface once those notifications
            // have finished so it cannot remain behind the native gray layer.
            Notify(part);
        }
    }

    private static bool RestoreInvalidationNotes(
        IEnumerable<(WIVSMNote Note, int Velocity)> invalidationNotes)
    {
        var restored = true;
        foreach (var (note, velocity) in invalidationNotes)
        {
            try
            {
                if (note.NoteVelocity != velocity)
                    note.NoteVelocity = velocity;
                restored &= note.NoteVelocity == velocity;
            }
            catch
            {
                restored = false;
            }
        }
        return restored;
    }

    private static void ApplySnapshot(string direction, IReadOnlyDictionary<IntPtr, int> snapshot)
    {
        RegisterShiftDiagnosticsLog.Write(
            $"history snapshot entered direction={direction} null={snapshot == null}");
        ArgumentNullException.ThrowIfNull(snapshot);
        RegisterShiftDiagnosticsLog.Write(
            $"history snapshot begin direction={direction} count={snapshot.Count} " +
            $"expected=[{FormatSnapshot(snapshot)}]");
        Dictionary<IntPtr, int> actual;
        try
        {
            lock (Sync)
            {
                foreach (var pair in snapshot)
                    SetCore(pair.Key, pair.Value);
                actual = new Dictionary<IntPtr, int>(snapshot.Count);
                foreach (var handle in snapshot.Keys)
                    actual[handle] = TryKey(handle, out var key) &&
                                     Values.TryGetValue(key, out var value)
                        ? value : DefaultValue;
            }
        }
        catch (Exception exception)
        {
            RegisterShiftDiagnosticsLog.Write(
                $"history snapshot exception direction={direction} " +
                $"exception={exception.GetType().Name}: {exception.Message}");
            throw;
        }
        var matches = snapshot.All(pair => actual.TryGetValue(pair.Key, out var value) &&
                                                  value == pair.Value);
        RegisterShiftDiagnosticsLog.Write(
            $"history snapshot direction={direction} matches={matches} " +
            $"expected=[{FormatSnapshot(snapshot)}] actual=[{FormatSnapshot(actual)}]");
        if (!matches)
            throw new InvalidOperationException($"REG {direction} snapshot verification failed.");
    }

    private static string FormatSnapshot(IReadOnlyDictionary<IntPtr, int> snapshot)
        => string.Join(',', snapshot.Select(pair => $"0x{pair.Key.ToInt64():X}={pair.Value}"));

    private sealed class RegisterShiftHistoryEdit(
        WIVSMSequence sequence,
        WIVSMMidiPart part,
        IReadOnlyDictionary<IntPtr, int> before,
        IReadOnlyDictionary<IntPtr, int> after) : ICustomParameterHistoryEdit
    {
        public void ApplyBefore() => ApplySnapshot("undo", before);

        public void ApplyAfter() => ApplySnapshot("redo", after);

        public void AfterApply()
        {
            RegisterShiftDiagnosticsLog.Write(
                $"history apply part=0x{((IntPtr)part).ToInt64():X}");
            // The active-part wrapper can be recreated by native undo/redo, so broadcast the
            // refresh instead of filtering it through the wrapper captured by this entry.
            Notify(null);
            ValuesChanged?.Invoke();
            CommandManager.InvalidateRequerySuggested();
            PublishAndRender(sequence, part);
        }
    }

    private static void Register(WIVSMNote note)
    {
        lock (Sync)
            if (!Generations.ContainsKey(note.CppObjPtr))
                Generations[note.CppObjPtr] = Interlocked.Increment(ref _nextGeneration);
    }

    private static bool TryKey(IntPtr handle, out NoteKey key)
    {
        if (Generations.TryGetValue(handle, out var generation))
        {
            key = new NoteKey(handle, generation);
            return true;
        }
        key = default;
        return false;
    }

    private static void SetCore(IntPtr handle, int value)
    {
        if (handle == IntPtr.Zero) return;
        if (!Generations.TryGetValue(handle, out var generation))
            generation = Generations[handle] = Interlocked.Increment(ref _nextGeneration);
        var key = new NoteKey(handle, generation);
        value = Math.Clamp(value, MinValue, MaxValue);
        if (value == 0) Values.Remove(key); else Values[key] = (sbyte)value;
    }

    private static WIVSMNote? FindNote(WIVSMSequence sequence, RegisterShiftProjectEntry entry)
    {
        if (entry.Track < 0 || (ulong)entry.Track >= sequence.NumTrack ||
            sequence.GetTrack((ulong)entry.Track) is not WIVSMMidiTrack track ||
            entry.Part < 0 || (ulong)entry.Part >= track.NumParts ||
            track.GetPart((ulong)entry.Part) is not WIVSMMidiPart part)
            return null;
        if (entry.Note >= 0 && (ulong)entry.Note < part.NumNotes &&
            part.GetNote((ulong)entry.Note) is { } indexed &&
            indexed.RelPosTick.Value == entry.RelPosTick && indexed.NoteNumber == entry.NoteNumber)
            return indexed;
        var occurrence = 0;
        for (ulong index = 0; index < part.NumNotes; index++)
            if (part.GetNote(index) is { } note && note.RelPosTick.Value == entry.RelPosTick &&
                note.NoteNumber == entry.NoteNumber && occurrence++ == entry.Occurrence)
                return note;
        return null;
    }

    private static void Notify(WIVSMMidiPart? part)
    {
        void Raise() => Changed?.Invoke(part);
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher == null || dispatcher.CheckAccess()) Raise();
        else dispatcher.BeginInvoke((Action)Raise);
    }

    private readonly record struct NoteKey(IntPtr Handle, long Generation);
}

internal sealed class RegisterShiftProjectLoadState(
    RegisterShiftProjectData? data,
    RegisterShiftProjectLoadState? previous)
{
    internal RegisterShiftProjectData? Data { get; } = data;
    internal RegisterShiftProjectLoadState? Previous { get; } = previous;
    internal bool Applied { get; set; }
}
