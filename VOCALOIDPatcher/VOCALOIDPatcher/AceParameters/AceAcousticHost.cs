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
using VOCALOIDPatcher.Utils;
using Yamaha.VOCALOID;
using Yamaha.VOCALOID.VSM;

namespace VOCALOIDPatcher.AceParameters;

internal static class AceAcousticHost
{
    private sealed class PartState(WIVSMMidiPart part)
    {
        internal readonly WIVSMMidiPart Part = part;
        internal readonly nint Source = (nint)part.Sequence!;
        internal long Generation;
        internal bool Rendering;
        internal Cache? Cache;
        internal CancellationTokenSource? Cancellation;
        internal int RebindQueued;
    }

    private sealed record Cache(string Input, long Length, DateTime Written, int Controls, string Output);
    private sealed record Request(nint Part, PartState State, long Generation, string Input, long Length,
        DateTime Written, int Signature, AceAcousticControls Controls, CancellationToken Token);

    private static readonly ConcurrentDictionary<nint, PartState> Parts = new();
    private static readonly ConcurrentDictionary<nint, byte> PendingSources = new();
    private static readonly ConcurrentDictionary<string, byte> Files = new();
    private static readonly ConcurrentDictionary<string, byte> InFlight = new();
    // Bound memory and CPU independently of the number of edited Parts.
    private static readonly SemaphoreSlim Worker = new(1, 1);
    private static readonly string DirectoryPath = Path.Combine(Path.GetTempPath(), "VOCALOIDPatcher", "AceAcoustic", Guid.NewGuid().ToString("N"));
    private static readonly MethodInfo? RegisterPlacement = AccessTools.Method(typeof(AudioPlayer), "RegisterAudioPlacementWithFile",
        [typeof(WIVSMSequence), typeof(WIVSMMidiPart)]);
    [ThreadStatic] private static bool _bypass;
    internal static bool Enabled => Settings.AceParameters && Settings.AceParameterMapping;

    static AceAcousticHost() => AppDomain.CurrentDomain.ProcessExit += (_, _) => Cleanup(all: true);

