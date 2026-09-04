using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Windows;
using VOCALOIDPatcher.Config;
using Yamaha.VOCALOID;
using Yamaha.VOCALOID.MusicalEditor;
using Yamaha.VOCALOID.VSM;

namespace VOCALOIDPatcher.Evec;

internal sealed class EvecProjectLoadState(
    EvecProjectData? data,
    EvecProjectLoadState? previous)
{
    internal EvecProjectData? Data { get; } = data;
    internal EvecProjectLoadState? Previous { get; } = previous;
    internal bool Applied { get; set; }
}

internal static class EvecService
{
    internal sealed class LyricMoveTransfer
    {
        internal readonly WIVSMMidiPart Part;
        internal readonly WIVSMSequence? Sequence;
        internal readonly LyricMoveTransferItem[] Items;
        internal readonly Dictionary<IntPtr, LyricMoveTransferItem> ItemsByHandle;
        internal readonly LyricMoveTransfer? Previous;
        internal bool Finished;

        internal LyricMoveTransfer(
            WIVSMMidiPart part,
            WIVSMSequence? sequence,
            LyricMoveTransferItem[] items,
            LyricMoveTransfer? previous)
        {
            Part = part;
            Sequence = sequence;
            Items = items;
            ItemsByHandle = items.ToDictionary(item => item.Target.CppObjPtr);
            Previous = previous;
        }
    }

    internal sealed record LyricMoveTransferItem(
        WIVSMNote Target,
        EvecNoteState RequestedState,
        EvecHistorySnapshot BeforeSnapshot);

    internal sealed class PartStructureTransfer
    {
        internal readonly WIVSMSequence? Sequence;
        internal readonly PartStructureSource[] Sources;

        internal PartStructureTransfer(
            WIVSMSequence? sequence,
            PartStructureSource[] sources)
        {
            Sequence = sequence;
            Sources = sources;
        }
    }

    internal sealed record PartStructureSource(
        long AbsPosTick,
        int NoteNumber,
        int Order,
        EvecNoteState State,
        EvecHistorySnapshot Snapshot);

    internal sealed class RemovalTransfer
    {
        internal readonly WIVSMSequence? Sequence;
        internal readonly IntPtr[] Handles;
        internal readonly EvecHistorySnapshot[] EvecSnapshots;

        internal RemovalTransfer(
            WIVSMSequence? sequence,
            IntPtr[] handles,
            EvecHistorySnapshot[] evecSnapshots)
        {
            Sequence = sequence;
            Handles = handles;
            EvecSnapshots = evecSnapshots;
        }
    }

    internal sealed record SequenceCloseState(
        IntPtr SequenceHandle,
        IntPtr[] NoteHandles);

    internal sealed class VoiceBankChange
    {
        internal readonly WIVSMMidiPart Part;
        internal readonly WIVSMSequence? Sequence;
        internal readonly string OriginalVoiceBankId;
        internal readonly VoiceBankChangeItem[] Items;
        internal bool Finished;

        internal VoiceBankChange(
            WIVSMMidiPart part,
            WIVSMSequence? sequence,
            string originalVoiceBankId,
            VoiceBankChangeItem[] items)
        {
            Part = part;
            Sequence = sequence;
            OriginalVoiceBankId = originalVoiceBankId;
            Items = items;
        }
    }

    internal sealed record VoiceBankChangeItem(
        WIVSMNote Note,
        EvecNoteState BeforeState,
        EvecHistorySnapshot BeforeSnapshot);

    internal sealed class ClipboardPartPropertyTransfer
    {
        internal readonly WIVSMSequence? Sequence;
        internal readonly PartProperty Property;
        internal readonly ClipboardPartPropertyTransferItem[] Items;

        internal ClipboardPartPropertyTransfer(
            WIVSMSequence? sequence,
            PartProperty property,
            ClipboardPartPropertyTransferItem[] items)
        {
            Sequence = sequence;
            Property = property;
            Items = items;
        }
    }

    internal sealed record ClipboardPartPropertyTransferItem(
        WIVSMMidiPart Source,
        WIVSMMidiPart Target,
        bool VoiceBankChanges,
        EvecHistorySnapshot[] BeforeSnapshots,
        EvecNoteState[] SourceStates);

    internal sealed class ClipboardPropertyTransfer
    {
        internal readonly WIVSMSequence? Sequence;
        internal readonly ClipboardPropertyTransferItem[] Items;

        internal ClipboardPropertyTransfer(
            WIVSMSequence? sequence,
            ClipboardPropertyTransferItem[] items)
        {
            Sequence = sequence;
            Items = items;
        }
    }

    internal sealed record ClipboardPropertyTransferItem(
        WIVSMNote Target,
        EvecNoteState SourceState,
        EvecNoteState BeforeState,
        EvecHistorySnapshot BeforeSnapshot);

    private static readonly object Sync = new();
    private static readonly Dictionary<IntPtr, long> Generations = new();
    private static readonly Dictionary<NoteKey, EvecCachedState> States = new();
    private static readonly Dictionary<IntPtr, EvecHistory> Histories = new();
    private static readonly Dictionary<IntPtr, PendingHistoryTransition> PendingHistoryTransitions = new();
    private static readonly Dictionary<WIVSMMidiPart, VoiceBankChange> PendingAutomaticVoiceBankChanges =
        new(ReferenceEqualityComparer.Instance);
    private static readonly HashSet<IntPtr> PendingTempoSequences = new();
    private const int MaxHistoryEntries = 512;
    private static long _nextGeneration;
    [ThreadStatic] private static EvecProjectLoadState? _pendingProjectLoad;
    [ThreadStatic] private static LyricMoveTransfer? _pendingLyricMove;

    internal static event Action? Changed;
    internal static event Action<WIVSMMidiPart?>? PartChanged;

    internal static bool IsEnabled => Settings.EvecEnabled;

    internal static EvecNoteState GetState(WIVSMNote? note)
    {
        if (note == null)
            return EvecNoteState.Empty;

        string phonemes = note.Phonemes;
        string voiceBankId = GetVoiceBankId(note);
        lock (Sync)
            Register(note);

        // Rin/Len CTop 301 and one plain extension repeat both serialize as
        // "C C V". A live state/sidecar may disambiguate the current note, but
        // old states for the same spelling must not be resurrected after an
        // undo or external phoneme edit.
        lock (Sync)
        {
            if (TryKey(note.CppObjPtr, out var key))
            {
                if (States.TryGetValue(key, out var cached) &&
                    string.Equals(cached.Phonemes, phonemes, StringComparison.Ordinal) &&
                    string.Equals(cached.VoiceBankId, voiceBankId, StringComparison.Ordinal))
                    return cached.State.Clone();
            }
        }

        bool hasDetectedState = EvecPhonemeRecomposer.TryParseEvecFromPhonemes(
            phonemes,
            out var detected,
            out _) && detected.HasAnyEvec;

        if (hasDetectedState)
        {
            var part = note.Parent as WIVSMMidiPart;
            var capabilities = EvecVoicebankDetector.GetCapabilities(part?.VoiceBank());
            detected = EvecPhonemeRecomposer.ResolvePlainAttackAmbiguity(
                phonemes,
                detected,
                capabilities.PlainAttackId);
            detected = capabilities.Normalize(phonemes, detected);
            hasDetectedState = detected.HasAnyEvec;
        }

        lock (Sync)
        {
            if (TryKey(note.CppObjPtr, out var key))
            {
                if (hasDetectedState)
                    States[key] = new EvecCachedState(detected.Clone(), phonemes, voiceBankId);
                else
                    States.Remove(key);
            }
        }

        return hasDetectedState ? detected : EvecNoteState.Empty;
    }

    internal static bool HasEvec(WIVSMNote? note) => GetState(note).HasAnyEvec;

    internal static bool SetNoteEvec(WIVSMNote note, EvecNoteState newState, bool commit = true)
    {
        if (note == null) return false;

        var requestedState = newState.Clone();
        newState = NormalizeStateForNote(note, newState);

        var sequence = note.Parent?.Sequence;
        var beforeState = NormalizeStateForNote(note, GetState(note));
        var beforeSnapshot = CaptureHistorySnapshot(note, beforeState);
        bool success;
        string committedPhonemes;

        if (commit && sequence != null)
        {
            using (var transaction = new Transaction(sequence) { Result = false })
            {
                success = TryApplyToNote(note, newState, out committedPhonemes);
                transaction.Result = success;
            }
        }
        else
        {
            success = TryApplyToNote(note, newState, out committedPhonemes);
        }

        if (success)
        {
            CacheState(note, newState, committedPhonemes);
            if (commit && sequence != null)
            {
                RecordHistory(
                    sequence,
                    new EvecHistoryChange(
                        beforeSnapshot,
                        CaptureHistorySnapshot(note, newState)));
            }
        }

        EvecDiagnosticLog.Record(
            beforeState,
            requestedState,
            newState,
            success,
            beforeSnapshot.Phonemes,
            committedPhonemes);

        if (commit)
            Refresh();

        return success;
    }

