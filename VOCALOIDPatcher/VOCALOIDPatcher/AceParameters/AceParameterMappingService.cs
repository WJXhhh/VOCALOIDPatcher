using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using VOCALOIDPatcher.Config;
using VOCALOIDPatcher.Translation;
using VOCALOIDPatcher.Utils;
using Yamaha.VOCALOID.VSM;

namespace VOCALOIDPatcher.AceParameters;

// Keeps the derived native sound controllers in sync with the ACE carrier
// curves. The native update observer delivers controller notifications only
// after Commit has already started, so the mapping compares carrier curve
// snapshots inside the Commit prefix instead of relying on those notifications.
internal static class AceParameterMappingService
{
    private sealed class SequenceState
    {
        public readonly Dictionary<(IntPtr Part, AceParameterKind Kind), long> Snapshot = new();
        public bool Initialized;
        public bool Disposed;
    }

    private static readonly ConditionalWeakTable<WIVSMSequence, SequenceState> States = new();
    private static int _projectLoadDepth;
    private static int _synchronizing;

    [ThreadStatic] private static AceProjectData? _pendingArchive;

    internal static void BeginProjectLoad(string? filePath)
    {
        if (Interlocked.Increment(ref _projectLoadDepth) > 1)
            return;
        try
        {
            _pendingArchive = filePath != null &&
                              (filePath.EndsWith(".vpr", StringComparison.OrdinalIgnoreCase) ||
                               filePath.EndsWith(".vpr.bak", StringComparison.OrdinalIgnoreCase)) &&
                              File.Exists(filePath)
                ? AceParameterProjectArchive.Read(filePath)
                : null;
        }
        catch (Exception e)
        {
            _pendingArchive = null;
            Debug.Print(TranslationManager.Tr("VOCALOIDPatcher_Debug_AceParameter_Failed", e.Message));
        }
    }

    internal static void EndProjectLoad()
    {
        int depth = Interlocked.Decrement(ref _projectLoadDepth);
        if (depth <= 0)
            _pendingArchive = null;
    }

    internal static void OnSequenceCommit(WIVSMSequence sequence, bool updateHistory)
    {
        if (Volatile.Read(ref _synchronizing) != 0)
            return;
        try
        {
            if (!Settings.AceParameters || !Settings.AceParameterMapping)
                return;
            if (sequence == null || Volatile.Read(ref _projectLoadDepth) > 0)
                return;

            var state = EnsureState(sequence);
            if (!state.Initialized)
            {
                // First commit of this sequence (new project or an uncovered
                // load path): apply mapping for existing curves so the very
                // first edit is audible, then take the snapshot baseline.
                foreach (var part in EnumerateParts(sequence))
                {
                    if (part.IsAi)
                        continue;
                    foreach (var definition in AceParameterDefinitions.All)
                    {
                        if (!AceParameterRegistry.IsMappingEnabled(definition))
                            continue;
                        var curve = ReadCarrierCurve(part, definition);
                        if (curve.Count > 0)
                            ApplyMapping(part, definition, curve);
                    }
                }
                RefreshSnapshot(sequence, state);
                state.Initialized = true;
                return;
            }
            SyncChangedCarriers(sequence, state);
        }
        catch (Exception e)
        {
            Debug.Print(TranslationManager.Tr("VOCALOIDPatcher_Debug_AceParameter_Failed", e.Message));
        }
    }

    internal static void OnSequenceHistoryRestored(WIVSMSequence sequence)
    {
        try
        {
            if (sequence == null || !States.TryGetValue(sequence, out var state) || state.Disposed)
                return;
            RefreshSnapshot(sequence, state);
        }
        catch (Exception e)
        {
            Debug.Print(TranslationManager.Tr("VOCALOIDPatcher_Debug_AceParameter_Failed", e.Message));
        }
    }

