#if PATCHER_ISOLATED_VALIDATION
using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Windows.Threading;
using HarmonyLib;
using VOCALOIDPatcher.BreathVolume;
using VOCALOIDPatcher.ConsonantTiming;
using Yamaha.VOCALOID.DSE;
using Yamaha.VOCALOID.VDM;
using Yamaha.VOCALOID.VSM;

namespace VOCALOIDPatcher.Rendering;

internal static class RendererHealthValidation
{
    private static bool _removeEveryStart;
    private static string? _runtimePath;
    private static int _moves;
    private static Action? _beforeNative;
    [HarmonyPriority(-100)]
    private static void FailStart()
    {
        var inject = _beforeNative; _beforeNative = null; inject?.Invoke();
        if (!_removeEveryStart) return;
        string path = _runtimePath!;
        string expected = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "VOCALOID6", "VSM", "VOCALOIDPatcher.Probe." + Environment.ProcessId);
        if (!path.StartsWith(expected + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Fault fixture is outside this isolated probe's namespace.");
        if (Directory.Exists(path)) Directory.Move(path, path + ".fault-" + ++_moves);
    }
    private static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
    private static void Pump(WIVSMSequence source)
    {
        while (source.InvokeRendererObserver()) { }
        var frame = new DispatcherFrame();
        Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(() => frame.Continue = false));
        Dispatcher.PushFrame(frame);
    }
    internal static void Execute(WIVSMSequenceManager manager, DSEManager dse, DatabaseManager database, string directory)
    {
        RendererIncidentLog.DirectoryPath = Path.Combine(directory, "diagnostics");
        // Match Editor startup: the duration adapter is global even when this
        // Part owns only timing/transition controls. Missing duration state
        // must not erase timing failure evidence or abort health observation.
        VOCALOIDPatcher.DurationSelection.NativeDurationSelection.Initialize(
            Path.Combine(Patcher.DataDir, "native", "v6patch_clock.dll"));
        var fault = new Harmony("VOCALOIDPatcher.renderer-health-fault");
        fault.Patch(AccessTools.Method(typeof(WIVSMSequence), nameof(WIVSMSequence.StartAsyncRendering)),
            new HarmonyMethod(typeof(RendererHealthValidation), nameof(FailStart)));
        var source = manager.CreateSequence(new VSMSequenceData { SamplingRate = VSMSamplingRate._44100, MaxNumTracks = 4, MaxUndoCount = 100 })!;
        try
        {
            var track = (WIVSMMidiTrack)source.InsertTrackEx(0, VSMTrackType.Midi, "health")!;
            var parts = new[] { track.InsertPart(VSMAbsTick.Zero, new VSMRelTick(4800), "a")!,
                track.InsertPart(new VSMAbsTick(4800), new VSMRelTick(4800), "b")! };
            foreach (var part in parts)
            {
                Check(part.SetVoiceBankID("BL8CEAM5N4XN3LFK"), "Voicebank unavailable.");
                Check(part.InsertNote(new VSMRelTick(960), new VSMNoteEvent(960, 60, 64), part.GetDefaultNoteExpression(),
                    part.GetDefaultAiNoteExpression(), "fixture", "l a", true, 4) != null, "Cannot create note.");
            }
            source.InsertTempo(VSMRelTick.Zero, 12000); source.InterpolatedMasterVolume(); source.Commit(false);
            BreathVolumeService.ResetHistory(source);
            var session = SampledTimingHost.Attach(source, new LocalConsonantTimingBackend(manager, dse, database));
            SampledTimingHost.SetCoefficient(parts[0].GetNote(0)!, .7f);
            using (var preparation = SampledTimingHost.PrepareTransitions(parts[0]))
            {
                var query = preparation.Query();
                var edge = query.Layout.Edges.First(e => TransitionPhaseMapping.CanEdit(e.Current));
                var controls = query.Controls.WithPhase(edge.Identity, .3f);
                SampledTimingHost.CommitTransitions(preparation,
                    preparation.Prepared.QueryTransitionControls(query.Layout, controls, preparation.Revision));
            }
            using var observer = new WIVSMRendererObserver();
            int completed = 0, canceled = 0;
            observer.Completed += (_, _) => completed++;
            observer.Canceled += (_, _) => canceled++;
            source.AddRendererObserver(observer);
            void Wait(Func<bool> predicate, string name)
            {
                var watch = System.Diagnostics.Stopwatch.StartNew();
                while (watch.ElapsedMilliseconds < 12000)
                { Pump(source); if (predicate()) break; Thread.Sleep(2); }
                Check(predicate(), name + ": " + JsonSerializer.Serialize(SampledTimingHost.Diagnostics(source)));
                Console.WriteLine("renderer.health=" + name);
            }
            bool Valid() => source.IsFinishedRendering && parts.All(p => p.HasValidRenderedScore && p.HasValidRenderedWave);
            string State() => JsonSerializer.SerializeToElement(RendererHealthMonitor.Diagnostics(source)).GetProperty("Recovery").GetString() ?? "";
            void Dirty()
            {
                source.StopAsyncRendering();
                foreach (var part in parts) SampledTimingVsmBinding.InvalidateRenderedPart(part, part.NotesEnumerator);
            }
            source.StartAsyncRendering(); Wait(Valid, "baseline-two-parts");
            string document = session.Document.Fingerprint, transitions = session.Transitions.Snapshot.Fingerprint;
            int velocity = parts[0].GetNote(0)!.NoteVelocity;
            _runtimePath = RenderTemporaryDirectory.Read(source);
            string mode = Environment.GetEnvironmentVariable("VOCALOID_RENDER_HEALTH_CASE") ?? "temp";
            bool nativeOnly = mode == "native";
            if (mode == "watchdog")
            {
                Dirty();
                var dispatch = RendererHealthMonitor.BeginDispatch(source);
                try
                {
                    foreach (var part in parts)
                        AccessTools.Method(typeof(WIVSMRendererObserver), "InvokeStartEvent").Invoke(observer, [nint.Zero, (nint)part]);
                }
                finally { RendererHealthMonitor.EndDispatch(dispatch); }
                RendererHealthMonitor.ExpireProgressForValidation(source);
                Check(!source.IsFinishedRendering && !source.CanAsyncRendering() && State() != "restart-submitted",
                    "Watchdog restarted work that the native scheduler still considers unfinished.");
                Console.WriteLine("renderer.health=busy-watchdog-logs-without-restarting");
                source.StartAsyncRendering(); Wait(Valid, "resume-after-watchdog");
            }
            else if (!nativeOnly)
            {
                Dirty(); Directory.Move(_runtimePath, _runtimePath + ".fault-once");
                source.StartAsyncRendering();
                Wait(() => Valid() && State() == "recovered", "missing-temp-auto-recovery-two-parts");
                Check(canceled >= 1 && completed >= 3, "Fault was not delivered and recovered by real native callbacks.");
                Check(session.Document.Fingerprint == document && session.Transitions.Snapshot.Fingerprint == transitions &&
                    parts[0].GetNote(0)!.NoteVelocity == velocity, "Recovery changed retained controls or source inputs.");
            }
            else
            {
                TransitionExecutionPlan execution;
                using (var preparation = SampledTimingHost.PrepareTransitions(parts[0]))
                {
                    var q = preparation.Query();
                    execution = preparation.Prepared.QueryTransitionControls(q.Layout, q.Controls, preparation.Revision);
                }
                Dirty();
                _beforeNative = () =>
                {
                    var progress = NativeTransitionGain.Progress(execution);
                    Check(progress.Length != 0, "Native fault fixture has no transition progress.");
                    progress[0].End++;
                    NativeSampledTiming.Publish(parts[0], 990_001, new System.Collections.Generic.Dictionary<nint, float>
                        { [(nint)parts[0].GetNote(0)!] = .7f },
                        execution.RenderAnchors.Select(p => new NativePhonemeTiming.Point(p.From, p.To)).ToArray(),
                        NativeTransitionGain.Gains(execution), progress);
                };
                source.StartAsyncRendering();
                Wait(() => Valid() && State() == "recovered" && NativeSampledTiming.ReadStatus((nint)parts[0]).Epoch != 990_001,
                    "native-timing-failure-republishes-retained-controls");
            }

            // One dispatch scope fans out a callback once; benign or obsolete
            // cancellation must not retire/restart the current successful render.
            var scope = RendererHealthMonitor.BeginDispatch(source);
            try { AccessTools.Method(typeof(WIVSMRendererObserver), "InvokeCancelEvent").Invoke(observer,
                [nint.Zero, (nint)parts[0], VSMRenderCancelReason.UserCanceled]); }
            finally { RendererHealthMonitor.EndDispatch(scope); }
            Pump(source); Check(Valid(), "Benign cancellation changed valid rendering.");
            long requestBeforeStale = JsonSerializer.SerializeToElement(RendererHealthMonitor.Diagnostics(source)).GetProperty("request").GetInt64();
            scope = RendererHealthMonitor.BeginDispatch(source);
            try { AccessTools.Method(typeof(WIVSMRendererObserver), "InvokeCancelEvent").Invoke(observer,
                [nint.Zero, (nint)parts[0], VSMRenderCancelReason.FailedToStartDSE]); }
            finally { RendererHealthMonitor.EndDispatch(scope); }
            Pump(source);
            Check(Valid() && JsonSerializer.SerializeToElement(RendererHealthMonitor.Diagnostics(source)).GetProperty("request").GetInt64() == requestBeforeStale &&
                State() == "obsolete-failure", "Delayed failure restarted an already valid render.");

            if (mode == "temp")
            {
                Dirty(); _removeEveryStart = true; _moves = 0;
                source.StartAsyncRendering();
                Wait(() => source.IsFinishedRendering && State() == "retry-budget-exhausted", "persistent-failure-stops-after-one-retry");
                Check(_moves == 2, "Persistent failure was retried indefinitely or was not retried.");
                _removeEveryStart = false;
                RenderTemporaryDirectory.Repair(source);
                parts[0].GetNote(0)!.NoteVelocity = velocity + 1; source.Commit(true);
                source.StartAsyncRendering(); Wait(Valid, "new-edit-renders-after-failed-retry");
                source.StopAsyncRendering(); source.Undo(); source.StartAsyncRendering(); Wait(Valid, "undo-after-recovery");
            }
            Check(session.Document.Fingerprint == document && session.Transitions.Snapshot.Fingerprint == transitions &&
                parts[0].GetNote(0)!.NoteVelocity == velocity, "Recovery polluted undo or control data.");
            Thread.Sleep(50);
            string log = Path.Combine(RendererIncidentLog.DirectoryPath, "renderer.jsonl");
            string marker = mode == "native" ? "\"Epoch\":990001" : mode == "watchdog" ? "no-progress-30s" : "\"reason\":8";
            Check(File.Exists(log) && File.ReadAllText(log).Contains(marker), "Original native failure was not persisted.");
            if (nativeOnly)
                Check(File.ReadAllText(log).Contains("\"TransitionMismatch\":{\"Reason\":4"),
                    "Expected/actual ART mismatch was not retained before route retirement.");
            File.WriteAllText(Path.Combine(directory, "renderer-health.json"), JsonSerializer.Serialize(new
            { completed, canceled, persistentFaultAttempts = _moves, controlsPreserved = true, undoPreserved = true,
                diagnostics = SampledTimingHost.Diagnostics(source) }, new JsonSerializerOptions { WriteIndented = true }));
            source.RemoveRendererObserver(observer);
            if (mode == "temp") PlainSource(manager);
        }
        finally { _removeEveryStart = false; _beforeNative = null; source.Close(); fault.UnpatchAll(fault.Id); }
    }

