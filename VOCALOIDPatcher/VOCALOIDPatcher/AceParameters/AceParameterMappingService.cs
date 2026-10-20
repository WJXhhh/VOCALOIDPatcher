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

// Archive/lifecycle bridge for the existing carrier curves. Sound is generated
// by AceAcousticHost from rendered PCM; no native sound controller is rewritten.
internal static class AceParameterMappingService
{
    private sealed class SequenceState
    {
        public bool Initialized;
        public bool Disposed;
    }

    private static readonly ConditionalWeakTable<WIVSMSequence, SequenceState> States = new();
    private static int _projectLoadDepth;

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
        try
        {
            if (sequence == null || Volatile.Read(ref _projectLoadDepth) > 0)
                return;

            var state = EnsureState(sequence);
            state.Initialized = true;
            AceAcousticHost.SourceChanged(sequence);
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
            AceAcousticHost.SourceChanged(sequence);
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
            state.Initialized = true;
            AceAcousticHost.SourceChanged(sequence);
        }
        catch (Exception e)
        {
            Debug.Print(TranslationManager.Tr("VOCALOIDPatcher_Debug_AceParameter_Failed", e.Message));
        }
    }

    internal static void OnSequenceClosing(WIVSMSequence sequence)
    {
        AceAcousticHost.Close(sequence);
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
        if (sequence != null) AceAcousticHost.SourceChanged(sequence);
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
        return $"initialized={state.Initialized} backend=pcm-acoustic disposed={state.Disposed}";
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
