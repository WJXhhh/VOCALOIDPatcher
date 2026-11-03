#if PATCHER_ISOLATED_VALIDATION
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using HarmonyLib;
using VOCALOIDPatcher.Rendering;
using Yamaha.VOCALOID.DSE;
using Yamaha.VOCALOID.VDM;
using Yamaha.VOCALOID.VSM;
using Yamaha.VOCALOID.TrackEditor;

namespace VOCALOIDPatcher.ConsonantTiming;

internal static class TimingPublicationRetryValidation
{
    private static nint _source;
    private static int _internalCommits;
    private static void CountCommit(WIVSMSequence __instance)
    {
        if ((nint)__instance == _source && TimingCacheInvalidationScope.IsActive(_source)) _internalCommits++;
    }
    private static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
    private static void Pump(WIVSMSequence source)
    {
        for (int i = 0; i < 128 && source.InvokeRendererObserver(); i++) { }
        var frame = new DispatcherFrame();
        Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(() => frame.Continue = false));
        Dispatcher.PushFrame(frame);
    }
    private static int BluePixels(UIMidiPart view)
    {
        view.Measure(new Size(200, 40)); view.Arrange(new Rect(0, 0, 200, 40)); view.UpdateLayout();
        var bitmap = new RenderTargetBitmap(200, 40, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(view);
        byte[] pixels = new byte[200 * 40 * 4]; bitmap.CopyPixels(pixels, 800, 0);
        int blue = 0;
        for (int i = 0; i < pixels.Length; i += 4)
            if (pixels[i] > 150 && pixels[i + 1] > 50 && pixels[i + 2] < 80 && pixels[i + 3] > 200) blue++;
        return blue;
    }

    internal static void Execute(WIVSMSequenceManager manager, DSEManager dse, DatabaseManager database, string directory)
    {
        RendererIncidentLog.DirectoryPath = Path.Combine(directory, "diagnostics");
        DurationSelection.NativeDurationSelection.Initialize(Path.Combine(Patcher.DataDir, "native", "v6patch_clock.dll"));
        var harmony = new Harmony("VOCALOIDPatcher.publication-retry-count");
        harmony.Patch(AccessTools.Method(typeof(WIVSMSequence), nameof(WIVSMSequence.Commit)),
            new HarmonyMethod(typeof(TimingPublicationRetryValidation), nameof(CountCommit)));
        var source = manager.CreateSequence(new VSMSequenceData { SamplingRate = VSMSamplingRate._44100, MaxNumTracks = 4, MaxUndoCount = 100 })!;
        _source = (nint)source;
        var checks = new List<object>();
        try
        {
            var track = (WIVSMMidiTrack)source.InsertTrackEx(0, VSMTrackType.Midi, "retry")!;
            var part = track.InsertPart(VSMAbsTick.Zero, new VSMRelTick(4800), "retry")!;
            Check(part.SetVoiceBankID("BL8CEAM5N4XN3LFK"), "Voicebank unavailable.");
            for (int i = 0; i < 4; i++)
                Check(part.InsertNote(new VSMRelTick(480 + i * 720), new VSMNoteEvent(600, 60 + i, 64),
                    part.GetDefaultNoteExpression(), part.GetDefaultAiNoteExpression(), "fixture", "l a", true, 4) != null, "Missing fixture note.");
            source.InsertTempo(VSMRelTick.Zero, 12000); source.InterpolatedMasterVolume(); source.Commit(false);
            var backend = new LocalConsonantTimingBackend(manager, dse, database);
            var session = SampledTimingHost.Attach(source, backend);
            var note = part.GetNote(1)!;
            var address = SampledTimingVsmBinding.Address(note);
            SampledTimingHost.Load(source, new Dictionary<TimingNoteAddress, SampledTimingPlan>
                { [address] = new(note.Phonemes, [new(TimingAdjustment.PhonemeBoundary, 0, -.005f)]) });
            note.NoteVelocity = 72; Check(source.Commit(false), "Cannot commit fixture edit.");
            int started = 0, completed = 0, progressed = 0, partialBlue = 0;
            var view = new UIMidiPart { Part = part, Background = Brushes.DodgerBlue, FramePen = new Pen(Brushes.White, 1), Width = 200, Height = 40 };
            Check(BluePixels(view) == 0, "Unrendered Part unexpectedly filled.");
            using var observer = new WIVSMRendererObserver();
            observer.Started += (_, _) => started++;
            observer.Progressed += (sender, e) =>
            {
                progressed++; view.OnRendererProgressed(sender, e);
                if (view.Progress.SecondEnd is > 0 and < 100) partialBlue = Math.Max(partialBlue, BluePixels(view));
            };
            observer.Completed += (sender, e) => { completed++; view.OnRendererCompleted(sender, e); };
            Check(source.AddRendererObserver(observer), "Cannot observe fixture rendering.");
            source.BlockRenderingEnabled = true;
            var engine = AccessTools.Field(typeof(LocalConsonantTimingBackend), "_engine").GetValue(backend)!;
            nint mutex = (nint)AccessTools.Field(typeof(NativeConsonantTimingEngine), "_nativeMutex").GetValue(engine)!;
            using var locked = new ManualResetEventSlim();
            uint lockResult = uint.MaxValue;
            var holder = new Thread(() =>
            {
                lockResult = WaitForSingleObject(mutex, 1000); locked.Set();
                if (lockResult == 0) { Thread.Sleep(350); ReleaseMutex(mutex); }
            });
            holder.Start(); Check(locked.Wait(2000) && lockResult == 0, "Could not hold native mutex.");
            _internalCommits = 0;
            var watch = Stopwatch.StartNew(); source.StartAsyncRendering();
            var deferred = JsonSerializer.SerializeToElement(SampledTimingHost.Diagnostics(source));
            Check(deferred.GetProperty("publicationRetry").GetBoolean() && !deferred.GetProperty("suspended").GetBoolean(),
                "Real native lock contention permanently suspended rendering.");
            Check(!source.CanAsyncRendering(), "Rendering started with unpublished controls.");
            checks.Add(new { name = "real-native-mutex-contention-deferred", elapsedMs = watch.Elapsed.TotalMilliseconds, internalCommits = _internalCommits });
            holder.Join();
            // Additional busy notifications must reuse this source's queued retry.
            SampledTimingHost.Recover(source, new TimingPlannerBusyException("duplicate busy notification"));
            SampledTimingHost.Recover(source, new TimingPlannerBusyException("duplicate busy notification"));
            while (watch.ElapsedMilliseconds < 15000 && (completed == 0 || !source.IsFinishedRendering)) { Pump(source); Thread.Sleep(2); }
            Check(completed == 1 && started == 1 && part.HasValidRenderedWave && part.HasValidRenderedScore,
                "Deferred publication did not produce exactly one completed render: " + JsonSerializer.Serialize(SampledTimingHost.Diagnostics(source)));
            Check(_internalCommits == 1, "Publication retries repeated native cache invalidation.");
            Check(progressed > 0 && partialBlue > 0 && BluePixels(view) > 7000, "Native partial/completed blue progress did not reach the actual Part control.");
            checks.Add(new { name = "retry-coalesced-and-progress-restored", started, completed, progressed, partialBlue,
                completedBlue = BluePixels(view), internalCommits = _internalCommits, nativeEnabled = source.CanAsyncRendering() });
            var final = JsonSerializer.SerializeToElement(SampledTimingHost.Diagnostics(source));
            Check(!final.GetProperty("suspended").GetBoolean() && !final.GetProperty("publicationRetry").GetBoolean(), "Retry state leaked after completion.");
            Check(note.NoteVelocity == 72 && !source.IsStaged && session.Document.Snapshot().Count == 1, "Retry changed persistent input.");
            SampledTimingHost.Recover(source, new TimingPlannerBusyException("queued retry before a real failure"));
            Check(JsonSerializer.SerializeToElement(SampledTimingHost.Diagnostics(source)).GetProperty("publicationRetry").GetBoolean(), "Retry was not queued.");
            SampledTimingHost.Recover(source, new InvalidDataException("Injected invalid controls."));
            var rejected = JsonSerializer.SerializeToElement(SampledTimingHost.Diagnostics(source));
            Check(rejected.GetProperty("suspended").GetBoolean() && !rejected.GetProperty("publicationRetry").GetBoolean(),
                "A queued busy retry survived a genuine preparation failure.");
            SampledTimingHost.Recover(source, new TimingPlannerBusyException("stale busy notification after rejection"));
            Check(!JsonSerializer.SerializeToElement(SampledTimingHost.Diagnostics(source)).GetProperty("publicationRetry").GetBoolean(),
                "A stale busy notification revived a rejected revision.");
            checks.Add(new { name = "genuine-failure-cancels-retry", controlsPreserved = session.Document.Snapshot().Count == 1,
                suspended = true, retryPending = false });
            File.WriteAllText(Path.Combine(directory, "publication-retry.json"), JsonSerializer.Serialize(checks, new JsonSerializerOptions { WriteIndented = true }));
            source.RemoveRendererObserver(observer);
        }
        finally
        {
            source.StopAsyncRendering(); SampledTimingHost.Close(source); source.Close();
            harmony.UnpatchAll(harmony.Id); _source = 0;
        }
        if (Environment.GetEnvironmentVariable("VOCALOID_PUBLICATION_RETRY_PROJECT") is { Length: > 0 } projectPath)
            ValidateCapturedProject(projectPath, directory);
    }

    private static void ValidateCapturedProject(string projectPath, string directory)
    {
        string originalHash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(projectPath)));
        string copy = Path.Combine(directory, "captured-source.vpr"); File.Copy(projectPath, copy, true);
        var shell = RuntimeHelpers.GetUninitializedObject(typeof(Yamaha.VOCALOID.Sequence)); GC.SuppressFinalize(shell);
        object?[] args = [copy, null];
        var load = ((Yamaha.VOCALOID.Sequence.LoadResult, bool))AccessTools.Method(typeof(Yamaha.VOCALOID.Sequence), "LoadProjectSequenceFile").Invoke(shell, args)!;
        Check(load.Item1 == Yamaha.VOCALOID.Sequence.LoadResult.Success && args[1] is WIVSMSequence, "Captured project load failed.");
        var source = (WIVSMSequence)args[1]!;
        try
        {
            // Mirror the native Editor's post-load volume/tempo baseline;
            // OpenSequence alone does not initialize a renderable song.
            source.IsGlobalTempoEnabled = false;
            source.BlockRenderingLengthMsec = WIVSMSequence.DefaultBlockRenderingBlockSize;
            source.BlockRenderingPartDurationThresholdMsec = WIVSMSequence.DefaultBlockRenderingPartDurationThresholdMsec;
            Check(source.InterpolatedMasterVolume(), "Captured master interpolation failed.");
            if (source.IsStaged) Check(source.Commit(false), "Captured load baseline commit failed.");
            var parts = source.MidiTracks.SelectMany(t => t.MidiParts).Where(p => p.NumNotes > 0).ToArray();
            var before = JsonSerializer.SerializeToElement(SampledTimingHost.Diagnostics(source));
            var views = parts.ToDictionary(p => (nint)p, p => new UIMidiPart
                { Part = p, Background = Brushes.DodgerBlue, FramePen = new Pen(Brushes.White, 1), Width = 200, Height = 40 });
            int progress = 0, completed = 0;
            using var observer = new WIVSMRendererObserver();
            observer.Progressed += (sender, e) => { progress++; if (views.TryGetValue((nint)e.MidiPart, out var view)) view.OnRendererProgressed(sender, e); };
            observer.Completed += (sender, e) => { completed++; if (views.TryGetValue((nint)e.MidiPart, out var view)) view.OnRendererCompleted(sender, e); };
            Check(source.AddRendererObserver(observer), "Captured project observer failed.");
            source.BlockRenderingEnabled = true;
            source.StopAsyncRendering();
            foreach (var part in parts)
                SampledTimingVsmBinding.InvalidateRenderedContexts(part, part.NotesEnumerator.Chunk(33));
            var watch = Stopwatch.StartNew(); source.StartAsyncRendering();
            bool Valid() => source.IsFinishedRendering && parts.All(p => p.HasValidRenderedWave && p.HasValidRenderedScore);
            while (watch.ElapsedMilliseconds < 45000 && !Valid()) { Pump(source); Thread.Sleep(2); }
            Pump(source);
            var final = JsonSerializer.SerializeToElement(SampledTimingHost.Diagnostics(source));
            Check(Valid() && !final.GetProperty("suspended").GetBoolean(),
                $"Captured project render failed (staged={source.IsStaged}, enabled={source.CanAsyncRendering()}, events={progress}/{completed}): " + final);
            Check(final.GetProperty("storedPlans").GetInt32() == before.GetProperty("storedPlans").GetInt32(), "Captured controls changed.");
            int[] blue = parts.Select(p => BluePixels(views[(nint)p])).ToArray();
            Check(blue.All(n => n > 7000), "Captured project has a completed Part without native blue fill.");
            Check(originalHash == Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(projectPath))), "Source project was modified.");
            File.WriteAllText(Path.Combine(directory, "captured-project-retry.json"), JsonSerializer.Serialize(new
                { parts = parts.Length, storedPlans = final.GetProperty("storedPlans").GetInt32(), progress, completed,
                    bluePixels = blue, elapsedMs = watch.Elapsed.TotalMilliseconds, sourceSha256 = originalHash, sourceUnchanged = true,
                    suspended = false, nativeEnabled = source.CanAsyncRendering() }, new JsonSerializerOptions { WriteIndented = true }));
            source.RemoveRendererObserver(observer);
        }
        finally { source.StopAsyncRendering(); SampledTimingHost.Close(source); source.Close(); }
    }

    [DllImport("kernel32.dll")] private static extern uint WaitForSingleObject(nint handle, uint milliseconds);
    [DllImport("kernel32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool ReleaseMutex(nint handle);
}
#endif
