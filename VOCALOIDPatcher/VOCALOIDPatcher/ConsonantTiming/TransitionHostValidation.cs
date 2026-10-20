#if PATCHER_ISOLATED_VALIDATION
using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Windows;
using System.Windows.Threading;
using HarmonyLib;
using VOCALOIDPatcher.BreathVolume;
using VOCALOIDPatcher.Config;
using VOCALOIDPatcher.DurationSelection;
using VOCALOIDPatcher.Patch;
using VOCALOIDPatcher.Patch.Patches;
using Yamaha.VOCALOID.DSE;
using Yamaha.VOCALOID.VDM;
using Yamaha.VOCALOID.VSM;

namespace VOCALOIDPatcher.ConsonantTiming;

[CLSCompliant(false)]
public static class TransitionHostValidation
{
    private static bool _failSave;
    public static void Run(WIVSMSequenceManager manager, DSEManager dse, DatabaseManager database, string directory)
    {
        Exception? error = null;
        var thread = new Thread(() => { try { Execute(manager, dse, database, directory); } catch (Exception e) { error = e; } });
        thread.SetApartmentState(ApartmentState.STA); thread.Start(); thread.Join();
        if (error != null) throw new InvalidOperationException("Transition host validation failed.", error);
    }
    private static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
    private static void FailSave() { if (_failSave) throw new IOException("Injected transition archive write failure."); }
    private static void Pump()
    {
        var frame = new DispatcherFrame();
        Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() => frame.Continue = false));
        Dispatcher.PushFrame(frame);
    }
    private static string Pcm(string path) => Convert.ToHexString(SHA256.HashData(PcmData(path)));
    private static byte[] PcmData(string path)
    {
        using var input = File.OpenRead(path); using var reader = new BinaryReader(input);
        input.Position = 12;
        while (input.Position + 8 <= input.Length)
        {
            string key = Encoding.ASCII.GetString(reader.ReadBytes(4)); uint size = reader.ReadUInt32();
            if (size > input.Length - input.Position) throw new InvalidDataException("Invalid fixture WAV.");
            if (key == "data") return reader.ReadBytes(checked((int)size));
            input.Position += size + (size & 1);
        }
        throw new InvalidDataException("Missing fixture PCM.");
    }
    private static void Execute(WIVSMSequenceManager manager, DSEManager dse, DatabaseManager database, string directory)
    {
        Directory.CreateDirectory(directory); var application = new Application();
        Patcher.ConfigManager = new ConfigManager(Path.Combine(directory, "isolated-config.json"));
        Patcher.DataDir = Path.GetDirectoryName(Path.GetDirectoryName(Environment.GetEnvironmentVariable("VOCALOID_TIMING_PROBE_NATIVE")))!;
        foreach (var pair in new (string, object)[] { ("SequenceManager", manager), ("DSEManager", dse), ("DatabaseManager", database) })
            typeof(Yamaha.VOCALOID.App).GetProperty(pair.Item1)!.SetValue(null, pair.Item2);
        var harmony = new Harmony("VOCALOIDPatcher.isolated-transition-host");
        foreach (PatchBase patch in new PatchBase[]
        {
            new RendererHealthRequestPatch(), new RendererHealthStopPatch(), new RendererHealthDispatchPatch(), new RendererHealthStartedPatch(),
            new RendererHealthProgressPatch(), new RendererHealthCompletedPatch(), new RendererHealthCanceledPatch(), new RendererHealthClosePatch(),
            new SampledTimingRenderStartPatch(), new SampledTimingRenderCompletionPatch(), new SampledTimingRenderCancellationPatch(), new SampledTimingDirtyPatch(),
            new SampledTimingProjectSavePatch(), new SampledTimingProjectLoadPatch(), new SampledTimingDuplicateSequencePatch(),
            new RegisterShiftProjectLoadPatch(), new RegisterShiftProjectSavePatch(), new RegisterShiftSequenceStartRenderingPatch(),
            new RegisterShiftSequenceClosePatch(),
            new ConsonantTimingCommitPatch(), new ConsonantTimingUndoPatch(), new ConsonantTimingRedoPatch(), new ConsonantTimingClosePatch(),
            new BreathVolumeCommitHistoryPatch(), new BreathVolumeUndoPatch(), new BreathVolumeRedoPatch(), new BreathVolumeDirtyPatch(),
            new BreathVolumeProjectSavePatch(), new BreathVolumeDuplicateNotePatch(), new BreathVolumeRemoveNotePatch(),
            new BreathVolumeProjectLoadPatch(), new BreathVolumeDuplicateSequencePatch()
        })
        {
            var target = AccessTools.Method(patch.TargetClass, patch.TargetMethodName, patch.ArgumentTypes);
            Check(target != null, "Missing production host patch: " + patch.PatchName);
            HarmonyMethod? Hook(string name) => AccessTools.Method(patch.GetType(), name) is { } method ? new HarmonyMethod(method) : null;
            harmony.Patch(target, Hook("Prefix"), Hook("Postfix"), finalizer: Hook("Finalizer"));
        }
        harmony.Patch(AccessTools.Method(typeof(TransitionProjectArchive), "Write"), new HarmonyMethod(typeof(TransitionHostValidation), nameof(FailSave)));
        var states = new List<object>();
        var pcmPaths = new Dictionary<string, string>();
        void Record(object state) { states.Add(state); File.WriteAllText(Path.Combine(directory, "transition-host.json"), JsonSerializer.Serialize(states, new JsonSerializerOptions { WriteIndented = true })); }
        bool EqualAudio(string a, string b)
        {
            if (a == b) return true;
            var left = PcmData(pcmPaths[a]); var right = PcmData(pcmPaths[b]);
            if (left.Length != right.Length || left.Length == 0 || left.Length % 2 != 0) return false;
            int peak = 0, changed = 0; double squared = 0;
            for (int i = 0; i < left.Length; i += 2)
            {
                int delta = Math.Abs(BitConverter.ToInt16(left, i) - BitConverter.ToInt16(right, i));
                peak = Math.Max(peak, delta); squared += delta * (double)delta;
                if (delta != 0) changed++;
            }
            double rms = Math.Sqrt(squared / (left.Length / 2));
            // Controlled replays with identical restored NOTE inputs exhibit
            // <= 2 PCM16 LSB in a short interval. Record this numerical
            // envelope separately from meaningful audio changes.
            bool equal = peak <= 2 && rms <= .1;
            Record(new { name = "pcm-comparison", a, b, peak, rms, changed, equal });
            return equal;
        }
        var shell = RuntimeHelpers.GetUninitializedObject(typeof(Yamaha.VOCALOID.Sequence)); GC.SuppressFinalize(shell);
        var save = AccessTools.Method(typeof(Yamaha.VOCALOID.Sequence), "SaveSequence");
        var load = AccessTools.Method(typeof(Yamaha.VOCALOID.Sequence), "LoadProjectSequenceFile");
        bool Save(WIVSMSequence sequence, string name) => (bool)save.Invoke(shell, [sequence, directory, name, ".vpr"])!;
        WIVSMSequence Open(string name)
        {
            object?[] args = [Path.Combine(directory, name + ".vpr"), null];
            var result = ((Yamaha.VOCALOID.Sequence.LoadResult, bool))load.Invoke(shell, args)!;
            Check(result.Item1 == Yamaha.VOCALOID.Sequence.LoadResult.Success && args[1] is WIVSMSequence, "VPR open failed.");
            return (WIVSMSequence)args[1]!;
        }
        WIVSMMidiPart Part(WIVSMSequence source) => (WIVSMMidiPart)((WIVSMMidiTrack)source.GetTrack(0UL)!).GetPart(0UL)!;
        string Render(WIVSMSequence source, WIVSMMidiPart part, string name, WIVSMMidiPart[]? waitParts = null)
        {
            // Each probe waits for the render it starts. Undispatched events
            // from an earlier stopped service must not complete a new probe.
            source.StopAsyncRendering();
            while (source.InvokeRendererObserver()) { }
            bool completed = false, canceled = false;
            var pending = (waitParts ?? [part]).Select(p => (nint)p).ToHashSet();
            using var observer = new WIVSMRendererObserver();
            observer.Completed += (_, e) => { pending.Remove((nint)e.MidiPart); completed = pending.Count == 0; };
            observer.Canceled += (_, e) => { if (pending.Contains((nint)e.MidiPart)) canceled = true; };
            source.AddRendererObserver(observer); source.BlockRenderingEnabled = false;
            try
            {
                var watch = System.Diagnostics.Stopwatch.StartNew();
                source.StartAsyncRendering();
                while ((!completed || !source.IsFinishedRendering) && !canceled && watch.ElapsedMilliseconds < 15000)
                { while (source.InvokeRendererObserver()) { } Pump(); Thread.Sleep(1); }
                Check(completed && source.IsFinishedRendering && !canceled && part.HasValidRenderedScore && part.HasValidRenderedWave,
                    "Transition host render failed: " + name + "; " + JsonSerializer.Serialize(SampledTimingHost.Diagnostics(source)));
                string path = Path.Combine(directory, name + ".wav"); File.Copy(part.WaveFilePath, path, true);
                string hash = Pcm(path); pcmPaths.TryAdd(hash, path);
                var status = NativeSampledTiming.ReadStatus((nint)part);
                var gain = NativeTransitionGain.ReadStatus((nint)part);
                Check(status.Failures == 0, "Unified timing snapshot failed.");
                Check(gain.Failures == 0, "Native transition source/gain snapshot failed.");
                Record(new { name, hash, renderMilliseconds = watch.Elapsed.TotalMilliseconds, status.Epoch, status.Scopes, status.CacheKeys,
                    gainApplied = gain.Applied, gainFailures = gain.Failures,
                    notes = part.NotesEnumerator.Select(n => new { n.Phonemes, n.NoteVelocity, position = n.RelPosTick.Value, pitch = n.NoteNumber }).ToArray(), wave = part.WaveFilePath,
                    host = SampledTimingHost.Diagnostics(source), transition = SampledTimingHost.TransitionData(source).Fingerprint });
                Console.WriteLine("transition.host=" + name); return hash;
            }
            finally { source.StopAsyncRendering(); source.RemoveRendererObserver(observer); }
        }
        void Edit(WIVSMMidiPart part, float db, int delta)
        {
            TransitionLayout layout; TransitionControlPlan controls; long revision;
            using (var preparation = SampledTimingHost.PrepareTransitions(part))
            {
                var query = preparation.Query(); layout = query.Layout; revision = preparation.Revision;
                var edge = layout.Edges[1];
                controls = new(layout.Fingerprint, [new(new(edge.Identity, TransitionEndpoint.End), edge.Current.End + delta)], [new(edge.Identity, db)]);
                if (Environment.GetEnvironmentVariable("VOCALOID_TRANSITION_COVERAGE_GENERAL") == "1")
                    controls = controls.WithCoverage(edge.Identity, TransitionSourceSides.Right);
                if (Environment.GetEnvironmentVariable("VOCALOID_TRANSITION_ONSET") == "1")
                    controls = controls.WithOnset(layout.Edges.First(e => e.Identity.From == "Sil").Identity, -8);
                if (Environment.GetEnvironmentVariable("VOCALOID_TRANSITION_PHASE") == "1")
                    foreach (var targetEdge in layout.Edges.Where(e => TransitionPhaseMapping.CanEdit(e.Current)))
                        controls = controls.WithPhase(targetEdge.Identity, db < -6 ? -.5f : .5f);
                var execution = preparation.Prepared.QueryTransitionControls(layout, controls, revision);
                var endpoint = new TransitionEndpointRef(layout.Edges[^1].Identity, TransitionEndpoint.Begin);
                var range = preparation.Prepared.QueryTransitionRange(layout, controls, endpoint, revision);
                var cached = preparation.Prepared.QueryTransitionRange(layout,
                    new(layout.Fingerprint, controls.Junctions, [new(edge.Identity, db == -6 ? 3 : -6)], controls.Onsets, controls.Phases, controls.Coverages), endpoint, revision);
                Check(cached.CacheHit && cached.ReachableFrames.SequenceEqual(range.ReachableFrames), "Conditional range cache depends on gain.");
                Check(range.Minimum >= edge.Current.End + delta + 1, "Conditional range ignored the adjacent edited endpoint.");
                Record(new { name = "conditional-range", range.Minimum, range.Maximum, rangePlans = range.NativePlans,
                    rangeMilliseconds = range.Milliseconds, cachedPlans = cached.NativePlans, aliases = range.Junction.Aliases.Length, validationPlans = execution.NativePlans });
            }
            SampledTimingHost.SetTransitions(part, layout, controls, revision);
        }

        if (Environment.GetEnvironmentVariable("VOCALOID_TRANSITION_FRESH") == "1")
        {
            FreshTransitionValidation.Execute(manager, directory);
            harmony.UnpatchAll(harmony.Id); application.Shutdown(); return;
        }
        if (Environment.GetEnvironmentVariable("VOCALOID_RENDER_HEALTH") == "1")
        {
            try { Rendering.RendererHealthValidation.Execute(manager, dse, database, directory); }
            finally { harmony.UnpatchAll(harmony.Id); application.Shutdown(); }
            return;
        }
        if (Environment.GetEnvironmentVariable("VOCALOID_GENG_HOST") == "1")
        {
            File.Copy(Environment.GetEnvironmentVariable("VOCALOID_GENG_PROJECT")!, Path.Combine(directory, "geng-source.vpr"), true);
            var captured = Open("geng-source");
            try
            {
                var track = (WIVSMMidiTrack)captured.GetTrack(0UL)!;
                var part = Part(captured);
                foreach (var other in track.PartsEnumerator.OfType<WIVSMMidiPart>().Where(p => (nint)p != (nint)part).ToArray())
                    Check(track.RemovePart(other), "Cannot isolate captured Part.");
                captured.InterpolatedMasterVolume(); captured.Commit(false);
                Render(captured, part, "geng-baseline");
                using var liveObserver = new WIVSMRendererObserver();
                var liveEvents = new List<string>();
                liveObserver.Completed += (_, e) => liveEvents.Add("complete");
                liveObserver.Canceled += (_, e) => liveEvents.Add("cancel:" + e.CancelReason);
                captured.AddRendererObserver(liveObserver);
                long diagnosticRevision = SampledTimingHost.Find(captured)!.Revision;
                ulong diagnosticEpoch = NativeSampledTiming.ReadStatus((nint)part).Epoch;
                AccessTools.Method(typeof(WIVSMRendererObserver), "InvokeCancelEvent").Invoke(liveObserver,
                    [nint.Zero, (nint)part, VSMRenderCancelReason.UserCanceled]);
                Check(SampledTimingHost.Find(captured)!.Revision == diagnosticRevision &&
                    NativeSampledTiming.ReadStatus((nint)part).Epoch == diagnosticEpoch,
                    "Cancellation diagnostics altered the active publication.");
                Record(new { name = "delayed-cancel-diagnostic", host = SampledTimingHost.Diagnostics(captured) });
                void PumpLive()
                {
                    var frame = new DispatcherFrame();
                    Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(() => frame.Continue = false));
                    Dispatcher.PushFrame(frame);
                }
                bool zhuang = Environment.GetEnvironmentVariable("VOCALOID_GENG_ZHUANG") == "1";
                for (int pass = 0; pass < 24; pass++)
                {
                    liveEvents.Clear();
                    using (var preparation = SampledTimingHost.PrepareTransitions(part))
                    {
                        var q = preparation.Query();
                        var edge = q.Layout.Edges.Single(e => zhuang ? e.Identity.From == "uAN" && e.Identity.To == "Sil" :
                            e.Identity.From == "@N" && e.Identity.To == "k" && e.Identity.Left?.Note.Note == 6);
                        var endpoint = new TransitionEndpointRef(edge.Identity, zhuang ? (TransitionEndpoint)(pass % 3) : TransitionEndpoint.Begin);
                        var range = System.Threading.Tasks.Task.Run(() => preparation.Prepared.QueryTransitionRange(q.Layout, q.Controls, endpoint,
                            preparation.Revision)).GetAwaiter().GetResult();
                        var controls = q.Controls.WithJunction(q.Layout, endpoint,
                            zhuang ? range.ReachableFrames[(pass * 37) % range.ReachableFrames.Length] :
                                edge.Current.Begin + (pass % 2 == 0 ? 0 : 3));
                        SampledTimingHost.CommitTransitions(preparation,
                            preparation.Prepared.QueryTransitionControls(q.Layout, controls, preparation.Revision));
                    }
                    PumpLive();
                    Thread.Sleep(pass % 8 * 2);
                    var watch = System.Diagnostics.Stopwatch.StartNew();
                    while ((!captured.IsFinishedRendering || !part.HasValidRenderedWave) && watch.ElapsedMilliseconds < 5000)
                    { while (captured.InvokeRendererObserver()) { } PumpLive(); Thread.Sleep(1); }
                    while (captured.InvokeRendererObserver()) { }
                    var nativeStatus = NativeSampledTiming.ReadStatus((nint)part);
                    Record(new { name = "geng-live-" + pass, events = liveEvents.ToArray(), finished = captured.IsFinishedRendering,
                        wave = part.HasValidRenderedWave, score = part.HasValidRenderedScore,
                        nativeStatus.Epoch, nativeStatus.Scopes, nativeStatus.CacheKeys,
                        gainFailures = NativeTransitionGain.ReadStatus((nint)part).Failures, cursor = NativeSampledTiming.PhonemeDiagnostic((nint)part),
                        host = SampledTimingHost.Diagnostics(captured) });
                    Check(part.HasValidRenderedWave && part.HasValidRenderedScore, "Live edit failed without a forced stop/restart.");
                    Check(nativeStatus.Scopes != 0 || nativeStatus.CacheKeys != 0, "New control publication was never consumed.");
                }
                captured.RemoveRendererObserver(liveObserver);
            }
            finally { captured.Close(); harmony.UnpatchAll(harmony.Id); application.Shutdown(); }
            return;
        }
        if (Environment.GetEnvironmentVariable("VOCALOID_TRANSITION_REOPEN") is { Length: > 0 } fixture)
        {
            File.Copy(fixture, Path.Combine(directory, "reopen-fixture.vpr"));
            var reopened = Open("reopen-fixture");
            try
            {
                reopened.InterpolatedMasterVolume(); reopened.Commit(false);
                Render(reopened, Part(reopened), "isolated-reopened");
            }
            finally { reopened.Close(); }
            return;
        }
        var source = manager.CreateSequence(new VSMSequenceData { SamplingRate = VSMSamplingRate._44100, MaxNumTracks = 4, MaxUndoCount = 100 })!;
        try
        {
            source.InsertTimeSig(0, new VSMTimeSigEvent(4, 4)); source.InsertTempo(VSMRelTick.Zero, 12000);
            var track = (WIVSMMidiTrack)source.InsertTrackEx(0, VSMTrackType.Midi, "transition-validation")!;
            var part = track.InsertPart(VSMAbsTick.Zero, new VSMRelTick(4800), "transition-validation")!;
            part.SetVoiceBankID("BL8CEAM5N4XN3LFK");
            var note = part.InsertNote(new VSMRelTick(960), new VSMNoteEvent(960, 60, 64), part.GetDefaultNoteExpression(),
                part.GetDefaultAiNoteExpression(), "validation", "l a", true, 4)!;
            source.InterpolatedMasterVolume(); source.Commit(false); BreathVolumeService.ResetHistory(source);
            SampledTimingHost.Attach(source, new LocalConsonantTimingBackend(manager, dse, database));
            NativeTransitionGain.Initialize(Path.Combine(Patcher.DataDir, "native", "v6patch_clock.dll"));
            SampledTimingVsmBinding.InvalidateRenderedPart(part, [note]);
            string baseline = Render(source, part, "baseline");
            if (Environment.GetEnvironmentVariable("VOCALOID_TRANSITION_COVERAGE") == "1")
            {
                part.SetVoiceBankID("BY98KLLZTDYH7YEE"); note.SetPhonemes("s\\ iE_r", true, 4); source.Commit(true);
                SampledTimingHost.SetPhonemePlan(note, new("s\\ iE_r",
                    [new(TimingAdjustment.PhonemeBoundary, 2, (float)(-120 * 256d / 44100))]));
                string timingOnly = Render(source, part, "coverage-timing-only");
                bool expressionUi = Environment.GetEnvironmentVariable("VOCALOID_TRANSITION_EXPRESSION_UI") == "1";
                int[] protectedFrames;
                using (var preparation = SampledTimingHost.PrepareTransitions(part))
                {
                    var q = preparation.Query(); protectedFrames = q.Execution.RenderAnchors.Select(a => a.To).ToArray();
                    var edge = q.Layout.Edges.Single(e => e.Identity.From == "s\\" && e.Identity.To == "iE_r");
                    if (expressionUi) ConsonantTimingOverlay.ValidateTransitionBand(q.Layout, q.Execution, note, directory);
                    var controls = expressionUi ? ConsonantTimingOverlay.ValidateDiamondPlan(q.Layout, q.Execution, q.Controls, edge.Identity, note, -.75f) :
                        q.Controls.WithCoverage(edge.Identity, TransitionSourceSides.Right).WithPhase(edge.Identity, -.75f);
                    var execution = preparation.Prepared.QueryTransitionControls(q.Layout, controls, preparation.Revision);
                    Check(execution.RenderAnchors.Select(a => a.To).SequenceEqual(protectedFrames), "Coverage moved protected anchors.");
                    SampledTimingHost.CommitTransitions(preparation, execution);
                }
                string covered = Render(source, part, "coverage-and-phase");
                Check(!EqualAudio(covered, timingOnly), "Coverage did not change short compound-vowel audio.");
                var savedData = SampledTimingHost.TransitionData(source);
                Check(savedData.Data!.Coverages.Length == 1 && savedData.Data.Phases.Length == 1, "Coverage was not stored independently of phase.");
                source.Undo(); part = Part(source); note = part.GetNote(0UL)!;
                Check(EqualAudio(Render(source, part, "coverage-undo"), timingOnly), "Shared Undo failed to restore timing-only audio.");
                source.Redo(); part = Part(source); note = part.GetNote(0UL)!;
                Check(EqualAudio(Render(source, part, "coverage-redo"), covered), "Shared Redo failed to restore coverage audio.");
                Check(SampledTimingHost.TransitionData(source).Payload.Span.SequenceEqual(savedData.Payload.Span), "Redo changed archived coverage bytes.");
                Check(Save(source, "coverage-controlled"), "Coverage VPR save failed.");
                var reopened = Open("coverage-controlled");
                try
                {
                    reopened.InterpolatedMasterVolume(); reopened.Commit(false);
                    Check(EqualAudio(Render(reopened, Part(reopened), "coverage-reopened"), covered), "Coverage save/load changed sound.");
                    Check(SampledTimingHost.TransitionData(reopened).Data!.Coverages.Length == 1, "Reopen discarded source coverage.");
                }
                finally { reopened.Close(); }
                if (expressionUi)
                {
                    using (var same = SampledTimingHost.PrepareTransitions(part))
                    {
                        var q = same.Query(); var edge = q.Layout.Edges.Single(e => e.Identity.From == "s\\" && e.Identity.To == "iE_r");
                        long revision = same.Session.Revision;
                        var returned = ConsonantTimingOverlay.ValidateDiamondPlan(q.Layout, q.Execution, q.Controls, edge.Identity, note, -.75f);
                        Check(ReferenceEquals(returned, q.Controls), "Return-to-origin changed source/progress controls.");
                        SampledTimingHost.CommitTransitions(same, same.Prepared.QueryTransitionControls(q.Layout, returned, same.Revision));
                        Check(same.Session.Revision == revision, "Return-to-origin created history or cache invalidation.");
                    }
                    foreach (string operation in new[] { "reset-diamond", "zero-progress", "source-off", "late-diamond" })
                    {
                        using (var edit = SampledTimingHost.PrepareTransitions(part))
                        {
                            var q = edit.Query(); var edge = q.Layout.Edges.Single(e => e.Identity.From == "s\\" && e.Identity.To == "iE_r");
                            var row = q.Execution.Segments.Where(s => s.To != null).ElementAt(q.Layout.Edges.IndexOf(edge));
                            var controls = operation switch
                            {
                                "reset-diamond" => TransitionExpressionControl.Reset(q.Controls, edge.Identity),
                                "zero-progress" => TransitionExpressionControl.Progress(q.Controls, row, edge.Identity, 0),
                                "source-off" => TransitionExpressionControl.Source(q.Controls, row, edge.Identity, false),
                                _ => ConsonantTimingOverlay.ValidateDiamondPlan(q.Layout, q.Execution, q.Controls, edge.Identity, note, .75f)
                            };
                            var execution = edit.Prepared.QueryTransitionControls(q.Layout, controls, edit.Revision);
                            Check(execution.RenderAnchors.SequenceEqual(q.Execution.RenderAnchors) && execution.Segments.Zip(q.Execution.Segments).All(p =>
                                (p.First.Begin, p.First.Split, p.First.End) == (p.Second.Begin, p.Second.Split, p.Second.End)), "Frontend expression edit moved timing geometry.");
                            SampledTimingHost.CommitTransitions(edit, execution);
                        }
                        string audio = Render(source, part, "ui-" + operation);
                        Check(operation == "reset-diamond" ? EqualAudio(audio, timingOnly) : !EqualAudio(audio, covered), "Frontend expression/reset audio check failed.");
                        var data = SampledTimingHost.TransitionData(source).Data!;
                        if (operation == "zero-progress") Check(data.Phases.IsEmpty && data.Coverages.Length == 1, "Zero progress lost complete source.");
                        if (operation == "source-off") Check(data.Coverages.IsEmpty && data.Phases.Length == 1, "Complete-source switch discarded progress.");
                        source.Undo(); part = Part(source); note = part.GetNote(0UL)!;
                        Check(EqualAudio(Render(source, part, "ui-undo-" + operation), covered), "Frontend expression Undo did not restore sound.");
                        Check(SampledTimingHost.TransitionData(source).Payload.Span.SequenceEqual(savedData.Payload.Span), "Frontend Undo did not restore archived controls.");
                    }
                    Record(new { name = "expression-ui-acceptance", fixedTiming = true, productionDiamondPlan = true, reset = true,
                        zeroProgressWithFullSource = true, sourceSwitchKeepsPhase = true, returnedDragIsNoOp = true, sharedUndo = true });
                }
                // Native edits and phoneme timing must rebuild the entire Part
                // snapshot with coverage retained, rather than reuse stale PCM.
                note.NoteVelocity = 100; source.Commit(true);
                Check(!EqualAudio(Render(source, part, "coverage-velocity"), covered), "VEL did not rerender with coverage.");
                source.Undo(); part = Part(source); note = part.GetNote(0UL)!;
                Check(EqualAudio(Render(source, part, "coverage-undo-velocity"), covered), "VEL Undo lost coverage.");
                SampledTimingHost.SetPhonemePlan(note, new("s\\ iE_r",
                    [new(TimingAdjustment.PhonemeBoundary, 2, (float)(-132 * 256d / 44100))]));
                Render(source, part, "coverage-more-compression");
                Check(SampledTimingHost.TransitionData(source).Data!.Coverages.Length == 1, "Timing edit erased source coverage.");
                source.Undo(); part = Part(source); note = part.GetNote(0UL)!;
                Check(EqualAudio(Render(source, part, "coverage-undo-compression"), covered), "Timing Undo failed to restore source progression.");
                using (var preparation = SampledTimingHost.PrepareTransitions(part))
                {
                    var q = preparation.Query(); var edge = q.Layout.Edges.Single(e => e.Identity.From == "s\\" && e.Identity.To == "iE_r");
                    var controls = new TransitionControlPlan(q.Layout.Fingerprint, q.Controls.Junctions, [new(edge.Identity, -6)],
                        q.Controls.Onsets, q.Controls.Phases, q.Controls.Coverages);
                    SampledTimingHost.CommitTransitions(preparation, preparation.Prepared.QueryTransitionControls(q.Layout, controls, preparation.Revision));
                }
                string gainCombined = Render(source, part, "coverage-gain");
                Check(!EqualAudio(gainCombined, covered), "Gain vanished from source-coverage composition.");
                var coverageExtraTrack = (WIVSMMidiTrack)source.InsertTrackEx(1, VSMTrackType.Midi, "coverage-coverageParallel")!;
                var coverageExtra = coverageExtraTrack.InsertPart(VSMAbsTick.Zero, new VSMRelTick(4800), "coverage-coverageParallel")!;
                coverageExtra.SetVoiceBankID(part.VoiceBankID);
                var coverageExtraNote = coverageExtra.InsertNote(new VSMRelTick(960), new VSMNoteEvent(960, 60, 64), coverageExtra.GetDefaultNoteExpression(),
                    coverageExtra.GetDefaultAiNoteExpression(), "validation", "s\\ iE_r", true, 4)!;
                source.Commit(true);
                using (var preparation = SampledTimingHost.PrepareTransitions(coverageExtra))
                {
                    var q = preparation.Query(); var edge = q.Layout.Edges.Single(e => e.Identity.From == "s\\" && e.Identity.To == "iE_r");
                    SampledTimingHost.CommitTransitions(preparation, preparation.Prepared.QueryTransitionControls(q.Layout,
                        q.Controls.WithCoverage(edge.Identity, TransitionSourceSides.Both).WithPhase(edge.Identity, .75f), preparation.Revision));
                }
                // Uncached input on both Parts so scopes must really overlap.
                note.NoteVelocity = 66; coverageExtraNote.NoteVelocity = 68; source.Commit(true);
                Render(source, part, "coverage-parallel", [part, coverageExtra]);
                string[] coverageParallel = [Pcm(part.WaveFilePath), Pcm(coverageExtra.WaveFilePath)];
                string secondPath = Path.Combine(directory, "coverage-coverageParallel-second.wav");
                File.Copy(coverageExtra.WaveFilePath, secondPath, true); pcmPaths.TryAdd(coverageParallel[1], secondPath);
                Check(NativeSampledTiming.PeakScopes > 1, "Coverage Part render scopes did not overlap.");
                SampledTimingVsmBinding.InvalidateRenderedPart(part, [note]);
                Check(EqualAudio(Render(source, part, "coverage-sequential-first"), coverageParallel[0]), "Parallel coverage leaked into first Part.");
                SampledTimingVsmBinding.InvalidateRenderedPart(coverageExtra, [coverageExtraNote]);
                Check(EqualAudio(Render(source, coverageExtra, "coverage-sequential-second"), coverageParallel[1]), "Parallel coverage leaked into second Part.");
                Check(SampledTimingHost.TransitionData(source).Data!.Coverages.Length == 2, "Second Part overwrote first Part source coverage.");
                Check(Save(source, "coverage-cache"), "Coverage cache fixture save failed.");
                source.Close();
                var coverageCached = Open("coverage-cache");
                try
                {
                    coverageCached.InterpolatedMasterVolume(); coverageCached.Commit(false);
                    var first = Part(coverageCached); var second = (WIVSMMidiPart)((WIVSMMidiTrack)coverageCached.GetTrack(1UL)!).GetPart(0UL)!;
                    Check(EqualAudio(Render(coverageCached, first, "coverage-native-cache", [first, second]), coverageParallel[0]), "Coverage native cache changed audio.");
                    Check(Pcm(second.WaveFilePath) == coverageParallel[1], "Second Part coverageCached coverage changed audio.");
                    var status = NativeSampledTiming.ReadStatus((nint)first);
                    Check(status.Scopes == 0 && status.CacheKeys > 0, "Coverage cache was not reused after source close.");
                }
                finally { coverageCached.Close(); }
                Record(new { name = "coverage-acceptance", fixedTiming = true, sharedUndo = true, saveReopen = true,
                    velocityAndTimingInvalidation = true, gainComposition = true, coverageParallel = true, cacheReload = true });
                return;
            }
            if (Environment.GetEnvironmentVariable("VOCALOID_TRANSITION_COMPRESSION") == "1")
            {
                note.SetPhonemes("a", true, 4); source.Commit(true);
                foreach (string mode in new[] { "plain", "phase", "endpoints", "legacy", "all" })
                {
                    SampledTimingHost.SetPhonemePlan(note, null);
                    using (var reset = SampledTimingHost.PrepareTransitions(part))
                    {
                        var q = reset.Query();
                        SampledTimingHost.CommitTransitions(reset, reset.Prepared.QueryTransitionControls(q.Layout,
                            new(q.Layout.Fingerprint), reset.Revision));
                    }
                    using (var preparation = SampledTimingHost.PrepareTransitions(part))
                    {
                        var q = preparation.Query(); var controls = q.Controls;
                        if (mode is "endpoints" or "all")
                        {
                            foreach (var endpoint in new[] { new TransitionEndpointRef(q.Layout.Edges[0].Identity, TransitionEndpoint.Begin),
                                new TransitionEndpointRef(q.Layout.Edges[^1].Identity, TransitionEndpoint.End) })
                            {
                                var range = preparation.Prepared.QueryTransitionRange(q.Layout, controls, endpoint, preparation.Revision);
                                int goal = endpoint.Endpoint == TransitionEndpoint.Begin ? range.Junction.CurrentFrame - 24 : range.Junction.CurrentFrame + 24;
                                controls = controls.WithJunction(q.Layout, endpoint, range.ReachableFrames.OrderBy(f => Math.Abs(f - goal)).First());
                            }
                        }
                        if (mode is "phase" or "all")
                            foreach (var edge in q.Layout.Edges.Where(e => TransitionPhaseMapping.CanEdit(e.Current))) controls = controls.WithPhase(edge.Identity, -.75f);
                        if (mode is "legacy" or "all") controls = controls.WithOnset(q.Layout.Edges.First(e => e.Identity.From == "Sil").Identity, -12);
                        SampledTimingHost.CommitTransitions(preparation, preparation.Prepared.QueryTransitionControls(q.Layout, controls, preparation.Revision));
                    }
                    int[] compressionOriginal;
                    using (var p = SampledTimingHost.PrepareTransitions(part)) compressionOriginal = p.Query().Layout.Notes.Single().Original.ToArray();
                    if (mode == "legacy")
                    {
                        var session = SampledTimingHost.Find(source)!;
                        var data = session.Transitions.Snapshot.Data!;
                        session.SetTransitions(data with { Onsets = data.Onsets.Select(o => o with { TimingScale = null }).ToImmutableArray() });
                    }
                    string? uncompressed = null, compressed = null;
                    string originalData = SampledTimingHost.TransitionData(source).Fingerprint;
                    foreach (double ratio in new[] { 1d, .5, .15, .03 })
                    {
                        int delta = (int)Math.Round((compressionOriginal[^1] - compressionOriginal[0]) * (1 - ratio) / 2);
                        var plan = new SampledTimingPlan("a", delta == 0 ? [] :
                            [new(TimingAdjustment.PhonemeBoundary, 0, (float)(delta * 256d / 44100)),
                             new(TimingAdjustment.PhonemeBoundary, 1, (float)(-delta * 256d / 44100))]);
                        string name = $"compression-{mode}-{ratio:0.00}";
                        var planning = System.Diagnostics.Stopwatch.StartNew();
                        {
                            long[] candidate;
                            var session = SampledTimingHost.Find(source)!;
                            using (var phonePrep = session.Prepare(note)) candidate = phonePrep.QueryPhonemes(plan, session.Revision).Result.Boundaries;
                            SampledTimingHost.SetPhonemePlan(note, delta == 0 ? null : plan);
                            using (var transitionPrep = SampledTimingHost.PrepareTransitions(part))
                            {
                                var q = transitionPrep.Query();
                                int[] protectedFrames = q.Layout.Notes.Single().Original.Select(f => q.Execution.RenderAnchors.Single(a => a.From == f).To).ToArray();
                                Check(protectedFrames[^1] - protectedFrames[0] == compressionOriginal[^1] - compressionOriginal[0] - 2 * delta,
                                    "Transition composition changed compressed phoneme boundaries.");
                                Record(new { name = name + "-plan", mode, ratio, candidate, protectedFrames,
                                    planningMilliseconds = planning.Elapsed.TotalMilliseconds,
                                    rows = q.Execution.Segments.Where(s => s.To != null).Select(s => new { s.From, s.To, s.Begin, s.Split, s.End }).ToArray() });
                            }
                            string hash = Render(source, part, name);
                            if (ratio == 1) uncompressed = hash;
                            if (ratio == .5)
                            {
                                source.Undo(); part = Part(source); note = part.GetNote(0UL)!;
                                Check(EqualAudio(Render(source, part, name + "-undo"), uncompressed!), "Compression Undo changed reference audio.");
                                Check(SampledTimingHost.TransitionData(source).Fingerprint == originalData, "Compression Undo lost original transition bytes.");
                                source.Redo(); part = Part(source); note = part.GetNote(0UL)!;
                                Check(EqualAudio(Render(source, part, name + "-redo"), hash), "Compression Redo changed audio.");
                            }
                            if (ratio == .03) compressed = hash;
                        }
                    }
                    Check(Save(source, "compressed-" + mode), "Compressed project save failed.");
                    var reopened = Open("compressed-" + mode);
                    try
                    {
                        reopened.InterpolatedMasterVolume(); reopened.Commit(false);
                        Check(EqualAudio(Render(reopened, Part(reopened), "compressed-" + mode + "-reopened"), compressed!), "Compressed reopen changed audio.");
                    }
                    finally { reopened.Close(); }
                    var raw = SampledTimingHost.TransitionData(source).Data!;
                    using (var edit = SampledTimingHost.PrepareTransitions(part))
                    {
                        var q = edit.Query(); var edge = q.Layout.Edges.First(e => TransitionPhaseMapping.CanEdit(e.Current));
                        SampledTimingHost.CommitTransitions(edit, edit.Prepared.QueryTransitionControls(q.Layout,
                            q.Controls.WithPhase(edge.Identity, .5f), edit.Revision));
                    }
                    Check(SampledTimingHost.TransitionData(source).Data!.Onsets.Select(o => o.OffsetSeconds).SequenceEqual(raw.Onsets.Select(o => o.OffsetSeconds)),
                        "Unrelated phase edit erased a sub-frame onset reference.");
                    source.Undo(); part = Part(source); note = part.GetNote(0UL)!;
                    SampledTimingHost.SetPhonemePlan(note, null);
                    Check(EqualAudio(Render(source, part, "compression-" + mode + "-restored"), uncompressed!), "Expansion failed to restore original transition sound.");
                }
                Record(new { name = "compression-acceptance", compressed = true, sharedUndo = true, saveReopen = true, referenceRestored = true,
                    scheduleReuses = NativeSampledTiming.ScheduleReuses }); return;
            }
            if (Environment.GetEnvironmentVariable("VOCALOID_TRANSITION_PRONUNCIATION") == "1")
            {
                var other = part.InsertNote(new VSMRelTick(2880), new VSMNoteEvent(960, 64, 64), part.GetDefaultNoteExpression(),
                    part.GetDefaultAiNoteExpression(), "validation", "l a", true, 4)!;
                var siblingTrack = (WIVSMMidiTrack)source.InsertTrackEx(1, VSMTrackType.Midi, "pronunciation-parallel")!;
                var sibling = siblingTrack.InsertPart(VSMAbsTick.Zero, new VSMRelTick(4800), "pronunciation-parallel")!;
                sibling.SetVoiceBankID(part.VoiceBankID);
                var siblingNote = sibling.InsertNote(new VSMRelTick(960), new VSMNoteEvent(960, 60, 64), sibling.GetDefaultNoteExpression(),
                    sibling.GetDefaultAiNoteExpression(), "validation", "l a", true, 4)!;
                source.Commit(true); string plain = Render(source, part, "pronunciation-plain", [part, sibling]);
                if (Environment.GetEnvironmentVariable("VOCALOID_TRANSITION_PRONUNCIATION_PLAIN") == "1")
                {
                    note.SetPhonemes("l o", true, 4); source.Commit(true);
                    plain = Render(source, part, "plain-pronunciation");
                    other.NoteVelocity = 19; source.Commit(true);
                    string vel = Render(source, part, "plain-velocity");
                    source.Undo(); part = Part(source);
                    string undone = Render(source, part, "plain-undo-velocity");
                    source.Redo(); part = Part(source);
                    string redone = Render(source, part, "plain-redo-velocity");
                    Record(new { name = "plain-diagnostic", plain, vel, undone, redone });
                    Check(EqualAudio(undone, plain) && EqualAudio(redone, vel) && !EqualAudio(plain, vel), "Plain native velocity history changed PCM.");
                    return;
                }
                Edit(part, -6, 4); string controlled = Render(source, part, "pronunciation-controlled");
                Edit(sibling, -9, 5); string extraControlled = Render(source, sibling, "pronunciation-sibling-controlled");
                var originalData = SampledTimingHost.TransitionData(source);
                Guid originalId = originalData.Data!.Notes.Single(n => n.Address.Track == 0 && n.Address.Note == 0).Id;
                if (Environment.GetEnvironmentVariable("VOCALOID_TRANSITION_CONTEXT_ONLY") == "1")
                {
                    part.InsertNote(new VSMRelTick(480), new VSMNoteEvent(480, 60, 64), part.GetDefaultNoteExpression(),
                        part.GetDefaultAiNoteExpression(), "validation", "m a", true, 4);
                    source.Commit(true); Render(source, part, "context-only"); return;
                }
                Check(note.SetPhonemes("l o", true, 4), "Cannot change fixture pronunciation."); source.Commit(true);
                string changed = Render(source, part, "pronunciation-changed");
                Check(!EqualAudio(changed, controlled) && part.HasValidRenderedScore && part.HasValidRenderedWave, "Pronunciation edit retained stale render output.");
                using (var preparation = SampledTimingHost.PrepareTransitions(part))
                {
                    var q = preparation.Query();
                    Check(q.Controls.Phases.Length != 0, "Pronunciation edit discarded unrelated same-Part phase controls.");
                    Check(q.Controls.Phases.All(p => p.Edge.Left?.Note.Note != 0 && p.Edge.Right?.Note.Note != 0), "Old phase rebound to a different pronunciation.");
                }
                Check(SampledTimingHost.TransitionData(source).Data!.Notes.Any(n => n.Id == originalId && n.Address.Topology == "l a"), "Pronunciation edit destroyed native Undo data.");
                Check(Save(source, "pronunciation-changed"), "Changed pronunciation save failed.");
                Check(TransitionProjectArchive.Read(Path.Combine(directory, "pronunciation-changed.vpr")).Data!.Notes.All(n => n.Address.Topology != "l a" || n.Address.Track != 0 || n.Address.Note != 0), "Save attached old controls to changed pronunciation.");
                other.NoteVelocity = 19; siblingNote.NoteVelocity = 27; source.Commit(true);
                string velocity = Render(source, part, "pronunciation-velocity", [part, sibling]);
                Check(!EqualAudio(velocity, changed) && Pcm(sibling.WaveFilePath) != extraControlled, "Pronunciation edit blocked later same-Part or other-Part rendering.");
                Check(NativeTransitionGain.ReadStatus((nint)sibling).Applied > 0, "Other Part lost its controls after pronunciation edit.");
                part.MoveNote(new VSMRelTick(1200), note); note.SetNoteNumber(62); source.Commit(true);
                Render(source, part, "pronunciation-moved");
                Check(part.HasValidRenderedScore && part.HasValidRenderedWave && note.GetPhonemePositions()?.Count == 3,
                    "Moved changed note did not obtain a valid new score and phoneme positions.");
                source.Undo(); part = Part(source); note = part.GetNote(0UL)!;
                Check(EqualAudio(Render(source, part, "pronunciation-undo-move"), velocity), "Undo move after pronunciation edit changed PCM.");
                source.Undo(); part = Part(source); note = part.GetNote(0UL)!;
                Check(EqualAudio(Render(source, part, "pronunciation-undo-velocity", [part, sibling]), changed), "Undo VEL after pronunciation edit changed PCM.");
                source.Undo(); part = Part(source); note = part.GetNote(0UL)!;
                Check(EqualAudio(Render(source, part, "pronunciation-undo"), controlled), "Undo pronunciation did not recover original transition PCM.");
                Check(SampledTimingHost.TransitionData(source).Fingerprint == originalData.Fingerprint, "Undo pronunciation changed stored controls.");
                source.Redo(); part = Part(source); note = part.GetNote(0UL)!;
                string redo = Render(source, part, "pronunciation-redo");
                Check(EqualAudio(redo, changed), "Redo pronunciation reapplied obsolete controls.");
                Edit(part, -4, 2); Render(source, part, "pronunciation-new-controls");
                Check(SampledTimingHost.TransitionData(source).Data!.Notes.Any(n => n.Address.Track == 0 && n.Address.Note == 0 && n.Id != originalId), "New pronunciation reused the old transition target identity.");
                source.Undo(); source.Undo(); part = Part(source); note = part.GetNote(0UL)!;
                Check(EqualAudio(Render(source, part, "pronunciation-undo-new-controls"), controlled), "New transition edit prevented native pronunciation Undo.");
                var neighbour = part.InsertNote(new VSMRelTick(480), new VSMNoteEvent(480, 60, 64), part.GetDefaultNoteExpression(),
                    part.GetDefaultAiNoteExpression(), "validation", "m a", true, 4)!;
                source.Commit(true); Render(source, part, "pronunciation-context-inserted");
                using (var preparation = SampledTimingHost.PrepareTransitions(part))
                {
                    var q = preparation.Query();
                    Check(q.Layout.Edges.Any(e => e.Identity.From == "a" && e.Identity.To == "l"), "Fixture did not replace the old silence context.");
                    Check(q.Controls.Phases.All(p => p.Edge.From != "Sil" || p.Edge.Right?.Note.Note != 1), "Silence control rebound to a new neighbouring transition.");
                    Check(q.Controls.Phases.Any(p => p.Edge.Left?.Note.Note == 2 || p.Edge.Right?.Note.Note == 2), "Context edit removed unrelated note controls.");
                }
                source.Undo(); part = Part(source);
                Check(EqualAudio(Render(source, part, "pronunciation-context-undo"), controlled), "Undo context replacement did not restore original PCM.");
                var reopened = Open("pronunciation-changed");
                try
                {
                    reopened.InterpolatedMasterVolume(); reopened.Commit(false);
                    Check(EqualAudio(Render(reopened, Part(reopened), "pronunciation-reopened"), changed), "Changed pronunciation reopen altered PCM.");
                }
                finally { reopened.Close(); }
                Record(new { name = "pronunciation-acceptance", changed = true, samePart = true, otherPart = true, saved = true,
                    movedScore = true, sharedUndoRedo = true, newControlIdentity = true, neighbourContext = true });
                return;
            }
            if (Environment.GetEnvironmentVariable("VOCALOID_TRANSITION_PHASE") == "1")
            {
                using (var phasePrep = SampledTimingHost.PrepareTransitions(part))
                {
                    var q = phasePrep.Query(); var targetEdge = q.Layout.Edges.First(e => TransitionPhaseMapping.CanEdit(e.Current));
                    var phase = phasePrep.Prepared.QueryTransitionControls(q.Layout, q.Controls.WithPhase(targetEdge.Identity, -.5f), phasePrep.Revision);
                    Check(phase.RenderAnchors.SequenceEqual(q.Execution.RenderAnchors), "Phase-only edit moved timing anchors.");
                    Check(phase.Segments.Select(s => (s.Begin,s.Split,s.End)).SequenceEqual(q.Execution.Segments.Select(s => (s.Begin,s.Split,s.End))), "Phase-only edit changed allocation.");
                    SampledTimingHost.CommitTransitions(phasePrep, phase);
                }
                string only = Render(source, part, "phase-only");
                var data = SampledTimingHost.TransitionData(source).Data!;
                Check(data.Phases.Length == 1 && data.Junctions.IsEmpty && data.Onsets.IsEmpty && data.Gains.IsEmpty, "Phase-only data acquired implicit timing/gain edits.");
                Check(only != baseline, "Phase-only control did not affect audio.");
                using (var same = SampledTimingHost.PrepareTransitions(part))
                {
                    var q = same.Query(); long revision = same.Session.Revision;
                    SampledTimingHost.CommitTransitions(same, q.Execution);
                    Check(same.Session.Revision == revision, "Identical phase edit created history/invalidation.");
                }
                source.Undo(); Check(Render(source, part, "phase-only-undo") == baseline, "Phase-only Undo failed.");
                source.Redo(); Check(Render(source, part, "phase-only-redo") == only, "Phase-only Redo changed PCM.");
                source.Undo(); Check(Render(source, part, "phase-only-reset") == baseline, "Phase-only reset failed.");
            }
            using (var display = SampledTimingHost.PrepareTransitions(part))
            {
                var ready = display.Query();
                ConsonantTimingOverlay.ValidateTransitionBand(ready.Layout, ready.Execution, note, directory);
            }
            if (Environment.GetEnvironmentVariable("VOCALOID_TRANSITION_UI_ONLY") == "1")
            { Record(new { name = "ui-acceptance", phaseDrag = true, fixedRange = true, rendered = true }); return; }
            Edit(part, -6, 4); string a = Render(source, part, "transition-a"); Check(a != baseline, "Transition controls did not alter PCM.");
            Guid id = SampledTimingHost.TransitionData(source).Data!.Notes.Single().Id;
            SampledTimingHost.SetPhonemePlan(note, new("l a", [new(TimingAdjustment.NoteCoefficient, -1, .375f),
                new(TimingAdjustment.PhonemeBoundary, 2, (float)(2 * 256d / 44100))]));
            string combined = Render(source, part, "timing-combined");
            part.MoveNote(new VSMRelTick(1200), note); note.SetNoteNumber(62); source.Commit(true);
            Check(SampledTimingHost.TransitionData(source).Data!.Notes.Single().Id == id, "Native move replaced stable transition note ID.");
            string moved = Render(source, part, "native-moved");
            Edit(part, 3, 5); string b = Render(source, part, "transition-b");
            source.Undo(); part = Part(source); note = part.GetNote(0UL)!; Check(Render(source, part, "undo-transition") == moved, "Shared transition Undo crossed native edit.");
            source.Undo(); part = Part(source); note = part.GetNote(0UL)!; Check(Render(source, part, "undo-native") == combined, "Native Undo lost transition identity.");
            source.Undo(); Check(Render(source, part, "undo-timing") == a, "Timing Undo lost transition controls.");
            source.Undo(); Check(Render(source, part, "undo-all") == baseline, "Transition Undo did not restore native PCM.");
            foreach (var expected in new[] { a, combined, moved, b })
            { source.Redo(); part = Part(source); note = part.GetNote(0UL)!; Check(Render(source, part, "redo-" + states.Count) == expected, "Shared Redo changed PCM."); }
            Check(Save(source, "controlled"), "Transition project save failed.");
            string fingerprint = SampledTimingHost.TransitionData(source).Fingerprint;
            Check(TransitionProjectArchive.Read(Path.Combine(directory, "controlled.vpr")).Fingerprint == fingerprint, "VPR lost transition controls.");
            Edit(part, -9, 6); Render(source, part, "after-save");
            byte[] original = File.ReadAllBytes(Path.Combine(directory, "controlled.vpr")); _failSave = true;
            try { Check(!Save(source, "controlled"), "Failed transition write reported save success."); }
            finally { _failSave = false; }
            Check(original.SequenceEqual(File.ReadAllBytes(Path.Combine(directory, "controlled.vpr"))) && SampledTimingHost.IsDirty(source), "Failed save changed old VPR or cleared dirty state.");
            source.Undo(); part = Part(source); note = part.GetNote(0UL)!; Check(Render(source, part, "undo-after-save") == b, "Undo failed after save rollback.");
            var loaded = Open("controlled");
            try
            {
                Check(SampledTimingHost.Find(loaded)?.HasBackend != true, "VPR load allocated a private engine.");
                loaded.InterpolatedMasterVolume(); loaded.Commit(false);
                Check(Render(loaded, Part(loaded), "reopened") == b, "VPR reopen changed transition PCM.");
                Check(Save(loaded, "roundtrip"), "Transition project roundtrip failed.");
                Check(TransitionProjectArchive.Read(Path.Combine(directory, "roundtrip.vpr")).Fingerprint == fingerprint, "Transition archive changed on roundtrip.");
            }
            finally { loaded.Close(); }
            if (Environment.GetEnvironmentVariable("VOCALOID_TRANSITION_PHASE") == "1")
            {
                File.Copy(Path.Combine(directory, "controlled.vpr"), Path.Combine(directory, "legacy.vpr"));
                var oldJson = System.Text.Json.Nodes.JsonNode.Parse(TransitionProjectArchive.Read(Path.Combine(directory, "controlled.vpr")).Payload.Span)!;
                oldJson.AsObject().Remove("Phases");
                var oldArchive = TransitionArchiveData.Parse(Encoding.UTF8.GetBytes(" \n" + oldJson.ToJsonString() + "\n"));
                TransitionProjectArchive.Write(Path.Combine(directory, "legacy.vpr"), oldArchive);
                var legacy = Open("legacy");
                try
                {
                    Check(SampledTimingHost.Find(legacy)?.HasBackend != true, "Legacy load required a backend.");
                    Check(Save(legacy, "legacy-roundtrip"), "Legacy save failed.");
                    Check(TransitionProjectArchive.Read(Path.Combine(directory, "legacy-roundtrip.vpr")).Payload.Span.SequenceEqual(oldArchive.Payload.Span), "Legacy save added/reinterpreted phase data.");
                    legacy.InterpolatedMasterVolume(); legacy.Commit(false); var lp = Part(legacy);
                    string oldPcm = Render(legacy, lp, "legacy-no-phase");
                    using (var same = SampledTimingHost.PrepareTransitions(lp))
                    {
                        var q = same.Query(); long revision = same.Session.Revision;
                        legacy.StartAsyncRendering(); Check(legacy.CanAsyncRendering(), "Legacy async service did not start.");
                        SampledTimingHost.CommitTransitions(same, q.Execution);
                        Check(legacy.CanAsyncRendering() && same.Session.Revision == revision, "Legacy no-op stopped rendering or created history.");
                        legacy.StopAsyncRendering();
                        var targetEdge = q.Layout.Edges.First(e => TransitionPhaseMapping.CanEdit(e.Current));
                        var phase = same.Prepared.QueryTransitionControls(q.Layout, q.Controls.WithPhase(targetEdge.Identity, .5f), same.Revision);
                        Check(phase.RenderAnchors.SequenceEqual(q.Execution.RenderAnchors), "Legacy phase edit moved its existing range.");
                        SampledTimingHost.CommitTransitions(same, phase);
                    }
                    Check(Render(legacy, lp, "legacy-with-phase") != oldPcm, "Legacy phase edit did not affect audio.");
                    legacy.Undo(); Check(Render(legacy, lp, "legacy-phase-undo") == oldPcm, "Legacy phase Undo changed old audio.");
                    Check(SampledTimingHost.TransitionData(legacy).Payload.Span.SequenceEqual(oldArchive.Payload.Span), "Legacy Undo lost original bytes.");
                }
                finally { legacy.Close(); }
            }
            var copy = manager.DuplicateSequence(source, new VSMSequenceData { SamplingRate = VSMSamplingRate._44100, MaxNumTracks = 4, MaxUndoCount = 100 })!;
            try
            {
                copy.InterpolatedMasterVolume(); copy.Commit(false); var cp = Part(copy);
                SampledTimingVsmBinding.InvalidateRenderedPart(cp, cp.NotesEnumerator);
                Check(Render(copy, cp, "sequence-copy") == b, "Sequence copy lost transition routing.");
                Check(Save(copy, "copy"), "Sequence copy failed save.");
                Check(TransitionProjectArchive.Read(Path.Combine(directory, "copy.vpr")).Fingerprint == fingerprint, "Sequence copy lost transition archive.");
            }
            finally { copy.Close(); }
            DurationSelectionHost.Set(note, new("l a", .5, DurationCandidatePolicy.ExpandedExperimental));
            string duration = Render(source, part, "duration-combined"); Check(duration != b, "Independent duration disappeared from unified snapshot.");
            source.Undo(); part = Part(source); note = part.GetNote(0UL)!;
            Check(Render(source, part, "undo-duration") == b, "Duration Undo changed transition/timing state.");
            // Delete/reinsert must not attach controls to a new same-name note.
            part.RemoveNote(note); source.Commit(true); Pump();
            Check(!SampledTimingHost.TransitionData(source).Data!.Notes.IsEmpty, "Delete destroyed transition undo data.");
            Check(Save(source, "deleted"), "Deleted-note project could not save.");
            Check(!TransitionProjectArchive.Read(Path.Combine(directory, "deleted.vpr")).HasData, "Deleted transition controls serialized against a vacant locator.");
            source.Undo(); part = Part(source); note = part.GetNote(0UL)!;
            Check(Render(source, part, "undo-delete") == b, "Delete Undo lost transition PCM.");
            part.RemoveNote(note); source.Commit(true);
            note = part.InsertNote(new VSMRelTick(1200), new VSMNoteEvent(960, 62, 64), part.GetDefaultNoteExpression(),
                part.GetDefaultAiNoteExpression(), "validation", "l a", true, 4)!;
            source.Commit(true); Edit(part, -4, 3); Render(source, part, "same-locator-new-note");
            Check(SampledTimingHost.TransitionData(source).Data!.Notes.Single().Id != id, "New note reused a deleted note's transition identity.");
            Check(NativeTransitionGain.ReadStatus((nint)part).Applied > 0, "New note's transition control was silently detached.");
            source.Undo(); source.Undo(); source.Undo(); part = Part(source); note = part.GetNote(0UL)!;
            Check(Render(source, part, "undo-replacement") == b && SampledTimingHost.TransitionData(source).Data!.Notes.Single().Id == id,
                "Undo replacement did not restore the original transition identity and PCM.");
            // Unknown schema must open/copy/save as opaque data without planning.
            File.Copy(Path.Combine(directory, "controlled.vpr"), Path.Combine(directory, "future.vpr"));
            var future = TransitionArchiveData.Parse(Encoding.UTF8.GetBytes("{\"Version\":99,\"Future\":{\"keep\":true}}"));
            TransitionProjectArchive.Write(Path.Combine(directory, "future.vpr"), future);
            var unknown = Open("future");
            try
            {
                Check(SampledTimingHost.Find(unknown)?.HasBackend != true && Save(unknown, "future-saved"), "Future controls required backend to open/save.");
                Check(TransitionProjectArchive.Read(Path.Combine(directory, "future-saved.vpr")).Payload.Span.SequenceEqual(future.Payload.Span), "Future controls were rewritten.");
                bool rejected = false; try { SampledTimingHost.BeforeRendering(unknown); } catch (NotSupportedException) { rejected = true; }
                Check(rejected, "Future controls silently rendered without execution.");
            }
            finally { unknown.Close(); }
            // A separate controlled Part must retain its native cache when its
            // neighbor changes, and both must route correctly in parallel.
            var extraTrack = (WIVSMMidiTrack)source.InsertTrackEx(source.NumTrack, VSMTrackType.Midi, "parallel")!;
            var extra = extraTrack.InsertPart(VSMAbsTick.Zero, new VSMRelTick(4800), "parallel")!;
            extra.SetVoiceBankID(part.VoiceBankID);
            var extraNote = extra.InsertNote(new VSMRelTick(960), new VSMNoteEvent(960, 60, 64), extra.GetDefaultNoteExpression(),
                extra.GetDefaultAiNoteExpression(), "validation", "l a", true, 4)!;
            source.Commit(true); Edit(extra, -12, 8); Render(source, extra, "extra-part");
            string untouched = Pcm(part.WaveFilePath);
            extraNote.NoteVelocity = 17; source.Commit(true); Render(source, extra, "extra-velocity");
            Check(Pcm(part.WaveFilePath) == untouched && NativeSampledTiming.ReadStatus((nint)part).Scopes == 0,
                "Transition edit invalidated an unrelated Part.");
            note.NoteVelocity = 65; extraNote.NoteVelocity = 66; source.Commit(true);
            Render(source, part, "parallel", [part, extra]);
            string[] parallel = [Pcm(part.WaveFilePath), Pcm(extra.WaveFilePath)];
            Check(NativeSampledTiming.PeakScopes > 1, "Part render scopes did not overlap.");
            SampledTimingVsmBinding.InvalidateRenderedPart(part, [note]);
            Check(Render(source, part, "sequential-first") == parallel[0], "Parallel transition PCM differs for first Part.");
            SampledTimingVsmBinding.InvalidateRenderedPart(extra, [extraNote]);
            Check(Render(source, extra, "sequential-second") == parallel[1], "Parallel transition PCM differs for second Part.");
            Check(Save(source, "cache-final"), "Cannot save final cache fixture.");
            source.Close();
            var cached = Open("cache-final");
            try
            {
                cached.InterpolatedMasterVolume(); cached.Commit(false);
                var cachedFirst = Part(cached);
                var cachedSecond = (WIVSMMidiPart)((WIVSMMidiTrack)cached.GetTrack(1UL)!).GetPart(0UL)!;
                Check(Render(cached, cachedFirst, "native-cache-reopen", [cachedFirst, cachedSecond]) == parallel[0], "Native cached project PCM changed.");
                var status = NativeSampledTiming.ReadStatus((nint)cachedFirst);
                Check(status.Scopes == 0 && status.CacheKeys > 0, "Native transition cache was not reused after source Close.");
                Check(Pcm(cachedSecond.WaveFilePath) == parallel[1], "Second Part cached PCM changed.");
            }
            finally { cached.Close(); }
            var shared = manager.CreateSequence(new VSMSequenceData { SamplingRate = VSMSamplingRate._44100, MaxNumTracks = 4, MaxUndoCount = 100 })!;
            try
            {
                shared.InsertTimeSig(0, new VSMTimeSigEvent(4, 4)); shared.InsertTempo(VSMRelTick.Zero, 12000);
                var st = (WIVSMMidiTrack)shared.InsertTrackEx(0, VSMTrackType.Midi, "shared-validation")!;
                var sp = st.InsertPart(VSMAbsTick.Zero, new VSMRelTick(3840), "shared-validation")!;
                sp.SetVoiceBankID("BCNFCY43LB2LZCD4");
                var sn = sp.InsertNote(new VSMRelTick(960), new VSMNoteEvent(960, 60, 64), sp.GetDefaultNoteExpression(),
                    sp.GetDefaultAiNoteExpression(), "validation", "n n#2 a", true, 0)!;
                shared.InterpolatedMasterVolume(); shared.Commit(false); BreathVolumeService.ResetHistory(shared);
                string plain = Render(shared, sp, "shared-baseline");
                using (var preparation = SampledTimingHost.PrepareTransitions(sp))
                {
                    var q = preparation.Query();
                    var joint = q.Layout.Junctions.First(j => j.Aliases.Select(a => a.Edge).Distinct().Count() > 1);
                    var range = preparation.Prepared.QueryTransitionRange(q.Layout, q.Controls, joint.Aliases[0], preparation.Revision);
                    var sides = joint.Aliases.Select(a => a.Edge).Distinct().ToArray();
                    int frame = range.ReachableFrames.OrderBy(f => f == joint.CurrentFrame ? 1 : 0).ThenBy(f => Math.Abs(f - joint.CurrentFrame)).First();
                    var plan = new TransitionControlPlan(q.Layout.Fingerprint, [new(joint.Aliases[0], frame)], [new(sides[0], -6), new(sides[1], 3)]);
                    if (Environment.GetEnvironmentVariable("VOCALOID_TRANSITION_PHASE") == "1")
                        foreach (var targetEdge in q.Layout.Edges.Where(e => TransitionPhaseMapping.CanEdit(e.Current)))
                            plan = plan.WithPhase(targetEdge.Identity, targetEdge.Identity.Reverse ? -.5f : .5f);
                    if (Environment.GetEnvironmentVariable("VOCALOID_TRANSITION_COVERAGE_GENERAL") == "1")
                        foreach (var targetEdge in q.Layout.Edges)
                        {
                            var row = targetEdge.Current; var material = row.MaterialCoverage;
                            var coverage = (row.Split - row.Begin >= 2 && material?.Left != null ? TransitionSourceSides.Left : TransitionSourceSides.None) |
                                (row.End - row.Split >= 2 && material?.Right != null ? TransitionSourceSides.Right : TransitionSourceSides.None);
                            if (coverage != TransitionSourceSides.None) plan = plan.WithCoverage(targetEdge.Identity, coverage);
                        }
                    var execution = preparation.Prepared.QueryTransitionControls(q.Layout, plan, preparation.Revision);
                    SampledTimingHost.CommitTransitions(preparation, execution);
                    var stored = SampledTimingHost.TransitionData(shared).Data!;
                    Check(stored.Gains.Length == 2, "Shared endpoint merged independent ART gains.");
                    if (Environment.GetEnvironmentVariable("VOCALOID_TRANSITION_PHASE") == "1")
                        Check(stored.Phases.Length != 0, "Shared ART lost independent phase controls.");
                    if (Environment.GetEnvironmentVariable("VOCALOID_TRANSITION_COVERAGE_GENERAL") == "1")
                        Check(stored.Coverages.Length != 0, "Shared ART lost source coverage.");
                    if (frame != joint.CurrentFrame)
                        Check(stored.Junctions.Length == 1 && stored.Junctions[0].Aliases.Length == joint.Aliases.Length, "Shared junction was not stored exactly once with all aliases.");
                    Record(new { name = "shared-range", joint.CurrentFrame, frame, reachable = range.ReachableFrames.Length, aliases = joint.Aliases.Length,
                        coveredSources = execution.Controls.Coverages.Select(c => new { c.Edge.From, c.Edge.To, c.Edge.Stage, c.Edge.Reverse, c.Sides }).ToArray() });
                }
                string controlled = Render(shared, sp, "shared-gains"); Check(controlled != plain, "Shared ART gains did not alter PCM.");
                shared.Undo(); Check(Render(shared, sp, "shared-undo") == plain, "Shared ART edit was not one undo step.");
                shared.Redo(); Check(Render(shared, sp, "shared-redo") == controlled, "Shared ART redo changed PCM.");
                Check(Save(shared, "shared-controls"), "Shared ART controls failed VPR save.");
                var reopened = Open("shared-controls");
                try { reopened.InterpolatedMasterVolume(); reopened.Commit(false); Check(Render(reopened, Part(reopened), "shared-reopened") == controlled, "Shared ART VPR changed PCM."); }
                finally { reopened.Close(); }
            }
            finally { shared.Close(); }
            Record(new { name = "acceptance", saved = true, sharedHistory = true, cacheReload = true, copy = true, unknownSchema = true, rollback = true });
        }
        finally { if (source.IsOpen) source.Close(); harmony.UnpatchAll(harmony.Id); application.Shutdown(); }
    }
}
#endif
