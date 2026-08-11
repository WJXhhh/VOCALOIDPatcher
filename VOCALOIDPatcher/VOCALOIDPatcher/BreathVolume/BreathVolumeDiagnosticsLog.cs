using System;
using System.Collections.Generic;
using System.Linq;
using VOCALOIDPatcher.Utils;

namespace VOCALOIDPatcher.BreathVolume;

/// <summary>
/// Compatibility facade for existing BVL diagnostics. It never opens a second
/// log file and never forwards diagnostic message contents.
/// </summary>
internal static class BreathVolumeDiagnosticsLog
{
    private static readonly object SnapshotLock = new();
    private static NativeBreathDiagnostics? _lastSnapshot;
    private static NativeDseDiagnostics? _lastDseSnapshot;

    internal static string FilePath => RuntimeObservationLog.LogPath;

    internal static void Initialize()
    {
        RuntimeObservationLog.Write("breath.diagnostic", "initialize", new Dictionary<string, object?>
        {
            ["editorVersion"] = typeof(Yamaha.VOCALOID.App).Assembly.GetName().Version?.ToString(),
            ["patcherVersion"] = global::VOCALOIDPatcher.Patcher.Version.ToString(),
        });
    }

    internal static void Write(string message)
    {
        try
        {
            string value = message ?? string.Empty;
            RuntimeObservationLog.Write("breath.diagnostic", "point", new Dictionary<string, object?>
            {
                ["messageId"] = RuntimeObservationLog.HashText(value),
                ["messageLength"] = value.Length,
            });
        }
        catch
        {
        }
    }

    internal static void WriteTraditionalDetection(
        string source,
        IntPtr partHandle,
        long scoreFrames,
        long audioSamples,
        long samplesPerFrame,
        int sampleRate,
        TraditionalBreathDetectionResult detection,
        int storedRangeCount,
        double elapsedMilliseconds)
    {
        try
        {
            RenderCycle renderCycle = RuntimeObservationLog.RenderCycleForPart(partHandle);
            var data = new Dictionary<string, object?>
            {
                ["source"] = source,
                ["partId"] = RuntimeObservationLog.ObjectId("part", partHandle),
                ["scoreFrames"] = scoreFrames,
                ["audioSamples"] = audioSamples,
                ["samplesPerFrame"] = samplesPerFrame,
                ["sampleRate"] = sampleRate,
                ["pitchedOnsets"] = detection.PitchedOnsets,
                ["evaluatedGaps"] = detection.EvaluatedGaps,
                ["activityCandidates"] = detection.ActivityCandidates,
                ["rejectedShortActivity"] = detection.RejectedShortActivity,
                ["rejectedShortLead"] = detection.RejectedShortLead,
                ["rejectedPreviousTail"] = detection.RejectedPreviousTail,
                ["activeFrames"] = detection.ActiveFrames,
                ["maximumUnpitchedRms"] = detection.MaxUnpitchedRms,
                ["maximumUnpitchedPeak"] = detection.MaxUnpitchedPeak,
                ["detectedRanges"] = detection.Ranges.Count,
                ["storedRanges"] = storedRangeCount,
                ["elapsedMilliseconds"] = elapsedMilliseconds,
            };
            RuntimeObservationLog.AddCycleData(data, renderCycle: renderCycle);
            RuntimeObservationLog.Write("breath.traditionalDetection", "complete", data);
        }
        catch
        {
        }
    }

    internal static void WriteRegions(
        IntPtr partHandle,
        string source,
        long scoreFrames,
        int detectedRanges,
        int noteCount,
        int mappedRegions)
    {
        try
        {
            RenderCycle renderCycle = RuntimeObservationLog.RenderCycleForPart(partHandle);
            var data = new Dictionary<string, object?>
            {
                ["source"] = source,
                ["partId"] = RuntimeObservationLog.ObjectId("part", partHandle),
                ["scoreFrames"] = scoreFrames,
                ["detectedRanges"] = detectedRanges,
                ["noteCount"] = noteCount,
                ["mappedRegions"] = mappedRegions,
            };
            RuntimeObservationLog.AddCycleData(data, renderCycle: renderCycle);
            RuntimeObservationLog.Write("breath.regions", "mapped", data);
        }
        catch
        {
        }
    }

