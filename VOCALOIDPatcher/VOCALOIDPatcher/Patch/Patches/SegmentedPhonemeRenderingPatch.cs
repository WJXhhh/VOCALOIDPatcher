using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using HarmonyLib;
using VOCALOIDPatcher.Config;
using VOCALOIDPatcher.Formats.LibreSvip;
using VOCALOIDPatcher.Formats.LibreSvip.Plugins.Vsqx;
using VOCALOIDPatcher.Utils;
using Yamaha.VOCALOID;
using Yamaha.VOCALOID.VAE;
using Yamaha.VOCALOID.MusicalEditor;
using Yamaha.VOCALOID.VSM;

namespace VOCALOIDPatcher.Patch.Patches;

public sealed class SegmentedPhonemeSequenceCommitPatch : PatchBase
{
    public override string PatchName        => nameof(SegmentedPhonemeSequenceCommitPatch);
    public override Type TargetClass        => typeof(WIVSMSequence);
    public override string TargetMethodName => "Commit";
    public override Type[] ArgumentTypes    => new[] { typeof(bool) };

    [HarmonyPrefix]
    private static void Prefix(WIVSMSequence __instance, out bool __state)
    {
        try
        {
            __state = __instance.IsStaged;
        }
        catch
        {
            __state = true;
        }
    }

    [HarmonyPostfix]
    private static void Postfix(WIVSMSequence __instance, bool __state, ref bool __result)
    {
        if (__result && __state)
            SegmentedPhonemeRenderCoordinator.SequenceChanged(__instance, "commit");
    }
}

public sealed class SegmentedPhonemeSequenceStartRenderingPatch : PatchBase
{
    public override string PatchName        => nameof(SegmentedPhonemeSequenceStartRenderingPatch);
    public override Type TargetClass        => typeof(WIVSMSequence);
    public override string TargetMethodName => "StartAsyncRendering";
    public override Type[] ArgumentTypes    => Type.EmptyTypes;

    [HarmonyPostfix]
    private static void Postfix(WIVSMSequence __instance)
    {
        SegmentedPhonemeRenderCoordinator.SequenceRenderingStarted(__instance);
    }
}

public sealed class SegmentedPhonemeSequenceUndoPatch : PatchBase
{
    public override string PatchName        => nameof(SegmentedPhonemeSequenceUndoPatch);
    public override Type TargetClass        => typeof(WIVSMSequence);
    public override string TargetMethodName => "Undo";
    public override Type[] ArgumentTypes    => Type.EmptyTypes;

    [HarmonyPostfix]
    private static void Postfix(WIVSMSequence __instance)
    {
        SegmentedPhonemeRenderCoordinator.SequenceChanged(__instance, "undo");
    }
}

public sealed class SegmentedPhonemeSequenceRedoPatch : PatchBase
{
    public override string PatchName        => nameof(SegmentedPhonemeSequenceRedoPatch);
    public override Type TargetClass        => typeof(WIVSMSequence);
    public override string TargetMethodName => "Redo";
    public override Type[] ArgumentTypes    => Type.EmptyTypes;

    [HarmonyPostfix]
    private static void Postfix(WIVSMSequence __instance)
    {
        SegmentedPhonemeRenderCoordinator.SequenceChanged(__instance, "redo");
    }
}

public sealed class SegmentedPhonemeWaveFilePathPatch : PatchBase
{
    public override string PatchName        => nameof(SegmentedPhonemeWaveFilePathPatch);
    public override Type TargetClass        => typeof(WIVSMMidiPart);
    public override string TargetMethodName => "get_WaveFilePath";
    public override Type[] ArgumentTypes    => Type.EmptyTypes;

    [HarmonyPostfix]
    private static void Postfix(WIVSMMidiPart __instance, ref string __result)
    {
        if (Settings.ExtendedChinesePinyin
            && SegmentedPhonemeRenderCoordinator.TryGetOverridePath(__instance, out string path))
        {
            __result = path;
        }
    }
}

public sealed class SegmentedPhonemeValidRenderedWavePatch : PatchBase
{
    public override string PatchName        => nameof(SegmentedPhonemeValidRenderedWavePatch);
    public override Type TargetClass        => typeof(WIVSMMidiPart);
    public override string TargetMethodName => "get_HasValidRenderedWave";
    public override Type[] ArgumentTypes    => Type.EmptyTypes;

    [HarmonyPostfix]
    private static void Postfix(WIVSMMidiPart __instance, ref bool __result)
    {
        if (!__result
            && Settings.ExtendedChinesePinyin
            && SegmentedPhonemeRenderCoordinator.TryGetOverridePath(__instance, out string path)
            && File.Exists(path))
        {
            __result = true;
        }
    }
}

public sealed class SegmentedPhonemeScoreFilePathPatch : PatchBase
{
    public override string PatchName        => nameof(SegmentedPhonemeScoreFilePathPatch);
    public override Type TargetClass        => typeof(WIVSMMidiPart);
    public override string TargetMethodName => "get_ScoreFilePath";
    public override Type[] ArgumentTypes    => Type.EmptyTypes;

    [HarmonyPostfix]
    private static void Postfix(WIVSMMidiPart __instance, ref string __result)
    {
        if (Settings.ExtendedChinesePinyin
            && SegmentedPhonemeRenderCoordinator.TryGetOverrideScorePath(__instance, out string path))
        {
            __result = path;
        }
    }
}

public sealed class SegmentedPhonemeValidRenderedScorePatch : PatchBase
{
    public override string PatchName        => nameof(SegmentedPhonemeValidRenderedScorePatch);
    public override Type TargetClass        => typeof(WIVSMMidiPart);
    public override string TargetMethodName => "get_HasValidRenderedScore";
    public override Type[] ArgumentTypes    => Type.EmptyTypes;

    [HarmonyPostfix]
    private static void Postfix(WIVSMMidiPart __instance, ref bool __result)
    {
        if (!__result
            && Settings.ExtendedChinesePinyin
            && SegmentedPhonemeRenderCoordinator.TryGetOverrideScorePath(__instance, out string path)
            && File.Exists(path))
        {
            __result = true;
        }
    }
}

public sealed class SegmentedPhonemeRendererProgressPatch : PatchBase
{
    public override string PatchName        => nameof(SegmentedPhonemeRendererProgressPatch);
    public override Type TargetClass        => typeof(WIVSMMidiPart);
    public override string TargetMethodName => "get_RendererProgress";
    public override Type[] ArgumentTypes    => Type.EmptyTypes;

    [HarmonyPostfix]
    private static void Postfix(WIVSMMidiPart __instance, ref VSMRendererProgress __result)
    {
        if (Settings.ExtendedChinesePinyin
            && SegmentedPhonemeRenderCoordinator.TryGetOverridePath(__instance, out string path)
            && File.Exists(path))
        {
            __result = VSMRendererProgressExtension.FullProgress;
        }
    }
}

internal static class SegmentedPhonemeRenderCoordinator
{
    private const double CrossfadeSeconds = 0.005;
    private const int MinimumAudiblePeak = 128;
    private const int SequenceScanDebounceMilliseconds = 100;
    private const int NativeCloseQuietMilliseconds = 1500;
    private static long _nextJobId;
    private static readonly object SyncRoot = new();
    private static readonly Dictionary<nint, RenderJob> Jobs = new();
    private static readonly Dictionary<nint, OverrideState> OverridePaths = new();
    private static readonly HashSet<WIVSMSequence> InternalSequences = new(
        ReferenceEqualityComparer.Instance);
    private static readonly HashSet<nint> PendingSequenceScans = new();
    private static readonly Dictionary<nint, CancellationTokenSource> PendingSequenceScanDelays = new();
    private static readonly HashSet<nint> PendingRendererPumps = new();
    private static readonly List<WIVSMSequence> PendingCloseSequences = new();
    private static readonly SemaphoreSlim NativeRenderSessionGate = new(1, 1);
    private static readonly SemaphoreSlim NativeCleanupGate = new(1, 1);
    private static long _lastNativeForegroundActivity = Environment.TickCount64;
    private static bool _nativeCloseActive;
    private static readonly MethodInfo? RegisterAudioPlacementMethod = AccessTools.Method(
        typeof(AudioPlayer),
        "RegisterAudioPlacementWithFile",
        new[] { typeof(WIVSMSequence), typeof(WIVSMMidiPart) });
    private static readonly PropertyInfo? PlacementManagerProperty = AccessTools.Property(
        typeof(AudioPlayer),
        "placementManager");

    static SegmentedPhonemeRenderCoordinator()
    {
        Settings.ExtendedChinesePinyinChanged += OnExtendedChinesePinyinChanged;
        ExtendedPinyinDiagnosticLog.Write(
            "session",
            $"native split renderer initialized; log={ExtendedPinyinDiagnosticLog.LogPath}");
    }

    private sealed record SplitPlan(
        int NoteIndex,
        long RelPosTick,
        long DurationTick,
        long SplitRelTick,
        string Lyric,
        string Phonemes,
        string FirstPhoneme,
        string SecondPhoneme);

