using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using HarmonyLib;
using VOCALOIDPatcher.BreathVolume;
using VOCALOIDPatcher.Config;
using VOCALOIDPatcher.Patch.Patches;
using VOCALOIDPatcher.Rendering;
using VOCALOIDPatcher.Utils;
using Yamaha.VOCALOID;
using Yamaha.VOCALOID.VSM;

namespace VOCALOIDPatcher.ConsonantTiming.Smoothing;

/// <summary>
/// Runtime host coordinator for transition timing audio smoothing.
/// Monitors rendered MIDI parts, detects transition parameter volume differences,
/// applies adaptive micro-crossfade and local loudness envelope smoothing,
/// and substitutes the resulting audio into the VOCALOID 6 playback and pianoroll rendering pipelines.
/// </summary>
internal static class TransitionSmoothingHost
{
    private sealed class PartState(WIVSMMidiPart part)
    {
        internal readonly WIVSMMidiPart Part = part;
        internal readonly nint PartId = (nint)part;
        internal readonly nint SourceId = (nint)part.Sequence!;
        internal long Generation;
        internal long PendingGeneration;
        internal bool Rendering;
        internal Cache? Cache;
        // Retained only for exact input comparison; Substitute never reads it.
        internal Cache? ReusableCache;
        internal string? Error;
        internal int RebindQueued;
        internal readonly SemaphoreSlim Gate = new(1, 1);
        internal readonly PostProcessingQuietPeriod QuietPeriod = new(100);
    }

    private sealed record Cache(string Input, long Length, DateTime Written, string Output,
        WaveJunctionProcessor.JunctionInfo[] Junctions, TransitionSmootherConfig Config);
    private sealed record Request(PartState State, long Generation, string Input, long Length, DateTime Written,
        List<WaveJunctionProcessor.JunctionInfo> Junctions, TransitionSmootherConfig Config);

    private static readonly ConcurrentDictionary<nint, PartState> Parts = new();
    private static readonly ConcurrentDictionary<string, byte> Files = new();
    private static readonly ConcurrentDictionary<string, byte> InFlightFiles = new();
    private static readonly SemaphoreSlim PlanningWorker = new(1, 1);
    private static readonly string CacheDirectory = Path.Combine(Path.GetTempPath(), "VOCALOIDPatcher", "TransitionSmoothing", Guid.NewGuid().ToString("N"));
    private static readonly MethodInfo? RegisterPlacement = AccessTools.Method(typeof(AudioPlayer), "RegisterAudioPlacementWithFile",
        [typeof(WIVSMSequence), typeof(WIVSMMidiPart)]);

    internal static event Action? Changed;

    internal static void Initialize()
    {
        // Static constructor hook
    }

    static TransitionSmoothingHost()
    {
        AppDomain.CurrentDomain.ProcessExit += (_, _) => CleanupFiles();
    }