    internal static void WriteNativeSnapshot(string phase, bool force = false)
    {
        WriteDseSnapshot(phase, force);
        NativeBreathDiagnostics snapshot = NativeBreathCapture.GetDiagnostics();
        lock (SnapshotLock)
        {
            if (!force && _lastSnapshot is { } previous &&
                previous.LoadState == snapshot.LoadState &&
                previous.InstallResult == snapshot.InstallResult &&
                previous.Error == snapshot.Error &&
                previous.CoreCalls == snapshot.CoreCalls &&
                previous.MappedContexts == snapshot.MappedContexts &&
                previous.ContextMisses == snapshot.ContextMisses &&
                previous.HookCalls == snapshot.HookCalls &&
                previous.SuccessfulBlocks == snapshot.SuccessfulBlocks &&
                previous.OutputSamples == snapshot.OutputSamples &&
                previous.OutputPeak == snapshot.OutputPeak &&
                previous.QueuedEvents == snapshot.QueuedEvents &&
                previous.DroppedEvents == snapshot.DroppedEvents &&
                previous.InvalidCalls == snapshot.InvalidCalls)
                return;
            _lastSnapshot = snapshot;
        }

        RuntimeObservationLog.Write("breath.nativeCapture", "snapshot", new Dictionary<string, object?>
        {
            ["phase"] = phase,
            ["loadState"] = snapshot.LoadState,
            ["installResult"] = snapshot.InstallResult,
            ["targetRva"] = snapshot.TargetRva,
            ["coreTargetRva"] = snapshot.CoreTargetRva,
            ["coreCalls"] = snapshot.CoreCalls,
            ["mappedContexts"] = snapshot.MappedContexts,
            ["contextMisses"] = snapshot.ContextMisses,
            ["hookCalls"] = snapshot.HookCalls,
            ["successfulBlocks"] = snapshot.SuccessfulBlocks,
            ["outputSamples"] = snapshot.OutputSamples,
            ["outputPeak"] = snapshot.OutputPeak,
            ["queuedUpdates"] = snapshot.QueuedEvents,
            ["droppedUpdates"] = snapshot.DroppedEvents,
            ["invalidCalls"] = snapshot.InvalidCalls,
            ["lastPartId"] = RuntimeObservationLog.ObjectId("part", snapshot.LastPartHandle),
            ["lastBeginFrame"] = snapshot.LastBeginFrame,
            ["lastEndFrame"] = snapshot.LastEndFrame,
            ["lastResult"] = snapshot.LastResult,
            ["errorTypeId"] = RuntimeObservationLog.HashText(snapshot.Error),
        });

    }

    private static void WriteDseSnapshot(string phase, bool force)
    {
        NativeDseDiagnostics snapshot = NativeDseCapture.GetDiagnostics();
        lock (SnapshotLock)
        {
            if (!force && _lastDseSnapshot is { } previous && previous == snapshot)
                return;
            _lastDseSnapshot = snapshot;
        }

        RuntimeObservationLog.Write("dse.engineCapture", "snapshot", new Dictionary<string, object?>
        {
            ["phase"] = phase,
            ["loadState"] = snapshot.LoadState,
            ["installResult"] = snapshot.InstallResult,
            ["vtableRva"] = snapshot.VtableRva,
            ["createBufferCalls"] = snapshot.CreateBufferCalls,
            ["addEventCalls"] = snapshot.AddEventCalls,
            ["setPrerollCalls"] = snapshot.SetPrerollCalls,
            ["startCalls"] = snapshot.StartCalls,
            ["stopCalls"] = snapshot.StopCalls,
            ["stepCalls"] = snapshot.StepCalls,
            ["stepSuccesses"] = snapshot.StepSuccesses,
            ["lastEventCount"] = snapshot.LastEventCount,
            ["lastEventCode"] = snapshot.LastEventCode,
            ["lastStartResult"] = snapshot.LastStartResult,
            ["lastStepResult"] = snapshot.LastStepResult,
            ["lastEventValueCount"] = snapshot.LastEventValueCount,
            ["lastEventSequence"] = snapshot.LastEventSequence,
            ["lastEventField01"] = snapshot.LastEventField01,
            ["lastEventField23"] = snapshot.LastEventField23,
            ["lastEventValueHash"] = snapshot.LastEventValueHash,
            ["lastEventSecondaryValueHash"] = snapshot.LastEventSecondaryValueHash,
            ["lastEventSecondaryValueCount"] = snapshot.LastEventSecondaryValueCount,
            ["lastInputFrame"] = snapshot.LastInputFrame,
            ["renderOutputSamples"] = snapshot.RenderOutputSamples,
            ["renderOutputHash"] = snapshot.RenderOutputHash,
            ["renderOutputPeak"] = snapshot.RenderOutputPeak,
            ["renderOutputEnergy"] = snapshot.RenderOutputEnergy,
            ["metadataSteps"] = snapshot.MetadataSteps,
            ["pointerlessSteps"] = snapshot.PointerlessSteps,
            ["pointerlessActiveSteps"] = snapshot.PointerlessActiveSteps,
            ["pointerlessLoudSteps"] = snapshot.PointerlessLoudSteps,
            ["pointerlessFirstFrame"] = snapshot.PointerlessFirstFrame,
            ["pointerlessLastFrame"] = snapshot.PointerlessLastFrame,
            ["lastMetadataField01"] = snapshot.LastMetadataField01,
            ["lastMetadataField23"] = snapshot.LastMetadataField23,
            ["lastMetadataFlags"] = snapshot.LastMetadataFlags,
            ["lastMetadataPointerMask"] = snapshot.LastMetadataPointerMask,
            ["errorTypeId"] = RuntimeObservationLog.HashText(snapshot.Error),
        });
    }

    internal static void WriteMarkers(string phase, IReadOnlyList<NativeBreathMarker> markers)
    {
        string fingerprint = string.Join(",", markers
            .OrderBy(marker => marker.BeginFrame)
            .ThenBy(marker => marker.EndFrame)
            .Select(marker => $"{marker.BeginFrame}:{marker.EndFrame}"));
        RuntimeObservationLog.Write("breath.markers", "snapshot", new Dictionary<string, object?>
        {
            ["phase"] = phase,
            ["count"] = markers.Count,
            ["rangesId"] = RuntimeObservationLog.HashText(fingerprint),
        });
    }
}