    internal static void OnProjectOpened(WIVSMSequence? sequence)
    {
        try
        {
            if (sequence == null)
                return;
            var state = EnsureState(sequence);
            bool applied = ApplyPendingArchive(sequence);
            // Direct controller inserts stage edit commands; commit them without
            // a history entry so the first user Undo cannot remove the restored
            // carrier curves.
            if (applied)
            {
                try { sequence.Commit(false); } catch { }
            }
            RefreshSnapshot(sequence, state);
            state.Initialized = true;
        }
        catch (Exception e)
        {
            Debug.Print(TranslationManager.Tr("VOCALOIDPatcher_Debug_AceParameter_Failed", e.Message));
        }
    }

    internal static void OnSequenceClosing(WIVSMSequence sequence)
    {
        try
        {
            if (sequence == null || !States.TryGetValue(sequence, out var state))
                return;
            state.Disposed = true;
            States.Remove(sequence);
        }
        catch
        {
        }
    }

    internal static void RebuildAll(WIVSMSequence sequence)
    {
        if (sequence == null || !Settings.AceParameters || !Settings.AceParameterMapping)
            return;
        try
        {
            using var transaction = new Yamaha.VOCALOID.Transaction(sequence);
            foreach (var part in EnumerateParts(sequence))
            {
                if (part.IsAi)
                    continue;
                foreach (var definition in AceParameterDefinitions.All)
                {
                    if (!AceParameterRegistry.IsMappingEnabled(definition))
                        continue;
                    var curve = ReadCarrierCurve(part, definition);
                    if (curve.Count > 0)
                        ApplyMapping(part, definition, curve);
                }
            }
            if (States.TryGetValue(sequence, out var state))
                RefreshSnapshot(sequence, state);
        }
        catch (Exception e)
        {
            Debug.Print(TranslationManager.Tr("VOCALOIDPatcher_Debug_AceParameter_Failed", e.Message));
        }
    }

    internal static AceProjectData BuildProjectData(WIVSMSequence sequence)
    {
        var data = new AceProjectData();
        var tracks = sequence?.MidiTracks;
        if (tracks == null)
            return data;
        for (int trackIndex = 0; trackIndex < tracks.Count; trackIndex++)
        {
            var parts = tracks[trackIndex].MidiParts;
            for (int partIndex = 0; partIndex < parts.Count; partIndex++)
            {
                var part = parts[partIndex];
                if (part == null || part.IsAi)
                    continue;
                foreach (var definition in AceParameterDefinitions.All)
                {
                    var curve = ReadCarrierCurve(part, definition);
                    if (curve.Count == 0)
                        continue;
                    var entry = new AceProjectEntry
                    {
                        Track = trackIndex,
                        Part = partIndex,
                        AbsPosTick = part.AbsPosTick.Value,
                        Kind = (int)definition.Kind,
                    };
                    foreach (var point in curve)
                    {
                        entry.Points.Add(new AceProjectPoint
                        {
                            Tick = point.Tick,
                            Value = point.Value,
                        });
                    }
                    data.Entries.Add(entry);
                }
            }
        }
        return data;
    }

    private static bool ApplyPendingArchive(WIVSMSequence sequence)
    {
        var data = _pendingArchive;
        if (data == null)
            return false;
        ApplyProjectData(sequence, data);
        return true;
    }

    private static void ApplyProjectData(WIVSMSequence sequence, AceProjectData data)
    {
        var tracks = sequence.MidiTracks;
        // The archive is the source of truth: clear any natively persisted
        // carrier controllers (Expression etc.) before restoring from JSON.
        foreach (var track in tracks)
        {
            foreach (var part in track.MidiParts)
            {
                if (part == null || part.IsAi)
                    continue;
                foreach (var definition in AceParameterDefinitions.All)
                    RemoveCarrierControllers(part, definition);
            }
        }

        foreach (var entry in data.Entries)
        {
            if (!TryResolvePart(tracks, entry, out var part))
                continue;
            if (!TryGetDefinition(entry.Kind, out var definition))
                continue;
            foreach (var point in entry.Points)
            {
                try
                {
                    part.InsertController(new VSMRelTick(point.Tick), definition.CarrierType, point.Value);
                }
                catch (Exception)
                {
                    // Skip invalid/stale points instead of failing the load.
                }
            }
        }
    }