    internal static void ApplyStateToNotes(IEnumerable<WIVSMNote> notes, EvecNoteState state)
    {
        UpdateNotes(notes, _ => state.Clone());
    }

    internal static void UpdateVoiceColor(IEnumerable<WIVSMNote> notes, int voiceColorId)
    {
        UpdateNotes(notes, current =>
        {
            current.VoiceColorId = voiceColorId;
            return current;
        });
    }

    internal static void UpdateAttack(IEnumerable<WIVSMNote> notes, int attackId)
    {
        UpdateNotes(notes, current =>
        {
            current.AttackId = attackId;
            return current;
        });
    }

    internal static void UpdateRelease(IEnumerable<WIVSMNote> notes, int releaseId)
    {
        UpdateNotes(notes, current =>
        {
            current.ReleaseId = releaseId;
            return current;
        });
    }

    internal static void UpdateConsonantExtension(IEnumerable<WIVSMNote> notes, int extension)
    {
        UpdateNotes(notes, (note, current) =>
        {
            var part = note.Parent as WIVSMMidiPart;
            var capabilities = EvecVoicebankDetector.GetCapabilities(part?.VoiceBank());
            return capabilities.SelectConsonantExtension(note.Phonemes, current, extension);
        });
    }

    private static void UpdateNotes(
        IEnumerable<WIVSMNote> notes,
        Func<EvecNoteState, EvecNoteState> update)
    {
        UpdateNotes(notes, (_, current) => update(current));
    }

    private static void UpdateNotes(
        IEnumerable<WIVSMNote> notes,
        Func<WIVSMNote, EvecNoteState, EvecNoteState> update)
    {
        var noteList = notes
            .Where(note => note != null)
            .GroupBy(note => note.CppObjPtr)
            .Select(group => group.First())
            .ToList();
        if (noteList.Count == 0)
            return;

        var plans = noteList
            .Select(note =>
            {
                var beforeState = NormalizeStateForNote(note, GetState(note));
                // Dimension updaters mutate their argument. Keep a distinct
                // before snapshot so undo/history/diagnostics never inherit
                // the newly requested value by reference.
                var requested = update(note, beforeState.Clone());
                return new EvecUpdatePlan(
                    note,
                    beforeState,
                    requested,
                    NormalizeStateForNote(note, requested),
                    CaptureHistorySnapshot(note, beforeState));
            })
            .ToList();
        var sequence = noteList[0].Parent?.Sequence;
        bool canUseSingleTransaction = sequence != null &&
                                       noteList.All(note => sequence.Equals(note.Parent?.Sequence));
        bool anySucceeded = false;

        if (canUseSingleTransaction)
        {
            using (var transaction = new Transaction(sequence!) { Result = false })
            {
                foreach (var plan in plans)
                {
                    if (!TryApplyToNote(plan.Note, plan.State, out var committedPhonemes))
                        continue;

                    plan.CommittedPhonemes = committedPhonemes;
                    plan.AfterSnapshot = CaptureHistorySnapshot(plan.Note, plan.State);
                    plan.Succeeded = true;
                    anySucceeded = true;
                }

                transaction.Result = anySucceeded;
            }

            if (anySucceeded)
            {
                foreach (var plan in plans.Where(item => item.Succeeded))
                    CacheState(plan.Note, plan.State, plan.CommittedPhonemes);

                RecordHistory(
                    sequence!,
                    plans
                        .Where(item => item.Succeeded && item.AfterSnapshot != null)
                        .Select(item => new EvecHistoryChange(
                            item.BeforeSnapshot,
                            item.AfterSnapshot!)));
            }

            foreach (var plan in plans)
            {
                EvecDiagnosticLog.Record(
                    plan.BeforeState,
                    plan.RequestedState,
                    plan.State,
                    plan.Succeeded,
                    plan.BeforeSnapshot.Phonemes,
                    plan.CommittedPhonemes);
            }
        }
        else
        {
            foreach (var plan in plans)
            {
                bool planSucceeded;
                string committedPhonemes;
                var planSequence = plan.Note.Parent?.Sequence;
                if (planSequence != null)
                {
                    using (var transaction = new Transaction(planSequence) { Result = false })
                    {
                        planSucceeded = TryApplyToNote(plan.Note, plan.State, out committedPhonemes);
                        transaction.Result = planSucceeded;
                    }
                }
                else
                {
                    planSucceeded = TryApplyToNote(plan.Note, plan.State, out committedPhonemes);
                }

                if (planSucceeded)
                {
                    CacheState(plan.Note, plan.State, committedPhonemes);
                    if (planSequence != null)
                    {
                        RecordHistory(
                            planSequence,
                            new EvecHistoryChange(
                                plan.BeforeSnapshot,
                                CaptureHistorySnapshot(plan.Note, plan.State)));
                    }
                    anySucceeded = true;
                }


                EvecDiagnosticLog.Record(
                    plan.BeforeState,
                    plan.RequestedState,
                    plan.State,
                    planSucceeded,
                    plan.BeforeSnapshot.Phonemes,
                    committedPhonemes);
            }
        }

        // Keep the UI and badges in sync after both commits and rollbacks. The
        // per-note path deliberately emits no notification, so a batch has one
        // coherent refresh instead of one refresh per selected note.
        Refresh();
    }

    private static bool TryApplyToNote(
        WIVSMNote note,
        EvecNoteState newState,
        out string committedPhonemes)
    {
        string currentPhonemes = note.Phonemes;
        string recomposed = EvecPhonemeRecomposer.Recompose(currentPhonemes, newState);
        bool phonemesChanged = !string.Equals(
            currentPhonemes,
            recomposed,
            StringComparison.Ordinal);
        bool wasProtected = note.IsProtected;
        bool wasValidPhonemes = note.IsValidPhonemes;
        int previousLangId = note.LangID;
        var previousPositions = note.GetPhonemePositions();
        // Use the logical state while its cached/sidecar realization still
        // exactly matches the note. Rin/Len CTop 301 and one plain repeat have
        // the same physical "C C V" spelling, so reparsing the string here
        // would falsely report that unrelated controls had changed.
        EvecNoteState previousState = GetState(note);
        bool articulationTimingChanged =
            previousState.VoiceColorId != newState.VoiceColorId ||
            previousState.AttackId != newState.AttackId ||
            previousState.ReleaseId != newState.ReleaseId ||
            previousState.ConsonantExtension != newState.ConsonantExtension;

        // Never cache an articulation that the note's physical phoneme string
        // cannot represent (for example, consonant extension on a vowel-only
        // note). That would recreate a second source of truth even if the VSM
        // write itself succeeded.
        if (newState.HasAnyEvec &&
            !EvecPhonemeRecomposer.CanRepresent(currentPhonemes, newState))
        {
            committedPhonemes = currentPhonemes;
            return false;
        }

        if (phonemesChanged)
        {
            note.IsProtected = false;

            // The Boolean is the validity state stored on the note, not a
            // request to run G2PA validation. EVEC phonemes are registered in
            // the voicebank PHDC and must remain renderable/valid after this
            // direct write. Routing the string through G2PA would normalize
            // the suffixes, so write it directly and verify the result.
            if (!note.SetPhonemes(recomposed, true, note.LangID) ||
                !string.Equals(note.Phonemes, recomposed, StringComparison.Ordinal))
            {
                if (!string.Equals(note.Phonemes, currentPhonemes, StringComparison.Ordinal))
                {
                    note.SetPhonemes(currentPhonemes, wasValidPhonemes, previousLangId);
                    RestorePhonemePositions(note, previousPositions);
                }
                note.IsProtected = wasProtected;
                committedPhonemes = currentPhonemes;
                return false;
            }
        }

        if (!articulationTimingChanged)
            RestorePhonemePositions(note, previousPositions);

        if (newState.HasAnyEvec)
        {
            // CTop and pronunciation extension add physical consonant copies,
            // so either can change the boundary count just like Color/Release.
            if (articulationTimingChanged)
            {
                // Suffix-less CTop and a plain repeat can have the same V6
                // phoneme string. Reset only timing owned by a logical option
                // that was removed before applying the new state, otherwise a
                // previous CTop boundary can survive and make the controls
                // sound interlocked even though their logical states changed.
                if (!phonemesChanged)
                    EvecTimingAllocator.ResetRemovedTiming(note, previousState, newState);
                EvecTimingAllocator.ApplyTiming(note, newState);
            }

            note.IsProtected = true;
        }
        else
        {
            // SetPhonemes already rebuilt the native boundary list. Do not
            // reset every edited boundary or overwrite expression/velocity:
            // those values may have been authored by the user before EVEC.
            note.IsProtected = false;
        }

        committedPhonemes = note.Phonemes;
        return string.Equals(committedPhonemes, recomposed, StringComparison.Ordinal);
    }

