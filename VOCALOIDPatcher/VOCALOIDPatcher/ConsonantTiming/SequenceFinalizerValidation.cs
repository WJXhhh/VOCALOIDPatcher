#if PATCHER_ISOLATED_VALIDATION
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Threading;
using System.Windows.Threading;
using HarmonyLib;
using VOCALOIDPatcher.Rendering;
using Yamaha.VOCALOID.VSM;

namespace VOCALOIDPatcher.ConsonantTiming;

internal static class SequenceFinalizerValidation
{
    private static bool _failClose;
    private static readonly List<WeakReference<WIVSMSequence>> FailedCloses = new();
    private static bool FailClose(WIVSMSequence __instance, ref bool __result)
    {
        if (!_failClose || !Utils.NativeRenderSession.IsInternalSequence(__instance)) return true;
        FailedCloses.Add(new(__instance)); __result = false; return false;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void DisposeWithCloseFailure(WIVSMSequenceManager manager, WIVSMNote note)
    {
        var backend = new LocalConsonantTimingBackend(manager, Yamaha.VOCALOID.App.DSEManager!, Yamaha.VOCALOID.App.DatabaseManager!);
        using (var prepared = backend.PrepareCoefficient(note, 1)) prepared.Query(new SampledTimingPlan(note.Phonemes, []), 1);
        _failClose = true;
        try { backend.Dispose(); }
        finally { _failClose = false; }
    }

    internal static void Execute(WIVSMSequenceManager manager, string directory)
    {
        RendererIncidentLog.DirectoryPath = Path.Combine(directory, "diagnostics");
        DurationSelection.NativeDurationSelection.Initialize(Path.Combine(Patcher.DataDir, "native", "v6patch_clock.dll"));
        manager.CacheCapacity = 1024L * 1024 * 1024;
        var shell = RuntimeHelpers.GetUninitializedObject(typeof(Yamaha.VOCALOID.Sequence));
        GC.SuppressFinalize(shell);
        object?[] args = [Environment.GetEnvironmentVariable("VOCALOID_TRANSITION_SOURCE")!, null];
        var loaded = ((Yamaha.VOCALOID.Sequence.LoadResult, bool))AccessTools.Method(typeof(Yamaha.VOCALOID.Sequence), "LoadProjectSequenceFile").Invoke(shell, args)!;
        if (loaded.Item1 != Yamaha.VOCALOID.Sequence.LoadResult.Success || args[1] is not WIVSMSequence source)
            throw new InvalidOperationException("Cannot load finalizer fixture.");
        var records = new List<object>();
        try
        {
            source.InterpolatedMasterVolume(); source.Commit(false);
            var parts = source.MidiTracks.SelectMany(t => t.MidiParts).Where(p => !p.IsAi && p.NumNotes != 0).ToArray();
            string Inputs() => JsonSerializer.Serialize(parts.Select(p => p.NotesEnumerator.Select(n => new {
                n.Phonemes, n.NoteVelocity, n.NoteNumber, position = n.RelPosTick.Value, duration = n.DurationTick.Value })));
            string inputs = Inputs();
            string controls = SampledTimingHost.TransitionData(source).Fingerprint;
            if (Environment.GetEnvironmentVariable("VOCALOID_SEQUENCE_CLEANUP_PROBE") == "1")
            {
                var fault = new Harmony("VOCALOIDPatcher.sequence-close-fault");
                fault.Patch(AccessTools.Method(typeof(WIVSMSequence), nameof(WIVSMSequence.Close)), new HarmonyMethod(typeof(SequenceFinalizerValidation), nameof(FailClose)));
                try
                {
                    DisposeWithCloseFailure(manager, parts[0].GetNote(0)!);
                    if (TimingSequenceCleanup.PendingCount == 0 || FailedCloses.Count == 0) throw new InvalidOperationException("Close fault was not retained.");
                    GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
                    if (FailedCloses.Any(w => !w.TryGetTarget(out _))) throw new InvalidOperationException("An unclosed native owner was collected.");
                    TimingSequenceCleanup.RetryPending();
                    if (TimingSequenceCleanup.PendingCount != 0 || FailedCloses.Any(w => w.TryGetTarget(out var s) && s.IsOpen))
                        throw new InvalidOperationException("Retained native owners did not close on retry.");
                    File.WriteAllText(Path.Combine(directory, "sequence-cleanup.json"), JsonSerializer.Serialize(new { failedCloses = FailedCloses.Count,
                        TimingSequenceCleanup.Failures, TimingSequenceCleanup.PendingCount, forcedGcPassed = true }));
                    Console.WriteLine("sequence-cleanup.retained-gc-retry=passed");
                }
                finally { _failClose = false; fault.UnpatchAll(fault.Id); FailedCloses.Clear(); }
                long released = TimingSequenceCleanup.AlreadyReleased;
                var stale = manager.CreateSequence(new VSMSequenceData { SamplingRate = VSMSamplingRate._44100, MaxNumTracks = 4, MaxUndoCount = 0 })!;
                nint stalePointer = (nint)stale;
                if (!stale.Close() || manager.HasSequence(stalePointer)) throw new InvalidOperationException("Cannot release stale-owner fixture.");
                // Reconstruct the stale managed-owner state without ever
                // dereferencing the released pointer. The manager is the proof.
                AccessTools.Property(typeof(WIVSMSequence), "CppObjPtr").SetValue(stale, stalePointer);
                AccessTools.Field(typeof(WIVSMSequence), "isCreateUnmanagedObj").SetValue(stale, true);
                TimingSequenceCleanup.Close(manager, stale, Environment.CurrentManagedThreadId, Dispatcher.CurrentDispatcher);
                if (stale.IsOpen || TimingSequenceCleanup.AlreadyReleased != released + 1)
                    throw new InvalidOperationException("Released native owner was not detached.");
                GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
                File.WriteAllText(Path.Combine(directory, "released-owner.json"), "{\"managerVerified\":true,\"wrapperDetached\":true,\"forcedGcPassed\":true}");
                return;
            }
            int target = int.TryParse(Environment.GetEnvironmentVariable("VOCALOID_SEQUENCE_TARGET_PART"), out int selected) ? selected : 6;
            using (var preparation = SampledTimingHost.PrepareTransitions(parts[target]))
            {
                var q = preparation.Query();
                var execution = preparation.Prepared.QueryTransitionControls(q.Layout, q.Controls, preparation.Revision);
                File.WriteAllText(Path.Combine(directory, "target-plan.json"), JsonSerializer.Serialize(new {
                    notes = q.Layout.Notes, edges = q.Layout.Edges.Select(e => new { e.Identity, e.Original, e.Current }),
                    q.Controls.Junctions, q.Controls.Gains, q.Controls.Onsets, q.Controls.Phases, q.Controls.Coverages,
                    execution.Segments, execution.RenderAnchors, execution.SourceProgress }, new JsonSerializerOptions { WriteIndented = true }));
            }
            source.BlockRenderingEnabled = true;
            source.BlockRenderingLengthMsec = WIVSMSequence.DefaultBlockRenderingBlockSize;
            source.BlockRenderingPartDurationThresholdMsec = WIVSMSequence.DefaultBlockRenderingPartDurationThresholdMsec;
            for (int repeat = 0; repeat < 6; repeat++)
            {
                source.BlockRenderingEnabled = repeat % 2 == 0;
                foreach (var part in parts) SampledTimingVsmBinding.InvalidateRenderedPart(part, part.NotesEnumerator.Take(33));
                source.StartAsyncRendering();
                var watch = Stopwatch.StartNew();
                do
                {
                    while (source.InvokeRendererObserver()) { }
                    var frame = new DispatcherFrame();
                    Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(() => frame.Continue = false));
                    Dispatcher.PushFrame(frame); Thread.Sleep(5);
                    if (watch.Elapsed.TotalSeconds > 90) throw new TimeoutException("Captured render timed out.");
                } while (!source.IsFinishedRendering);
                while (source.InvokeRendererObserver()) { }
                records.Add(new { repeat, source.BlockRenderingEnabled, inputsUnchanged = Inputs() == inputs,
                    controlsUnchanged = SampledTimingHost.TransitionData(source).Fingerprint == controls,
                    diagnostics = SampledTimingHost.Diagnostics(source), health = RendererHealthMonitor.Diagnostics(source),
                    cleanup = new { TimingSequenceCleanup.PendingCount, TimingSequenceCleanup.Failures, TimingSequenceCleanup.AlreadyReleased },
                    parts = parts.Select((p, i) => new { index = i, p.HasValidRenderedScore, p.HasValidRenderedWave,
                        timing = NativeSampledTiming.ReadStatus((nint)p), transition = NativeTransitionGain.ReadStatus((nint)p), failure = NativeTransitionGain.ReadFailure((nint)p) }).ToArray() });
                File.WriteAllText(Path.Combine(directory, "sequence-finalizer.json"), JsonSerializer.Serialize(records,
                    new JsonSerializerOptions { WriteIndented = true, IncludeFields = true }));
                Console.WriteLine("sequence-finalizer.render=" + repeat);
                if (parts.Any(p => SampledTimingHost.HasNativeRenderFailure((nint)p)))
                    throw new InvalidOperationException("Captured render has native failures.");
                if (Inputs() != inputs || SampledTimingHost.TransitionData(source).Fingerprint != controls)
                    throw new InvalidOperationException("Captured replay changed authored note or transition inputs.");
                if (JsonSerializer.SerializeToElement(RendererHealthMonitor.Diagnostics(source)).GetProperty("LastIncident").ValueKind != JsonValueKind.Null)
                    throw new InvalidOperationException("Captured replay required automatic recovery.");
                SampledTimingHost.RestartFailedRender(source, parts);
                GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
                TimingSequenceCleanup.RetryPending();
                if (TimingSequenceCleanup.PendingCount != 0) throw new InvalidOperationException("Private native sequences remained pending after reset.");
                Console.WriteLine("sequence-finalizer.gc=" + repeat);
            }
        }
        finally { source.StopAsyncRendering(); source.Close(); }
    }
}
#endif