    private static bool Dispatch(Action action)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher == null || dispatcher.HasShutdownStarted) return false;
        dispatcher.BeginInvoke((Action)(() =>
        {
            try { action(); }
            catch (Exception e) { Debug.Print("ACE acoustic scheduling: " + e.GetType().Name); }
        }));
        return true;
    }

    internal static void SourceChanged(WIVSMSequence source)
    {
        nint id = (nint)source;
        if (!PendingSources.TryAdd(id, 0)) return;
        if (!Dispatch(() =>
        {
            try
            {
                if (!source.IsOpen || NativeRenderSession.IsInternalSequence(source)) return;
                var active = EnumerateParts(source).ToArray();
                var ids = active.Select(p => (nint)p).ToHashSet();
                foreach (var entry in Parts.Where(p => p.Value.Source == id && !ids.Contains(p.Key))) Remove(entry.Key);
                foreach (var part in active)
                {
                    bool tracked = Parts.ContainsKey((nint)part);
                    if (part.IsAi || (!tracked && (!Enabled || !HasControls(part)))) continue;
                    Queue(part);
                }
            }
            finally { PendingSources.TryRemove(id, out _); }
        })) PendingSources.TryRemove(id, out _);
    }

    private static bool HasControls(WIVSMMidiPart part) =>
        AceParameterDefinitions.All.Any(d => part.GetNumController(d.CarrierType) > 0);

    internal static void RefreshSettings()
    {
        if (App.Shared?.Document?.Sequence?.VSMSequence is { } source) SourceChanged(source);
    }

    private static CancellationToken Restart(PartState state)
    {
        var previous = state.Cancellation;
        state.Cancellation = new CancellationTokenSource();
        previous?.Cancel(); previous?.Dispose();
        Interlocked.Increment(ref state.Generation);
        return state.Cancellation.Token;
    }

    private static void Queue(WIVSMMidiPart part)
    {
        var state = Parts.GetOrAdd((nint)part, _ => new(part));
        var token = Restart(state);
        long generation = state.Generation;
        _ = Debounce(state, generation, token);
    }

    private static async Task Debounce(PartState state, long generation, CancellationToken token, int attempt = 0)
    {
        try
        {
            await Task.Delay(120, token).ConfigureAwait(false);
            Dispatch(() =>
            {
                if (!Current((nint)state.Part, state, generation)) return;
                try
                {
                    var request = Capture(state, token);
                    if (request == null)
                    {
                        if (state.Cache != null) { state.Cache = null; RefreshPlacement(state.Part); Cleanup(); }
                        return;
                    }
                    if (Matches(state.Cache, request)) return;
                    state.Cache = null;
                    _ = Build(request);
                }
                catch (IOException) when (attempt < 2)
                {
                    _ = Debounce(state, generation, token, attempt + 1);
                }
                catch (Exception e)
                {
                    state.Cache = null; RefreshPlacement(state.Part); Cleanup();
                    Debug.Print("ACE acoustic preparation: " + e.GetType().Name);
                }
            });
        }
        catch (OperationCanceledException) { }
    }

    private static bool Current(nint id, PartState state, long generation) =>
        Parts.TryGetValue(id, out var live) && live == state && Volatile.Read(ref state.Generation) == generation;

    private static Request? Capture(PartState state, CancellationToken token)
    {
        var part = state.Part;
        if (!Enabled || state.Rendering || part.IsAi || part.Sequence is not { IsOpen: true, IsStaged: false } source ||
            part.NumNotes == 0 || !part.HasValidRenderedWave) return null;
        var hash = new HashCode();
        AceAcousticCurve Read(AceParameterKind kind)
        {
            var definition = AceParameterDefinitions.All.First(d => d.Kind == kind);
            if (kind == AceParameterKind.Falsetto && !Settings.AceFalsettoMapping) return AceAcousticCurve.Neutral;
            var points = AceParameterMappingService.ReadCarrierCurve(part, definition).OrderBy(p => p.Tick).ToArray();
            if (points.Length == 0) return AceAcousticCurve.Neutral;
            var split = new SortedSet<long>(points.Select(p => p.Tick));
            // Native curves interpolate in ticks. Split at tempo changes before
            // converting to seconds so time interpolation remains equivalent.
            foreach (var tempo in source.Tempos)
            {
                long tick = tempo.RelPosTick.Value - part.AbsPosTick.Value;
                if (tick > points[0].Tick && tick < points[^1].Tick) split.Add(tick);
            }
            var captured = new List<AceAcousticPoint>();
            int index = 0;
            foreach (long tick in split)
            {
                while (index + 1 < points.Length && points[index + 1].Tick <= tick) index++;
                double value = points[index].Value;
                if (index + 1 < points.Length)
                    value += (points[index + 1].Value - value) * ((tick - points[index].Tick) / (double)(points[index + 1].Tick - points[index].Tick));
                double seconds = source.PresendTimeSec + source.GetTimeFromTick(part.AbsBeginTick, part.AbsPosTick + new VSMRelTick(tick));
                double normalized = (Math.Clamp(value, 0, 100) - 50) / 50;
                captured.Add(new(seconds, normalized));
                hash.Add((int)kind); hash.Add(seconds); hash.Add(normalized);
            }
            return new(captured);
        }
        var controls = new AceAcousticControls(Read(AceParameterKind.Energy), Read(AceParameterKind.Air),
            Read(AceParameterKind.Tension), Read(AceParameterKind.Falsetto));
        if (controls.IsNeutral) return null;
        string input;
        try { _bypass = true; input = part.WaveFilePath; }
        finally { _bypass = false; }
        if (string.IsNullOrWhiteSpace(input) || !File.Exists(input)) throw new IOException("Source wave is unavailable.");
        var file = new FileInfo(input);
        return new((nint)part, state, state.Generation, input, file.Length, file.LastWriteTimeUtc, hash.ToHashCode(), controls, token);
    }

    private static bool Matches(Cache? cache, Request request) => cache != null &&
        cache.Input.Equals(request.Input, StringComparison.OrdinalIgnoreCase) && cache.Length == request.Length &&
        cache.Written == request.Written && cache.Controls == request.Signature && File.Exists(cache.Output);

    private static async Task<Cache> Process(Request request)
    {
        await Worker.WaitAsync(request.Token).ConfigureAwait(false);
        string output = Path.Combine(DirectoryPath, Guid.NewGuid().ToString("N") + ".wav");
        try
        {
            request.Token.ThrowIfCancellationRequested();
            InFlight.TryAdd(output, 0); Files.TryAdd(output, 0);
            for (int attempt = 0; ; attempt++)
            {
                try
                {
                    await Task.Run(() => AceAcousticWave.Create(request.Input, output, request.Controls, request.Token, compatiblePcm16: true), request.Token).ConfigureAwait(false);
                    break;
                }
                catch (IOException) when (attempt < 2)
                {
                    await Task.Delay(150, request.Token).ConfigureAwait(false);
                }
            }
            var file = new FileInfo(request.Input);
            if (!file.Exists || file.Length != request.Length || file.LastWriteTimeUtc != request.Written)
                throw new IOException("Source wave changed during acoustic processing.");
            request.Token.ThrowIfCancellationRequested();
            return new(request.Input, request.Length, request.Written, request.Signature, output);
        }
        catch { InFlight.TryRemove(output, out _); TryDelete(output); throw; }
        finally { Worker.Release(); }
    }

    private static async Task Build(Request request)
    {
        try
        {
            var cache = await Process(request).ConfigureAwait(false);
            if (!Dispatch(() =>
            {
                InFlight.TryRemove(cache.Output, out _);
                if (!Current(request.Part, request.State, request.Generation)) { TryDelete(cache.Output); return; }
                request.State.Cache = cache;
                RefreshPlacement(request.State.Part);
                Cleanup();
            })) { InFlight.TryRemove(cache.Output, out _); TryDelete(cache.Output); }
        }
        catch (OperationCanceledException) { }
        catch (Exception e)
        {
            Debug.Print("ACE acoustic processing: " + e.GetType().Name);
            Dispatch(() =>
            {
                if (!Current(request.Part, request.State, request.Generation)) return;
                request.State.Cache = null; RefreshPlacement(request.State.Part); Cleanup();
            });
        }
    }

    internal static string Substitute(WIVSMMidiPart part, string input)
    {
        if (_bypass || BreathVolumeService.IsReadingOriginalWave || !Enabled) return input;
        if (!Parts.TryGetValue((nint)part, out var state) || state.Rendering) return input;
        if (state.Cache is not { } cache) return input;
        if (cache.Input.Equals(input, StringComparison.OrdinalIgnoreCase)) return cache.Output;
        if (Interlocked.Exchange(ref state.RebindQueued, 1) == 0)
            Dispatch(() =>
            {
                state.RebindQueued = 0;
                if (Parts.TryGetValue((nint)part, out var live) && live == state) Queue(state.Part);
            });
        return input;
    }

    internal static void RenderStarted(WIVSMMidiPart part)
    {
        if (!Parts.TryGetValue((nint)part, out var state)) return;
        Restart(state); state.Rendering = true; state.Cache = null;
    }

    internal static void RenderCompleted(WIVSMMidiPart part) => Dispatch(() =>
    {
        if (part.Sequence is not { IsOpen: true } source || NativeRenderSession.IsInternalSequence(source) || part.IsAi) return;
        if (!Enabled && !Parts.ContainsKey((nint)part)) return;
        if (!Parts.ContainsKey((nint)part) && !HasControls(part)) return;
        var state = Parts.GetOrAdd((nint)part, _ => new(part));
        state.Rendering = false;
        Queue(part);
    });

    internal static void Close(WIVSMSequence source)
    {
        PendingSources.TryRemove((nint)source, out _);
        foreach (var item in Parts.Where(p => p.Value.Source == (nint)source)) Remove(item.Key);
        Cleanup();
    }

    private static void Remove(nint key)
    {
        if (!Parts.TryRemove(key, out var state)) return;
        state.Cancellation?.Cancel(); state.Cancellation?.Dispose();
        Interlocked.Increment(ref state.Generation);
    }

    internal static void EnsureForExport(WIVSMSequence source)
    {
        if (!Enabled) return;
        foreach (var part in EnumerateParts(source))
        {
            if (part.IsAi) continue;
            var state = Parts.GetOrAdd((nint)part, _ => new(part));
            var request = Capture(state, Restart(state));
            if (request == null)
            {
                // A non-neutral curve with unfinished native rendering cannot
                // silently export untreated audio and report success.
                bool active = AceParameterDefinitions.All.Any(d =>
                    (d.Kind != AceParameterKind.Falsetto || Settings.AceFalsettoMapping) &&
                    AceParameterMappingService.ReadCarrierCurve(part, d).Any(p => p.Value != 50));
                if (active && part.NumNotes > 0) throw new InvalidOperationException("ACE source render is not ready.");
                if (state.Cache != null) { state.Cache = null; RefreshPlacement(part); }
                continue;
            }
            if (Matches(state.Cache, request)) continue;
            var cache = Task.Run(() => Process(request)).GetAwaiter().GetResult();
            state.Cache = cache;
            InFlight.TryRemove(cache.Output, out _);
            RefreshPlacement(part);
        }
        Cleanup();
    }

    private static IEnumerable<WIVSMMidiPart> EnumerateParts(WIVSMSequence source) =>
        source.MidiTracks.SelectMany(t => t.MidiParts);

    private static void RefreshPlacement(WIVSMMidiPart part)
    {
        if (part.Sequence is not { IsOpen: true } source) return;
        RegisterPlacement?.Invoke(App.AudioPlayer, [source, part]);
        RenderedWaveCachePatch.InvalidateAndRefreshPart(part);
    }

    private static void TryDelete(string file)
    {
        try { if (File.Exists(file)) File.Delete(file); Files.TryRemove(file, out _); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private static void Cleanup(bool all = false)
    {
        foreach (string file in Files.Keys)
            if (!InFlight.ContainsKey(file) && (all || !Parts.Values.Any(s => s.Cache?.Output == file))) TryDelete(file);
    }
}