    private static void RestorePhonemePositions(WIVSMNote note, IReadOnlyList<int> previousPositions)
    {
        var currentPositions = note.GetPhonemePositions();
        if (previousPositions.Count != currentPositions.Count)
            return;

        // The final position is the note's end boundary rather than the start
        // of a phoneme. Preserve every editable phoneme-start boundary that
        // remains legal for the replacement suffix.
        for (int index = 0; index < previousPositions.Count - 1; index++)
        {
            int position = previousPositions[index];
            if (currentPositions[index] == position)
                continue;

            var range = note.GetAcceptablePhonemePositionRange(index);
            range.Normalize();
            if (range.DurationTick.Value > 0 && position >= range.Begin && position <= range.End)
                note.SetEditedPhonemePosition(index, new VSMRelTick(position));
        }
    }

    private static void CacheState(WIVSMNote note, EvecNoteState state, string phonemes)
    {
        lock (Sync)
        {
            Register(note);
            if (!TryKey(note.CppObjPtr, out var key))
                return;

            if (state.HasAnyEvec)
                States[key] = new EvecCachedState(
                    state.Clone(),
                    phonemes,
                    GetVoiceBankId(note));
            else
                States.Remove(key);
        }
    }

    private static EvecNoteState NormalizeStateForNote(WIVSMNote note, EvecNoteState state)
    {
        var part = note.Parent as WIVSMMidiPart;
        return EvecVoicebankDetector.GetCapabilities(part?.VoiceBank()).Normalize(note.Phonemes, state);
    }

    private static string GetVoiceBankId(WIVSMNote note) =>
        (note.Parent as WIVSMMidiPart)?.VoiceBankID ?? string.Empty;

    internal static void ReconcileAfterUndo(WIVSMSequence sequence) =>
        ReconcileHistory(sequence, undo: true);

    internal static void ReconcileAfterRedo(WIVSMSequence sequence) =>
        ReconcileHistory(sequence, undo: false);

    private static void RecordHistory(
        WIVSMSequence sequence,
        params EvecHistoryChange[] changes) =>
        RecordHistory(sequence, (IEnumerable<EvecHistoryChange>)changes);

    private static void RecordHistory(
        WIVSMSequence sequence,
        IEnumerable<EvecHistoryChange> changes)
    {
        var materialized = changes
            .Where(change => HasPhysicalDifference(change.Before, change.After))
            .ToArray();
        if (materialized.Length == 0)
            return;

        RecordHistoryTransition(
            sequence,
            materialized.Select(change => change.Before),
            materialized.Select(change => change.After));
    }

    private static void RecordHistoryTransition(
        WIVSMSequence sequence,
        IEnumerable<EvecHistorySnapshot> before,
        IEnumerable<EvecHistorySnapshot> after)
    {
        var beforeSnapshots = before.ToArray();
        var afterSnapshots = after.ToArray();
        if (beforeSnapshots.Length == 0 && afterSnapshots.Length == 0)
            return;

        lock (Sync)
        {
            IntPtr sequenceHandle = (IntPtr)sequence;
            if (!Histories.TryGetValue(sequenceHandle, out var history))
            {
                history = new EvecHistory();
                Histories[sequenceHandle] = history;
            }

            history.Undo.Add(new EvecHistoryEdit(beforeSnapshots, afterSnapshots));
            history.Redo.Clear();
            if (history.Undo.Count > MaxHistoryEntries)
                history.Undo.RemoveAt(0);
        }
    }

    private static void RecordOrStageHistoryTransition(
        WIVSMSequence sequence,
        IEnumerable<EvecHistorySnapshot> before,
        IEnumerable<EvecHistorySnapshot> after)
    {
        var beforeSnapshots = before.ToArray();
        var afterSnapshots = after.ToArray();
        if (!sequence.IsStaged)
        {
            RecordHistoryTransition(sequence, beforeSnapshots, afterSnapshots);
            return;
        }

        lock (Sync)
        {
            IntPtr sequenceHandle = (IntPtr)sequence;
            if (!PendingHistoryTransitions.TryGetValue(sequenceHandle, out var pending))
            {
                pending = new PendingHistoryTransition();
                PendingHistoryTransitions[sequenceHandle] = pending;
            }

            pending.Transitions.Apply(
                beforeSnapshots,
                afterSnapshots,
                snapshot => snapshot.Handle);
        }
    }

    private static void ReconcileHistory(WIVSMSequence sequence, bool undo)
    {
        EvecHistoryEdit? edit;
        lock (Sync)
        {
            if (!Histories.TryGetValue((IntPtr)sequence, out var history))
                return;

            var historySource = undo ? history.Undo : history.Redo;
            edit = historySource.LastOrDefault();
        }

        if (edit == null)
            return;

        var target = undo ? edit.Before : edit.After;
        var sourceSnapshots = undo ? edit.After : edit.Before;
        var notes = FindCurrentNotes(sequence, target);
        if (!MatchesHistoryTarget(notes, target, sourceSnapshots))
            return;

        lock (Sync)
        {
            if (!Histories.TryGetValue((IntPtr)sequence, out var history))
                return;

            var historySource = undo ? history.Undo : history.Redo;
            var destination = undo ? history.Redo : history.Undo;
            if (historySource.Count == 0 || !ReferenceEquals(historySource[^1], edit))
                return;

            historySource.RemoveAt(historySource.Count - 1);
            destination.Add(edit);
        }

        var targetHandles = target.Select(snapshot => snapshot.Handle).ToHashSet();
        foreach (var snapshot in sourceSnapshots)
        {
            if (!targetHandles.Contains(snapshot.Handle))
                RemoveHandle(snapshot.Handle);
        }

        foreach (var snapshot in target)
        {
            if (!notes.TryGetValue(snapshot.Handle, out var note))
                continue;

            CacheState(note, snapshot.State, snapshot.Phonemes);
        }

        Refresh();
    }

    private static Dictionary<IntPtr, WIVSMNote> FindCurrentNotes(
        WIVSMSequence sequence,
        IReadOnlyList<EvecHistorySnapshot> snapshots)
    {
        var wanted = snapshots.Select(snapshot => snapshot.Handle).ToHashSet();
        var result = new Dictionary<IntPtr, WIVSMNote>();

        try
        {
            for (ulong trackIndex = 0; trackIndex < sequence.NumTrack && result.Count < wanted.Count; trackIndex++)
            {
                if (sequence.GetTrack(trackIndex) is not WIVSMMidiTrack track)
                    continue;

                for (ulong partIndex = 0; partIndex < track.NumParts && result.Count < wanted.Count; partIndex++)
                {
                    if (track.GetPart(partIndex) is not WIVSMMidiPart part)
                        continue;

                    for (ulong noteIndex = 0; noteIndex < part.NumNotes && result.Count < wanted.Count; noteIndex++)
                    {
                        if (part.GetNote(noteIndex) is { } note && wanted.Contains(note.CppObjPtr))
                            result[note.CppObjPtr] = note;
                    }
                }
            }
        }
        catch
        {
            result.Clear();
        }

        return result;
    }

    private static bool MatchesHistoryTarget(
        IReadOnlyDictionary<IntPtr, WIVSMNote> notes,
        IReadOnlyList<EvecHistorySnapshot> target,
        IReadOnlyList<EvecHistorySnapshot> source)
    {
        var sourceByHandle = source.ToDictionary(snapshot => snapshot.Handle);
        foreach (var snapshot in target)
        {
            if (!notes.TryGetValue(snapshot.Handle, out var note))
                return false;

            if (!string.Equals(note.Phonemes, snapshot.Phonemes, StringComparison.Ordinal))
                return false;
            if (!string.Equals(note.Lyric, snapshot.Lyric, StringComparison.Ordinal))
                return false;

            // Different phoneme strings uniquely identify the native history
            // transition. For the rare same-string transition, use the owned
            // timing/protection fields as the discriminator.
            if (sourceByHandle.TryGetValue(snapshot.Handle, out var sourceSnapshot) &&
                string.Equals(
                    sourceSnapshot.Phonemes,
                    snapshot.Phonemes,
                    StringComparison.Ordinal))
            {
                if (!sourceSnapshot.Positions.SequenceEqual(snapshot.Positions) &&
                    !note.GetPhonemePositions().SequenceEqual(snapshot.Positions))
                    return false;
                if (sourceSnapshot.IsProtected != snapshot.IsProtected &&
                    note.IsProtected != snapshot.IsProtected)
                    return false;
            }
        }

        return true;
    }

    private static EvecHistorySnapshot CaptureHistorySnapshot(
        WIVSMNote note,
        EvecNoteState state)
    {
        int[] positions;
        try
        {
            positions = note.GetPhonemePositions().ToArray();
        }
        catch
        {
            positions = Array.Empty<int>();
        }

        return new EvecHistorySnapshot(
            note.CppObjPtr,
            state.Clone(),
            note.Lyric,
            note.Phonemes,
            positions,
            note.IsProtected);
    }

