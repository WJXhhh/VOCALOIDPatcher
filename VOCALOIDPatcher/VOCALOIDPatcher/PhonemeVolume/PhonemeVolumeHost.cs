using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using HarmonyLib;
using VOCALOIDPatcher.BreathVolume;
using VOCALOIDPatcher.Config;
using VOCALOIDPatcher.ConsonantTiming;
using VOCALOIDPatcher.Patch.Patches;
using VOCALOIDPatcher.Utils;
using Yamaha.VOCALOID;
using Yamaha.VOCALOID.VSM;

namespace VOCALOIDPatcher.PhonemeVolume;

internal static class PhonemeVolumeHost
{
    private sealed class PartState(WIVSMMidiPart part)
    {
        internal readonly WIVSMMidiPart Part = part;
        internal readonly nint PartId = (nint)part;
        internal readonly nint SourceId = (nint)part.Sequence!;
        internal long Generation;
        internal bool Rendering;
        internal Cache? Cache;
        internal string? Error;
        internal int RebindQueued;
        internal readonly SemaphoreSlim Gate = new(1, 1);
    }
    private sealed record Cache(string Input, long Length, DateTime Written, string Output);
    private sealed record Request(PartState State, long Generation, string Input, long Length, DateTime Written, PhonemeGainRegion[] Regions);
    private static readonly object Sync = new();
    private static readonly Dictionary<PhonemeVolumeKey, PhonemeVolumePlan> Values = new();
    private static readonly Dictionary<nint, List<PhonemeVolumeArchive.Entry>> Unbound = new();
    private static readonly ConcurrentDictionary<nint, PartState> Parts = new();
    private static readonly ConcurrentDictionary<string, byte> Files = new();
    private static readonly ConcurrentDictionary<string, byte> InFlightFiles = new();
    private static readonly string CacheDirectory = Path.Combine(Path.GetTempPath(), "VOCALOIDPatcher", "PhonemeVolume", Guid.NewGuid().ToString("N"));
    private static readonly MethodInfo? RegisterPlacement = AccessTools.Method(typeof(AudioPlayer), "RegisterAudioPlacementWithFile",
        [typeof(WIVSMSequence), typeof(WIVSMMidiPart)]);
    [ThreadStatic] private static int _bypass;
    internal static event Action? Changed;

    internal static void Initialize()
    {
        // Referencing the type once initializes the subscriptions below.
    }

    static PhonemeVolumeHost()
    {
        Changed += ConsonantTimingOverlay.VolumeChanged;
        BreathVolumeService.NoteCopied += (source, target) =>
        {
            try
            {
                var plan = Get(source);
                SetCore(Key(target), plan.Topology == target.Phonemes ? plan : new(target.Phonemes));
                if (!plan.IsDefault && target.Parent is { } part && part.Sequence is { } owner && !NativeRenderSession.IsInternalSequence(owner))
                    Parts.GetOrAdd((nint)part, _ => new(part));
            }
            catch (Exception e) { Debug.Print("Phoneme volume copy: " + e.GetType().Name); }
        };
        BreathVolumeService.RebuildCompleted += (part, _, current) =>
        {
            if (current && Parts.TryGetValue((nint)part, out var state)) RequestPart(state.Part);
        };
        AppDomain.CurrentDomain.ProcessExit += (_, _) => CleanupFiles();
    }

    private static PhonemeVolumeKey Key(WIVSMNote note) => new(note.CppObjPtr, BreathVolumeService.NoteGeneration(note));
    internal static PhonemeVolumePlan Get(WIVSMNote note)
    {
        var key = Key(note);
        lock (Sync)
            return Values.TryGetValue(key, out var plan) && plan.Topology == note.Phonemes ? plan : new(note.Phonemes);
    }

    private static void SetCore(PhonemeVolumeKey key, PhonemeVolumePlan plan)
    {
        lock (Sync)
        {
            if (plan.IsDefault) Values.Remove(key);
            else Values[key] = plan;
        }
    }