    private sealed record OverrideState(
        string Path,
        string ScorePath,
        WIVSMMidiPart Part,
        IReadOnlyList<SplitPlan> Plans);

    private sealed record NativeCarrier(string Lyric, string Phonemes, string[] Tokens);
    private sealed record CarrierTiming(
        SplitPlan Plan,
        long FirstStartRelTick,
        long FirstEndRelTick,
        long SecondStartRelTick);

    internal readonly record struct WavePhonemeSpan(
        long StartRelTick,
        long EndRelTick,
        string Phoneme);

    private sealed class RenderJob
    {
        public RenderJob(WIVSMSequence sequence, WIVSMMidiPart part, List<SplitPlan> plans)
        {
            Id = Interlocked.Increment(ref _nextJobId);
            Sequence = sequence;
            Part = part;
            Plans = plans;
        }

        private int _started;
        public long Id { get; }
        public volatile bool Superseded;
        public WIVSMSequence Sequence { get; }
        public WIVSMMidiPart Part { get; }
        public List<SplitPlan> Plans { get; }

        public bool IsStarted => Volatile.Read(ref _started) != 0;

        public bool TryStart()
            => Interlocked.CompareExchange(ref _started, 1, 0) == 0;
    }

    public static void SequenceRenderingStarted(WIVSMSequence? sequence)
    {
        if (sequence == null || !Settings.ExtendedChinesePinyin)
            return;

        nint sequenceKey = (nint)sequence;
        bool scanPending;
        lock (SyncRoot)
        {
            if (InternalSequences.Contains(sequence))
                return;
            scanPending = PendingSequenceScans.Contains(sequenceKey);
        }

        if (scanPending)
        {
            ExtendedPinyinDiagnosticLog.Write(
                "trigger-skipped",
                $"sequence=0x{sequenceKey:X}; source=render-start; reason=scan-pending");
            return;
        }

        nint[] partKeys;
        try
        {
            partKeys = sequence.MidiParts.Select(part => (nint)part).ToArray();
        }
        catch
        {
            SequenceChanged(sequence, "render-start");
            return;
        }

        string? skipReason = null;
        lock (SyncRoot)
        {
            if (partKeys.Any(key => Jobs.ContainsKey(key)))
                skipReason = "job-active";
            else if (partKeys.Any(key => OverridePaths.ContainsKey(key)))
                skipReason = "override-current";
        }

        if (skipReason != null)
        {
            ExtendedPinyinDiagnosticLog.Write(
                "trigger-skipped",
                $"sequence=0x{sequenceKey:X}; source=render-start; reason={skipReason}");
            return;
        }

        // StartAsyncRendering is needed for an unstaged project load. Normal edits
        // have already entered through Commit/Undo/Redo and must not start a
        // second full carrier-rendering job.
        SequenceChanged(sequence, "render-start");
    }