    private static void Dispatch(Action action)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher == null || dispatcher.CheckAccess()) action();
        else dispatcher.BeginInvoke(action);
    }

    internal static void RequestPart(WIVSMMidiPart part)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher?.CheckAccess() == true) QueuePart(part);
        else if (Parts.TryGetValue((nint)part, out var state))
            Dispatch(() => { if (Parts.TryGetValue(state.PartId, out var live) && live == state) QueuePart(state.Part); });
    }

    private static void QueuePart(WIVSMMidiPart part, bool upstreamReady = false)
    {
        if (part.Sequence is not { } sequence || NativeRenderSession.IsInternalSequence(sequence)) return;
        if (VoiceComp.VoiceCompHost.Owns(part))
        {
            if (Parts.TryGetValue((nint)part, out var previous))
            { Interlocked.Increment(ref previous.Generation); previous.Cache = null; previous.ReusableCache = null; }
            return;
        }
        var state = Parts.GetOrAdd((nint)part, _ => new(part));
        long generation = Interlocked.Increment(ref state.Generation);
        state.PendingGeneration = generation;
        state.Error = null;
        state.ReusableCache = state.Cache ?? state.ReusableCache;
        state.Cache = null;
        int delay = upstreamReady ? state.QuietPeriod.UpstreamReady(Environment.TickCount64) : state.QuietPeriod.Edited(Environment.TickCount64);
        if (upstreamReady && (BreathVolumeService.HasPendingWave(part) || PhonemeVolume.PhonemeVolumeHost.HasPendingWave(part)))
            delay = Math.Max(delay, 100);
        _ = BuildAfterQuietPeriod(state, generation, delay);
    }

    private static async Task BuildAfterQuietPeriod(PartState state, long generation, int delay)
    {
        await Task.Delay(delay).ConfigureAwait(false);
        if (Volatile.Read(ref state.Generation) != generation) return;
        Dispatch(() =>
        {
            if (!IsCurrent(state, generation)) return;
            _ = PrepareBuild(state, generation);
        });
    }

    // Capture native owners on the Dispatcher, as the transition UI does, but
    // run the expensive isolated query off it. Serialize fallback planning so
    // rendering many Parts cannot flood the native query engine.
    private static async Task PrepareBuild(PartState state, long generation)
    {
        SampledTimingHost.TransitionPreparation? preparation = null;
        bool processing = false;
        try
        {
            var part = state.Part;
            if (state.Rendering || !Settings.TransitionSmoothingEnabled || part.IsAi ||
                part.Sequence is not { IsOpen: true, IsStaged: false } || !part.HasValidRenderedWave) return;
            var plan = SampledTimingHost.TransitionView(part);
            if (plan == null)
            {
                if (!NeedsFallbackPlan(part)) return;
                // Keep preparation and its disposal on the Dispatcher; only
                // its immutable query input is used by the worker.
                await PlanningWorker.WaitAsync();
                try
                {
                    if (!IsCurrent(state, generation)) return;
                    preparation = SampledTimingHost.PrepareTransitions(part);
                    plan = await Task.Run(() => IsCurrent(state, generation) ? preparation.Query().Execution : null);
                }
                finally { PlanningWorker.Release(); }
                if (preparation == null || preparation.Revision != preparation.Session.Revision) return;
            }
            if (plan == null || !IsCurrent(state, generation)) return;
            var request = Capture(state, generation, plan);
            if (request != null && !TryReuse(state, request)) { processing = true; _ = ProcessAsync(request); }
        }
        catch (Exception e)
        {
            if (!IsCurrent(state, generation)) return;
            state.Error = e.GetType().Name;
            Debug.Print("Transition smoothing preparation: " + e.GetType().Name);
            Changed?.Invoke();
        }
        finally
        {
            if (!processing && IsCurrent(state, generation)) state.PendingGeneration = 0;
            preparation?.Dispose();
        }
    }

    private static bool IsCurrent(PartState state, long generation) =>
        Volatile.Read(ref state.Generation) == generation && Parts.TryGetValue(state.PartId, out var live) && live == state;

    private static TransitionExecutionPlan? GetExecutionPlan(WIVSMMidiPart part)
    {
        var view = SampledTimingHost.TransitionView(part);
        if (view != null) return view;

        if (!NeedsFallbackPlan(part)) return null;

        if (part.Sequence is { IsOpen: true, IsStaged: false } && !part.IsAi && part.NumNotes is >= 1 and <= 33)
        {
            try
            {
                using var prep = SampledTimingHost.PrepareTransitions(part);
                var query = prep.Query();
                return query.Execution;
            }
            catch { /* Not ready or unsupported */ }
        }
        return null;
    }

    private static bool NeedsFallbackPlan(WIVSMMidiPart part)
    {
        if (part.Sequence is not { IsOpen: true, IsStaged: false } source || part.IsAi || part.NumNotes is < 1 or > 33)
            return false;
        var config = Settings.GetTransitionSmoothingConfig();
        if (config.Mode == TransitionSmoothingMode.Disabled) return false;
        if (config.Mode == TransitionSmoothingMode.Always || config.VolumeThresholdDb <= 0) return true;
        // Include timing/progress-only edits, but avoid planning unedited Parts
        // on every render completion.
        var data = SampledTimingHost.TransitionData(source).Data;
        if (data == null || !data.HasControls) return false;
        var address = SampledTimingVsmBinding.Address(part.GetNote(0UL)!);
        return TransitionProjectBinding.HasPart(data, address.Track, address.Part);
    }

    internal static List<WaveJunctionProcessor.JunctionInfo>? CollectJunctions(
        WIVSMMidiPart part,
        TransitionExecutionPlan plan,
        TransitionSmootherConfig config)
    {
        return TransitionJunctionPlanner.Collect(plan, config);
    }

    private static Request? Capture(PartState state, long generation, TransitionExecutionPlan? preparedPlan = null)
    {
        var part = state.Part;
        if (part.Sequence is not { IsOpen: true } source || state.Rendering || source.IsStaged) return null;
        if (part.IsAi || !Settings.TransitionSmoothingEnabled) return null;

        var config = Settings.GetTransitionSmoothingConfig();
        if (config.Mode == TransitionSmoothingMode.Disabled) return null;

        var plan = preparedPlan ?? GetExecutionPlan(part);
        if (plan == null) return null;

        var junctions = CollectJunctions(part, plan, config);
        if (junctions == null || junctions.Count == 0) return null;

        string input = ReadInput(part);
        if (string.IsNullOrEmpty(input) || !File.Exists(input)) return null;

        var file = new FileInfo(input);
        return new(state, generation, input, file.Length, file.LastWriteTimeUtc, junctions, config);
    }

    private static string ReadInput(WIVSMMidiPart part)
        => WaveProcessingReadScope.ReadBefore(WaveProcessingStage.TransitionSmoothing, () => part.WaveFilePath);

    private static bool TryReuse(PartState state, Request request)
    {
        var cached = state.Cache ?? state.ReusableCache;
        if (cached == null || !cached.Input.Equals(request.Input, StringComparison.OrdinalIgnoreCase) ||
            cached.Length != request.Length || cached.Written != request.Written || cached.Config != request.Config ||
            !cached.Junctions.SequenceEqual(request.Junctions) || !File.Exists(cached.Output)) return false;
        bool restore = state.Cache == null;
        state.Cache = cached; state.ReusableCache = null;
        state.PendingGeneration = 0;
        if (restore) { RefreshPlacement(state.Part); Changed?.Invoke(); }
        return true;
    }

    private static Cache Process(Request request)
    {
        string output = Path.Combine(CacheDirectory, Guid.NewGuid().ToString("N") + ".wav");
        InFlightFiles.TryAdd(output, 0);
        Files.TryAdd(output, 0);
        try
        {
            WaveJunctionProcessor.ProcessWaveFile(request.Input, output, request.Junctions, request.Config);
            var file = new FileInfo(request.Input);
            if (!file.Exists || file.Length != request.Length || file.LastWriteTimeUtc != request.Written)
                throw new IOException("Source audio changed during transition smoothing.");
            return new(request.Input, request.Length, request.Written, output, request.Junctions.ToArray(), request.Config);
        }
        catch { InFlightFiles.TryRemove(output, out _); throw; }
    }

    private static async Task ProcessAsync(Request request)
    {
        bool locked = false;
        try
        {
            if (!IsCurrent(request.State, request.Generation)) return;
            await request.State.Gate.WaitAsync().ConfigureAwait(false); locked = true;
            var cache = await Task.Run(() => IsCurrent(request.State, request.Generation) ? Process(request) : null).ConfigureAwait(false);
            Dispatch(() =>
            {
                if (cache == null) return;
                if (!IsCurrent(request.State, request.Generation))
                {
                    InFlightFiles.TryRemove(cache.Output, out _); CleanupFiles(); return;
                }
                request.State.Cache = cache;
                request.State.PendingGeneration = 0;
                request.State.ReusableCache = null;
                InFlightFiles.TryRemove(cache.Output, out _);
                RefreshPlacement(request.State.Part);
                CleanupFiles();
                Changed?.Invoke();
            });
        }
        catch (Exception e)
        {
            Dispatch(() =>
            {
                if (!IsCurrent(request.State, request.Generation)) return;
                request.State.PendingGeneration = 0;
                request.State.Error = e.GetType().Name;
                Debug.Print("Transition smoothing processing: " + e.GetType().Name);
                Changed?.Invoke();
            });
        }
        finally { if (locked) request.State.Gate.Release(); }
    }

    /// <summary>
    /// WaveFilePath interceptor: called from BreathVolumeWavePathPatch.
    /// If transition smoothing has produced a smoothed wave file, returns the smoothed path.
    /// </summary>
    internal static string Substitute(WIVSMMidiPart part, string input)
    {
        if (!WaveProcessingReadScope.ShouldApply(WaveProcessingStage.TransitionSmoothing) ||
            BreathVolumeService.IsReadingOriginalWave || !Settings.TransitionSmoothingEnabled)
            return input;
        if (Parts.TryGetValue((nint)part, out var state) && state.Cache is { } cache)
        {
            if (cache.Input.Equals(input, StringComparison.OrdinalIgnoreCase)) return cache.Output;
            if (Interlocked.Exchange(ref state.RebindQueued, 1) == 0)
                Dispatch(() =>
                {
                    state.RebindQueued = 0;
                    RebindReady(state, input);
                });
        }
        return input;
    }

    private static void RebindReady(PartState state, string input)
    {
        if (!Parts.TryGetValue(state.PartId, out var live) || live != state || state.Rendering) return;
        if (!ReadInput(state.Part).Equals(input, StringComparison.OrdinalIgnoreCase)) return;
        QueuePart(state.Part, upstreamReady: true);
    }

    internal static bool HasPendingWave(WIVSMMidiPart part) => Settings.TransitionSmoothingEnabled && Parts.TryGetValue((nint)part, out var state) &&
        (state.Rendering || state.PendingGeneration != 0 && state.PendingGeneration == state.Generation);

    internal static void RenderStarted(WIVSMMidiPart part)
    {
        if (!Parts.TryGetValue((nint)part, out var state))
        {
            if (part.IsAi || !Settings.TransitionSmoothingEnabled) return;
            state = Parts.GetOrAdd((nint)part, _ => new(part));
        }
        Interlocked.Increment(ref state.Generation);
        state.Rendering = true;
        state.Cache = null;
        state.ReusableCache = null;
    }

    internal static void RenderCompleted(WIVSMMidiPart part)
    {
        if (!Parts.TryGetValue((nint)part, out var state))
        {
            if (part.IsAi || !Settings.TransitionSmoothingEnabled) return;
            state = Parts.GetOrAdd((nint)part, _ => new(part));
        }
        state.Rendering = false;
        RequestPart(part);
    }

    internal static void SourceChanged(WIVSMSequence source)
    {
        var active = EnumerateParts(source).ToDictionary(p => (nint)p);
        foreach (var state in Parts.Values)
        {
            if (state.SourceId != (nint)source) continue;
            if (active.TryGetValue(state.PartId, out var part)) RequestPart(part);
            else if (Parts.TryRemove(state.PartId, out _)) Interlocked.Increment(ref state.Generation);
        }
        Changed?.Invoke();
    }

    internal static void Close(WIVSMSequence source)
    {
        foreach (var part in EnumerateParts(source))
        {
            if (Parts.TryRemove((nint)part, out var state)) Interlocked.Increment(ref state.Generation);
        }
        foreach (var state in Parts.Values.Where(s => s.SourceId == (nint)source))
            if (Parts.TryRemove(state.PartId, out _)) Interlocked.Increment(ref state.Generation);
        CleanupFiles();
    }

    internal static void EnsureForExport(WIVSMSequence source)
    {
        if (!Settings.TransitionSmoothingEnabled) return;
        var config = Settings.GetTransitionSmoothingConfig();
        if (config.Mode == TransitionSmoothingMode.Disabled) return;

        foreach (var part in EnumerateParts(source))
        {
            if (part.IsAi) continue;
            if (VoiceComp.VoiceCompHost.Owns(part)) continue;
            var plan = GetExecutionPlan(part);
            if (plan == null) continue;
            var junctions = CollectJunctions(part, plan, config);
            if (junctions == null || junctions.Count == 0) continue;

            var state = Parts.GetOrAdd((nint)part, _ => new(part));
            var request = Capture(state, state.Generation, plan);
            if (request == null || request.Junctions.Count == 0) continue;

            if (TryReuse(state, request)) continue;

            long generation = Interlocked.Increment(ref state.Generation);
            var cache = Task.Run(() =>
            {
                state.Gate.Wait();
                try { return Process(request with { Generation = generation }); }
                finally { state.Gate.Release(); }
            }).GetAwaiter().GetResult();

            state.Cache = cache;
            state.PendingGeneration = 0;
            state.ReusableCache = null;
            InFlightFiles.TryRemove(cache.Output, out _);
            RefreshPlacement(part);
        }
    }

    internal static void RefreshSettings()
    {
        foreach (var state in Parts.Values) RequestPart(state.Part);
        Changed?.Invoke();
    }

    private static IEnumerable<WIVSMMidiPart> EnumerateParts(WIVSMSequence source)
    {
        for (ulong t = 0; t < source.NumTrack; t++)
            if (source.GetTrack(t) is WIVSMMidiTrack track)
                for (ulong p = 0; p < track.NumParts; p++)
                    if (track.GetPart(p) is WIVSMMidiPart part) yield return part;
    }

    private static void RefreshPlacement(WIVSMMidiPart part)
    {
        if (part.Sequence is not { IsOpen: true } source) return;
        if (App.AudioPlayer is { } player) RegisterPlacement?.Invoke(player, [source, part]);
        RenderedWaveCachePatch.InvalidateAndRefreshPart(part);
    }

    private static void CleanupFiles()
    {
        try
        {
            if (Directory.Exists(CacheDirectory))
            {
                foreach (var file in Directory.EnumerateFiles(CacheDirectory, "*.wav"))
                {
                    if (!InFlightFiles.ContainsKey(file) && !Parts.Values.Any(s => s.Cache?.Output == file || s.ReusableCache?.Output == file))
                    {
                        try { File.Delete(file); Files.TryRemove(file, out _); }
                        catch { }
                    }
                }
            }
        }
        catch { }
    }

    internal static string Status(WIVSMMidiPart part)
    {
        if (!Settings.TransitionSmoothingEnabled) return "Disabled";
        if (part.IsAi) return "Unavailable";
        if (Parts.TryGetValue((nint)part, out var state))
        {
            if (state.Error != null) return "Error";
            if (state.Cache != null) return "Ready";
            if (state.Rendering) return "Rendering";
        }
        return "Original";
    }
}