    internal static void Set(WIVSMNote note, int index, double decibels)
    {
        if (!Settings.IndividualPhonemeVolume || note.Sequence is not { IsStaged: false } source || note.Parent is not { IsAi: false } part)
            throw new InvalidOperationException("Phoneme volume requires an editable sampled Part.");
        var key = Key(note);
        var address = SampledTimingVsmBinding.Address(note);
        if (Unbound.TryGetValue((nint)source, out var unbound) &&
            unbound.Any(e => (e.Address.Track, e.Address.Part, e.Address.Note) == (address.Track, address.Part, address.Note)))
            throw new NotSupportedException("An unbound saved phoneme volume control occupies this locator and is preserved.");
        var before = Get(note);
        var after = before.With(index, decibels);
        if (before.SameAs(after)) return;
        var edit = new VolumeEdit(source, part, key, before, after);
        edit.ApplyAfter();
        try { CustomParameterHistoryCoordinator.Push(source, edit); }
        catch { edit.ApplyBefore(); throw; }
        edit.AfterApply();
    }

    private sealed record VolumeEdit(WIVSMSequence Source, WIVSMMidiPart Part, PhonemeVolumeKey Key,
        PhonemeVolumePlan Before, PhonemeVolumePlan After) : ICustomParameterHistoryEdit
    {
        private void Apply(PhonemeVolumePlan plan)
        {
            if (BreathVolumeService.IsNoteGenerationActive(Key.Handle, Key.Generation)) SetCore(Key, plan);
        }
        public void ApplyBefore() => Apply(Before);
        public void ApplyAfter() => Apply(After);
        public void AfterApply()
        {
            if (Source.IsOpen) RequestPart(Part);
            Changed?.Invoke();
            CommandManager.InvalidateRequerySuggested();
        }
    }

