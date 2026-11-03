#if PATCHER_ISOLATED_VALIDATION
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Threading;
using HarmonyLib;
using VOCALOIDPatcher.AceParameters;
using VOCALOIDPatcher.Config;
using VOCALOIDPatcher.ConsonantTiming;
using VOCALOIDPatcher.Patch;
using VOCALOIDPatcher.Patch.Patches;
using VOCALOIDPatcher.PhonemeVolume;
using VOCALOIDPatcher.Rendering;
using Yamaha.VOCALOID.VSM;

namespace VOCALOIDPatcher.VoiceComp;

internal static class ControllerDeletionValidation
{
    private sealed record Origin(string Stack);
    private static readonly ConditionalWeakTable<WIVSMSequence, Origin> Origins = new();
    private static void Created(WIVSMSequence? __result)
    {
        if (__result != null) Origins.Add(__result, new(Environment.StackTrace));
    }
    private static void Finalizing(WIVSMSequence __instance)
    {
        if (__instance.IsOpen && Origins.TryGetValue(__instance, out var origin))
            Console.WriteLine(JsonSerializer.Serialize(new { stage = "open-sequence-finalizer", pointer = ((nint)__instance).ToString("X"), origin.Stack }));
    }
    private static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
    private static void Pump(WIVSMSequence source)
    {
        for (int i = 0; i < 64 && source.InvokeRendererObserver(); i++) { }
        var frame = new DispatcherFrame();
        Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(() => frame.Continue = false));
        Dispatcher.PushFrame(frame); Thread.Sleep(2);
    }
    private static void Collect()
    { GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect(); }
    internal static void Execute(WIVSMSequenceManager manager, string directory)
    {
        if (Environment.GetEnvironmentVariable("VOCALOID_SEQUENCE_REUSED_OWNER_PROBE") == "1")
        { VerifyReusedOwner(manager, directory); return; }
        string project = Environment.GetEnvironmentVariable("VOCALOID_CONTROLLER_DELETE_SOURCE")!;
        var harmony = new Harmony("VOCALOIDPatcher.controller-delete-probe");
        foreach (var method in typeof(WIVSMSequenceManager).GetMethods().Where(m => m.ReturnType == typeof(WIVSMSequence)))
            harmony.Patch(method, postfix: new HarmonyMethod(typeof(ControllerDeletionValidation), nameof(Created)));
        harmony.Patch(AccessTools.DeclaredMethod(typeof(WIVSMSequence), "Finalize"), new HarmonyMethod(typeof(ControllerDeletionValidation), nameof(Finalizing)));
        foreach (PatchBase patch in new PatchBase[] { new AceParameterProjectLoadPatch(), new AceParameterCommitMappingPatch(),
            new AceParameterUndoMappingPatch(), new AceParameterRedoMappingPatch(), new AceParameterSequenceClosePatch(),
            new ControllerRemovalScopePatch(), new ControllerRemovalRollbackPatch() })
        {
            HarmonyMethod? Hook(string name) => AccessTools.Method(patch.GetType(), name) is { } method ? new HarmonyMethod(method) : null;
            harmony.Patch(AccessTools.Method(patch.TargetClass, patch.TargetMethodName, patch.ArgumentTypes), Hook("Prefix"), Hook("Postfix"), finalizer: Hook("Finalizer"));
        }
        var shell = RuntimeHelpers.GetUninitializedObject(typeof(Yamaha.VOCALOID.Sequence)); GC.SuppressFinalize(shell);
        object?[] args = [project, null];
        var loaded = ((Yamaha.VOCALOID.Sequence.LoadResult, bool))AccessTools.Method(typeof(Yamaha.VOCALOID.Sequence), "LoadProjectSequenceFile").Invoke(shell, args)!;
        Check(loaded.Item1 == Yamaha.VOCALOID.Sequence.LoadResult.Success && args[1] is WIVSMSequence, "Cannot load controller fixture.");
        var source = (WIVSMSequence)args[1]!;
        var records = new List<object>();
        void Record(object value)
        {
            records.Add(value); Console.WriteLine(JsonSerializer.Serialize(value));
            File.WriteAllText(Path.Combine(directory, "controller-deletion.json"), JsonSerializer.Serialize(records, new JsonSerializerOptions { WriteIndented = true }));
        }
        try
        {
            source.InterpolatedMasterVolume(); source.Commit(false);
            var part = source.MidiTracks[0].MidiParts[0];
            SampledTimingVsmBinding.InvalidateRenderedPart(part, [part.GetNote(0)!]);
            source.BlockRenderingEnabled = false; source.StartAsyncRendering();
            var timer = Stopwatch.StartNew();
            while (!(source.IsFinishedRendering && part.HasValidRenderedWave && part.HasValidRenderedScore))
            {
                Check(timer.Elapsed < TimeSpan.FromSeconds(60), "Native controller fixture did not finish.");
                Pump(source); SampledTimingHost.ThrowIfCompRenderFailed(source);
            }
            source.StopAsyncRendering();
            VoiceCompHost.EnsureForExport(source);
            string Capture(string stage)
            {
                VoiceCompHost.EnsureForExport(source);
                AceAcousticHost.EnsureForExport(source);
                string input = WaveProcessingReadScope.ReadBefore(WaveProcessingStage.AceAcoustic, () => part.WaveFilePath);
                string copy = Path.Combine(directory, stage + ".wav");
                var controls = AceAcousticHost.CaptureControls(part, source, out _);
                Check(!controls.Energy.IsNeutral, "The reported energy curve unexpectedly became neutral.");
                AceAcousticWave.Create(input, copy, controls, compatiblePcm16: true);
                bool hostMatchesDirectDsp = File.ReadAllBytes(copy).SequenceEqual(File.ReadAllBytes(part.WaveFilePath));
                Check(hostMatchesDirectDsp, "The host ACE output differs from a fresh render of its captured controls.");
                string inputCopy = Path.Combine(directory, stage + "-input.wav"); File.Copy(input, inputCopy, true);
                Record(new { stage, inputHash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(input))),
                    outputHash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(copy))),
                    hostMatchesDirectDsp, cleanup = TimingSequenceCleanup.Diagnostics(), comp = VoiceCompHost.View(part)?.Status });
                return copy;
            }
            string before = Capture("before"); Collect();
            var definition = AceParameterDefinitions.All.Single(d => d.Kind == AceParameterKind.Energy);
            long begin = 16804 - part.AbsPosTick.Value, end = 17093 - part.AbsPosTick.Value;
            var selected = part.GetControlParametersEnumerator(definition.CarrierType).Where(p => p.RelPosTick.Value >= begin && p.RelPosTick.Value <= end).ToArray();
            Record(new { stage = "selection", count = selected.Length, points = selected.Select(p => new { tick = part.AbsPosTick.Value + p.RelPosTick.Value, p.Value }) });
            Check(selected.Length > 0, "Saved fixture lacks the reported controller range.");
            foreach (var point in selected.Reverse()) Check(part.RemoveController(point), "Cannot remove selected controller.");
            Check(source.Commit(true), "Delete commit failed.");
            string deleted = Capture("deleted"); Collect();
            string undo;
            source.Undo(); undo = Capture("undo"); Collect();
            source.Redo(); string redo = Capture("redo"); Collect();
            var format = CompWave.Inspect(before);
            long Sample(long tick) => (long)Math.Ceiling((source.PresendTimeSec + source.GetTimeFromTick(part.AbsBeginTick, new VSMAbsTick(tick))) * format.Rate);
            byte[] Pcm(string path)
            {
                var layout = CompWave.Inspect(path);
                using var stream = File.OpenRead(path); stream.Position = layout.Offset;
                var bytes = new byte[checked((int)(layout.Frames * layout.Align))]; stream.ReadExactly(bytes); return bytes;
            }
            byte[] a = Pcm(before), b = Pcm(deleted);
            int first = checked((int)(Sample(16804) * format.Align)), last = checked((int)(Sample(17107) * format.Align));
            bool prefixSame = a.AsSpan(0, first).SequenceEqual(b.AsSpan(0, first));
            bool suffixSame = a.AsSpan(last).SequenceEqual(b.AsSpan(last));
            bool editedChanged = !a.AsSpan(first, last - first).SequenceEqual(b.AsSpan(first, last - first));
            Record(new { stage = "unselected-pcm", prefixSame, suffixSame, first, last,
                editedChanged, undoSame = a.SequenceEqual(Pcm(undo)), redoSame = b.SequenceEqual(Pcm(redo)) });
            Check(prefixSame && suffixSame && editedChanged, "Controller deletion did not stay within the edited native interval.");
            Check(a.SequenceEqual(Pcm(undo)) && b.SequenceEqual(Pcm(redo)), "Controller deletion history changed PCM.");
            int loops = int.TryParse(Environment.GetEnvironmentVariable("VOCALOID_CONTROLLER_DELETE_LOOPS"), out int count) ? Math.Clamp(count, 0, 30) : 0;
            for (int i = 0; i < loops; i++)
            {
                source.Undo(); VoiceCompHost.EnsureForExport(source); Collect();
                source.Redo(); VoiceCompHost.EnsureForExport(source); Collect();
                Record(new { stage = "stress", iteration = i + 1, cleanup = TimingSequenceCleanup.Diagnostics() });
            }
            VoiceCompHost.DrainForManagerDisposal();
            TimingSequenceCleanup.RetryPending(); Collect();
            Record(new { stage = "delete-undo-redo-forced-gc", passed = true, cleanup = TimingSequenceCleanup.Diagnostics() });
        }
        finally
        {
            VoiceCompHost.DrainForManagerDisposal(); source.StopAsyncRendering(); source.Close();
            TimingSequenceCleanup.RetryPending(); Collect(); harmony.UnpatchAll(harmony.Id);
        }
    }

    private static void VerifyReusedOwner(WIVSMSequenceManager manager, string directory)
    {
        var stale = manager.CreateSequence(new VSMSequenceData { SamplingRate = VSMSamplingRate._44100, MaxNumTracks = 4, MaxUndoCount = 0 })!;
        Check(stale.Close(), "Cannot release original owner.");
        var live = manager.CreateSequence(new VSMSequenceData { SamplingRate = VSMSamplingRate._44100, MaxNumTracks = 4, MaxUndoCount = 0 })!;
        Utils.NativeRenderSession.RegisterInternalSequence(live);
        nint pointer = (nint)live;
        // Model the already-observed stale-wrapper state after VSM reuses an
        // address. Never access the old native object; only its managed fields.
        AccessTools.Property(typeof(WIVSMSequence), "CppObjPtr").SetValue(stale, pointer);
        AccessTools.Field(typeof(WIVSMSequence), "isCreateUnmanagedObj").SetValue(stale, true);
        try
        {
            TimingSequenceCleanup.Close(manager, stale, Environment.CurrentManagedThreadId, Dispatcher.CurrentDispatcher);
            bool liveOwnerPreserved = manager.HasSequence(pointer) && live.IsOpen;
            var result = new { stage = "reused-sequence-owner", liveOwnerPreserved, staleDetached = !stale.IsOpen,
                liveStillRegistered = Utils.NativeRenderSession.IsInternalSequence(live), cleanup = TimingSequenceCleanup.Diagnostics() };
            Console.WriteLine(JsonSerializer.Serialize(result));
            File.WriteAllText(Path.Combine(directory, "reused-owner.json"), JsonSerializer.Serialize(result));
            Check(liveOwnerPreserved && !stale.IsOpen && Utils.NativeRenderSession.IsInternalSequence(live), "Stale cleanup destroyed the replacement owner.");
        }
        finally
        {
            // The old implementation closes the replacement's native object.
            // Let the manager-proven released-wrapper path rescue that owner
            // before forced GC, so this test can report without killing itself.
            TimingSequenceCleanup.Close(manager, stale, Environment.CurrentManagedThreadId, Dispatcher.CurrentDispatcher);
            TimingSequenceCleanup.Close(manager, live, Environment.CurrentManagedThreadId, Dispatcher.CurrentDispatcher);
            TimingSequenceCleanup.RetryPending(); Collect();
        }
        // Both wrappers must stay retained even when cleanup is first requested
        // off the owner thread while the same address is represented twice.
        var queuedStale = manager.CreateSequence(new VSMSequenceData { SamplingRate = VSMSamplingRate._44100, MaxNumTracks = 4, MaxUndoCount = 0 })!;
        Check(queuedStale.Close(), "Cannot release queued stale fixture.");
        var queuedLive = manager.CreateSequence(new VSMSequenceData { SamplingRate = VSMSamplingRate._44100, MaxNumTracks = 4, MaxUndoCount = 0 })!;
        Utils.NativeRenderSession.RegisterInternalSequence(queuedLive);
        AccessTools.Property(typeof(WIVSMSequence), "CppObjPtr").SetValue(queuedStale, (nint)queuedLive);
        AccessTools.Field(typeof(WIVSMSequence), "isCreateUnmanagedObj").SetValue(queuedStale, true);
        int ownerThread = Environment.CurrentManagedThreadId, pending = TimingSequenceCleanup.PendingCount;
        try
        {
            Task.Run(() =>
            {
                TimingSequenceCleanup.Close(manager, queuedStale, ownerThread, null);
                TimingSequenceCleanup.Close(manager, queuedLive, ownerThread, null);
            }).GetAwaiter().GetResult();
            Check(TimingSequenceCleanup.PendingCount == pending + 2, "A reused address dropped a pending owning wrapper.");
            Collect(); TimingSequenceCleanup.RetryPending();
            Check(!queuedStale.IsOpen && !queuedLive.IsOpen && TimingSequenceCleanup.PendingCount == pending, "Queued owning wrappers did not drain.");
            Console.WriteLine("sequence-ownership.distinct-wrappers-and-gc=passed");
        }
        finally
        {
            TimingSequenceCleanup.Close(manager, queuedStale, ownerThread, Dispatcher.CurrentDispatcher);
            TimingSequenceCleanup.Close(manager, queuedLive, ownerThread, Dispatcher.CurrentDispatcher);
            TimingSequenceCleanup.RetryPending(); Collect();
        }
    }
}
#endif