    private static bool HasPhysicalDifference(
        EvecHistorySnapshot before,
        EvecHistorySnapshot after) =>
        !string.Equals(before.Lyric, after.Lyric, StringComparison.Ordinal) ||
        !string.Equals(before.Phonemes, after.Phonemes, StringComparison.Ordinal) ||
        !before.Positions.SequenceEqual(after.Positions) ||
        before.IsProtected != after.IsProtected;

    internal static void ResetNotes(IEnumerable<WIVSMNote> notes)
    {
        ApplyStateToNotes(notes, EvecNoteState.Empty);
    }

    internal static void CloneState(WIVSMNote source, WIVSMNote target)
    {
        var state = GetState(source);
        if (state.HasAnyEvec)
        {
            SetNoteEvec(target, state.Clone(), commit: false);
        }
    }

    internal static void ClonePartStates(WIVSMMidiPart? source, WIVSMMidiPart? target)
    {
        if (!IsEnabled || source == null || target == null)
            return;

        ulong count = Math.Min(source.NumNotes, target.NumNotes);
        for (ulong index = 0; index < count; index++)
        {
            if (source.GetNote(index) is { } sourceNote &&
                target.GetNote(index) is { } targetNote)
            {
                CloneState(sourceNote, targetNote);
            }
        }
    }

    internal static void CloneTrackStates(WIVSMTrack? source, WIVSMTrack? target)
    {
        if (source is not WIVSMMidiTrack sourceTrack ||
            target is not WIVSMMidiTrack targetTrack)
        {
            return;
        }

        ulong count = Math.Min(sourceTrack.NumParts, targetTrack.NumParts);
        for (ulong index = 0; index < count; index++)
        {
            ClonePartStates(
                sourceTrack.GetPart(index) as WIVSMMidiPart,
                targetTrack.GetPart(index) as WIVSMMidiPart);
        }
    }

    internal static void CloneSequenceStates(WIVSMSequence? source, WIVSMSequence? target)
    {
        if (!IsEnabled || source == null || target == null)
            return;

        ulong count = Math.Min(source.NumTrack, target.NumTrack);
        for (ulong index = 0; index < count; index++)
            CloneTrackStates(source.GetTrack(index), target.GetTrack(index));
    }

    internal static IntPtr[] CaptureClipboardNoteHandles(
        WIVSMClipboard clipboard,
        bool includeNotes,
        bool includeMidiParts)
    {
        if (clipboard == null)
            return Array.Empty<IntPtr>();

        var handles = new HashSet<IntPtr>();
        try
        {
            if (includeNotes)
            {
                for (ulong index = 0; index < clipboard.NumNote; index++)
                {
                    if (clipboard.GetNote(index) is { } note)
                        handles.Add(note.CppObjPtr);
                }
            }

            if (includeMidiParts)
            {
                for (ulong partIndex = 0; partIndex < clipboard.NumMidiPart; partIndex++)
                {
                    if (clipboard.GetMidiPart(partIndex) is not { } part)
                        continue;
                    for (ulong noteIndex = 0; noteIndex < part.NumNotes; noteIndex++)
                    {
                        if (part.GetNote(noteIndex) is { } note)
                            handles.Add(note.CppObjPtr);
                    }
                }
            }
        }
        catch
        {
            // Return the handles captured before a stale clipboard wrapper.
        }

        return handles.ToArray();
    }

    internal static void ReleaseHandles(IEnumerable<IntPtr> handles)
    {
        foreach (var handle in handles)
            RemoveHandle(handle);
    }

    internal static void ReapplyTimingAfterGeometryChange(WIVSMNote note)
    {
        if (!IsEnabled || note == null)
            return;

        var state = NormalizeStateForNote(note, GetState(note));
        if (!state.HasAnyEvec)
            return;

        // Duration and left-edge edits can move the logical note end without
        // changing the EVEC phoneme string. Re-anchor Common/VSil boundaries
        // in the caller's existing native transaction so a release does not
        // grow with every later note resize.
        EvecTimingAllocator.ApplyTiming(note, state);
    }

    internal static void ReapplyPartTimingAfterPositionChange(WIVSMMidiPart? part)
    {
        if (!IsEnabled || part == null)
            return;

        for (ulong index = 0; index < part.NumNotes; index++)
        {
            if (part.GetNote(index) is not { } note)
                continue;
            try
            {
                ReapplyTimingAfterGeometryChange(note);
            }
            catch
            {
                // One stale native wrapper must not prevent the remaining
                // notes in a moved Part from being re-anchored.
            }
        }
    }

    internal static PartStructureTransfer? PreparePartStructureTransfer(
        IEnumerable<WIVSMMidiPart> parts)
    {
        if (!IsEnabled || parts == null)
            return null;

        var materialized = parts.Where(part => part != null).ToArray();
        if (materialized.Length == 0)
            return null;

        WIVSMSequence? sequence = materialized[0].Sequence;
        if (sequence != null && materialized.Any(part => !sequence.Equals(part.Sequence)))
            sequence = null;

        var sources = new List<PartStructureSource>();
        int order = 0;
        foreach (var part in materialized)
        {
            for (ulong index = 0; index < part.NumNotes; index++)
            {
                if (part.GetNote(index) is not { } note)
                    continue;
                var state = GetState(note);
                sources.Add(new PartStructureSource(
                    note.AbsPosTick.Value,
                    note.NoteNumber,
                    order++,
                    state.Clone(),
                    CaptureHistorySnapshot(note, state)));
            }
        }

        if (!sources.Any(source => source.State.HasAnyEvec))
            return null;

        return new PartStructureTransfer(
            sequence,
            sources
                .OrderBy(source => source.AbsPosTick)
                .ThenBy(source => source.NoteNumber)
                .ThenBy(source => source.Order)
                .ToArray());
    }

    internal static void CompletePartStructureTransfer(
        PartStructureTransfer? transfer,
        IEnumerable<WIVSMMidiPart> resultParts)
    {
        if (transfer == null || resultParts == null)
            return;

        var targets = resultParts
            .Where(part => part != null)
            .SelectMany(part => part.Notes)
            .Where(note => note != null)
            .OrderBy(note => note.AbsPosTick.Value)
            .ThenBy(note => note.NoteNumber)
            .ToArray();
        if (targets.Length == 0)
            return;

        int count = Math.Min(transfer.Sources.Length, targets.Length);
        var afterSnapshots = new List<EvecHistorySnapshot>(count);
        for (int index = 0; index < count; index++)
        {
            var target = targets[index];
            var requested = transfer.Sources[index].State;
            var state = NormalizeStateForNote(target, requested);
            if (state.HasAnyEvec)
            {
                if (!SetNoteEvec(target, state, commit: false))
                {
                    RemoveNote(target);
                    state = GetState(target);
                }
            }
            else
            {
                RemoveNote(target);
            }

            afterSnapshots.Add(CaptureHistorySnapshot(target, state));
        }

        var afterHandles = afterSnapshots.Select(snapshot => snapshot.Handle).ToHashSet();
        foreach (var source in transfer.Sources)
        {
            if (!afterHandles.Contains(source.Snapshot.Handle))
                RemoveHandle(source.Snapshot.Handle);
        }

        if (transfer.Sequence != null)
        {
            RecordOrStageHistoryTransition(
                transfer.Sequence,
                transfer.Sources.Select(source => source.Snapshot),
                afterSnapshots);
        }

        Refresh();
    }

    internal static IntPtr[] CapturePartNoteHandles(WIVSMMidiPart? part)
    {
        if (part == null)
            return Array.Empty<IntPtr>();

        var handles = new List<IntPtr>();
        try
        {
            for (ulong index = 0; index < part.NumNotes; index++)
            {
                if (part.GetNote(index) is { } note)
                    handles.Add(note.CppObjPtr);
            }
        }
        catch
        {
            // Return handles captured before a stale native wrapper.
        }
        return handles.ToArray();
    }

    internal static RemovalTransfer? PrepareRemoval(IEnumerable<WIVSMMidiPart> parts)
    {
        if (parts == null)
            return null;

        var materialized = parts.Where(part => part != null).ToArray();
        if (materialized.Length == 0)
            return null;

        WIVSMSequence? sequence = materialized[0].Sequence;
        if (sequence != null && materialized.Any(part => !sequence.Equals(part.Sequence)))
            sequence = null;

        var handles = new HashSet<IntPtr>();
        var snapshots = new List<EvecHistorySnapshot>();
        foreach (var part in materialized)
        {
            try
            {
                for (ulong index = 0; index < part.NumNotes; index++)
                {
                    if (part.GetNote(index) is not { } note)
                        continue;

                    handles.Add(note.CppObjPtr);
                    var state = GetState(note);
                    if (state.HasAnyEvec)
                        snapshots.Add(CaptureHistorySnapshot(note, state));
                }
            }
            catch
            {
                // Keep objects captured before a stale native wrapper.
            }
        }

        return new RemovalTransfer(sequence, handles.ToArray(), snapshots.ToArray());
    }