    private static void PlainSource(WIVSMSequenceManager manager)
    {
        var source = manager.CreateSequence(new VSMSequenceData { SamplingRate = VSMSamplingRate._44100, MaxNumTracks = 4, MaxUndoCount = 100 })!;
        try
        {
            var track = (WIVSMMidiTrack)source.InsertTrackEx(0, VSMTrackType.Midi, "plain-health")!;
            var part = track.InsertPart(VSMAbsTick.Zero, new VSMRelTick(4800), "plain-health")!;
            Check(part.SetVoiceBankID("BL8CEAM5N4XN3LFK"), "Plain voicebank unavailable.");
            part.InsertNote(new VSMRelTick(960), new VSMNoteEvent(960, 60, 64), part.GetDefaultNoteExpression(),
                part.GetDefaultAiNoteExpression(), "fixture", "l a", true, 4);
            source.InterpolatedMasterVolume(); source.Commit(false);
            using var observer = new WIVSMRendererObserver(); source.AddRendererObserver(observer);
            bool Valid() => source.IsFinishedRendering && part.HasValidRenderedScore && part.HasValidRenderedWave;
            void Wait()
            {
                var watch = System.Diagnostics.Stopwatch.StartNew();
                while (!Valid() && watch.ElapsedMilliseconds < 12000) { Pump(source); Thread.Sleep(2); }
                Pump(source); Check(Valid(), "Plain source recovery failed: " + JsonSerializer.Serialize(RendererHealthMonitor.Diagnostics(source)));
            }
            source.StartAsyncRendering(); Wait();
            source.StopAsyncRendering(); SampledTimingVsmBinding.InvalidateRenderedPart(part, part.NotesEnumerator);
            string path = RenderTemporaryDirectory.Read(source);
            Check(path.Contains("VOCALOIDPatcher.Probe." + Environment.ProcessId + Path.DirectorySeparatorChar), "Plain fixture is not isolated.");
            Directory.Move(path, path + ".fault-plain");
            source.StartAsyncRendering(); Wait();
            Check(SampledTimingHost.Find(source) == null, "Plain recovery created a timing document.");
            Check(JsonSerializer.SerializeToElement(RendererHealthMonitor.Diagnostics(source)).GetProperty("Recovery").GetString() == "recovered",
                "Plain source did not exercise automatic recovery.");
            Check(part.GetNote(0)!.NoteVelocity == 64, "Plain recovery changed velocity.");
            Console.WriteLine("renderer.health=plain-source-auto-recovery-without-timing-session");
            source.RemoveRendererObserver(observer);
        }
        finally { source.Close(); }
    }
}
#endif
