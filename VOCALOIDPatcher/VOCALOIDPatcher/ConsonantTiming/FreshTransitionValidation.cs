#if PATCHER_ISOLATED_VALIDATION
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Windows.Threading;
using VOCALOIDPatcher.BreathVolume;
using VOCALOIDPatcher.DurationSelection;
using Yamaha.VOCALOID.VSM;

namespace VOCALOIDPatcher.ConsonantTiming;

// Deliberately no coefficient edit or other transition initialization before
// the first gesture: a warmed-up timing fixture misses startup-only failures.
internal static class FreshTransitionValidation
{
    internal static void Execute(WIVSMSequenceManager manager, string directory)
    {
        Rendering.RendererIncidentLog.DirectoryPath = Path.Combine(directory, "diagnostics");
        NativeDurationSelection.Initialize(Path.Combine(Patcher.DataDir, "native", "v6patch_clock.dll"));
        manager.CacheCapacity = 1024L * 1024 * 1024;
        var sourcePath = Environment.GetEnvironmentVariable("VOCALOID_TRANSITION_SOURCE");
        WIVSMSequence source;
        if (!string.IsNullOrEmpty(sourcePath))
        {
            var shell = System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(Yamaha.VOCALOID.Sequence));
            GC.SuppressFinalize(shell);
            object?[] args = [sourcePath, null];
            var loaded = ((Yamaha.VOCALOID.Sequence.LoadResult, bool))HarmonyLib.AccessTools.Method(typeof(Yamaha.VOCALOID.Sequence), "LoadProjectSequenceFile").Invoke(shell, args)!;
            if (loaded.Item1 != Yamaha.VOCALOID.Sequence.LoadResult.Success || args[1] is not WIVSMSequence opened)
                throw new InvalidOperationException("Cannot load captured source fixture.");
            source = opened;
        }
        else source = manager.CreateSequence(new VSMSequenceData { SamplingRate = VSMSamplingRate._44100, MaxNumTracks = 4, MaxUndoCount = 100 })!;
        var records = new List<object>();
        try
        {
            if (string.IsNullOrEmpty(sourcePath))
            {
                var track = (WIVSMMidiTrack)source.InsertTrackEx(0, VSMTrackType.Midi, "fresh")!;
                var added = track.InsertPart(VSMAbsTick.Zero, new VSMRelTick(7680), "fresh")!;
                if (!added.SetVoiceBankID(Environment.GetEnvironmentVariable("VOCALOID_TRANSITION_BANK") ?? "BETDB8W6KWZPYEB9"))
                    throw new InvalidOperationException("Fixture bank unavailable.");
                added.InsertNote(new VSMRelTick(1920), new VSMNoteEvent(480, 62, 64), added.GetDefaultNoteExpression(),
                    added.GetDefaultAiNoteExpression(), "fixture", "m u", true, 4);
                source.InsertTempo(VSMRelTick.Zero, 12000); source.InterpolatedMasterVolume(); source.Commit(false);
                BreathVolumeService.ResetHistory(source);
            }
            var part = (WIVSMMidiPart)((WIVSMMidiTrack)source.GetTrack(0)!).GetPart(0)!;
            var note = part.GetNote(0)!;
            if (!string.IsNullOrEmpty(sourcePath)) { source.InterpolatedMasterVolume(); source.Commit(false); }
            if (Environment.GetEnvironmentVariable("VOCALOID_TRANSITION_CAPTURE") is { Length: > 0 } capture)
                SampledTimingHost.Load(source, null, TransitionArchiveData.From(JsonSerializer.Deserialize<TransitionProjectData>(File.ReadAllBytes(capture))!));
            if (Environment.GetEnvironmentVariable("VOCALOID_TRANSITION_CAPTURE_VPR") == "1")
            {
                var shell = System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(Yamaha.VOCALOID.Sequence));
                GC.SuppressFinalize(shell);
                bool saved = (bool)HarmonyLib.AccessTools.Method(typeof(Yamaha.VOCALOID.Sequence), "SaveSequence")
                    .Invoke(shell, [source, directory, "transition-live-repro", ".vpr"])!;
                if (!saved) throw new InvalidOperationException("Cannot save captured transition fixture.");
                return;
            }
            using var observer = new WIVSMRendererObserver();
            int completions = 0; string? cancellation = null;
            observer.Completed += (_, _) => completions++;
            observer.Canceled += (_, e) => cancellation = e.CancelReason.ToString();
            source.AddRendererObserver(observer); source.BlockRenderingEnabled = true;
            source.BlockRenderingLengthMsec = WIVSMSequence.DefaultBlockRenderingBlockSize;
            source.BlockRenderingPartDurationThresholdMsec = WIVSMSequence.DefaultBlockRenderingPartDurationThresholdMsec;
            void Render(string label)
            {
                RegisterShift.RegisterShiftService.PublishPart(source, part);
                int before = completions; cancellation = null;
                source.StartAsyncRendering();
                var watch = System.Diagnostics.Stopwatch.StartNew();
                while (watch.ElapsedMilliseconds < 12000)
                {
                    while (source.InvokeRendererObserver()) { }
                    var frame = new DispatcherFrame();
                    Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(() => frame.Continue = false));
                    Dispatcher.PushFrame(frame);
                    if (completions > before && source.IsFinishedRendering) break;
                    Thread.Sleep(2);
                }
                var record = new { label, completions, cancellation, enabled = source.CanAsyncRendering(), finished = source.IsFinishedRendering,
                    score = part.HasValidRenderedScore, wave = part.HasValidRenderedWave, host = SampledTimingHost.Diagnostics(source) };
                records.Add(record);
                File.WriteAllText(Path.Combine(directory, "fresh-transition.json"), JsonSerializer.Serialize(records, new JsonSerializerOptions { WriteIndented = true }));
                Console.WriteLine("transition.fresh=" + JsonSerializer.Serialize(record));
                if (completions <= before || !source.IsFinishedRendering || !source.CanAsyncRendering() || cancellation != null)
                    throw new InvalidOperationException("Fresh transition render failed: " + label);
                if (source.MidiTracks.SelectMany(t => t.MidiParts).Any(p => !p.HasValidRenderedScore || !p.HasValidRenderedWave ||
                    NativeSampledTiming.ReadStatus((nint)p).Failures != 0))
                    throw new InvalidOperationException("Another Part failed: " + label);
                if (JsonSerializer.SerializeToElement(Rendering.RendererHealthMonitor.Diagnostics(source))
                    .GetProperty("LastIncident").ValueKind != JsonValueKind.Null)
                    throw new InvalidOperationException("Successful replay required recovery: " + label);
            }
            Render("plain-baseline");
            if (Environment.GetEnvironmentVariable("VOCALOID_TRANSITION_DURATION") == "1")
            {
                DurationSelectionHost.Set(note, new(note.Phonemes, 1.2)); Render("duration-edited");
                DurationSelectionHost.Set(note, null); Render("duration-restored");
            }
            for (int i = 0; i < 8; i++)
            {
                using (var prepared = SampledTimingHost.PrepareTransitions(part))
                {
                    var q = System.Threading.Tasks.Task.Run(() => prepared.Query()).GetAwaiter().GetResult();
                    var edge = q.Layout.Edges[i % 2 == 0 ? 0 : ^1];
                    var controls = i < 4 ? q.Controls.WithPhase(edge.Identity, (i + 1) * .15f) :
                        new TransitionControlPlan(q.Layout.Fingerprint, q.Controls.Junctions, [new(edge.Identity, i % 2 == 0 ? -6 : 3)],
                            q.Controls.Onsets, q.Controls.Phases, q.Controls.Coverages);
                    var execution = System.Threading.Tasks.Task.Run(() => prepared.Prepared.QueryTransitionControls(q.Layout, controls, prepared.Revision)).GetAwaiter().GetResult();
                    SampledTimingHost.CommitTransitions(prepared, execution);
                }
                Render("gesture-" + i);
                source.StopAsyncRendering(); note.NoteVelocity = 40 + i * 8; source.Commit();
                Render("velocity-" + i);
            }
            for (int i = 0; i < 12; i++)
            {
                source.StopAsyncRendering(); note.NoteVelocity = i % 2 == 0 ? 64 : 80; source.Commit();
                Render("repeat-velocity-" + i);
            }
            if (Environment.GetEnvironmentVariable("VOCALOID_TRANSITION_REG_MATRIX") == "1")
            {
                foreach (int shift in new[] { 0, -1, 1, 12, -12, 1 })
                {
                    RegisterShift.RegisterShiftService.SetValues(source, part, [note.CppObjPtr], shift);
                    Render("register-" + shift);
                }
                source.Undo(); Render("register-undo");
                if (RegisterShift.RegisterShiftService.CaptureValue(note) != -12) throw new InvalidOperationException("REG undo lost value.");
                source.Redo(); Render("register-redo");
                if (RegisterShift.RegisterShiftService.CaptureValue(note) != 1) throw new InvalidOperationException("REG redo lost value.");
                var savedControls = SampledTimingHost.TransitionData(source).Fingerprint;
                var shell = System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(Yamaha.VOCALOID.Sequence));
                GC.SuppressFinalize(shell);
                bool saved = (bool)HarmonyLib.AccessTools.Method(typeof(Yamaha.VOCALOID.Sequence), "SaveSequence")
                    .Invoke(shell, [source, directory, "register-transition-roundtrip", ".vpr"])!;
                if (!saved) throw new InvalidOperationException("REG/transition save failed.");
                source.StopAsyncRendering(); source.RemoveRendererObserver(observer); source.Close();
                object?[] args = [Path.Combine(directory, "register-transition-roundtrip.vpr"), null];
                var loaded = ((Yamaha.VOCALOID.Sequence.LoadResult, bool))HarmonyLib.AccessTools.Method(typeof(Yamaha.VOCALOID.Sequence), "LoadProjectSequenceFile").Invoke(shell, args)!;
                if (loaded.Item1 != Yamaha.VOCALOID.Sequence.LoadResult.Success || args[1] is not WIVSMSequence reopened)
                    throw new InvalidOperationException("REG/transition reload failed.");
                source = reopened; source.InterpolatedMasterVolume(); source.Commit(false);
                source.AddRendererObserver(observer);
                part = (WIVSMMidiPart)((WIVSMMidiTrack)source.GetTrack(0)!).GetPart(0)!; note = part.GetNote(0)!;
                if (RegisterShift.RegisterShiftService.CaptureValue(note) != 1 || SampledTimingHost.TransitionData(source).Fingerprint != savedControls)
                    throw new InvalidOperationException("Saved REG/transition controls changed.");
                Render("register-reloaded");
                source.StopAsyncRendering();
                var secondTrack = (WIVSMMidiTrack)source.InsertTrackEx(1, VSMTrackType.Midi, "other")!;
                var other = secondTrack.InsertPart(VSMAbsTick.Zero, new VSMRelTick(7680), "other")!;
                if (!other.SetVoiceBankID(part.VoiceBankID)) throw new InvalidOperationException("Cannot set second bank.");
                var second = other.InsertNote(new VSMRelTick(1920), new VSMNoteEvent(480, 62, 64), other.GetDefaultNoteExpression(),
                    other.GetDefaultAiNoteExpression(), "fixture", "m u", true, 4)!;
                source.Commit();
                RegisterShift.RegisterShiftService.SetValues(source, other, [second.CppObjPtr], -1);
                using (var prepared = SampledTimingHost.PrepareTransitions(other))
                {
                    var q = System.Threading.Tasks.Task.Run(() => prepared.Query()).GetAwaiter().GetResult();
                    var controls = q.Controls.WithPhase(q.Layout.Edges[1].Identity, -.25f);
                    SampledTimingHost.CommitTransitions(prepared, System.Threading.Tasks.Task.Run(() =>
                        prepared.Prepared.QueryTransitionControls(q.Layout, controls, prepared.Revision)).GetAwaiter().GetResult());
                }
                Render("opposite-register-two-parts");
                for (int i = 0; i < 6; i++)
                {
                    source.StopAsyncRendering(); note.NoteVelocity = 64 + i % 2 * 16; second.NoteVelocity = 80 - i % 2 * 16; source.Commit();
                    Render("two-part-velocity-" + i);
                }
            }
            source.StopAsyncRendering(); source.RemoveRendererObserver(observer);
        }
        finally { source.Close(); }
    }
}
#endif