    internal static RemovalTransfer? PrepareTrackRemoval(WIVSMTrack? track)
    {
        if (track is not WIVSMMidiTrack midiTrack)
            return null;

        var parts = new List<WIVSMMidiPart>();
        try
        {
            for (ulong index = 0; index < midiTrack.NumParts; index++)
            {
                if (midiTrack.GetPart(index) is { } part)
                    parts.Add(part);
            }
        }
        catch
        {
            // Use the Parts that were captured before a stale wrapper.
        }

        return PrepareRemoval(parts);
    }

    internal static void CompleteRemoval(RemovalTransfer? transfer)
    {
        if (transfer == null)
            return;

        ReleaseHandles(transfer.Handles);
        if (transfer.Sequence != null && transfer.EvecSnapshots.Length > 0)
        {
            RecordOrStageHistoryTransition(
                transfer.Sequence,
                transfer.EvecSnapshots,
                Array.Empty<EvecHistorySnapshot>());
        }
    }

    internal static SequenceCloseState CaptureSequenceClose(WIVSMSequence? sequence)
    {
        if (sequence == null)
            return new SequenceCloseState(IntPtr.Zero, Array.Empty<IntPtr>());

        var handles = new HashSet<IntPtr>();
        try
        {
            for (ulong trackIndex = 0; trackIndex < sequence.NumTrack; trackIndex++)
            {
                if (sequence.GetTrack(trackIndex) is not WIVSMMidiTrack track)
                    continue;

                for (ulong partIndex = 0; partIndex < track.NumParts; partIndex++)
                {
                    if (track.GetPart(partIndex) is not { } part)
                        continue;
                    foreach (var handle in CapturePartNoteHandles(part))
                        handles.Add(handle);
                }
            }
        }
        catch
        {
            // The prefix must never interfere with native sequence closing.
        }

        return new SequenceCloseState((IntPtr)sequence, handles.ToArray());
    }

    internal static void CompleteSequenceClose(SequenceCloseState? state)
    {
        if (state == null || state.SequenceHandle == IntPtr.Zero)
            return;

        lock (Sync)
        {
            foreach (var handle in state.NoteHandles)
            {
                if (TryKey(handle, out var key))
                    States.Remove(key);
                Generations.Remove(handle);
            }

            Histories.Remove(state.SequenceHandle);
            PendingHistoryTransitions.Remove(state.SequenceHandle);
            PendingTempoSequences.Remove(state.SequenceHandle);

            foreach (var part in PendingAutomaticVoiceBankChanges
                         .Where(pair => pair.Value.Sequence != null &&
                                        (IntPtr)pair.Value.Sequence == state.SequenceHandle)
                         .Select(pair => pair.Key)
                         .ToArray())
            {
                PendingAutomaticVoiceBankChanges.Remove(part);
            }
        }

        if (_pendingLyricMove?.Sequence != null &&
            (IntPtr)_pendingLyricMove.Sequence == state.SequenceHandle)
        {
            _pendingLyricMove = _pendingLyricMove.Previous;
        }
    }

    internal static LyricMoveTransfer? PrepareLyricMove(
        WIVSMMidiPart part,
        bool moveRight)
    {
        if (!IsEnabled || part == null)
            return null;

        var notes = part.Notes;
        var selected = part.StraightSelectedNote;
        if (notes == null || notes.Count == 0 || selected == null || selected.Count == 0)
            return null;

        var selectedHandles = selected.Select(note => note.CppObjPtr).ToHashSet();
        var selectedIndices = notes
            .Select((note, index) => (note, index))
            .Where(pair => selectedHandles.Contains(pair.note.CppObjPtr))
            .Select(pair => pair.index)
            .ToArray();
        if (selectedIndices.Length == 0)
            return null;

        int firstSelected = selectedIndices.Min();
        int lastSelected = selectedIndices.Max();
        var assignments = EvecLyricMovePlanner.Build(
            notes.Count,
            firstSelected,
            lastSelected,
            selected.Count == 1,
            moveRight);

        var items = new List<LyricMoveTransferItem>();
        bool hasLogicalState = false;
        foreach (var assignment in assignments)
        {
            var requestedState = assignment.SourceIndex is { } sourceIndex
                ? GetState(notes[sourceIndex])
                : EvecNoteState.Empty;
            var target = notes[assignment.TargetIndex];
            var beforeState = GetState(target);
            hasLogicalState |= requestedState.HasAnyEvec || beforeState.HasAnyEvec;
            items.Add(new LyricMoveTransferItem(
                target,
                requestedState.Clone(),
                CaptureHistorySnapshot(target, beforeState)));
        }

        if (!hasLogicalState)
            return null;

        var transfer = new LyricMoveTransfer(
            part,
            part.Sequence,
            items.ToArray(),
            _pendingLyricMove);
        _pendingLyricMove = transfer;
        return transfer;
    }

    internal static void ReconcileRawPhonemeWrite(
        WIVSMNote note,
        bool success)
    {
        if (!success || note == null)
            return;

        // A successful direct VSM write is an explicit new physical source of
        // truth. Drop any same-spelling cached ambiguity first; EVEC-owned
        // writes publish their state again after this native call returns.
        RemoveNote(note);

        var transfer = _pendingLyricMove;
        if (transfer == null || transfer.Finished ||
            !transfer.ItemsByHandle.TryGetValue(note.CppObjPtr, out var item))
            return;

        try
        {
            var state = NormalizeStateForNote(note, item.RequestedState);
            if (state.HasAnyEvec &&
                EvecPhonemeRecomposer.IsExactRealization(note.Phonemes, state))
            {
                CacheState(note, state, note.Phonemes);
                EvecTimingAllocator.ApplyTiming(note, state);
            }
            else
            {
                RemoveNote(note);
            }
        }
        catch
        {
            // The cache was already invalidated above. Leave native data
            // authoritative if optional lyric-move transfer fails.
        }
    }

    internal static void CompleteLyricMove(LyricMoveTransfer? transfer)
    {
        if (transfer == null || transfer.Finished)
            return;

        transfer.Finished = true;
        RestoreLyricMoveContext(transfer);
        var changes = new List<EvecHistoryChange>();
        foreach (var item in transfer.Items)
        {
            try
            {
                if (!transfer.Part.HasNote(item.Target))
                {
                    RemoveNote(item.Target);
                    continue;
                }

                var expected = NormalizeStateForNote(item.Target, item.RequestedState);
                EvecNoteState afterState;
                if (expected.HasAnyEvec &&
                    EvecPhonemeRecomposer.IsExactRealization(item.Target.Phonemes, expected))
                {
                    CacheState(item.Target, expected, item.Target.Phonemes);
                    afterState = expected;
                }
                else
                {
                    RemoveNote(item.Target);
                    afterState = GetState(item.Target);
                }

                changes.Add(new EvecHistoryChange(
                    item.BeforeSnapshot,
                    CaptureHistorySnapshot(item.Target, afterState)));
            }
            catch
            {
                RemoveNote(item.Target);
            }
        }

        if (transfer.Sequence != null && changes.Count > 0)
            RecordHistory(transfer.Sequence, changes);
        Refresh();
    }

    internal static void AbortLyricMove(LyricMoveTransfer? transfer)
    {
        if (transfer == null || transfer.Finished)
            return;

        transfer.Finished = true;
        RestoreLyricMoveContext(transfer);
        foreach (var item in transfer.Items)
        {
            try
            {
                if (transfer.Part.HasNote(item.Target) &&
                    string.Equals(
                        item.Target.Phonemes,
                        item.BeforeSnapshot.Phonemes,
                        StringComparison.Ordinal) &&
                    string.Equals(
                        item.Target.Lyric,
                        item.BeforeSnapshot.Lyric,
                        StringComparison.Ordinal))
                {
                    CacheState(
                        item.Target,
                        item.BeforeSnapshot.State,
                        item.BeforeSnapshot.Phonemes);
                }
                else
                {
                    RemoveNote(item.Target);
                }
            }
            catch
            {
                RemoveNote(item.Target);
            }
        }
    }

    private static void RestoreLyricMoveContext(LyricMoveTransfer transfer)
    {
        if (ReferenceEquals(_pendingLyricMove, transfer))
            _pendingLyricMove = transfer.Previous;
    }

    internal static VoiceBankChange? PrepareVoiceBankChange(WIVSMMidiPart part) =>
        PrepareVoiceBankChange(part, normalizeState: true, unlock: true);

    internal static VoiceBankChange? PrepareAutomaticVoiceBankChange(WIVSMMidiPart part) =>
        PrepareVoiceBankChange(part, normalizeState: false, unlock: false);

