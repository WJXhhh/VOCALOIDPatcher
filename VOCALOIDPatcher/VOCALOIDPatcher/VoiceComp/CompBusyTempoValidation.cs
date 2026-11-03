#if PATCHER_ISOLATED_VALIDATION
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;
using System.Windows.Threading;
using HarmonyLib;
using VOCALOIDPatcher.ConsonantTiming;
using Yamaha.VOCALOID.DSE;
using Yamaha.VOCALOID.VDM;
using Yamaha.VOCALOID.VSM;

namespace VOCALOIDPatcher.VoiceComp;

internal static class CompBusyTempoValidation
{
    private static int _busyLeft, _busySeen;
    private static void InjectBusy(WIVSMSequence source)
    {
        if (_busyLeft > 0 && Utils.NativeRenderSession.IsInternalSequence(source) && SampledTimingHost.HasData(source) &&
            new StackTrace().GetFrames().Any(f =>
            f.GetMethod()?.DeclaringType?.FullName?.Contains("CompNativeSource") == true))
        { _busyLeft--; _busySeen++; throw new TimingPlannerBusyException("Injected private-source planning contention."); }
    }
    private static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
    private static void Pump(WIVSMSequence source)
    {
        for (int i = 0; i < 64 && source.InvokeRendererObserver(); i++) { }
        var frame = new DispatcherFrame();
        Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(() => frame.Continue = false));
        Dispatcher.PushFrame(frame);
    }

    internal static void Execute(WIVSMSequenceManager manager, DSEManager dse, DatabaseManager database, string directory)
    {
        Rendering.RendererIncidentLog.DirectoryPath = Path.Combine(directory, "diagnostics");
        var fault = new Harmony("VOCALOIDPatcher.comp-busy-tempo-validation");
        fault.Patch(AccessTools.Method(typeof(SampledTimingHost), "BeforeRendering"),
            new HarmonyMethod(typeof(CompBusyTempoValidation), nameof(InjectBusy)));
        var source = manager.CreateSequence(new VSMSequenceData { SamplingRate = VSMSamplingRate._44100, MaxNumTracks = 4, MaxUndoCount = 100 })!;
        var records = new List<object>();
        try
        {
            var track = (WIVSMMidiTrack)source.InsertTrackEx(0, VSMTrackType.Midi, "comp-busy-tempo")!;
            var part = track.InsertPart(VSMAbsTick.Zero, new VSMRelTick(3360), "comp-busy-tempo")!;
            Check(part.SetVoiceBankID("BL8CEAM5N4XN3LFK"), "Missing test bank.");
            for (int i = 0; i < 3; i++) Check(part.InsertNote(new VSMRelTick(480 + i * 720), new VSMNoteEvent(600, 60 + i, 64),
                part.GetDefaultNoteExpression(), part.GetDefaultAiNoteExpression(), "fixture", "l a", true, 4) != null, "Missing note.");
            var tempo = source.InsertTempo(VSMRelTick.Zero, 14600)!;
            source.InterpolatedMasterVolume(); Check(source.Commit(false), "Fixture commit failed.");
            SampledTimingHost.Attach(source, new LocalConsonantTimingBackend(manager, dse, database));
            var note = part.GetNote(0)!;
            SampledTimingHost.Load(source, new Dictionary<TimingNoteAddress, SampledTimingPlan>
                { [SampledTimingVsmBinding.Address(note)] = new(note.Phonemes, [new(TimingAdjustment.PhonemeBoundary, 0, -.005f)]) });
            _busyLeft = 4; _busySeen = 0;
            VoiceCompHost.Set(part.GetNote(1)!, new(Isolation: new(true)));
            VoiceCompHost.PartState Ready()
            {
                var watch = Stopwatch.StartNew();
                while (watch.ElapsedMilliseconds < 30000)
                {
                    Pump(source);
                    var state = VoiceCompHost.View(part);
                    if (state?.Status == "Failed") throw new InvalidOperationException("Comp failed: " + state.Error);
                    if (state is { Dirty: false, Running: false, Wave: not null })
                    {
                        Check(CompWave.Peaks(state.Wave).Any(p => p > .00001f), "Published Comp PCM is silent.");
                        return state;
                    }
                    Thread.Sleep(2);
                }
                throw new TimeoutException("Comp did not become ready.");
            }
            var first = Ready();
            Check(_busySeen == 4, "Private-source planner contention was not exercised.");
            string fastWave = first.Wave!; long fastFrames = CompWave.Inspect(fastWave).Frames;
            string fastHash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(fastWave)));
            long version = first.NativeVersion, generation = first.Generation;
            SampledTimingVsmBinding.InvalidateRenderedPart(part, [note]);
            Check(first.NativeVersion == version && first.Generation == generation && !first.Dirty,
                "Internal cache invalidation re-notified/canceled VoiceComp.");
            records.Add(new { name = "private-planner-busy-recovers", busyRetries = _busySeen, fastFrames, internalNotificationSuppressed = true });
            tempo.Value = 7300; Check(source.Commit(false), "Slow BPM commit failed.");
            Check(VoiceCompHost.Substitute(part, "native-fallback") == "native-fallback" && VoiceCompHost.View(part)?.Wave == null,
                "Old fast composition was published after slowing BPM.");
            var slow = Ready(); string slowWave = slow.Wave!; long slowFrames = CompWave.Inspect(slowWave).Frames;
            Check(slowFrames > fastFrames * 1.5, "Slow BPM did not change composed audio duration.");
            tempo.Value = 14600; Check(source.Commit(false), "Restored BPM commit failed.");
            Check(VoiceCompHost.Substitute(part, "native-fallback") == "native-fallback" && VoiceCompHost.View(part)?.Composition == null,
                "Old slow composition survived restored BPM.");
            var restored = Ready(); long restoredFrames = CompWave.Inspect(restored.Wave!).Frames;
            Check(restoredFrames == fastFrames, "BPM round trip retained slow audio duration.");
            records.Add(new { name = "bpm-146-73-146", fastFrames, slowFrames, restoredFrames,
                fastHash, restoredHash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(restored.Wave!))),
                staleWaveRejected = true, nonSilent = true });
            string retained = restored.Wave!;
            VoiceCompHost.Set(part.GetNote(1)!, new(Body: new(TrimBeginMs: 60000)));
            bool rejected = false;
            try { Ready(); } catch (InvalidOperationException) { rejected = true; }
            Check(rejected && VoiceCompHost.View(part) is { Wave: null, Composition: null } failed &&
                failed.LastCompletedWave == retained && File.Exists(retained), "Failure lost retained data or published stale audio.");
            Check(VoiceCompHost.Substitute(part, "native-fallback") == "native-fallback", "Failure returned old composed audio.");
            bool exportRejected = false;
            try { VoiceCompHost.EnsureForExport(source); } catch (InvalidOperationException) { exportRejected = true; }
            Check(exportRejected, "Failed current recipe exported an old result.");
            records.Add(new { name = "failed-recipe-retains-file-without-publishing", retainedFile = true, staleAudioRejected = true, exportRejected });
            File.WriteAllText(Path.Combine(directory, "comp-busy-tempo.json"), JsonSerializer.Serialize(records, new JsonSerializerOptions { WriteIndented = true }));
        }
        finally
        {
            _busyLeft = 0; fault.UnpatchAll(fault.Id); VoiceCompHost.Close(source);
            source.StopAsyncRendering(); SampledTimingHost.Close(source); source.Close();
        }
    }
}
#endif
