#if PATCHER_ISOLATED_VALIDATION
using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;
using System.Windows.Threading;
using HarmonyLib;
using VOCALOIDPatcher.Config;
using VOCALOIDPatcher.ConsonantTiming;
using VOCALOIDPatcher.ConsonantTiming.Smoothing;
using VOCALOIDPatcher.PhonemeVolume;
using Yamaha.VOCALOID.DSE;
using Yamaha.VOCALOID.VDM;
using Yamaha.VOCALOID.VSM;

namespace VOCALOIDPatcher.Rendering;

internal static class PostProcessingReuseValidation
{
    private static readonly Dictionary<nint, TransitionExecutionPlan> Plans = new();
    private static readonly int[] Processes = new int[2];
    private static readonly Type[] Hosts = [typeof(PhonemeVolumeHost), typeof(TransitionSmoothingHost)];
    private static bool FixedView(WIVSMMidiPart part, ref TransitionExecutionPlan? __result)
    {
        if (!Plans.TryGetValue((nint)part, out var plan)) return true;
        __result = plan; return false;
    }
    private static void Count(MethodBase __originalMethod) => Interlocked.Increment(ref Processes[__originalMethod.DeclaringType == Hosts[0] ? 0 : 1]);
    private static bool SkipPlacement() => false;
    private static void Check(bool result, string message) { if (!result) throw new InvalidOperationException(message); }
    private static object? Cache(int host, WIVSMMidiPart part)
    {
        object states = AccessTools.Field(Hosts[host], "Parts").GetValue(null)!;
        object?[] args = [(nint)part, null];
        if (!(bool)states.GetType().GetMethod("TryGetValue")!.Invoke(states, args)!) return null;
        return AccessTools.Field(args[1]!.GetType(), "Cache").GetValue(args[1]);
    }
    private static string Output(int host, WIVSMMidiPart part) =>
        (string)(Cache(host, part)?.GetType().GetProperty("Output")?.GetValue(Cache(host, part)) ?? "");
    private static string Hash(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));
    private static void CompareFresh(int host, WIVSMMidiPart part, string directory)
    {
        object cache = Cache(host, part)!;
        object Read(string name) => cache.GetType().GetProperty(name)!.GetValue(cache)!;
        string output = Path.Combine(directory, "fresh-" + Guid.NewGuid().ToString("N") + ".wav");
        try
        {
            if (host == 0) PhonemeGainWaveProcessor.Create((string)Read("Input"), output, (PhonemeGainRegion[])Read("Regions"));
            else WaveJunctionProcessor.ProcessWaveFile((string)Read("Input"), output,
                (WaveJunctionProcessor.JunctionInfo[])Read("Junctions"), (TransitionSmootherConfig)Read("Config"));
            Check(Hash(output) == Hash(Output(host, part)), "Reused output differs from fresh DSP on identical input.");
        }
        finally { if (File.Exists(output)) File.Delete(output); }
    }
    private static void Pump(WIVSMSequence source)
    {
        for (int i = 0; i < 64 && source.InvokeRendererObserver(); i++) { }
        var frame = new DispatcherFrame();
        Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(() => frame.Continue = false));
        Dispatcher.PushFrame(frame); Thread.Sleep(2);
    }
    private static void Wait(WIVSMSequence source, Func<bool> done)
    {
        var timer = Stopwatch.StartNew();
        while (timer.ElapsedMilliseconds < 30000)
        { Pump(source); if (done()) return; }
        throw new TimeoutException("Post-processing did not complete.");
    }
    private static void Settle(WIVSMSequence source)
    {
        var timer = Stopwatch.StartNew(); while (timer.ElapsedMilliseconds < 180) Pump(source);
    }
    internal static void Execute(WIVSMSequenceManager manager, DSEManager dse, DatabaseManager database, string directory)
    {
        bool expectReuse = Environment.GetEnvironmentVariable("VOCALOID_POST_REUSE_EXPECT") != "0";
        var harmony = new Harmony("VOCALOIDPatcher.post-processing-reuse-probe");
        harmony.Patch(AccessTools.Method(typeof(SampledTimingHost), "TransitionView"), new HarmonyMethod(typeof(PostProcessingReuseValidation), nameof(FixedView)));
        foreach (var host in Hosts)
        {
            harmony.Patch(AccessTools.Method(host, "Process"), new HarmonyMethod(typeof(PostProcessingReuseValidation), nameof(Count)));
            // A headless fixture has no AudioPlayer or pianoroll. Only suppress
            // publication to these UI objects; run capture, scheduling and DSP.
            harmony.Patch(AccessTools.Method(host, "RefreshPlacement"), new HarmonyMethod(typeof(PostProcessingReuseValidation), nameof(SkipPlacement)));
        }
        var source = manager.CreateSequence(new VSMSequenceData { SamplingRate = VSMSamplingRate._44100, MaxNumTracks = 4, MaxUndoCount = 100 })!;
        var records = new List<object>();
        try
        {
            var track = (WIVSMMidiTrack)source.InsertTrackEx(0, VSMTrackType.Midi, "reuse-fixture")!;
            var parts = Enumerable.Range(0, 2).Select(i => track.InsertPart(new VSMAbsTick(i * 4800), new VSMRelTick(3360), "reuse-fixture")!).ToArray();
            foreach (var part in parts)
            {
                Check(part.SetVoiceBankID("BL8CEAM5N4XN3LFK"), "Missing legal probe bank.");
                for (int i = 0; i < 3; i++) Check(part.InsertNote(new VSMRelTick(480 + i * 720), new VSMNoteEvent(600, 60 + i, 64),
                    part.GetDefaultNoteExpression(), part.GetDefaultAiNoteExpression(), "fixture", "l a", true, 4) != null, "Missing fixture note.");
            }
            var tempo = source.InsertTempo(VSMRelTick.Zero, 14600)!; source.InterpolatedMasterVolume(); Check(source.Commit(false), "Fixture commit failed.");
            source.BlockRenderingEnabled = false; source.StartAsyncRendering();
            Wait(source, () => source.IsFinishedRendering && parts.All(p => p.HasValidRenderedScore && p.HasValidRenderedWave));
            source.StopAsyncRendering(); SampledTimingHost.Attach(source, new LocalConsonantTimingBackend(manager, dse, database));
            foreach (var part in parts)
            {
                using var prepared = SampledTimingHost.PrepareTransitions(part);
                Plans[(nint)part] = prepared.Query().Execution;
                Check(TransitionSmoothingHost.CollectJunctions(part, Plans[(nint)part], Settings.GetTransitionSmoothingConfig())?.Count > 0, "No fixture junctions.");
                PhonemeVolumeHost.Set(part.GetNote(0)!, 1, -6);
            }
            Wait(source, () => parts.All(p => Cache(0, p) != null));
            foreach (var part in parts) TransitionSmoothingHost.RequestPart(part);
            Wait(source, () => parts.All(p => Cache(1, p) != null));
            string[] hashes = parts.SelectMany(p => new[] { Hash(Output(0, p)), Hash(Output(1, p)) }).ToArray();
            int[] before = Processes.ToArray(); var timer = Stopwatch.StartNew();
            string originalInput = WaveProcessingReadScope.ReadBefore(WaveProcessingStage.PhonemeVolume, () => parts[0].WaveFilePath);
            for (int i = 0; i < 10; i++)
            {
                PhonemeVolumeHost.SourceChanged(source);
                Check(PhonemeVolumeHost.Substitute(parts[0], originalInput) == originalInput, "Unverified candidate was published during debounce.");
                if (expectReuse) AccessTools.Method(Hosts[0], "CleanupFiles").Invoke(null, null);
                Wait(source, () => parts.All(p => Cache(0, p) != null));
                TransitionSmoothingHost.SourceChanged(source);
                Wait(source, () => parts.All(p => Cache(1, p) != null));
            }
            int[] duplicateWork = Processes.Zip(before, (a, b) => a - b).ToArray();
            Check(duplicateWork.All(n => n == (expectReuse ? 0 : 20)), "Unexpected no-change DSP work: " + string.Join(",", duplicateWork));
            Check(parts.SelectMany(p => new[] { Hash(Output(0, p)), Hash(Output(1, p)) }).SequenceEqual(hashes), "No-change output differs.");
            records.Add(new { name = "ten-no-change-notifications-two-parts", duplicateWork, elapsedMs = timer.Elapsed.TotalMilliseconds, hashes });

            before = Processes.ToArray(); string untouchedVolume = Output(0, parts[1]), untouchedSmooth = Output(1, parts[1]);
            PhonemeVolumeHost.Set(parts[0].GetNote(0)!, 1, -3); PhonemeVolumeHost.SourceChanged(source);
            Wait(source, () => parts.All(p => Cache(0, p) != null)); TransitionSmoothingHost.SourceChanged(source);
            Wait(source, () => parts.All(p => Cache(1, p) != null));
            int[] editedWork = Processes.Zip(before, (a, b) => a - b).ToArray();
            Check(editedWork.All(n => n == (expectReuse ? 1 : 2)), "Single-Part edit reprocessed wrong Parts.");
            if (expectReuse) Check(Output(0, parts[1]) == untouchedVolume && Output(1, parts[1]) == untouchedSmooth, "Unchanged Part lost result.");
            Check(Hash(Output(0, parts[0])) != hashes[0], "Gain edit retained old audio.");
            records.Add(new { name = "edit-one-part", editedWork, otherPartRetained = expectReuse });

            before = Processes.ToArray(); Settings.TransitionSmoothingCrossfadeMs = 13; TransitionSmoothingHost.RefreshSettings();
            Wait(source, () => parts.All(p => Cache(1, p) != null));
            Check(Processes[1] - before[1] == 2, "Changed smoothing configuration was reused.");
            records.Add(new { name = "smoothing-config-changed", builds = Processes[1] - before[1] });

            before = Processes.ToArray(); string input = WaveProcessingReadScope.ReadBefore(WaveProcessingStage.PhonemeVolume, () => parts[0].WaveFilePath);
            using (var stream = new FileStream(input, FileMode.Open, FileAccess.ReadWrite, FileShare.Read))
            { stream.Position = stream.Length - 2; stream.WriteByte(17); stream.WriteByte(1); }
            File.SetLastWriteTimeUtc(input, DateTime.UtcNow.AddSeconds(1));
            PhonemeVolumeHost.SourceChanged(source); Wait(source, () => parts.All(p => Cache(0, p) != null));
            Check(Processes[0] - before[0] == (expectReuse ? 1 : 2), "Same-path source change was reused.");
            records.Add(new { name = "same-path-upstream-overwrite", builds = Processes[0] - before[0] });
            TransitionSmoothingHost.SourceChanged(source); Wait(source, () => parts.All(p => Cache(1, p) != null));

            before = Processes.ToArray(); PhonemeVolumeHost.RenderStarted(parts[0]); TransitionSmoothingHost.RenderStarted(parts[0]);
            PhonemeVolumeHost.RenderCompleted(parts[0]); Wait(source, () => Cache(0, parts[0]) != null);
            TransitionSmoothingHost.RenderCompleted(parts[0]); Wait(source, () => Cache(1, parts[0]) != null);
            Check(Processes.Zip(before, (a, b) => a - b).All(n => n == 1), "Render boundary reused old output.");
            records.Add(new { name = "render-boundary-forces-rebuild", unchangedFileMetadata = true, builds = Processes.Zip(before, (a, b) => a - b).ToArray() });

            before = Processes.ToArray(); File.Delete(Output(0, parts[0]));
            PhonemeVolumeHost.SourceChanged(source); Wait(source, () => parts.All(p => Cache(0, p) != null));
            Check(Processes[0] - before[0] == (expectReuse ? 1 : 2), "Missing output was reused.");
            records.Add(new { name = "missing-output-rebuild", builds = Processes[0] - before[0] });

            if (expectReuse)
            {
                before = Processes.ToArray(); var originalPlan = Plans[(nint)parts[0]];
                Plans[(nint)parts[0]] = originalPlan with { Segments = originalPlan.Segments.Select(s =>
                    s with { Begin = s.Begin + 1, Split = s.Split + 1, End = s.End + 1 }).ToImmutableArray() };
                TransitionSmoothingHost.SourceChanged(source); Wait(source, () => parts.All(p => Cache(1, p) != null));
                Check(Processes[1] - before[1] == 1, "Changed junction coordinates were reused.");
                records.Add(new { name = "changed-junctions-same-source-and-config", builds = 1 });
                Plans[(nint)parts[0]] = originalPlan;
                TransitionSmoothingHost.SourceChanged(source); Wait(source, () => parts.All(p => Cache(1, p) != null));

                string editedHash = Hash(Output(0, parts[0])); before = Processes.ToArray();
                PhonemeVolumeHost.Set(parts[0].GetNote(0)!, 1, -9); PhonemeVolumeHost.Set(parts[0].GetNote(0)!, 1, -3);
                Wait(source, () => Cache(0, parts[0]) != null);
                Check(Processes[0] == before[0] && Hash(Output(0, parts[0])) == editedHash, "Fast final-value restoration did not reuse exact result.");
                records.Add(new { name = "rapid-change-and-restore", builds = 0, hash = editedHash });

                before = Processes.ToArray(); PhonemeVolumeHost.Set(parts[0].GetNote(0)!, 1, -9);
                PhonemeVolumeHost.EnsureForExport(source); string exportHash = Hash(Output(0, parts[0])); Settle(source);
                Check(Processes[0] - before[0] == 1 && exportHash != editedHash && exportHash == Hash(Output(0, parts[0])), "Export reused previous gain or repeated queued work.");
                records.Add(new { name = "export-current-gain-before-debounce", builds = 1, hash = exportHash });

                long fastFrames = VoiceComp.CompWave.Inspect(input).Frames;
                var frames = new List<long>();
                foreach (int bpm in new[] { 7300, 14600 })
                {
                    tempo.Value = bpm; Check(source.Commit(false), "Tempo edit failed.");
                    foreach (var part in parts) { PhonemeVolumeHost.RenderStarted(part); TransitionSmoothingHost.RenderStarted(part); }
                    source.StartAsyncRendering();
                    Wait(source, () => source.IsFinishedRendering && parts.All(p => p.HasValidRenderedScore && p.HasValidRenderedWave));
                    source.StopAsyncRendering();
                    foreach (var part in parts)
                    {
                        using var prepared = SampledTimingHost.PrepareTransitions(part); Plans[(nint)part] = prepared.Query().Execution;
                        PhonemeVolumeHost.RenderCompleted(part);
                    }
                    Wait(source, () => parts.All(p => Cache(0, p) != null));
                    foreach (var part in parts) TransitionSmoothingHost.RenderCompleted(part);
                    Wait(source, () => parts.All(p => Cache(1, p) != null));
                    long rendered = VoiceComp.CompWave.Inspect(Output(0, parts[0])).Frames; frames.Add(rendered);
                    Check(rendered == VoiceComp.CompWave.Inspect(WaveProcessingReadScope.ReadBefore(WaveProcessingStage.PhonemeVolume, () => parts[0].WaveFilePath)).Frames,
                        "Post-processing retained old tempo source.");
                }
                Check(frames[0] > fastFrames * 1.5 && frames[1] == fastFrames, "BPM round trip retained wrong audio length.");
                records.Add(new { name = "actual-native-bpm-146-73-146", fastFrames, slowFrames = frames[0], restoredFrames = frames[1] });
                foreach (var part in parts) { CompareFresh(0, part, directory); CompareFresh(1, part, directory); }
                records.Add(new { name = "same-input-fresh-dsp-reference", exactWaveChecks = 4, passed = true });
            }

            PhonemeVolumeHost.Set(parts[0].GetNote(0)!, 1, 0); Settle(source);
            Check(Cache(0, parts[0]) == null && PhonemeVolumeHost.Substitute(parts[0], input) == input, "Neutral gain published retained output.");
            records.Add(new { name = "neutral-reset-falls-back", passed = true });
            if (expectReuse)
            {
                source.Undo(); parts[0] = ((WIVSMMidiTrack)source.GetTrack(0)!).GetPart(0)!;
                Check(PhonemeVolumeHost.Get(parts[0].GetNote(0)!).Values[1] == -9, "Undo did not restore current gain.");
                Wait(source, () => Cache(0, parts[0]) != null); CompareFresh(0, parts[0], directory);
                source.Redo(); parts[0] = ((WIVSMMidiTrack)source.GetTrack(0)!).GetPart(0)!; Settle(source);
                Check(PhonemeVolumeHost.Get(parts[0].GetNote(0)!).IsDefault && Cache(0, parts[0]) == null, "Redo retained processed gain.");
                records.Add(new { name = "native-undo-redo-gain-reset", passed = true });
                string pendingFile = Output(0, parts[1]);
                PhonemeVolumeHost.RequestPart(parts[1]); TransitionSmoothingHost.RequestPart(parts[1]);
                PhonemeVolumeHost.Close(source); TransitionSmoothingHost.Close(source); Settle(source);
                Check(parts.All(p => Cache(0, p) == null && Cache(1, p) == null) && !File.Exists(pendingFile), "Close retained candidate files or queued work.");
                records.Add(new { name = "close-while-debouncing", passed = true });
            }
            File.WriteAllText(Path.Combine(directory, "post-processing-reuse.json"), JsonSerializer.Serialize(new { expectReuse, records }, new JsonSerializerOptions { WriteIndented = true }));
        }
        finally
        {
            PhonemeVolumeHost.Close(source); TransitionSmoothingHost.Close(source); Plans.Clear();
            source.StopAsyncRendering(); SampledTimingHost.Close(source); source.Close(); harmony.UnpatchAll(harmony.Id);
        }
    }
}
#endif