    private static bool TryResolvePart(
        List<WIVSMMidiTrack> tracks,
        AceProjectEntry entry,
        out WIVSMMidiPart part)
    {
        part = null!;
        if (entry.Track >= 0 && entry.Track < tracks.Count)
        {
            var parts = tracks[entry.Track].MidiParts;
            if (entry.Part >= 0 && entry.Part < parts.Count)
            {
                var indexed = parts[entry.Part];
                if (indexed != null && indexed.AbsPosTick.Value == entry.AbsPosTick)
                {
                    part = indexed;
                    return true;
                }
            }
        }
        foreach (var track in tracks)
        {
            foreach (var candidate in track.MidiParts)
            {
                if (candidate != null && candidate.AbsPosTick.Value == entry.AbsPosTick)
                {
                    part = candidate;
                    return true;
                }
            }
        }
        return false;
    }

    internal static List<AceCurvePoint> ReadCarrierCurve(WIVSMMidiPart part, AceParameterDefinition definition)
    {
        var result = new List<AceCurvePoint>();
        if (part == null)
            return result;
        int count = checked((int)part.GetNumController(definition.CarrierType));
        for (int index = 0; index < count; index++)
        {
            var controller = part.GetController(definition.CarrierType, (ulong)index);
            if (controller == null)
                continue;
            result.Add(new AceCurvePoint(controller.RelPosTick.Value, controller.Value));
        }
        return result;
    }

    private static SequenceState EnsureState(WIVSMSequence sequence)
        => States.GetValue(sequence, _ => new SequenceState());

    // Host-probe diagnostics only; not used by the product paths.
    internal static string DescribeState(WIVSMSequence sequence)
    {
        if (sequence == null)
            return "no-sequence";
        if (!States.TryGetValue(sequence, out var state))
            return "no-state";
        return $"initialized={state.Initialized} snapshot={state.Snapshot.Count} disposed={state.Disposed}";
    }

    private static void SyncChangedCarriers(WIVSMSequence sequence, SequenceState state)
    {
        var changes = new List<(WIVSMMidiPart Part, AceParameterDefinition Definition, long Signature)>();
        foreach (var part in EnumerateParts(sequence))
        {
            if (part.IsAi)
                continue;
            foreach (var definition in AceParameterDefinitions.All)
            {
                long signature = ComputeCarrierSignature(part, definition);
                var key = ((IntPtr)part, definition.Kind);
                bool known = state.Snapshot.TryGetValue(key, out long previous);
                if (!known)
                {
                    if (signature == ComputeEmptySignature())
                        continue;
                    changes.Add((part, definition, signature));
                    continue;
                }
                if (previous != signature)
                    changes.Add((part, definition, signature));
            }
        }
        if (changes.Count == 0)
            return;

        Interlocked.Exchange(ref _synchronizing, 1);
        try
        {
            foreach (var (part, definition, signature) in changes)
            {
                if (!AceParameterRegistry.IsMappingEnabled(definition))
                {
                    state.Snapshot[((IntPtr)part, definition.Kind)] = signature;
                    continue;
                }
                var curve = ReadCarrierCurve(part, definition);
                if (curve.Count > 0)
                    ApplyMapping(part, definition, curve);
                else
                    ClearDerived(part, definition);
                state.Snapshot[((IntPtr)part, definition.Kind)] = signature;
            }
        }
        finally
        {
            Interlocked.Exchange(ref _synchronizing, 0);
        }
    }