    private static VoiceBankChange? PrepareVoiceBankChange(
        WIVSMMidiPart part,
        bool normalizeState,
        bool unlock)
    {
        if (!IsEnabled || part == null)
            return null;

        var items = new List<VoiceBankChangeItem>();
        for (ulong index = 0; index < part.NumNotes; index++)
        {
            if (part.GetNote(index) is not { } note)
                continue;

            var state = GetState(note);
            if (normalizeState)
                state = NormalizeStateForNote(note, state);
            if (!state.HasAnyEvec)
                continue;

            items.Add(new VoiceBankChangeItem(
                note,
                state,
                CaptureHistorySnapshot(note, state)));
        }

        if (items.Count == 0)
            return null;

        var change = new VoiceBankChange(
            part,
            part.Sequence,
            part.IsAi ? part.AiVoiceBankID : part.VoiceBankID,
            items.ToArray());
        if (!unlock)
            return change;

        try
        {
            UnlockVoiceBankChange(change);
            return change;
        }
        catch
        {
            AbortVoiceBankChange(change);
            return null;
        }
    }

    private static void UnlockVoiceBankChange(VoiceBankChange change)
    {
        // V6's part-wide G2PA reset deliberately skips protected notes. EVEC
        // uses that bit to own its physical token string, so it must be
        // released immediately before native G2PA is allowed to run.
        foreach (var item in change.Items)
            item.Note.IsProtected = false;
    }

    internal static void QueueAutomaticVoiceBankChange(
        VoiceBankChange? change,
        bool success)
    {
        if (change == null || change.Finished)
            return;

        bool voiceBankChanged;
        try
        {
            string currentVoiceBankId = change.Part.IsAi
                ? change.Part.AiVoiceBankID
                : change.Part.VoiceBankID;
            voiceBankChanged = !string.Equals(
                currentVoiceBankId,
                change.OriginalVoiceBankId,
                StringComparison.Ordinal);
        }
        catch
        {
            voiceBankChanged = true;
        }

        if (!success || !voiceBankChanged)
        {
            change.Finished = true;
            if (voiceBankChanged)
                DiscardChangedVoiceBankState(change);
            return;
        }

        VoiceBankChange? replaced = null;
        lock (Sync)
        {
            if (PendingAutomaticVoiceBankChanges.TryGetValue(
                    change.Part,
                    out replaced))
            {
                PendingAutomaticVoiceBankChanges.Remove(change.Part);
            }
            PendingAutomaticVoiceBankChanges[change.Part] = change;
        }

        if (replaced != null && !ReferenceEquals(replaced, change))
        {
            replaced.Finished = true;
            DiscardChangedVoiceBankState(replaced);
        }
    }

    internal static void AbortAutomaticVoiceBankChange(VoiceBankChange? change)
    {
        if (change == null || change.Finished)
            return;

        bool voiceBankChanged;
        try
        {
            string currentVoiceBankId = change.Part.IsAi
                ? change.Part.AiVoiceBankID
                : change.Part.VoiceBankID;
            voiceBankChanged = !string.Equals(
                currentVoiceBankId,
                change.OriginalVoiceBankId,
                StringComparison.Ordinal);
        }
        catch
        {
            voiceBankChanged = true;
        }

        change.Finished = true;
        if (voiceBankChanged)
            DiscardChangedVoiceBankState(change);
    }

    internal static VoiceBankChange? BeginAutomaticVoiceBankReset(WIVSMMidiPart part)
    {
        if (!IsEnabled || part == null)
            return null;

        VoiceBankChange? pending;
        lock (Sync)
        {
            if (!PendingAutomaticVoiceBankChanges.Remove(part, out pending))
                return null;
        }

        if (pending == null)
            return null;
        VoiceBankChange change = pending;

        try
        {
            UnlockVoiceBankChange(change);
            return change;
        }
        catch
        {
            change.Finished = true;
            DiscardChangedVoiceBankState(change);
            return null;
        }
    }

    internal static void CompleteAutomaticVoiceBankReset(
        VoiceBankChange? change,
        bool success)
    {
        if (change == null || change.Finished)
            return;

        change.Finished = true;
        if (!success)
        {
            DiscardChangedVoiceBankState(change);
            Refresh();
            return;
        }

        foreach (var item in change.Items)
        {
            try
            {
                if (!change.Part.HasNote(item.Note))
                {
                    RemoveNote(item.Note);
                    continue;
                }

                var compatibleState = NormalizeStateForNote(
                    item.Note,
                    item.BeforeState);
                if (compatibleState.HasAnyEvec &&
                    TryApplyToNote(item.Note, compatibleState, out var committedPhonemes))
                {
                    CacheState(item.Note, compatibleState, committedPhonemes);
                }
                else
                {
                    item.Note.IsProtected = false;
                    RemoveNote(item.Note);
                }
            }
            catch
            {
                try { item.Note.IsProtected = false; } catch { }
                RemoveNote(item.Note);
            }
        }

        Refresh();
    }

    private static void DiscardChangedVoiceBankState(VoiceBankChange change)
    {
        foreach (var item in change.Items)
        {
            try { item.Note.IsProtected = false; } catch { }
            RemoveNote(item.Note);
        }
    }

    internal static void CompleteVoiceBankChange(VoiceBankChange? change, bool success)
    {
        if (change == null || change.Finished)
            return;

        change.Finished = true;
        if (!success)
        {
            RestoreVoiceBankChangeProtection(change);
            return;
        }

        var changes = new List<EvecHistoryChange>();
        foreach (var item in change.Items)
        {
            try
            {
                if (!change.Part.HasNote(item.Note))
                {
                    RemoveNote(item.Note);
                    continue;
                }

                var afterState = EvecNoteState.Empty;
                var afterSnapshot = CaptureHistorySnapshot(item.Note, afterState);
                RemoveNote(item.Note);
                changes.Add(new EvecHistoryChange(item.BeforeSnapshot, afterSnapshot));
            }
            catch
            {
                // The native voice-bank change already succeeded. A stale
                // wrapper must not turn optional sidecar cleanup into failure.
                RemoveNote(item.Note);
            }
        }

        if (change.Sequence != null && changes.Count > 0)
            RecordHistory(change.Sequence, changes);

        Refresh();
    }

    internal static void AbortVoiceBankChange(VoiceBankChange? change)
    {
        if (change == null || change.Finished)
            return;

        change.Finished = true;
        RestoreVoiceBankChangeProtection(change);
    }

    private static void RestoreVoiceBankChangeProtection(VoiceBankChange change)
    {
        foreach (var item in change.Items)
        {
            try
            {
                // Restore only while the old physical value is still intact.
                // If native code partially changed it before reporting failure,
                // leaving it editable is safer than protecting mismatched data.
                if (string.Equals(
                        item.Note.Phonemes,
                        item.BeforeSnapshot.Phonemes,
                        StringComparison.Ordinal))
                {
                    item.Note.IsProtected = item.BeforeSnapshot.IsProtected;
                }
            }
            catch
            {
                // The caller's native transaction remains responsible for
                // rollback; protection restoration is only a defensive aid.
            }
        }

        // ResetPhonemes raises a view refresh while SetVoiceBank is still
        // staged. That refresh may invalidate the old cache before a later
        // SetVoiceBank step fails and the caller rolls back. Seed the exact
        // old-bank snapshot now; it will become valid again after rollback,
        // while remaining harmless if a non-transactional caller does not.
        lock (Sync)
        {
            foreach (var item in change.Items)
            {
                try
                {
                    Register(item.Note);
                    if (TryKey(item.Note.CppObjPtr, out var key))
                    {
                        States[key] = new EvecCachedState(
                            item.BeforeState.Clone(),
                            item.BeforeSnapshot.Phonemes,
                            change.OriginalVoiceBankId);
                    }
                }
                catch { }
            }
        }
    }

    internal static void MarkTempoChanged(WIVSMSequence? sequence)
    {
        if (sequence == null)
            return;

        lock (Sync)
            PendingTempoSequences.Add((IntPtr)sequence);
    }

    internal static bool ApplyPendingTempoTiming(WIVSMSequence sequence)
    {
        if (sequence == null)
            return false;

        lock (Sync)
        {
            if (!PendingTempoSequences.Remove((IntPtr)sequence))
                return false;
        }

        if (!IsEnabled || !sequence.IsStaged)
            return true;

        try
        {
            for (ulong trackIndex = 0; trackIndex < sequence.NumTrack; trackIndex++)
            {
                if (sequence.GetTrack(trackIndex) is not WIVSMMidiTrack track)
                    continue;

                for (ulong partIndex = 0; partIndex < track.NumParts; partIndex++)
                {
                    if (track.GetPart(partIndex) is not WIVSMMidiPart part)
                        continue;

                    for (ulong noteIndex = 0; noteIndex < part.NumNotes; noteIndex++)
                    {
                        try
                        {
                            // Every EVEC write protects its resulting phonemes.
                            // This cheap guard avoids parsing ordinary notes
                            // when a large project commits a tempo curve.
                            if (part.GetNote(noteIndex) is not { IsProtected: true } note)
                                continue;

                            ReapplyTimingAfterGeometryChange(note);
                        }
                        catch
                        {
                            // One stale native wrapper must not prevent the
                            // rest of the project's EVEC notes from reanchoring.
                        }
                    }
                }
            }
        }
        catch
        {
            // Tempo editing must survive optional EVEC timing maintenance.
        }

        return true;
    }