    internal static void RequestPart(WIVSMMidiPart part)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher?.CheckAccess() == true) QueuePart(part);
        else if (Parts.TryGetValue((nint)part, out var state))
            Dispatch(() => { if (Parts.TryGetValue(state.PartId, out var live) && live == state) QueuePart(state.Part); });
    }

    private static void QueuePart(WIVSMMidiPart part)
    {
        if (NativeRenderSession.IsInternalSequence(part.Sequence)) return;
        var state = Parts.GetOrAdd((nint)part, _ => new(part));
        long generation = Interlocked.Increment(ref state.Generation);
        state.Error = null;
        state.Cache = null; // Never expose the previous control value as current.
        _ = BuildAfterQuietPeriod(state, generation);
    }

    private static async Task BuildAfterQuietPeriod(PartState state, long generation)
    {
        await Task.Delay(100).ConfigureAwait(false);
        if (Volatile.Read(ref state.Generation) != generation) return;
        Dispatch(() =>
        {
            if (!IsCurrent(state, generation)) return;
            try
            {
                var request = Capture(state, generation);
                if (request == null) return;
                if (request.Regions.Length == 0) { RefreshPlacement(state.Part); return; }
                _ = ProcessAsync(request);
            }
            catch (Exception e)
            {
                state.Error = e.GetType().Name;
                Debug.Print("Phoneme volume preparation: " + e.GetType().Name); Changed?.Invoke();
            }
        });
    }

    private static bool IsCurrent(PartState state, long generation) =>
        Volatile.Read(ref state.Generation) == generation && Parts.TryGetValue(state.PartId, out var live) && live == state;

    private static Request? Capture(PartState state, long generation)
    {
        var part = state.Part;
        if (part.Sequence is not { IsOpen: true } source || state.Rendering || source.IsStaged) return null;
        var regions = new List<PhonemeGainRegion>();
        if (Settings.IndividualPhonemeVolume && !part.IsAi)
        {
            for (ulong n = 0; n < part.NumNotes; n++)
            {
                if (part.GetNote(n) is not { } note) continue;
                if (!HasTopology(note)) continue;
                var plan = Get(note);
                if (plan.IsDefault) continue;
                var positions = note.GetPhonemePositions();
                if (!part.HasValidRenderedWave || positions.Count != plan.Values.Count + 1 || positions.Contains(int.MinValue) ||
                    positions.Zip(positions.Skip(1), (a, b) => a > b).Any(v => v))
                    throw new InvalidOperationException("Rendered phoneme timing is not ready.");
                for (int i = 0; i < plan.Values.Count; i++)
                {
                    if (plan.Values[i] == 0 || positions[i] == positions[i + 1]) continue;
                    double Time(int tick) => source.PresendTimeSec + source.GetTimeFromTick(part.AbsBeginTick, note.GetAbsPositionFromNoteBaseTick(tick));
                    regions.Add(new(Time(positions[i]), Time(positions[i + 1]), plan.Values[i]));
                }
            }
        }
        string input = ReadInput(part);
        if (string.IsNullOrEmpty(input) || !File.Exists(input)) return null;
        var file = new FileInfo(input);
        return new(state, generation, input, file.Length, file.LastWriteTimeUtc, regions.ToArray());
    }

    private static string ReadInput(WIVSMMidiPart part)
    {
        try { _bypass++; return part.WaveFilePath; }
        finally { _bypass--; }
    }

    private static Cache Process(Request request)
    {
        string output = Path.Combine(CacheDirectory, Guid.NewGuid().ToString("N") + ".wav");
        InFlightFiles.TryAdd(output, 0);
        Files.TryAdd(output, 0);
        try
        {
            PhonemeGainWaveProcessor.Create(request.Input, output, request.Regions);
            var file = new FileInfo(request.Input);
            if (!file.Exists || file.Length != request.Length || file.LastWriteTimeUtc != request.Written)
                throw new IOException("Source audio changed during phoneme gain processing.");
            return new(request.Input, request.Length, request.Written, output);
        }
        catch { InFlightFiles.TryRemove(output, out _); throw; }
    }

    private static async Task ProcessAsync(Request request)
    {
        bool locked = false;
        try
        {
            // Only the immutable file request crosses to a worker. Generation
            // is checked before expensive work and before publishing its result.
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
                request.State.Error = e.GetType().Name;
                Debug.Print("Phoneme volume processing: " + e.GetType().Name); Changed?.Invoke();
            });
        }
        finally { if (locked) request.State.Gate.Release(); }
    }

    // Postfix after BVL: its derived file is our input, so both gains compose
    // exactly once, independent of their feature switches.
    internal static string Substitute(WIVSMMidiPart part, string input)
    {
        if (_bypass != 0 || BreathVolumeService.IsReadingOriginalWave || !Settings.IndividualPhonemeVolume) return input;
        if (Parts.TryGetValue((nint)part, out var state) && state.Cache is { } cache)
        {
            if (cache.Input.Equals(input, StringComparison.OrdinalIgnoreCase)) return cache.Output;
            // An upstream derived source (BVL/EVEC) can change without another
            // native render-start event. Coalesce one rebind; no file work or
            // native timing query is allowed in this accessor.
            if (Interlocked.Exchange(ref state.RebindQueued, 1) == 0)
                Dispatch(() =>
                {
                    state.RebindQueued = 0;
                    if (Parts.TryGetValue(state.PartId, out var live) && live == state) QueuePart(state.Part);
                });
        }
        return input;
    }

    internal static void RenderStarted(WIVSMMidiPart part)
    {
        if (!Parts.TryGetValue((nint)part, out var state))
        {
            lock (Sync) if (Values.Count == 0) return;
            if (!HasControls(part)) return;
            state = Parts.GetOrAdd((nint)part, _ => new(part));
        }
        Interlocked.Increment(ref state.Generation);
        state.Rendering = true; state.Cache = null;
    }

    internal static void RenderCompleted(WIVSMMidiPart part)
    {
        if (!Parts.TryGetValue((nint)part, out var state)) return;
        state.Rendering = false;
        RequestPart(part);
    }

    internal static void SourceChanged(WIVSMSequence source)
    {
        if (!HasState(source)) return;
        var active = EnumerateParts(source).ToDictionary(p => (nint)p);
        foreach (var state in Parts.Values)
        {
            if (state.SourceId != (nint)source) continue;
            if (active.TryGetValue(state.PartId, out var part)) RequestPart(part);
            else if (Parts.TryRemove(state.PartId, out _)) Interlocked.Increment(ref state.Generation);
        }
        Changed?.Invoke();
    }

    internal static void EnsureForExport(WIVSMSequence source)
    {
        foreach (var part in EnumerateParts(source))
        {
            if (!HasControls(part) || !Settings.IndividualPhonemeVolume)
            {
                if (Parts.ContainsKey((nint)part)) RefreshPlacement(part);
                continue;
            }
            BreathVolumeService.EnsureCurrentWaveForExport(source, part);
            var state = Parts.GetOrAdd((nint)part, _ => new(part));
            var request = Capture(state, state.Generation);
            if (request == null)
            {
                if (HasControls(part)) throw new InvalidOperationException("Phoneme volume audio is not ready.");
                continue;
            }
            if (request.Regions.Length == 0) continue;
            if (state.Cache is { } cached && cached.Input == request.Input && cached.Length == request.Length && cached.Written == request.Written)
                continue;
            long generation = Interlocked.Increment(ref state.Generation);
            var cache = Task.Run(() =>
            {
                state.Gate.Wait();
                try { return Process(request with { Generation = generation }); }
                finally { state.Gate.Release(); }
            }).GetAwaiter().GetResult();
            state.Cache = cache;
            InFlightFiles.TryRemove(cache.Output, out _);
            RefreshPlacement(part);
        }
    }

    private static bool HasControls(WIVSMMidiPart part)
    {
        for (ulong i = 0; i < part.NumNotes; i++)
            if (part.GetNote(i) is { } note && HasTopology(note) && !Get(note).IsDefault) return true;
        return false;
    }

    private static bool HasTopology(WIVSMNote note) => !string.IsNullOrWhiteSpace(note.Phonemes) && note.Phonemes.Length <= 60;

    private static IEnumerable<WIVSMMidiPart> EnumerateParts(WIVSMSequence source)
    {
        for (ulong t = 0; t < source.NumTrack; t++)
            if (source.GetTrack(t) is WIVSMMidiTrack track)
                for (ulong p = 0; p < track.NumParts; p++)
                    if (track.GetPart(p) is WIVSMMidiPart part) yield return part;
    }

    internal static PhonemeVolumeArchive.Entry[] Snapshot(WIVSMSequence source)
    {
        var result = Unbound.TryGetValue((nint)source, out var unbound) ? new List<PhonemeVolumeArchive.Entry>(unbound) : new();
        foreach (var part in EnumerateParts(source))
            for (ulong n = 0; n < part.NumNotes; n++)
                if (part.GetNote(n) is { } note && HasTopology(note) && Get(note) is { IsDefault: false } plan)
                    result.Add(new(SampledTimingVsmBinding.Address(note), plan.Values.ToArray()));
        return result.ToArray();
    }

    internal static void Load(WIVSMSequence source, PhonemeVolumeArchive.Entry[] entries)
    {
        foreach (var entry in entries)
        {
            var a = entry.Address;
            var track = (ulong)a.Track < source.NumTrack ? source.GetTrack((ulong)a.Track) as WIVSMMidiTrack : null;
            var part = track != null && (ulong)a.Part < track.NumParts ? track.GetPart((ulong)a.Part) : null;
            var note = part != null && (ulong)a.Note < part.NumNotes ? part.GetNote((ulong)a.Note) : null;
            if (note == null || note.RelPosTick.Value != a.Position || note.DurationTick.Value != a.Duration ||
                note.NoteNumber != a.Pitch || part!.VoiceBankID != a.Bank || note.Phonemes != a.Topology)
            {
                if (!Unbound.TryGetValue((nint)source, out var unbound)) Unbound[(nint)source] = unbound = new();
                unbound.Add(entry); continue;
            }
            SetCore(Key(note), new(a.Topology, entry.Decibels));
        }
        foreach (var part in EnumerateParts(source)) if (HasControls(part)) RequestPart(part);
    }

    internal static void Close(WIVSMSequence source)
    {
        Unbound.Remove((nint)source);
        foreach (var part in EnumerateParts(source))
        {
            if (Parts.TryRemove((nint)part, out var state)) Interlocked.Increment(ref state.Generation);
            for (ulong n = 0; n < part.NumNotes; n++)
                if (part.GetNote(n) is { } note) { var key = Key(note); lock (Sync) Values.Remove(key); }
        }
        foreach (var state in Parts.Values.Where(s => s.SourceId == (nint)source))
            if (Parts.TryRemove(state.PartId, out _)) Interlocked.Increment(ref state.Generation);
        PhonemeVolumeKey[] keys;
        lock (Sync) keys = Values.Keys.ToArray();
        var expired = keys.Where(k => !BreathVolumeService.IsNoteGenerationActive(k.Handle, k.Generation)).ToArray();
        lock (Sync) foreach (var key in expired) Values.Remove(key);
        CleanupFiles();
    }

    internal static void RefreshSettings()
    {
        foreach (var state in Parts.Values) RequestPart(state.Part);
        Changed?.Invoke();
    }

    internal static void CopyUnbound(WIVSMSequence source, WIVSMSequence target)
    {
        if (Unbound.TryGetValue((nint)source, out var entries))
            Unbound[(nint)target] = entries.Select(e => e with { Decibels = e.Decibels.ToArray() }).ToList();
    }
    internal static bool HasUnbound(WIVSMSequence source) => Unbound.TryGetValue((nint)source, out var values) && values.Count != 0;
    internal static bool HasState(WIVSMSequence source) => HasUnbound(source) || Parts.Values.Any(s => s.SourceId == (nint)source);

    internal static string Status(WIVSMNote note)
    {
        if (!Settings.IndividualPhonemeVolume) return "Disabled";
        if (!HasTopology(note)) return "Unavailable";
        if (Get(note).IsDefault) return "Original";
        if (note.Parent is { } part && Parts.TryGetValue((nint)part, out var state))
        {
            if (state.Error != null) return "Unavailable";
            if (state.Cache != null) return "Ready";
        }
        return "Pending";
    }

    internal static object Diagnostics(WIVSMNote note) => new
    {
        enabled = Settings.IndividualPhonemeVolume,
        values = HasTopology(note) ? Get(note).Values : [],
        status = Status(note),
        error = note.Parent is { } part && Parts.TryGetValue((nint)part, out var state) ? state.Error : null
    };

    private static void RefreshPlacement(WIVSMMidiPart part)
    {
        if (part.Sequence is not { IsOpen: true } source) return;
        RegisterPlacement?.Invoke(App.AudioPlayer, [source, part]);
        RenderedWaveCachePatch.InvalidateAndRefreshPart(part);
    }

    private static void CleanupFiles()
    {
        var current = Parts.Values.Select(p => p.Cache?.Output).OfType<string>().ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var file in Files.Keys)
        {
            if (current.Contains(file) || InFlightFiles.ContainsKey(file)) continue;
            try { File.Delete(file); Files.TryRemove(file, out _); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    private static void Dispatch(Action action)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher == null) return;
        void Safe()
        {
            try { action(); }
            catch (Exception e) { Debug.Print("Phoneme volume UI update: " + e.GetType().Name); }
        }
        if (dispatcher.CheckAccess()) Safe();
        else dispatcher.BeginInvoke(DispatcherPriority.Background, (Action)Safe);
    }
}