    private static void RefreshSnapshot(WIVSMSequence sequence, SequenceState state)
    {
        state.Snapshot.Clear();
        foreach (var part in EnumerateParts(sequence))
        {
            if (part.IsAi)
                continue;
            foreach (var definition in AceParameterDefinitions.All)
            {
                state.Snapshot[((IntPtr)part, definition.Kind)] =
                    ComputeCarrierSignature(part, definition);
            }
        }
    }

    private static long ComputeEmptySignature()
    {
        unchecked
        {
            const long offset = 1469598103934665603L;
            const long prime = 1099511628211L;
            long hash = offset;
            hash ^= 0;
            hash *= prime;
            return hash;
        }
    }

    private static long ComputeCarrierSignature(WIVSMMidiPart part, AceParameterDefinition definition)
    {
        unchecked
        {
            const long offset = 1469598103934665603L;
            const long prime = 1099511628211L;
            long hash = offset;
            int count = checked((int)part.GetNumController(definition.CarrierType));
            for (int index = 0; index < count; index++)
            {
                var controller = part.GetController(definition.CarrierType, (ulong)index);
                if (controller == null)
                    continue;
                hash ^= controller.RelPosTick.Value;
                hash *= prime;
                hash ^= controller.Value;
                hash *= prime;
            }
            hash ^= count;
            hash *= prime;
            return hash;
        }
    }

    private static void ApplyMapping(
        WIVSMMidiPart part,
        AceParameterDefinition definition,
        IReadOnlyList<AceCurvePoint> curve)
    {
        var target = AceParameterRegistry.GetMappingTarget(definition);
        var targetKind = AceParameterRegistry.GetSoundTargetKind(definition);
        var derived = AceParameterMapping.BuildDerivedCurve(definition.Kind, targetKind, curve);
        if (MatchesExistingCurve(part, target, derived))
            return;
        ClearTarget(part, target);
        foreach (var point in derived)
            part.InsertController(new VSMRelTick(point.Tick), target, point.Value);
    }

    private static bool MatchesExistingCurve(
        WIVSMMidiPart part,
        VSMControllerType target,
        IReadOnlyList<NativeControllerPoint> derived)
    {
        int count = checked((int)part.GetNumController(target));
        if (count != derived.Count)
            return false;
        for (int index = 0; index < count; index++)
        {
            var controller = part.GetController(target, (ulong)index);
            if (controller == null)
                return false;
            if (controller.RelPosTick.Value != derived[index].Tick || controller.Value != derived[index].Value)
                return false;
        }
        return true;
    }

    private static void ClearDerived(WIVSMMidiPart part, AceParameterDefinition definition)
        => ClearTarget(part, AceParameterRegistry.GetMappingTarget(definition));

    private static void ClearTarget(WIVSMMidiPart part, VSMControllerType target)
    {
        int count = checked((int)part.GetNumController(target));
        for (int index = count - 1; index >= 0; index--)
        {
            var controller = part.GetController(target, (ulong)index);
            if (controller != null)
                part.RemoveController(controller);
        }
    }

    private static void RemoveCarrierControllers(WIVSMMidiPart part, AceParameterDefinition definition)
    {
        int count = checked((int)part.GetNumController(definition.CarrierType));
        for (int index = count - 1; index >= 0; index--)
        {
            var controller = part.GetController(definition.CarrierType, (ulong)index);
            if (controller != null)
                part.RemoveController(controller);
        }
    }

    private static IEnumerable<WIVSMMidiPart> EnumerateParts(WIVSMSequence sequence)
    {
        foreach (var track in sequence.MidiTracks)
        {
            foreach (var part in track.MidiParts)
            {
                if (part != null)
                    yield return part;
            }
        }
    }

    private static bool TryGetDefinition(int kind, out AceParameterDefinition definition)
    {
        foreach (var candidate in AceParameterDefinitions.All)
        {
            if ((int)candidate.Kind == kind)
            {
                definition = candidate;
                return true;
            }
        }
        definition = AceParameterDefinitions.All[0];
        return false;
    }
}