    internal static void ClearPendingTempoTiming(WIVSMSequence? sequence)
    {
        if (sequence == null)
            return;

        lock (Sync)
            PendingTempoSequences.Remove((IntPtr)sequence);
    }

    internal static void CompletePendingHistory(
        WIVSMSequence? sequence,
        bool success)
    {
        if (sequence == null || !success)
            return;

        PendingHistoryTransition? pending;
        lock (Sync)
        {
            if (!PendingHistoryTransitions.Remove((IntPtr)sequence, out pending))
                return;
        }

        if (pending.Transitions.Before.Count > 0 || pending.Transitions.After.Count > 0)
        {
            RecordHistoryTransition(
                sequence,
                pending.Transitions.Before.Values,
                pending.Transitions.After.Values);
        }
    }

    internal static void ClearPendingHistory(WIVSMSequence? sequence)
    {
        if (sequence == null)
            return;
        lock (Sync)
            PendingHistoryTransitions.Remove((IntPtr)sequence);
    }

    internal static ClipboardPropertyTransfer? PrepareClipboardPropertyTransfer(
        WIVSMClipboard clipboard,
        IReadOnlyList<WIVSMNote> targets)
    {
        if (clipboard == null || targets.Count == 0)
            return null;

        var sources = clipboard.GetNotes
            .Where(note => note != null)
            .ToList();
        if (sources.Count == 0)
            return null;

        IEnumerable<(WIVSMNote Target, WIVSMNote Source)> pairs = sources.Count == 1
            ? targets.Select(target => (target, sources[0]))
            : targets.Zip(sources, (target, source) => (target, source));

        var items = pairs
            .Where(pair => pair.Target != null && pair.Source != null)
            .Select(pair =>
            {
                var beforeState = NormalizeStateForNote(pair.Target, GetState(pair.Target));
                return new ClipboardPropertyTransferItem(
                    pair.Target,
                    GetState(pair.Source),
                    beforeState,
                    CaptureHistorySnapshot(pair.Target, beforeState));
            })
            .ToArray();
        if (items.Length == 0)
            return null;

        var sequence = items[0].Target.Parent?.Sequence;
        if (sequence != null && items.Any(item => !sequence.Equals(item.Target.Parent?.Sequence)))
            sequence = null;

        return new ClipboardPropertyTransfer(sequence, items);
    }

    internal static ClipboardPartPropertyTransfer? PrepareClipboardPartPropertyTransfer(
        WIVSMClipboard clipboard,
        IReadOnlyList<WIVSMPart> targets,
        PartProperty property)
    {
        if (!IsEnabled || clipboard == null || targets.Count == 0 ||
            !property.HasFlag(PartProperty.Note) &&
            !property.HasFlag(PartProperty.VoiceBank))
        {
            return null;
        }

        var sources = clipboard.GetParts
            .Where(part => part != null)
            .ToList();
        if (sources.Count == 0)
            return null;

        IEnumerable<(WIVSMPart Target, WIVSMPart Source)> pairs = sources.Count == 1
            ? targets.Select(target => (target, sources[0]))
            : targets.Zip(sources, (target, source) => (target, source));

        var items = pairs
            .Where(pair => pair.Target is WIVSMMidiPart && pair.Source is WIVSMMidiPart)
            .Select(pair =>
            {
                var source = (WIVSMMidiPart)pair.Source;
                var target = (WIVSMMidiPart)pair.Target;
                bool voiceBankChanges = property.HasFlag(PartProperty.VoiceBank) &&
                                        !string.Equals(
                                            target.IsAi ? target.AiVoiceBankID : target.VoiceBankID,
                                            source.IsAi ? source.AiVoiceBankID : source.VoiceBankID,
                                            StringComparison.Ordinal);
                return new ClipboardPartPropertyTransferItem(
                    source,
                    target,
                    voiceBankChanges,
                    CapturePartHistorySnapshots(target),
                    property.HasFlag(PartProperty.Note)
                        ? CapturePartStates(source)
                        : Array.Empty<EvecNoteState>());
            })
            .ToArray();
        if (items.Length == 0)
            return null;

        var sequence = items[0].Target.Sequence;
        if (sequence != null && items.Any(item => !sequence.Equals(item.Target.Sequence)))
            sequence = null;

        return new ClipboardPartPropertyTransfer(sequence, property, items);
    }

    internal static bool CompleteClipboardPartPropertyTransfer(
        ClipboardPartPropertyTransfer transfer)
    {
        bool copiesNotes = transfer.Property.HasFlag(PartProperty.Note);
        var cacheWrites = new List<(WIVSMNote Note, EvecNoteState State, string Phonemes)>();
        var beforeSnapshots = new List<EvecHistorySnapshot>();
        var afterSnapshots = new List<EvecHistorySnapshot>();

        foreach (var item in transfer.Items)
        {
            beforeSnapshots.AddRange(item.BeforeSnapshots);

            if (copiesNotes)
            {
                ulong count = Math.Min(
                    (ulong)item.SourceStates.Length,
                    item.Target.NumNotes);
                for (ulong index = 0; index < count; index++)
                {
                    if (item.Target.GetNote(index) is not { } targetNote)
                        continue;

                    var requestedState = item.SourceStates[index].Clone();
                    var normalizedState = NormalizeStateForNote(targetNote, requestedState);
                    string committedPhonemes = targetNote.Phonemes;
                    if (requestedState.HasAnyEvec &&
                        !TryApplyToNote(targetNote, normalizedState, out committedPhonemes))
                    {
                        return false;
                    }

                    cacheWrites.Add((targetNote, normalizedState, committedPhonemes));
                }
            }
            else if (item.VoiceBankChanges)
            {
                // CopyPartProperty only swaps the ID; unlike SetVoiceBank it
                // never runs G2PA. Strip the old bank's EVEC tokens explicitly
                // while the caller's native paste transaction is still open.
                foreach (var before in item.BeforeSnapshots.Where(
                             snapshot => snapshot.State.HasAnyEvec))
                {
                    WIVSMNote? targetNote = FindPartNoteByHandle(item.Target, before.Handle);
                    if (targetNote == null ||
                        !TryApplyToNote(
                            targetNote,
                            EvecNoteState.Empty,
                            out string committedPhonemes))
                    {
                        return false;
                    }

                    cacheWrites.Add((targetNote, EvecNoteState.Empty, committedPhonemes));
                }
            }

        }

        var plannedStates = cacheWrites
            .GroupBy(write => write.Note.CppObjPtr)
            .ToDictionary(group => group.Key, group => group.Last().State);
        foreach (var item in transfer.Items)
        {
            for (ulong index = 0; index < item.Target.NumNotes; index++)
            {
                if (item.Target.GetNote(index) is not { } note)
                    continue;
                var state = plannedStates.TryGetValue(note.CppObjPtr, out var planned)
                    ? planned
                    : NormalizeStateForNote(note, GetState(note));
                afterSnapshots.Add(CaptureHistorySnapshot(note, state));
            }
        }

        foreach (var item in transfer.Items)
        {
            if (!copiesNotes)
                continue;
            foreach (var snapshot in item.BeforeSnapshots)
                RemoveHandle(snapshot.Handle);
        }

        foreach (var write in cacheWrites)
            CacheState(write.Note, write.State, write.Phonemes);

        bool hasLogicalState = beforeSnapshots.Any(snapshot => snapshot.State.HasAnyEvec) ||
                               afterSnapshots.Any(snapshot => snapshot.State.HasAnyEvec);
        if (transfer.Sequence != null && hasLogicalState)
        {
            RecordHistoryTransition(
                transfer.Sequence,
                beforeSnapshots,
                afterSnapshots);
        }

        Refresh();
        return true;
    }

    private static EvecNoteState[] CapturePartStates(WIVSMMidiPart part)
    {
        int count = checked((int)part.NumNotes);
        var states = new EvecNoteState[count];
        for (int index = 0; index < count; index++)
        {
            states[index] = part.GetNote((ulong)index) is { } note
                ? NormalizeStateForNote(note, GetState(note))
                : EvecNoteState.Empty;
        }

        return states;
    }

    private static EvecHistorySnapshot[] CapturePartHistorySnapshots(WIVSMMidiPart part)
    {
        var snapshots = new List<EvecHistorySnapshot>();
        for (ulong index = 0; index < part.NumNotes; index++)
        {
            if (part.GetNote(index) is not { } note)
                continue;
            var state = NormalizeStateForNote(note, GetState(note));
            snapshots.Add(CaptureHistorySnapshot(note, state));
        }

        return snapshots.ToArray();
    }

    private static WIVSMNote? FindPartNoteByHandle(WIVSMMidiPart part, IntPtr handle)
    {
        for (ulong index = 0; index < part.NumNotes; index++)
        {
            if (part.GetNote(index) is { } note && note.CppObjPtr == handle)
                return note;
        }

        return null;
    }