    public static void SequenceChanged(WIVSMSequence? sequence, string source)
    {
        if (sequence == null || !Settings.ExtendedChinesePinyin)
            return;

        nint key = (nint)sequence;
        if (key == 0)
            return;

        var cancellation = new CancellationTokenSource();
        CancellationTokenSource? displacedCancellation = null;
        lock (SyncRoot)
        {
            if (InternalSequences.Contains(sequence))
            {
                cancellation.Dispose();
                return;
            }

            PendingSequenceScans.Add(key);
            _lastNativeForegroundActivity = Environment.TickCount64;
            if (PendingSequenceScanDelays.Remove(key, out CancellationTokenSource? current))
                displacedCancellation = current;
            PendingSequenceScanDelays[key] = cancellation;
        }

        displacedCancellation?.Cancel();
        ExtendedPinyinDiagnosticLog.Write(
            "trigger",
            $"sequence=0x{key:X}; source={source}; scan queued; "
            + $"debounceMs={SequenceScanDebounceMilliseconds}; replaced={displacedCancellation != null}");

        void Scan()
        {
            try
            {
                if (cancellation.IsCancellationRequested
                    || !Settings.ExtendedChinesePinyin
                    || !sequence.IsOpen)
                {
                    return;
                }

                lock (SyncRoot)
                {
                    if (InternalSequences.Contains(sequence))
                        return;
                }

                foreach (WIVSMMidiTrack track in sequence.MidiTracks)
                {
                    foreach (WIVSMMidiPart part in track.MidiParts)
                    {
                        if (cancellation.IsCancellationRequested)
                            return;
                        Begin(part);
                    }
                }
            }
            catch (Exception e)
            {
                ExtendedPinyinDiagnosticLog.Write(
                    "scan-failed",
                    $"sequence=0x{key:X}; {e.GetType().Name}: {e.Message}");
            }
        }

        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(
                        SequenceScanDebounceMilliseconds,
                        cancellation.Token)
                    .ConfigureAwait(false);

                Application? application = Application.Current;
                if (application == null || application.Dispatcher.HasShutdownStarted)
                {
                    cancellation.Token.ThrowIfCancellationRequested();
                    Scan();
                }
                else
                {
                    await WaitForNativeCloseAsync(cancellation.Token).ConfigureAwait(false);
                    await application.Dispatcher.InvokeAsync(
                            Scan,
                            DispatcherPriority.Background,
                            cancellation.Token)
                        .Task
                        .ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception e)
            {
                ExtendedPinyinDiagnosticLog.Write(
                    "scan-schedule-failed",
                    $"sequence=0x{key:X}; {e.GetType().Name}: {e.Message}");
            }
            finally
            {
                lock (SyncRoot)
                {
                    if (PendingSequenceScanDelays.TryGetValue(
                            key,
                            out CancellationTokenSource? current)
                        && ReferenceEquals(current, cancellation))
                    {
                        PendingSequenceScanDelays.Remove(key);
                        PendingSequenceScans.Remove(key);
                    }
                }

                cancellation.Dispose();
            }
        });
    }

    private static void Begin(WIVSMMidiPart? part)
    {
        if (part == null)
            return;

        nint key = (nint)part;
        Abandon(RemoveJob(key));
        if (!Settings.ExtendedChinesePinyin)
            return;

        try
        {
            WIVSMSequence? sourceSequence = part.Sequence;
            WIVSMSequenceManager? manager = App.SequenceManager;
            if (sourceSequence == null
                || manager == null
                || !TryLocatePart(sourceSequence, part, out int trackIndex, out int partIndex))
            {
                ExtendedPinyinDiagnosticLog.Write(
                    "prepare-skipped",
                    $"part=0x{key:X}; sequence={sourceSequence != null}; manager={manager != null}; "
                    + "part location unavailable");
                RemoveOverrideAndRestoreNative(part);
                return;
            }

            if (!TryBuildPlans(sourceSequence, part, out List<SplitPlan> plans))
            {
                RemoveOverrideAndRestoreNative(part);
                return;
            }

            var sequenceData = new VSMSequenceData
            {
                SamplingRate = sourceSequence.GetSamplingRate(),
                MaxNumTracks = Math.Max(32UL, sourceSequence.NumTrack),
                MaxUndoCount = 0,
            };
            WIVSMSequence? duplicate = manager.DuplicateSequence(sourceSequence, sequenceData);
            if (duplicate == null || !duplicate.IsOpen)
            {
                ExtendedPinyinDiagnosticLog.Write(
                    "duplicate-failed",
                    $"part=0x{key:X}; result={(duplicate == null ? "null" : duplicate.LastError.ToString())}");
                if (duplicate != null)
                    CloseSequenceSafely(duplicate);
                return;
            }

            lock (SyncRoot)
                InternalSequences.Add(duplicate);

            if (!TryGetPart(duplicate, trackIndex, partIndex, out WIVSMMidiPart? duplicatePart))
            {
                CloseSequenceSafely(duplicate);
                return;
            }

            var job = new RenderJob(duplicate, duplicatePart!, plans);
            RenderJob? displaced = null;
            lock (SyncRoot)
            {
                if (Jobs.Remove(key, out RenderJob? current))
                    displaced = current;
                Jobs[key] = job;
            }

            Abandon(displaced);
            string pairs = string.Join(
                ",",
                plans.Select(plan => $"{plan.FirstPhoneme}+{plan.SecondPhoneme}@{plan.SplitRelTick}"));
            ExtendedPinyinDiagnosticLog.Write(
                "prepared",
                $"job={job.Id}; sourcePart=0x{key:X}; notes={plans.Count}; pairs={pairs}");
            Complete(part);
        }
        catch (Exception e)
        {
            ExtendedPinyinDiagnosticLog.Write(
                "prepare-failed",
                $"part=0x{key:X}; {e.GetType().Name}: {e.Message}");
            // The native render remains authoritative when the fallback cannot be prepared.
        }
    }

    private static void Complete(WIVSMMidiPart? part)
    {
        if (part == null)
            return;

        RenderJob? job;
        lock (SyncRoot)
            Jobs.TryGetValue((nint)part, out job);
        if (job == null)
            return;

        if (job.Superseded
            || !Settings.ExtendedChinesePinyin
            || !SourceStillMatches(part, job.Plans))
        {
            RemoveJobIfCurrent((nint)part, job);
            Abandon(job);
            return;
        }

        if (job.TryStart())
            _ = Task.Run(() => RenderAndPublish(job, part));
    }

    private static RenderJob? RemoveJob(nint key)
    {
        lock (SyncRoot)
        {
            Jobs.Remove(key, out RenderJob? job);
            return job;
        }
    }

    private static void Abandon(RenderJob? job)
    {
        if (job == null)
            return;

        job.Superseded = true;
        ExtendedPinyinDiagnosticLog.Write(
            "superseded",
            $"job={job.Id}; started={job.IsStarted}");
        if (!job.IsStarted)
            CloseSequenceSafely(job.Sequence);
    }

    public static bool TryGetOverridePath(WIVSMMidiPart part, out string path)
    {
        lock (SyncRoot)
        {
            if (OverridePaths.TryGetValue((nint)part, out OverrideState? state))
            {
                path = state.Path;
                return true;
            }

            path = string.Empty;
            return false;
        }
    }

    public static bool TryGetOverrideScorePath(WIVSMMidiPart part, out string path)
    {
        lock (SyncRoot)
        {
            if (OverridePaths.TryGetValue((nint)part, out OverrideState? state)
                && !string.IsNullOrEmpty(state.ScorePath))
            {
                path = state.ScorePath;
                return true;
            }

            path = string.Empty;
            return false;
        }
    }

    public static bool TryGetWavePhonemeSpans(
        WIVSMMidiPart part,
        WIVSMNote note,
        out WavePhonemeSpan[] spans)
    {
        spans = Array.Empty<WavePhonemeSpan>();
        if (!Settings.ExtendedChinesePinyin)
            return false;

        long relPosTick = note.RelPosTick.Value;
        long durationTick = note.DurationTick.Value;
        string phonemes = CanonicalizePhonemes(note.Phonemes);
        lock (SyncRoot)
        {
            if (!OverridePaths.TryGetValue((nint)part, out OverrideState? state))
                return false;

            foreach (SplitPlan plan in state.Plans)
            {
                if (plan.RelPosTick != relPosTick
                    || plan.DurationTick != durationTick
                    || !string.Equals(plan.Phonemes, phonemes, StringComparison.Ordinal))
                {
                    continue;
                }

                spans = new[]
                {
                    new WavePhonemeSpan(plan.RelPosTick, plan.SplitRelTick, plan.FirstPhoneme),
                    new WavePhonemeSpan(
                        plan.SplitRelTick,
                        plan.RelPosTick + plan.DurationTick,
                        plan.SecondPhoneme),
                };
                return true;
            }
        }

        return false;
    }

    private static OverrideState? RemoveOverride(nint key)
    {
        lock (SyncRoot)
        {
            OverridePaths.Remove(key, out OverrideState? state);
            return state;
        }
    }

    private static void RemoveOverrideFiles(OverrideState state)
    {
        FileManager.RemoveFile(state.Path);
        FileManager.RemoveFile(state.ScorePath);
    }

    private static void RemoveOverrideAndRestoreNative(WIVSMMidiPart part)
    {
        OverrideState? state = RemoveOverride((nint)part);
        if (state == null)
            return;

        RemoveOverrideFiles(state);
        RefreshNativePlaybackAndWaveform(part);
    }

    private static void RemoveJobIfCurrent(nint key, RenderJob job)
    {
        lock (SyncRoot)
        {
            if (Jobs.TryGetValue(key, out RenderJob? current) && ReferenceEquals(current, job))
            {
                Jobs.Remove(key);
                _lastNativeForegroundActivity = Environment.TickCount64;
            }
        }
    }

    private static void CleanupNativeSession(
        WIVSMSequence sequence,
        WIVSMRendererObserver? observer,
        AsyncRenderState renderState,
        bool renderingStarted,
        bool observerAdded,
        bool renderSettled,
        long jobId)
    {
        ExtendedPinyinDiagnosticLog.Write(
            "native-session-cleanup",
            $"job={jobId}; queued=background; settled={renderSettled}");

        _ = Task.Run(async () =>
        {
            var totalTimer = Stopwatch.StartNew();
            long teardownMilliseconds = 0;
            long closeWaitMilliseconds = 0;
            long closeMilliseconds = 0;
            bool closed = false;
            bool teardownGateEntered = false;
            bool renderSessionReleased = false;
            bool closeWindowEntered = false;
            try
            {
                WaitForRendererPumpDrain(sequence, jobId);
                await NativeCleanupGate.WaitAsync().ConfigureAwait(false);
                teardownGateEntered = true;

                var teardownTimer = Stopwatch.StartNew();
                try
                {
                    if (renderingStarted && sequence.IsOpen)
                        sequence.StopAsyncRendering();
                }
                catch (Exception e)
                {
                    ExtendedPinyinDiagnosticLog.Write(
                        "native-session-stop-failed",
                        $"job={jobId}; {e.GetType().Name}: {e.Message}");
                }

                try
                {
                    if (observerAdded && sequence.IsOpen && observer != null)
                        sequence.RemoveRendererObserver(observer);
                }
                catch (Exception e)
                {
                    ExtendedPinyinDiagnosticLog.Write(
                        "native-observer-remove-failed",
                        $"job={jobId}; {e.GetType().Name}: {e.Message}");
                }

                if (observer != null)
                {
                    try
                    {
                        observer.Dispose();
                    }
                    catch
                    {
                    }
                }

                renderState.Dispose();
                teardownTimer.Stop();
                teardownMilliseconds = teardownTimer.ElapsedMilliseconds;
                NativeCleanupGate.Release();
                teardownGateEntered = false;
                NativeRenderSessionGate.Release();
                renderSessionReleased = true;

                var closeWaitTimer = Stopwatch.StartNew();
                await EnterNativeCloseWindowAsync().ConfigureAwait(false);
                closeWindowEntered = true;
                closeWaitTimer.Stop();
                closeWaitMilliseconds = closeWaitTimer.ElapsedMilliseconds;
                var closeTimer = Stopwatch.StartNew();
                closed = TryCloseSequenceDirect(sequence);
                closeTimer.Stop();
                closeMilliseconds = closeTimer.ElapsedMilliseconds;
            }
            catch (Exception e)
            {
                ExtendedPinyinDiagnosticLog.Write(
                    "native-session-close-failed",
                    $"job={jobId}; {e.GetType().Name}: {e.Message}");
            }
            finally
            {
                if (teardownGateEntered)
                    NativeCleanupGate.Release();
                if (!renderSessionReleased)
                    NativeRenderSessionGate.Release();
                if (closeWindowEntered)
                    ExitNativeCloseWindow();

                totalTimer.Stop();
                ExtendedPinyinDiagnosticLog.Write(
                    "native-session-cleanup-complete",
                    $"job={jobId}; closed={closed}; teardownMs={teardownMilliseconds}; "
                    + $"closeWaitMs={closeWaitMilliseconds}; closeMs={closeMilliseconds}; "
                    + $"totalMs={totalTimer.ElapsedMilliseconds}");
            }

            if (!closed)
                CloseSequenceSafely(sequence);
        });
    }

    private static void CloseSequenceSafely(WIVSMSequence sequence)
    {
        bool queued = false;
        lock (SyncRoot)
        {
            bool tracked = false;
            foreach (WIVSMSequence pending in PendingCloseSequences)
            {
                if (ReferenceEquals(pending, sequence))
                {
                    tracked = true;
                    break;
                }
            }

            if (!tracked)
            {
                PendingCloseSequences.Add(sequence);
                queued = true;
            }
        }

        if (!queued)
            return;

        ExtendedPinyinDiagnosticLog.Write(
            "sequence-close-queued",
            $"sequence=0x{(nint)sequence:X}; background=True");
        _ = Task.Run(() => RetrySequenceClose(sequence));
    }

    private static bool TryCloseSequenceDirect(WIVSMSequence sequence)
    {
        try
        {
            if (!sequence.IsOpen)
            {
                lock (SyncRoot)
                    InternalSequences.Remove(sequence);
                return true;
            }
            if (sequence.IsStaged && !sequence.Rollback())
                return false;
            if (sequence.Close())
            {
                lock (SyncRoot)
                    InternalSequences.Remove(sequence);
                ExtendedPinyinDiagnosticLog.Write("sequence-close", "duplicate closed");
                return true;
            }

            ExtendedPinyinDiagnosticLog.Write(
                "sequence-close-failed",
                $"result={sequence.LastError}");
            return false;
        }
        catch (Exception e)
        {
            ExtendedPinyinDiagnosticLog.Write(
                "sequence-close-failed",
                $"{e.GetType().Name}: {e.Message}");
            return false;
        }
    }

    private static async Task RetrySequenceClose(WIVSMSequence sequence)
    {
        bool closed = false;
        try
        {
            for (int attempt = 1; attempt <= 5 && !closed; attempt++)
            {
                WaitForRendererPumpDrain(sequence, null);
                bool closeWindowEntered = false;
                try
                {
                    await EnterNativeCloseWindowAsync().ConfigureAwait(false);
                    closeWindowEntered = true;
                    closed = TryCloseSequenceDirect(sequence);
                }
                finally
                {
                    if (closeWindowEntered)
                        ExitNativeCloseWindow();
                }

                if (!closed && attempt < 5)
                    await Task.Delay(attempt * 100).ConfigureAwait(false);
            }
        }
        catch (Exception e)
        {
            ExtendedPinyinDiagnosticLog.Write(
                "sequence-close-retry-failed",
                $"{e.GetType().Name}: {e.Message}");
        }

        if (!closed)
            return;

        lock (SyncRoot)
        {
            for (int index = PendingCloseSequences.Count - 1; index >= 0; index--)
            {
                if (ReferenceEquals(PendingCloseSequences[index], sequence))
                    PendingCloseSequences.RemoveAt(index);
            }
        }
    }

    private static async Task EnterNativeCloseWindowAsync()
    {
        while (true)
        {
            int delayMilliseconds;
            lock (SyncRoot)
            {
                long elapsed = Environment.TickCount64 - _lastNativeForegroundActivity;
                bool foregroundBusy = Jobs.Count != 0 || PendingSequenceScans.Count != 0;
                if (!foregroundBusy
                    && !_nativeCloseActive
                    && elapsed >= NativeCloseQuietMilliseconds)
                {
                    delayMilliseconds = 0;
                }
                else
                {
                    long remainingQuiet = Math.Max(0, NativeCloseQuietMilliseconds - elapsed);
                    delayMilliseconds = (int)Math.Clamp(remainingQuiet, 25, 100);
                }
            }

            if (delayMilliseconds != 0)
            {
                await Task.Delay(delayMilliseconds).ConfigureAwait(false);
                continue;
            }

            await NativeCleanupGate.WaitAsync().ConfigureAwait(false);
            lock (SyncRoot)
            {
                long elapsed = Environment.TickCount64 - _lastNativeForegroundActivity;
                if (Jobs.Count == 0
                    && PendingSequenceScans.Count == 0
                    && !_nativeCloseActive
                    && elapsed >= NativeCloseQuietMilliseconds)
                {
                    _nativeCloseActive = true;
                    return;
                }
            }

            NativeCleanupGate.Release();
        }
    }

    private static void ExitNativeCloseWindow()
    {
        lock (SyncRoot)
            _nativeCloseActive = false;
        NativeCleanupGate.Release();
    }

    private static async Task WaitForNativeCloseAsync(CancellationToken cancellationToken)
    {
        while (true)
        {
            lock (SyncRoot)
            {
                if (!_nativeCloseActive)
                    return;
            }

            await Task.Delay(10, cancellationToken).ConfigureAwait(false);
        }
    }

    private static void WaitForRendererPumpDrain(WIVSMSequence sequence, long? jobId)
    {
        nint key = (nint)sequence;
        DateTime deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (DateTime.UtcNow < deadline)
        {
            lock (SyncRoot)
            {
                if (!PendingRendererPumps.Contains(key))
                    return;
            }

            Thread.Sleep(5);
        }

        ExtendedPinyinDiagnosticLog.Write(
            "native-pump-drain-timeout",
            $"job={(jobId.HasValue ? jobId.Value.ToString() : "retry")}; sequence=0x{key:X}");
    }

    private static void RenderAndPublish(RenderJob job, WIVSMMidiPart sourcePart)
    {
        string? outputPath = null;
        string? scorePath = null;
        try
        {
            outputPath = RenderSplitPart(job, job.Sequence, job.Part, job.Plans, out scorePath);
            if (string.IsNullOrEmpty(outputPath)
                || job.Superseded
                || !Settings.ExtendedChinesePinyin
                || !SourceStillMatches(sourcePart, job.Plans))
            {
                ExtendedPinyinDiagnosticLog.Write(
                    "render-rejected",
                    $"job={job.Id}; hasWave={!string.IsNullOrEmpty(outputPath)}; "
                    + $"superseded={job.Superseded}; enabled={Settings.ExtendedChinesePinyin}");
                return;
            }

            nint key = (nint)sourcePart;
            OverrideState? displacedState = null;
            bool published = false;
            lock (SyncRoot)
            {
                if (Jobs.TryGetValue(key, out RenderJob? current)
                    && ReferenceEquals(current, job)
                    && !job.Superseded
                    && Settings.ExtendedChinesePinyin)
                {
                    if (OverridePaths.Remove(key, out OverrideState? displaced))
                        displacedState = displaced;
                    OverridePaths[key] = new OverrideState(
                        outputPath,
                        scorePath ?? string.Empty,
                        sourcePart,
                        job.Plans);
                    Jobs.Remove(key);
                    _lastNativeForegroundActivity = Environment.TickCount64;
                    published = true;
                }
            }

            if (displacedState != null)
                RemoveOverrideFiles(displacedState);
            if (!published)
                return;

            string publishedPath = outputPath;
            string publishedScorePath = scorePath ?? string.Empty;
            outputPath = null;
            scorePath = null;
            RefreshPlaybackAndWaveform(sourcePart, publishedPath, job.Id);
            ExtendedPinyinDiagnosticLog.Write(
                "published",
                $"job={job.Id}; sourcePart=0x{key:X}; waveBytes={SafeFileLength(publishedPath)}; "
                + $"scorePresent={!string.IsNullOrEmpty(publishedScorePath)}; "
                + $"scoreBytes={SafeFileLength(publishedScorePath)}");
        }
        catch (Exception e)
        {
            ExtendedPinyinDiagnosticLog.Write(
                "render-failed",
                $"job={job.Id}; {e.GetType().Name}: {e.Message}");
            // Keep the native rendered file and placement on any fallback failure.
        }
        finally
        {
            RemoveJobIfCurrent((nint)sourcePart, job);
            if (!string.IsNullOrEmpty(outputPath))
                FileManager.RemoveFile(outputPath);
            if (!string.IsNullOrEmpty(scorePath))
                FileManager.RemoveFile(scorePath);
        }
    }

    private static void RefreshPlaybackAndWaveform(WIVSMMidiPart part, string path, long jobId)
    {
        Application? application = Application.Current;
        if (application == null)
        {
            ExtendedPinyinDiagnosticLog.Write(
                "placement-failed",
                $"job={jobId}; Application.Current is null");
            return;
        }

        application.Dispatcher.BeginInvoke(new Action(() =>
        {
            try
            {
                if (!Settings.ExtendedChinesePinyin
                    || !TryGetOverridePath(part, out string currentPath)
                    || !string.Equals(currentPath, path, StringComparison.Ordinal))
                {
                    return;
                }

                WIVSMSequence? sequence = part.Sequence;
                AudioPlayer? audioPlayer = App.AudioPlayer;
                var placementManager = audioPlayer == null
                    ? null
                    : PlacementManagerProperty?.GetValue(audioPlayer) as AudioPlacementManager;
                bool placementAccepted = false;
                bool placementProbe = false;
                if (sequence != null && placementManager != null)
                {
                    var timeRange = AudioPlacementBuilder.CreateTimeRange(
                        sequence,
                        part,
                        PlacementStorageType.File,
                        null);
                    using var probe = AudioPlacementBuilder.CreateAudioPlacement(
                        sequence,
                        part,
                        PlacementStorageType.File,
                        null,
                        timeRange);
                    placementProbe = probe != null;
                    placementAccepted = placementManager.AddOrReplacePlacement(
                        sequence,
                        part,
                        PlacementStorageType.File);
                }
                else if (sequence != null && audioPlayer != null && RegisterAudioPlacementMethod != null)
                {
                    RegisterAudioPlacementMethod.Invoke(
                        audioPlayer,
                        new object[] { sequence, part });
                }

                string visiblePath = part.WaveFilePath;
                string visibleScorePath = part.ScoreFilePath;
                bool placementPresent = placementManager?.GetFirstPlacement(part) != null;
                int pitchViewsRefreshed = RefreshPitchCurves(part);
                ExtendedPinyinDiagnosticLog.Write(
                    "placement",
                    $"job={jobId}; sequence={sequence != null}; player={audioPlayer != null}; "
                    + $"manager={placementManager != null}; probe={placementProbe}; "
                    + $"accepted={placementAccepted}; method={RegisterAudioPlacementMethod != null}; "
                    + $"placementPresent={placementPresent}; "
                    + $"overrideVisible={string.Equals(visiblePath, path, StringComparison.Ordinal)}; "
                    + $"validWave={part.HasValidRenderedWave}; "
                    + $"fileExists={File.Exists(path)}; bytes={SafeFileLength(path)}; "
                    + $"scoreOverrideVisible={TryGetOverrideScorePath(part, out string scorePath) && string.Equals(visibleScorePath, scorePath, StringComparison.Ordinal)}; "
                    + $"validScore={part.HasValidRenderedScore}; scoreExists={File.Exists(visibleScorePath)}; "
                    + $"scoreBytes={SafeFileLength(visibleScorePath)}; pitchViews={pitchViewsRefreshed}");

                WaveformSvState.Invalidate();
                RenderedWaveCachePatch.InvalidateAndRefreshPart(part);
            }
            catch (Exception e)
            {
                ExtendedPinyinDiagnosticLog.Write(
                    "placement-failed",
                    $"job={jobId}; {e.GetType().Name}: {e.Message}");
                // The override remains available to the next normal playback or redraw request.
            }
        }), DispatcherPriority.Background);
    }

    private static void RefreshNativePlaybackAndWaveform(WIVSMMidiPart part)
    {
        Application? application = Application.Current;
        if (application == null)
            return;

        application.Dispatcher.BeginInvoke(new Action(() =>
        {
            try
            {
                if (TryGetOverridePath(part, out _))
                    return;

                WIVSMSequence? sequence = part.Sequence;
                AudioPlayer? audioPlayer = App.AudioPlayer;
                if (sequence != null && audioPlayer != null && RegisterAudioPlacementMethod != null)
                {
                    RegisterAudioPlacementMethod.Invoke(
                        audioPlayer,
                        new object[] { sequence, part });
                }

                WaveformSvState.Invalidate();
                RenderedWaveCachePatch.InvalidateAndRefreshPart(part);
                RefreshPitchCurves(part);
            }
            catch
            {
                // The next native playback or redraw request will refresh this part.
            }
        }), DispatcherPriority.Background);
    }

    private static void OnExtendedChinesePinyinChanged(bool enabled)
    {
        if (enabled)
            return;

        List<RenderJob> jobs;
        List<OverrideState> overrides;
        List<CancellationTokenSource> pendingScans;
        lock (SyncRoot)
        {
            jobs = new List<RenderJob>(Jobs.Values);
            overrides = new List<OverrideState>(OverridePaths.Values);
            pendingScans = new List<CancellationTokenSource>(PendingSequenceScanDelays.Values);
            Jobs.Clear();
            OverridePaths.Clear();
            PendingSequenceScanDelays.Clear();
            PendingSequenceScans.Clear();
        }

        foreach (CancellationTokenSource cancellation in pendingScans)
            cancellation.Cancel();
        foreach (RenderJob job in jobs)
            Abandon(job);
        foreach (OverrideState state in overrides)
            RemoveOverrideFiles(state);

        WaveformSvState.Invalidate();
        Application? application = Application.Current;
        if (application == null || overrides.Count == 0)
            return;

        application.Dispatcher.BeginInvoke(new Action(() =>
        {
            foreach (OverrideState state in overrides)
            {
                try
                {
                    WIVSMSequence? sequence = state.Part.Sequence;
                    AudioPlayer? audioPlayer = App.AudioPlayer;
                    if (sequence != null && audioPlayer != null && RegisterAudioPlacementMethod != null)
                    {
                        RegisterAudioPlacementMethod.Invoke(
                            audioPlayer,
                            new object[] { sequence, state.Part });
                    }

                    RenderedWaveCachePatch.InvalidateAndRefreshPart(state.Part);
                    RefreshPitchCurves(state.Part);
                }
                catch
                {
                }
            }
        }), DispatcherPriority.Background);
    }

    private static int RefreshPitchCurves(WIVSMMidiPart part)
    {
        Application? application = Application.Current;
        if (application == null)
            return 0;

        int count = 0;
        foreach (Window window in application.Windows)
        {
            foreach (PianorollView view in ShowOtherTracksNotesPatch.FindVisualChildren<PianorollView>(window))
            {
                if (view.DataContext is not MusicalEditorViewModel vm
                    || vm.ActivePart == null
                    || (nint)vm.ActivePart != (nint)part)
                {
                    continue;
                }

                view.UpdatePitchCurve();
                vm.UpdateViewport();
                count++;
            }
        }

        return count;
    }

    private static bool TryBuildPlans(
        WIVSMSequence sequence,
        WIVSMMidiPart part,
        out List<SplitPlan> plans)
    {
        plans = new List<SplitPlan>();
        List<WIVSMNote> notes = part.Notes;
        for (int index = 0; index < notes.Count; index++)
        {
            WIVSMNote note = notes[index];
            if (note.LangID != (int)VSMLanguageID.Chinese
                || !ChinesePinyinPhonemeConverter.TryConvertToken(
                    note.Lyric,
                    out ChinesePinyinSyllable syllable))
            {
                continue;
            }

            if (!ChinesePinyinPhonemeConverter.TryGetSegmentedSynthesisPhonemes(
                    syllable,
                    out string first,
                    out string second))
            {
                if (syllable.RequiresOverride)
                {
                    ExtendedPinyinDiagnosticLog.Write(
                        "plan-skipped",
                        $"part=0x{(nint)part:X}; note={index}; phonemes={syllable.Phonemes}; "
                        + "not a two-phoneme synthesis fallback");
                }
                continue;
            }

            string phonemes = CanonicalizePhonemes(note.Phonemes);
            if (!string.Equals(phonemes, syllable.Phonemes, StringComparison.Ordinal))
            {
                ExtendedPinyinDiagnosticLog.Write(
                    "plan-skipped",
                    $"part=0x{(nint)part:X}; note={index}; expected={syllable.Phonemes}; "
                    + $"actual={phonemes}; reason=phoneme-mismatch");
                continue;
            }

            if (note.DurationTick.Value < Yamaha.VOCALOID.Design.Sequence.minNoteTick * 2L)
            {
                ExtendedPinyinDiagnosticLog.Write(
                    "plan-skipped",
                    $"part=0x{(nint)part:X}; note={index}; phonemes={phonemes}; "
                    + $"duration={note.DurationTick.Value}; reason=too-short");
                continue;
            }

            long splitTick = GetSplitTick(note);

            plans.Add(new SplitPlan(
                index,
                note.RelPosTick.Value,
                note.DurationTick.Value,
                splitTick,
                note.Lyric,
                phonemes,
                first,
                second));
        }

        return plans.Count > 0;
    }

    private static long GetSplitTick(WIVSMNote note)
    {
        long start = note.RelPosTick.Value;
        long end = note.RelEndTick.Value;
        long minimum = Yamaha.VOCALOID.Design.Sequence.minNoteTick;
        try
        {
            List<int> positions = note.GetPhonemePositions();
            if (positions.Count >= 3)
            {
                WIVSMMidiPart? part = note.Parent;
                if (part != null)
                {
                    long candidate = note.GetAbsPositionFromNoteBaseTick(positions[1]).Value
                                     - part.AbsPosTick.Value;
                    if (candidate > start && candidate < end)
                        return Math.Clamp(candidate, start + minimum, end - minimum);
                }
            }
        }
        catch
        {
        }

        return Math.Clamp(start + Math.Max(minimum, (end - start) / 4L), start + minimum, end - minimum);
    }

    private static bool TryLocatePart(
        WIVSMSequence sequence,
        WIVSMMidiPart target,
        out int trackIndex,
        out int partIndex)
    {
        trackIndex = -1;
        partIndex = -1;
        List<WIVSMMidiTrack> tracks = sequence.MidiTracks;
        for (int i = 0; i < tracks.Count; i++)
        {
            List<WIVSMMidiPart> parts = tracks[i].MidiParts;
            for (int j = 0; j < parts.Count; j++)
            {
                if (!parts[j].Equals(target))
                    continue;

                trackIndex = i;
                partIndex = j;
                return true;
            }
        }

        return false;
    }

    private static bool TryGetPart(
        WIVSMSequence sequence,
        int trackIndex,
        int partIndex,
        out WIVSMMidiPart? part)
    {
        part = null;
        List<WIVSMMidiTrack> tracks = sequence.MidiTracks;
        if (trackIndex < 0 || trackIndex >= tracks.Count)
            return false;

        List<WIVSMMidiPart> parts = tracks[trackIndex].MidiParts;
        if (partIndex < 0 || partIndex >= parts.Count)
            return false;

        part = parts[partIndex];
        return true;
    }

    private sealed class AsyncRenderState : IDisposable
    {
        public AutoResetEvent Signal { get; } = new(false);
        public int Status;

        public void Reset()
        {
            Volatile.Write(ref Status, 0);
            while (Signal.WaitOne(0))
            {
            }
        }

        public void Dispose()
        {
            Signal.Dispose();
        }
    }

    private readonly record struct WaveStats(
        long Samples,
        int Channels,
        int SampleRate,
        int Peak,
        long FileBytes);

    private static string? RenderSplitPart(
        RenderJob job,
        WIVSMSequence sequence,
        WIVSMMidiPart part,
        IReadOnlyList<SplitPlan> plans,
        out string? scorePath)
    {
        scorePath = null;
        string outputPath = FileManager.TemporaryWaveFilePath;
        string firstCarrierPath = FileManager.TemporaryWaveFilePath;
        string secondCarrierPath = FileManager.TemporaryWaveFilePath;
        string scoreOutputPath = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.score");
        WIVSMRendererObserver? observer = null;
        bool observerAdded = false;
        bool renderingStarted = false;
        bool renderSettled = false;
        bool keepFile = false;
        bool keepScore = false;
        var renderState = new AsyncRenderState();
        var renderSessionWaitTimer = Stopwatch.StartNew();
        NativeRenderSessionGate.Wait();
        renderSessionWaitTimer.Stop();
        ExtendedPinyinDiagnosticLog.Write(
            "native-session-queue",
            $"job={job.Id}; waitMs={renderSessionWaitTimer.ElapsedMilliseconds}; "
            + $"superseded={job.Superseded}");
        try
        {
            if (job.Superseded || !Settings.ExtendedChinesePinyin)
                return null;

            bool transformed = RunOnUi(() =>
            {
                if (!ApplyCarrierVariant(part, plans, useFirstCarrier: true, job.Id))
                    return false;

                bool committed = sequence.Commit(false);
                ExtendedPinyinDiagnosticLog.Write(
                    "carrier-commit",
                    $"job={job.Id}; variant=first; committed={committed}; staged={sequence.IsStaged}");
                return committed;
            });
            if (!transformed || job.Superseded)
                return null;

            observer = RunOnUi(() => new WIVSMRendererObserver());
            nint targetPart = (nint)part;
            observer.Started += (_, args) =>
            {
                if ((nint)args.MidiPart == targetPart)
                {
                    ExtendedPinyinDiagnosticLog.Write(
                        "native-started",
                        $"job={job.Id}; duplicatePart=0x{targetPart:X}");
                }
            };
            observer.Completed += (_, args) =>
            {
                if ((nint)args.MidiPart != targetPart)
                    return;

                Volatile.Write(ref renderState.Status, 1);
                ExtendedPinyinDiagnosticLog.Write(
                    "native-completed",
                    $"job={job.Id}; progress={args.Progress.FirstEnd}/"
                    + $"{args.Progress.SecondBegin}/{args.Progress.SecondEnd}");
                renderState.Signal.Set();
            };
            observer.Canceled += (_, args) =>
            {
                if ((nint)args.MidiPart != targetPart)
                    return;

                Volatile.Write(ref renderState.Status, -1);
                ExtendedPinyinDiagnosticLog.Write(
                    "native-canceled",
                    $"job={job.Id}; reason={args.CancelReason}");
                renderState.Signal.Set();
            };

            bool started = RunOnUi(() =>
            {
                sequence.BlockRenderingEnabled = false;
                if (!sequence.AddRendererObserver(observer))
                    return false;

                observerAdded = true;
                sequence.StartAsyncRendering();
                renderingStarted = true;
                return true;
            });
            ExtendedPinyinDiagnosticLog.Write(
                "native-session",
                $"job={job.Id}; observerAdded={observerAdded}; started={started}");
            if (!started)
                return null;

            if (!WaitForNativeRender(job, sequence, renderState, TimeSpan.FromSeconds(90)))
                return null;
            renderSettled = true;

            if (!CopyRenderedWave(
                    job,
                    sequence,
                    part,
                    firstCarrierPath,
                    out WaveStats firstStats))
                return null;

            ExtendedPinyinDiagnosticLog.Write(
                "carrier-wave",
                $"job={job.Id}; variant=first; samples={firstStats.Samples}; "
                + $"peak={firstStats.Peak}; bytes={firstStats.FileBytes}");

            Dictionary<int, (long Start, long End)> firstBoundaries = CaptureCarrierBoundaries(
                part,
                plans,
                targetAtStart: true,
                job.Id);

            renderSettled = false;
            renderState.Reset();
            bool secondCommitted = RunOnUi(() =>
            {
                if (!ApplyCarrierVariant(part, plans, useFirstCarrier: false, job.Id))
                    return false;

                bool committed = sequence.Commit(false);
                ExtendedPinyinDiagnosticLog.Write(
                    "carrier-commit",
                    $"job={job.Id}; variant=second; committed={committed}; staged={sequence.IsStaged}");
                return committed;
            });
            if (!secondCommitted || job.Superseded)
                return null;

            if (!WaitForNativeRender(job, sequence, renderState, TimeSpan.FromSeconds(90)))
                return null;
            renderSettled = true;

            if (!CopyRenderedWave(
                    job,
                    sequence,
                    part,
                    secondCarrierPath,
                    out WaveStats secondStats))
            {
                return null;
            }

            ExtendedPinyinDiagnosticLog.Write(
                "carrier-wave",
                $"job={job.Id}; variant=second; samples={secondStats.Samples}; "
                + $"peak={secondStats.Peak}; bytes={secondStats.FileBytes}");

            if (!CopyRenderedScore(
                    job,
                    sequence,
                    part,
                    scoreOutputPath,
                    out long scoreFrames,
                    out long scoreBytes))
            {
                return null;
            }

            ExtendedPinyinDiagnosticLog.Write(
                "carrier-score",
                $"job={job.Id}; variant=second; frames={scoreFrames}; bytes={scoreBytes}");

            List<CarrierTiming> timings = BuildCarrierTimings(
                part,
                plans,
                firstBoundaries,
                job.Id);
            for (int index = 0; index < job.Plans.Count; index++)
            {
                CarrierTiming? timing = timings.FirstOrDefault(
                    item => item.Plan.NoteIndex == job.Plans[index].NoteIndex);
                if (timing != null)
                    job.Plans[index] = job.Plans[index] with { SplitRelTick = timing.FirstEndRelTick };
            }

            if (!MergeCarrierWaves(
                    part,
                    timings,
                    firstCarrierPath,
                    secondCarrierPath,
                    outputPath,
                    out WaveStats stats))
            {
                return null;
            }

            ExtendedPinyinDiagnosticLog.Write(
                "wave-ready",
                $"job={job.Id}; samples={stats.Samples}; channels={stats.Channels}; "
                + $"rate={stats.SampleRate}; peak={stats.Peak}; bytes={stats.FileBytes}");
            keepFile = true;
            keepScore = true;
            scorePath = scoreOutputPath;
            return outputPath;
        }
        catch (Exception e)
        {
            ExtendedPinyinDiagnosticLog.Write(
                "native-render-failed",
                $"job={job.Id}; {e.GetType().Name}: {e.Message}");
            return null;
        }
        finally
        {
            CleanupNativeSession(
                sequence,
                observer,
                renderState,
                renderingStarted,
                observerAdded,
                renderSettled,
                job.Id);
            FileManager.RemoveFile(firstCarrierPath);
            FileManager.RemoveFile(secondCarrierPath);
            if (!keepFile || job.Superseded)
                FileManager.RemoveFile(outputPath);
            if (!keepScore || job.Superseded)
            {
                FileManager.RemoveFile(scoreOutputPath);
                scorePath = null;
            }
        }
    }

    private static bool ApplyCarrierVariant(
        WIVSMMidiPart part,
        IReadOnlyList<SplitPlan> plans,
        bool useFirstCarrier,
        long jobId)
    {
        foreach (SplitPlan plan in plans)
        {
            List<WIVSMNote> notes = part.Notes;
            if (plan.NoteIndex < 0 || plan.NoteIndex >= notes.Count)
            {
                ExtendedPinyinDiagnosticLog.Write(
                    "split-failed",
                    $"job={jobId}; noteIndex={plan.NoteIndex}; noteCount={notes.Count}");
                return false;
            }

            WIVSMNote note = notes[plan.NoteIndex];
            if (note.RelPosTick.Value != plan.RelPosTick
                || note.DurationTick.Value != plan.DurationTick)
            {
                ExtendedPinyinDiagnosticLog.Write(
                    "carrier-failed",
                    $"job={jobId}; source note changed before carrier assignment");
                return false;
            }

            string target = useFirstCarrier ? plan.FirstPhoneme : plan.SecondPhoneme;
            if (!TryGetNativeCarrier(
                    target,
                    useFirstCarrier,
                    plan.FirstPhoneme,
                    out NativeCarrier carrier))
            {
                ExtendedPinyinDiagnosticLog.Write(
                    "carrier-failed",
                    $"job={jobId}; variant={(useFirstCarrier ? "first" : "second")}; "
                    + $"target={target}; no native carrier");
                return false;
            }

            if (!SetNativeCarrier(note, carrier))
            {
                ExtendedPinyinDiagnosticLog.Write(
                    "carrier-failed",
                    $"job={jobId}; variant={(useFirstCarrier ? "first" : "second")}; "
                    + $"target={target}; assignment failed");
                return false;
            }

            ExtendedPinyinDiagnosticLog.Write(
                "carrier",
                $"job={jobId}; variant={(useFirstCarrier ? "first" : "second")}; "
                + $"target={target}; carrierPhonemes={carrier.Phonemes}");
        }

        return true;
    }

    private static bool SetNativeCarrier(WIVSMNote note, NativeCarrier carrier)
    {
        try
        {
            note.IsProtected = false;
            (bool success, _) = G2PAMultiLingualManager.SetLyrics(
                note,
                carrier.Lyric,
                note.LangID);
            bool result = success
                          && note.SetPhonemes(carrier.Phonemes, true, note.LangID);
            note.IsProtected = true;
            return result;
        }
        catch
        {
            try
            {
                note.IsProtected = true;
            }
            catch
            {
            }
            return false;
        }
    }

    private static bool TryGetNativeCarrier(
        string targetPhoneme,
        bool targetAtStart,
        string desiredInitial,
        out NativeCarrier carrier)
    {
        carrier = null!;
        int bestScore = int.MaxValue;
        foreach ((string lyric, string phonemes) in VsqxPhonemeMaps.Pinyin2Xsampa)
        {
            string[] tokens = phonemes.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (tokens.Length == 0)
                continue;

            bool matches = targetAtStart
                ? string.Equals(tokens[0], targetPhoneme, StringComparison.Ordinal)
                : string.Equals(tokens[^1], targetPhoneme, StringComparison.Ordinal);
            if (!matches)
                continue;

            int neighborScore = targetAtStart
                ? 0
                : PhonemeFamilyDistance(desiredInitial, tokens[0]);
            int score = neighborScore * 10000 + tokens.Length * 100 + lyric.Length;
            if (score >= bestScore)
                continue;

            bestScore = score;
            carrier = new NativeCarrier(lyric, phonemes, tokens);
        }

        return carrier != null;
    }

    private static int PhonemeFamilyDistance(string desired, string candidate)
    {
        if (string.Equals(desired, candidate, StringComparison.Ordinal))
            return 0;

        int desiredFamily = GetPhonemeFamily(desired);
        int candidateFamily = GetPhonemeFamily(candidate);
        return desiredFamily != 0 && desiredFamily == candidateFamily ? 1 : 2;
    }

    private static int GetPhonemeFamily(string phoneme)
    {
        return phoneme switch
        {
            "f" or "x" or "s" or "s\\" or "s`" => 1,
            "p" or "p_h" or "t" or "t_h" or "k" or "k_h" => 2,
            "ts" or "ts_h" or "ts\\" or "ts\\_h" or "ts`" or "ts`_h" => 3,
            "m" or "n" => 4,
            "l" or "z`" => 5,
            _ => 0,
        };
    }

    private static Dictionary<int, (long Start, long End)> CaptureCarrierBoundaries(
        WIVSMMidiPart part,
        IReadOnlyList<SplitPlan> plans,
        bool targetAtStart,
        long jobId)
    {
        var result = new Dictionary<int, (long Start, long End)>();
        List<WIVSMNote> notes = part.Notes;
        foreach (SplitPlan plan in plans)
        {
            if (plan.NoteIndex < 0 || plan.NoteIndex >= notes.Count)
                continue;

            string target = targetAtStart ? plan.FirstPhoneme : plan.SecondPhoneme;
            if (!TryGetNativeCarrier(
                    target,
                    targetAtStart,
                    plan.FirstPhoneme,
                    out NativeCarrier carrier))
            {
                continue;
            }

            WIVSMNote note = notes[plan.NoteIndex];
            try
            {
                int tokenIndex = targetAtStart
                    ? Array.FindIndex(
                        carrier.Tokens,
                        token => string.Equals(token, target, StringComparison.Ordinal))
                    : Array.FindLastIndex(
                        carrier.Tokens,
                        token => string.Equals(token, target, StringComparison.Ordinal));
                List<int> positions = note.GetPhonemePositions();
                if (tokenIndex < 0 || positions.Count <= tokenIndex + 1)
                    continue;

                long start = note.GetAbsPositionFromNoteBaseTick(positions[tokenIndex]).Value
                             - part.AbsPosTick.Value;
                long end = note.GetAbsPositionFromNoteBaseTick(positions[tokenIndex + 1]).Value
                           - part.AbsPosTick.Value;
                if (end <= start)
                    continue;

                result[plan.NoteIndex] = (start, end);
                ExtendedPinyinDiagnosticLog.Write(
                    "carrier-boundary",
                    $"job={jobId}; variant={(targetAtStart ? "first" : "second")}; "
                    + $"target={target}; start={start}; end={end}");
            }
            catch (Exception e)
            {
                ExtendedPinyinDiagnosticLog.Write(
                    "carrier-boundary-failed",
                    $"job={jobId}; target={target}; {e.GetType().Name}: {e.Message}");
            }
        }

        return result;
    }

    private static List<CarrierTiming> BuildCarrierTimings(
        WIVSMMidiPart part,
        IReadOnlyList<SplitPlan> plans,
        IReadOnlyDictionary<int, (long Start, long End)> firstBoundaries,
        long jobId)
    {
        Dictionary<int, (long Start, long End)> secondBoundaries = CaptureCarrierBoundaries(
            part,
            plans,
            targetAtStart: false,
            jobId);
        var result = new List<CarrierTiming>(plans.Count);
        foreach (SplitPlan plan in plans)
        {
            long firstStart = plan.RelPosTick;
            long firstEnd = plan.SplitRelTick;
            long secondStart = plan.SplitRelTick;
            if (firstBoundaries.TryGetValue(plan.NoteIndex, out var first))
            {
                firstStart = first.Start;
                firstEnd = first.End;
            }
            if (secondBoundaries.TryGetValue(plan.NoteIndex, out var second))
                secondStart = second.Start;

            long noteEnd = plan.RelPosTick + plan.DurationTick;
            firstStart = Math.Clamp(firstStart, 0L, noteEnd - 1);
            firstEnd = Math.Clamp(firstEnd, firstStart + 1, noteEnd);
            secondStart = Math.Clamp(secondStart, 0L, noteEnd - 1);
            result.Add(new CarrierTiming(plan, firstStart, firstEnd, secondStart));
            ExtendedPinyinDiagnosticLog.Write(
                "carrier-alignment",
                $"job={jobId}; first={firstStart}-{firstEnd}; secondStart={secondStart}");
        }

        return result;
    }

    private static bool MergeCarrierWaves(
        WIVSMMidiPart part,
        IReadOnlyList<CarrierTiming> timings,
        string firstPath,
        string secondPath,
        string outputPath,
        out WaveStats stats)
    {
        stats = default;
        var first = new WaveFile();
        var second = new WaveFile();
        var output = new WaveFile();
        if (first.ReadWave(firstPath) != WaveFileError.None
            || second.ReadWave(secondPath) != WaveFileError.None
            || output.ReadWave(secondPath) != WaveFileError.None
            || first.ChannelCount != second.ChannelCount
            || first.SampleRate != second.SampleRate)
        {
            return false;
        }

        WIVSMSequence? sequence = part.Sequence;
        if (sequence == null)
            return false;

        long availableSamples = Math.Min(first.NumSamples, second.NumSamples);
        if (availableSamples <= 0)
            return false;
        long crossfadeSamples = Math.Max(1L, (long)Math.Round(second.SampleRate * CrossfadeSeconds));
        double fileDuration = second.NumSamples / (double)second.SampleRate;
        double leadInSeconds = Math.Max(0.0, fileDuration - part.DurationSec);
        bool mergedAny = false;
        foreach (CarrierTiming timing in timings)
        {
            SplitPlan plan = timing.Plan;
            double startSeconds = sequence.GetTimeFromTick(
                part.AbsPosTick,
                new VSMAbsTick(part.AbsPosTick.Value + timing.FirstStartRelTick));
            double splitSeconds = sequence.GetTimeFromTick(
                part.AbsPosTick,
                new VSMAbsTick(part.AbsPosTick.Value + timing.FirstEndRelTick));
            double secondStartSeconds = sequence.GetTimeFromTick(
                part.AbsPosTick,
                new VSMAbsTick(part.AbsPosTick.Value + timing.SecondStartRelTick));
            double endSeconds = sequence.GetTimeFromTick(
                part.AbsPosTick,
                new VSMAbsTick(part.AbsPosTick.Value + plan.RelPosTick + plan.DurationTick));
            long start = Math.Clamp(
                ToSample(leadInSeconds + startSeconds, second.SampleRate),
                0L,
                availableSamples);
            long split = Math.Clamp(
                ToSample(leadInSeconds + splitSeconds, second.SampleRate),
                start,
                availableSamples);
            long secondStart = Math.Clamp(
                ToSample(leadInSeconds + secondStartSeconds, second.SampleRate),
                0L,
                availableSamples);
            long end = Math.Clamp(
                ToSample(leadInSeconds + endSeconds, second.SampleRate),
                split,
                availableSamples);
            if (start >= split)
                continue;

            mergedAny = true;
            for (long sample = start; sample < split; sample++)
            {
                double startMix = Math.Clamp(
                    (sample - start) / (double)crossfadeSamples,
                    0.0,
                    1.0);
                double keepFirstMix = Math.Clamp(
                    (split - sample) / (double)crossfadeSamples,
                    0.0,
                    1.0);
                long alignedSource = Math.Clamp(
                    secondStart + sample - split,
                    0L,
                    availableSamples - 1);
                for (int channel = 0; channel < second.ChannelCount; channel++)
                {
                    double unshifted = second.SampleAtIndex(sample, channel);
                    double initial = unshifted
                                     + (first.SampleAtIndex(sample, channel) - unshifted) * startMix;
                    double aligned = second.SampleAtIndex(alignedSource, channel);
                    double merged = aligned + (initial - aligned) * keepFirstMix;
                    output.SetSampleAtIndex(
                        sample,
                        channel,
                        (short)Math.Clamp((int)Math.Round(merged), short.MinValue, short.MaxValue));
                }
            }

            long shiftedLength = Math.Min(end - split, availableSamples - secondStart);
            for (long offset = 0; offset < shiftedLength; offset++)
            {
                long destination = split + offset;
                long source = secondStart + offset;
                double endMix = Math.Clamp(
                    (end - destination) / (double)crossfadeSamples,
                    0.0,
                    1.0);
                for (int channel = 0; channel < second.ChannelCount; channel++)
                {
                    double shifted = second.SampleAtIndex(source, channel);
                    double unshifted = second.SampleAtIndex(destination, channel);
                    double merged = unshifted + (shifted - unshifted) * endMix;
                    output.SetSampleAtIndex(
                        destination,
                        channel,
                        (short)Math.Clamp((int)Math.Round(merged), short.MinValue, short.MaxValue));
                }
            }
        }

        if (!mergedAny || output.WriteWave(outputPath) != WaveFileError.None)
            return false;

        return TryReadWaveStats(outputPath, out stats) && stats.Peak >= MinimumAudiblePeak;
    }

    private static long ToSample(double seconds, int sampleRate)
        => (long)Math.Round(seconds * sampleRate);

    private static bool WaitForNativeRender(
        RenderJob job,
        WIVSMSequence sequence,
        AsyncRenderState state,
        TimeSpan timeout)
    {
        DateTime deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (job.Superseded || !Settings.ExtendedChinesePinyin)
                return false;

            PumpRendererCallbacks(sequence);
            int status = Volatile.Read(ref state.Status);
            if (status != 0)
                return status > 0;

            state.Signal.WaitOne(30);
        }

        ExtendedPinyinDiagnosticLog.Write("native-timeout", $"job={job.Id}; seconds={timeout.TotalSeconds}");
        return false;
    }

    private static void PumpRendererCallbacks(WIVSMSequence sequence)
    {
        const int callbacksPerSlice = 8;

        int PumpCore()
        {
            int count = 0;
            while (count < callbacksPerSlice
                   && sequence.IsOpen
                   && sequence.InvokeRendererObserver())
            {
                count++;
            }

            return count;
        }

        Application? application = Application.Current;
        if (application == null
            || application.Dispatcher.HasShutdownStarted
            || application.Dispatcher.CheckAccess())
        {
            PumpCore();
            return;
        }

        nint key = (nint)sequence;
        lock (SyncRoot)
        {
            if (!PendingRendererPumps.Add(key))
                return;
        }

        try
        {
            application.Dispatcher.BeginInvoke(new Action(() =>
            {
                try
                {
                    PumpCore();
                }
                catch (Exception e)
                {
                    ExtendedPinyinDiagnosticLog.Write(
                        "native-pump-failed",
                        $"{e.GetType().Name}: {e.Message}");
                }
                finally
                {
                    lock (SyncRoot)
                        PendingRendererPumps.Remove(key);
                }
            }), DispatcherPriority.Background);
        }
        catch
        {
            lock (SyncRoot)
                PendingRendererPumps.Remove(key);
        }
    }

    private static bool CopyRenderedWave(
        RenderJob job,
        WIVSMSequence sequence,
        WIVSMMidiPart part,
        string outputPath,
        out WaveStats stats)
    {
        stats = default;
        DateTime deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (DateTime.UtcNow < deadline)
        {
            if (job.Superseded)
                return false;

            PumpRendererCallbacks(sequence);
            string sourcePath = RunOnUi(() => part.WaveFilePath);
            try
            {
                if (!string.IsNullOrEmpty(sourcePath) && File.Exists(sourcePath))
                {
                    File.Copy(sourcePath, outputPath, true);
                    if (TryReadWaveStats(outputPath, out stats) && stats.Peak >= MinimumAudiblePeak)
                        return true;
                }
            }
            catch
            {
            }

            FileManager.RemoveFile(outputPath);
            Thread.Sleep(30);
        }

        ExtendedPinyinDiagnosticLog.Write(
            "wave-copy-failed",
            $"job={job.Id}; validRenderedWave={RunOnUi(() => part.HasValidRenderedWave)}; "
            + $"nativePathPresent={!string.IsNullOrEmpty(RunOnUi(() => part.WaveFilePath))}; "
            + $"lastPeak={stats.Peak}; lastBytes={stats.FileBytes}");
        return false;
    }

    private static bool CopyRenderedScore(
        RenderJob job,
        WIVSMSequence sequence,
        WIVSMMidiPart part,
        string outputPath,
        out long scoreFrames,
        out long scoreBytes)
    {
        scoreFrames = 0;
        scoreBytes = 0;
        DateTime deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (DateTime.UtcNow < deadline)
        {
            if (job.Superseded)
                return false;

            PumpRendererCallbacks(sequence);
            string sourcePath = RunOnUi(() => part.ScoreFilePath);
            try
            {
                if (!string.IsNullOrEmpty(sourcePath) && File.Exists(sourcePath))
                {
                    File.Copy(sourcePath, outputPath, true);
                    scoreBytes = SafeFileLength(outputPath);
                    scoreFrames = RunOnUi(() =>
                    {
                        using var scoreFile = new VSMScoreFile(outputPath);
                        return scoreFile.NumScores;
                    });
                    if (scoreFrames > 0 && scoreBytes > 0)
                        return true;
                }
            }
            catch
            {
            }

            FileManager.RemoveFile(outputPath);
            scoreFrames = 0;
            scoreBytes = 0;
            Thread.Sleep(30);
        }

        ExtendedPinyinDiagnosticLog.Write(
            "score-copy-failed",
            $"job={job.Id}; validRenderedScore={RunOnUi(() => part.HasValidRenderedScore)}; "
            + $"nativePathPresent={!string.IsNullOrEmpty(RunOnUi(() => part.ScoreFilePath))}; "
            + $"lastFrames={scoreFrames}; lastBytes={scoreBytes}");
        return false;
    }

    private static bool TryReadWaveStats(string path, out WaveStats stats)
    {
        stats = default;
        try
        {
            var wave = new WaveFile();
            if (wave.ReadWave(path) != WaveFileError.None || wave.NumSamples <= 0)
                return false;

            long step = Math.Max(1L, wave.NumSamples / 200000L);
            int peak = 0;
            for (long sample = 0; sample < wave.NumSamples; sample += step)
            {
                for (int channel = 0; channel < wave.ChannelCount; channel++)
                    peak = Math.Max(peak, Math.Abs((int)wave.SampleAtIndex(sample, channel)));
            }

            stats = new WaveStats(
                wave.NumSamples,
                wave.ChannelCount,
                wave.SampleRate,
                peak,
                SafeFileLength(path));
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static T RunOnUi<T>(Func<T> action)
    {
        Application? application = Application.Current;
        if (application == null
            || application.Dispatcher.HasShutdownStarted
            || application.Dispatcher.CheckAccess())
        {
            return action();
        }

        return application.Dispatcher.Invoke(action, DispatcherPriority.Background);
    }

    private static bool SourceStillMatches(WIVSMMidiPart part, IReadOnlyList<SplitPlan> plans)
    {
        try
        {
            List<WIVSMNote> notes = part.Notes;
            foreach (SplitPlan plan in plans)
            {
                if (plan.NoteIndex < 0 || plan.NoteIndex >= notes.Count)
                    return false;

                WIVSMNote note = notes[plan.NoteIndex];
                if (note.RelPosTick.Value != plan.RelPosTick
                    || note.DurationTick.Value != plan.DurationTick
                    || !string.Equals(note.Lyric, plan.Lyric, StringComparison.Ordinal)
                    || !string.Equals(
                        CanonicalizePhonemes(note.Phonemes),
                        plan.Phonemes,
                        StringComparison.Ordinal))
                {
                    return false;
                }
            }

            return true;
        }
        catch
        {
            return false;
        }
    }

    private static string CanonicalizePhonemes(string value)
        => string.Join(' ', value.Split(' ', StringSplitOptions.RemoveEmptyEntries));

    private static long SafeFileLength(string path)
    {
        try
        {
            return File.Exists(path) ? new FileInfo(path).Length : 0L;
        }
        catch
        {
            return 0L;
        }
    }
}