    internal static bool CompleteClipboardPropertyTransfer(ClipboardPropertyTransfer transfer)
    {
        var applied = new List<(ClipboardPropertyTransferItem Item,
            EvecNoteState State, string Phonemes, EvecHistorySnapshot After)>();

        foreach (var item in transfer.Items)
        {
            var requestedState = item.SourceState.Clone();
            var normalizedState = NormalizeStateForNote(item.Target, requestedState);
            string committedPhonemes;
            bool success;

            // A source without EVEC may still carry intentionally protected
            // manual phonemes. The native property copy already transferred
            // those fields correctly; only clear our stale logical cache.
            if (!requestedState.HasAnyEvec)
            {
                committedPhonemes = item.Target.Phonemes;
                success = true;
            }
            else
            {
                // Run inside CopyNotePropertyTo's existing native transaction.
                // This re-applies timing and disambiguates Rin/Len's identical
                // "C C V" spellings without creating a nested undo entry.
                success = TryApplyToNote(item.Target, normalizedState, out committedPhonemes);
            }

            EvecDiagnosticLog.Record(
                item.BeforeState,
                requestedState,
                normalizedState,
                success,
                item.BeforeSnapshot.Phonemes,
                committedPhonemes);
            if (!success)
                return false;

            applied.Add((
                item,
                normalizedState,
                committedPhonemes,
                CaptureHistorySnapshot(item.Target, normalizedState)));
        }

        foreach (var result in applied)
            CacheState(result.Item.Target, result.State, result.Phonemes);

        if (transfer.Sequence != null)
        {
            RecordHistory(
                transfer.Sequence,
                applied.Select(result => new EvecHistoryChange(
                    result.Item.BeforeSnapshot,
                    result.After)));
        }

        return true;
    }

    internal static void RemoveNote(WIVSMNote note)
    {
        if (note == null) return;
        RemoveHandle(note.CppObjPtr);
    }

    private static void RemoveHandle(IntPtr handle)
    {
        lock (Sync)
        {
            if (TryKey(handle, out var key))
                States.Remove(key);
            Generations.Remove(handle);
        }
    }

    internal static void Refresh()
    {
        Notify(null);
        try
        {
            var vm = (Application.Current?.MainWindow?.DataContext as MainViewModel)?.MusicalEditorVM;
            if (vm != null)
                vm.DoUpdateView(vm, Yamaha.VOCALOID.MusicalEditor.UpdateViewTypeFlag.NoteChanged);
        }
        catch { }
    }

    private static void Register(WIVSMNote note)
    {
        lock (Sync)
        {
            if (!Generations.ContainsKey(note.CppObjPtr))
                Generations[note.CppObjPtr] = Interlocked.Increment(ref _nextGeneration);
        }
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

    private static void Notify(WIVSMMidiPart? part)
    {
        void Raise()
        {
            PartChanged?.Invoke(part);
            Changed?.Invoke();
        }

        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher == null || dispatcher.CheckAccess())
            Raise();
        else
            dispatcher.BeginInvoke((Action)Raise);
    }

    #region Project Persistence

    internal static EvecProjectLoadState BeginProjectLoad(EvecProjectData? data)
    {
        var state = new EvecProjectLoadState(data, _pendingProjectLoad);
        _pendingProjectLoad = state;
        return state;
    }

    internal static void CompleteProjectLoad(WIVSMSequence sequence, EvecProjectLoadState? state)
    {
        if (state?.Data == null || state.Applied) return;
        state.Applied = true;

        lock (Sync)
        {
            Generations.Clear();
            States.Clear();
            Histories.Clear();
            PendingHistoryTransitions.Clear();
            PendingAutomaticVoiceBankChanges.Clear();
        }

        foreach (var entry in state.Data.Entries)
        {
            var note = FindNote(sequence, entry);
            if (note == null) continue;

            var requestedState = new EvecNoteState(
                entry.VoiceColorId,
                entry.AttackId,
                entry.ReleaseId,
                entry.ConsonantExtension);
            var evecState = NormalizeStateForNote(note, requestedState);
            if (evecState.HasAnyEvec)
            {
                // Reconcile the sidecar with the physical note instead of
                // caching metadata that the actual phonemes do not represent.
                if (TryApplyToNote(note, evecState, out var committedPhonemes))
                    CacheState(note, evecState, committedPhonemes);
            }
            else if (requestedState.HasAnyEvec &&
                     IsVoiceBankUnavailable(note) &&
                     PhysicalPhonemesMatchState(note, requestedState))
            {
                // Project loading applies the sidecar before V6 replaces a
                // missing voice bank. Preserve only an exact, physically
                // verifiable state until ReplaceVoice + G2PA can normalize it
                // against the actual replacement bank.
                CacheState(note, requestedState, note.Phonemes);
            }
        }

        Notify(null);
    }

    private static bool IsVoiceBankUnavailable(WIVSMNote note)
    {
        try
        {
            return (note.Parent as WIVSMMidiPart)?.VoiceBank() == null;
        }
        catch
        {
            return true;
        }
    }

    private static bool PhysicalPhonemesMatchState(
        WIVSMNote note,
        EvecNoteState state) =>
        EvecPhonemeRecomposer.IsExactRealization(note.Phonemes, state);

    internal static void EndProjectLoad(EvecProjectLoadState? state)
    {
        if (_pendingProjectLoad == state)
            _pendingProjectLoad = state?.Previous;
    }

    internal static EvecProjectData BuildProjectData(WIVSMSequence sequence)
    {
        var projectData = new EvecProjectData();

        for (ulong trackIndex = 0; trackIndex < sequence.NumTrack; trackIndex++)
        {
            if (sequence.GetTrack(trackIndex) is not WIVSMMidiTrack track) continue;

            for (ulong partIndex = 0; partIndex < track.NumParts; partIndex++)
            {
                if (track.GetPart(partIndex) is not WIVSMMidiPart part) continue;

                var occurrences = new Dictionary<(long Tick, int NoteNumber), int>();

                for (ulong noteIndex = 0; noteIndex < part.NumNotes; noteIndex++)
                {
                    if (part.GetNote(noteIndex) is not { } note) continue;

                    var state = NormalizeStateForNote(note, GetState(note));
                    if (!state.HasAnyEvec) continue;

                    var noteKeyTuple = (note.RelPosTick.Value, note.NoteNumber);
                    occurrences.TryGetValue(noteKeyTuple, out var occurrence);
                    occurrences[noteKeyTuple] = occurrence + 1;

                    projectData.Entries.Add(new EvecProjectEntry
                    {
                        Track = (int)trackIndex,
                        Part = (int)partIndex,
                        Note = (int)noteIndex,
                        RelPosTick = note.RelPosTick.Value,
                        NoteNumber = note.NoteNumber,
                        Occurrence = occurrence,
                        VoiceColorId = state.VoiceColorId,
                        AttackId = state.AttackId,
                        ReleaseId = state.ReleaseId,
                        ConsonantExtension = state.ConsonantExtension,
                    });
                }
            }
        }

        return projectData;
    }

    private static WIVSMNote? FindNote(WIVSMSequence sequence, EvecProjectEntry entry)
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
        {
            if (part.GetNote(index) is { } note && note.RelPosTick.Value == entry.RelPosTick &&
                note.NoteNumber == entry.NoteNumber && occurrence++ == entry.Occurrence)
                return note;
        }

        return null;
    }

    #endregion

    private sealed record EvecCachedState(
        EvecNoteState State,
        string Phonemes,
        string VoiceBankId);

    private sealed class EvecUpdatePlan(
        WIVSMNote note,
        EvecNoteState beforeState,
        EvecNoteState requestedState,
        EvecNoteState state,
        EvecHistorySnapshot beforeSnapshot)
    {
        internal WIVSMNote Note { get; } = note;
        internal EvecNoteState BeforeState { get; } = beforeState.Clone();
        internal EvecNoteState RequestedState { get; } = requestedState.Clone();
        internal EvecNoteState State { get; } = state;
        internal EvecHistorySnapshot BeforeSnapshot { get; } = beforeSnapshot;
        internal EvecHistorySnapshot? AfterSnapshot { get; set; }
        internal string CommittedPhonemes { get; set; } = note.Phonemes;
        internal bool Succeeded { get; set; }
    }

    internal sealed record EvecHistorySnapshot(
        IntPtr Handle,
        EvecNoteState State,
        string Lyric,
        string Phonemes,
        int[] Positions,
        bool IsProtected);

    private sealed record EvecHistoryChange(
        EvecHistorySnapshot Before,
        EvecHistorySnapshot After);

    private sealed record EvecHistoryEdit(
        EvecHistorySnapshot[] Before,
        EvecHistorySnapshot[] After);

    private sealed class EvecHistory
    {
        internal List<EvecHistoryEdit> Undo { get; } = new();
        internal List<EvecHistoryEdit> Redo { get; } = new();
    }

    private sealed class PendingHistoryTransition
    {
        internal EvecTransitionAccumulator<IntPtr, EvecHistorySnapshot> Transitions { get; } = new();
    }

    private readonly record struct NoteKey(IntPtr Handle, long Generation);
}
