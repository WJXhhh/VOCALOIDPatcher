using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using HarmonyLib;
using VOCALOIDPatcher.BreathVolume;
using VOCALOIDPatcher.Config;
using VOCALOIDPatcher.ConsonantTiming;
using VOCALOIDPatcher.DurationSelection;
using VOCALOIDPatcher.Patch.Patches;
using VOCALOIDPatcher.PhonemeVolume;
using VOCALOIDPatcher.Utils;
using Yamaha.VOCALOID;
using Yamaha.VOCALOID.VSM;

namespace VOCALOIDPatcher.VoiceComp;

internal static class VoiceCompHost
{
    private sealed class Project(WIVSMSequence source)
    {
        internal readonly WIVSMSequence Source = source;
        internal readonly Dictionary<DurationNoteKey, VoiceCompPlan> Values = new();
        internal readonly Dictionary<DurationNoteKey, DurationNoteKey> Aliases = new();
        internal VoiceCompArchive.Entry[] Unbound = [];
        internal string Saved = "";
        internal WIVSMUpdateObserver? Observer;
    }
    internal sealed class PartState(WIVSMMidiPart part)
    {
        internal readonly WIVSMMidiPart Part = part;
        internal readonly nint Source = (nint)part.Sequence!;
        internal readonly nint Id = (nint)part;
        internal long Generation, NativeVersion;
        internal bool Queued, Running, Dirty = true;
        internal string Status = "Pending";
        internal string? Error, Wave, LastCompletedWave;
        internal CompRenderPlan? Composition;
        internal float[] ResultPeaks = [];
        internal ImmutableArray<CompPhonemeSpan> Spans = [];
        internal string Stamp = "";
        internal readonly ConcurrentDictionary<string, byte> Files = new();
        internal Dictionary<string, CompSourceAudio> Cache = new();
        internal readonly LinkedList<string> CacheOrder = new();
        internal CancellationTokenSource? Cancellation;
        internal readonly ConcurrentDictionary<int, CompSourceAudio[]> Views = new();
    }
    private sealed record Target(int Index, long Begin, long End, long Vowel, VoiceCompPlan Plan);
    private static readonly Dictionary<nint, Project> Projects = new();
    private static readonly ConcurrentDictionary<nint, PartState> Parts = new();
    private static readonly Dictionary<DurationNoteKey, VoiceCompPlan> Clipboard = new();
    private static readonly SemaphoreSlim SourceWorker = new(1, 1);
    private static bool _stopping;
    private static readonly string DirectoryPath = Path.Combine(Path.GetTempPath(), "VOCALOIDPatcher", "VoiceComp", Guid.NewGuid().ToString("N"));
    private static readonly MethodInfo? RegisterPlacement = AccessTools.Method(typeof(AudioPlayer), "RegisterAudioPlacementWithFile",
        [typeof(WIVSMSequence), typeof(WIVSMMidiPart)]);
    internal static event Action? Changed;

    internal static void Initialize()
    {
        System.IO.Directory.CreateDirectory(DirectoryPath);
    }
    static VoiceCompHost()
    {
        BreathVolumeService.NoteCopied += (source, target) =>
        {
            var plan = Get(source); if (plan == null) return;
            if (target.Sequence is { } privateOwner && NativeRenderSession.IsInternalSequence(privateOwner)) return;
            if (target.Sequence is not { } owner) Clipboard[Key(target)] = plan;
            else For(owner).Values[Key(target)] = plan;
        };
        BreathVolumeService.Changed += (kind, part) =>
        { if (kind == BreathVolumeChangeKind.Values) { if (part != null) Queue(part); else QueueKnown(); } };
        PhonemeVolumeHost.Changed += QueueKnown;
        RegisterShift.RegisterShiftService.ValuesChanged += QueueKnown;
        Evec.EvecService.PartChanged += part => { if (part != null) Queue(part); else QueueKnown(); };
        Settings.EvecEnabledChanged += _ => QueueKnown();
        Settings.ExtendedChinesePinyinChanged += _ => QueueKnown();
        AppDomain.CurrentDomain.ProcessExit += (_, _) =>
        {
            foreach (var state in Parts.Values) state.Cancellation?.Cancel();
            // Never tear down native objects from the process-exit callback.
        };
    }
    private static DurationNoteKey Key(WIVSMNote note) => new(note.CppObjPtr, BreathVolumeService.NoteGeneration(note));
    private static Project For(WIVSMSequence source)
    {
        if (Projects.TryGetValue((nint)source, out var existing)) return existing;
        if (NativeRenderSession.IsInternalSequence(source)) throw new InvalidOperationException("Private source cannot own voice-comp edits.");
        var project = new Project(source); Projects[(nint)source] = project;
        var observer = new WIVSMUpdateObserver();
        observer.Notified += update =>
        {
            if (TimingCacheInvalidationScope.IsActive((nint)source)) return;
            if (update.NotifyType is VSMNotifyType.DidUpdateWaveFile or VSMNotifyType.DidUpdatePhonemePosition or
                VSMNotifyType.DidUpdateName or VSMNotifyType.DidUpdateColor) return;
            var affected = new HashSet<nint>(); bool all = update.IsMidiEffectType;
            foreach (var item in update.Objects)
            {
                if (item is WIVSMMidiPart removed && update.NotifyType == VSMNotifyType.DidRemove && Parts.TryRemove((nint)removed, out var oldState))
                { oldState.Cancellation?.Cancel(); if (!oldState.Running) Cleanup(oldState, true); continue; }
                WIVSMMidiPart? part = item switch
                {
                    WIVSMMidiPart p => p,
                    WIVSMNote n when update.NotifyType != VSMNotifyType.DidRemove => n.Parent,
                    WIVSMMidiController c when update.NotifyType != VSMNotifyType.DidRemove => c.Parent,
                    _ => null
                };
                if (part != null) affected.Add((nint)part); else all = true;
            }
            foreach (var state in Parts.Values.Where(p => p.Source == (nint)source && (all || affected.Contains(p.Id))))
            { state.NativeVersion++; state.Dirty = true; state.Cancellation?.Cancel(); }
        };
        if (!source.AddUpdateObserver(observer)) { Projects.Remove((nint)source); observer.Dispose(); throw new InvalidOperationException("Cannot observe voice-comp input."); }
        project.Observer = observer;
        // Shared native history captures note generations for delete/undo.
        _ = SampledTimingHost.ForDuration(source);
        project.Saved = Fingerprint(project); return project;
    }

    internal static VoiceCompPlan? Get(WIVSMNote note)
    {
        var key = Key(note);
        return note.Sequence is { } source && Projects.TryGetValue((nint)source, out var project)
            ? project.Values.GetValueOrDefault(key) : Clipboard.GetValueOrDefault(key);
    }
    internal static void Set(WIVSMNote note, VoiceCompPlan? plan)
        => SetMany([note], _ => plan);

    internal static SourceRecipe SnapshotSource(WIVSMNote donor, SourceRecipe source)
    {
        if (donor.IsAi) throw new NotSupportedException("Source snapshots require a sampled voicebank.");
        var evec = Evec.EvecService.GetState(donor); var expression = donor.GetNoteExpression();
        return source with { Lyric = donor.Lyric, Phonemes = donor.Phonemes, VowelIndex = -1,
            Evec = new(evec.VoiceColorId, evec.AttackId, evec.ReleaseId, evec.ConsonantExtension),
            Expression = new(donor.NoteVelocity, expression.Accent, expression.Decay, expression.BendDepth,
                expression.BendLength, expression.Opening, expression.RisePort, expression.FallPort) };
    }
    internal static void SetMany(IEnumerable<WIVSMNote> notes, Func<VoiceCompPlan?, VoiceCompPlan?> change, bool dryRun = false)
    {
        var edits = notes.ToArray(); if (edits.Length == 0) return;
        var source = edits[0].Sequence ?? throw new InvalidOperationException("Missing source.");
        if (!source.IsOpen || source.IsStaged || edits.Any(n => n.Parent?.IsAi != false || !string.IsNullOrEmpty(n.Parent.SecondaryVoiceBankID) || (nint)n.Sequence! != (nint)source))
            throw new InvalidOperationException("Voice comp requires editable sampled notes.");
        var project = For(source);
        var changes = new List<(DurationNoteKey Key, VoiceCompPlan? Before, VoiceCompPlan? After)>();
        foreach (var note in edits)
        {
            var address = SampledTimingVsmBinding.Address(note);
            if (project.Unbound.Any(e => (e.Address.Track, e.Address.Part, e.Address.Note) == (address.Track, address.Part, address.Note)))
                throw new NotSupportedException("A preserved unbound voice-comp recipe occupies this note locator.");
            var key = Key(note); var before = project.Values.GetValueOrDefault(key); var after = change(before);
            after?.Validate(); if (after?.IsEmpty == true) after = null;
            if (after?.Isolation is { } isolation && source.GetTimeFromTick(note.AbsPosTick, note.AbsEndTick) * 1000 + isolation.EndOffsetMs <= 0)
                throw new InvalidDataException("The sustain endpoint must follow the note start.");
            if (before != after) changes.Add((key, before, after));
        }
        if (dryRun) return;
        if (changes.Count == 0) { foreach (var note in edits) if (note.Parent is { } retry) Queue(retry); return; }
        var command = new Edit(project, changes.ToArray()); command.ApplyAfter();
        try { CustomParameterHistoryCoordinator.Push(source, command); }
        catch { command.ApplyBefore(); throw; }
        command.AfterApply();
    }
    private sealed record Edit(Project Project, (DurationNoteKey Key, VoiceCompPlan? Before, VoiceCompPlan? After)[] Changes) : ICustomParameterHistoryEdit
    {
        private void Apply(bool after)
        {
            foreach (var change in Changes)
            {
                var key = Project.Aliases.GetValueOrDefault(change.Key, change.Key); var plan = after ? change.After : change.Before;
                if (plan == null) Project.Values.Remove(key); else Project.Values[key] = plan;
            }
        }
        public void ApplyBefore() => Apply(false);
        public void ApplyAfter() => Apply(true);
        public void AfterApply() { NativeChanged(Project.Source); Changed?.Invoke(); CommandManager.InvalidateRequerySuggested(); }
    }

    internal static void RebindNativeHistory(WIVSMSequence source, IReadOnlyDictionary<DurationNoteKey, DurationNoteKey> keys)
    {
        if (!Projects.TryGetValue((nint)source, out var project)) return;
        foreach (var alias in project.Aliases.ToArray()) project.Aliases[alias.Key] = keys.GetValueOrDefault(alias.Value, alias.Value);
        foreach (var pair in keys)
        {
            if (project.Values.Remove(pair.Key, out var plan)) project.Values[pair.Value] = plan;
            project.Aliases[pair.Key] = pair.Value;
        }
        NativeChanged(source); Changed?.Invoke();
    }
    internal static bool Owns(WIVSMMidiPart part) => Settings.VoiceCompEnabled && part.Sequence is { } sequence &&
        Projects.TryGetValue((nint)sequence, out var project) && project.Values.Count != 0 &&
        !NativeRenderSession.IsInternalSequence(sequence) && part.Notes.Any(n => Get(n)?.Active == true);
    internal static PartState? View(WIVSMMidiPart part) => Parts.GetValueOrDefault((nint)part);
    internal static string Substitute(WIVSMMidiPart part, string original) => Settings.VoiceCompEnabled &&
        Parts.TryGetValue((nint)part, out var state) && !state.Dirty && state.Wave != null ? state.Wave : original;

    internal static void NativeChanged(WIVSMSequence source)
    {
        if (NativeRenderSession.IsInternalSequence(source) || !Projects.ContainsKey((nint)source)) return;
        var live = source.MidiTracks.SelectMany(t => t.MidiParts).ToArray();
        var ids = live.Select(p => (nint)p).ToHashSet();
        foreach (var stale in Parts.Values.Where(s => s.Source == (nint)source && !ids.Contains(s.Id)))
        { stale.Cancellation?.Cancel(); Parts.TryRemove(stale.Id, out _); if (!stale.Running) Cleanup(stale, true); }
        foreach (var part in live)
        {
            if (Owns(part)) Queue(part);
            else if (Parts.TryRemove((nint)part, out var state))
            {
                state.Cancellation?.Cancel(); Refresh(part); if (!state.Running) Cleanup(state, true);
                Evec.EvecRenderCoordinator.SequenceChanged(source, "voice-comp-restored");
                SegmentedPhonemeRenderCoordinator.SequenceChanged(source, "voice-comp-restored");
            }
        }
    }
    internal static void RefreshSettings()
    {
        foreach (var project in Projects.Values.ToArray())
        {
            var previous = Parts.Values.Where(p => p.Source == (nint)project.Source).Select(p => p.Part).ToArray();
            NativeChanged(project.Source);
            foreach (var part in previous) Refresh(part);
        }
        Changed?.Invoke();
    }
    private static void QueueKnown() => Dispatch(() =>
    {
        foreach (var state in Parts.Values) if (state.Part.Sequence?.IsOpen == true) Queue(state.Part);
    });
    internal static void Queue(WIVSMMidiPart part)
    {
        if (_stopping) return;
        if (Application.Current?.Dispatcher is { } dispatcher && !dispatcher.CheckAccess()) { Dispatch(() => Queue(part)); return; }
        if (!Owns(part)) return;
        var state = Parts.GetOrAdd((nint)part, _ => new(part));
        string stamp = InputKey(state) + string.Join(";", part.Notes.Select(n => Get(n)?.Fingerprint ?? ""));
        if (stamp == state.Stamp && (state.Running || !state.Dirty)) return;
        bool hadPublishedWave = state.Wave != null;
        state.Wave = null; state.Composition = null; state.ResultPeaks = []; state.Spans = []; state.Views.Clear();
        state.Stamp = stamp;
        state.Generation++; state.Dirty = true; state.Status = "Pending"; state.Cancellation?.Cancel();
        if (hadPublishedWave) Refresh(part);
        if (state.Queued) return; state.Queued = true;
        _ = Delay(state);
    }
    private static async Task Delay(PartState state)
    {
        await Task.Delay(120).ConfigureAwait(false);
        Dispatch(() => { state.Queued = false; if (!state.Running) Start(state); });
    }
    private static void Start(PartState state)
    {
        if (_stopping) return;
        if (!Parts.TryGetValue(state.Id, out var live) || live != state || !Owns(state.Part) ||
            state.Part.Sequence is not { IsOpen: true, IsStaged: false }) return;
        long generation = state.Generation; state.Running = true; state.Status = "Rendering"; state.Error = null;
        state.Cancellation?.Dispose(); state.Cancellation = new(); var token = state.Cancellation.Token;
        Changed?.Invoke(); _ = Build(state, generation, token);
    }

    private static async Task Build(PartState state, long generation, CancellationToken token)
    {
        string? output = null;
        try
        {
            var captured = CompNativeSource.Ui(() => Capture(state));
            if (captured.NeedAnchors)
            {
                var baseline = await Source(state, Enumerable.Range(0, captured.Targets.Length).ToArray(), null, false, captured.Input, token, anchorsOnly: true).ConfigureAwait(false);
                captured = captured with { Targets = captured.Targets.Select(target =>
                {
                    if (!target.Plan.Active || !target.Plan.HasSources) return target;
                    var note = baseline.Notes.Single(n => n.Index == target.Index);
                    int vowel = note.VowelIndex;
                    if (target.Plan.Consonant != null && vowel < 0) throw new InvalidOperationException("Select the target vowel entrance.");
                    return target with { Begin = Math.Max(0, note.Boundaries[0]), Vowel = vowel < 0 ? note.NoteBegin : note.Boundaries[vowel] };
                }).ToArray() };
            }
            var slices = new List<SlicePlacement>(); var views = new Dictionary<int, CompSourceAudio[]>();
            var spans = new List<CompPhonemeSpan>();
            foreach (var group in captured.Groups)
            {
                token.ThrowIfCancellationRequested();
                var audio = await Source(state, group.Indices, null, group.Hold, captured.Input, token,
                    holdOffsetMs: captured.Targets[group.Indices[^1]].Plan.Isolation?.EndOffsetMs ?? 0,
                    holdLengthMs: captured.Targets[group.Indices[^1]].Plan.Isolation?.SourceLengthMs ?? 0).ConfigureAwait(false);
                long length = group.Hold ? Math.Min(audio.Samples, captured.Targets[group.Indices[^1]].End) : audio.Samples;
                var slice = new SlicePlacement(audio.Path, 0, 0, length,
                    FadeOut: group.Hold ? Fade(captured.Rate, captured.Targets[group.Indices[^1]].Plan.Isolation?.FadeMs ?? 5, length) : 0,
                    Role: CompSourceRole.Connected);
                slices.Add(slice); AddSpans(audio, slice, spans);
                foreach (int index in group.Indices) views[index] = [audio];
            }
            for (int i = 0; i < captured.Targets.Length; i++)
            {
                var target = captured.Targets[i]; var plan = target.Plan;
                if (!plan.Active || !plan.HasSources) continue;
                if (plan.Consonant != null)
                {
                    var consonant = await Source(state, [i], plan.Consonant, false, captured.Input, token).ConfigureAwait(false);
                    var vowel = await Source(state, [i], plan.Vowel!, false, captured.Input, token,
                        minimumLength: target.End - target.Begin).ConfigureAwait(false);
                    var c = consonant.Notes.Single(); var v = vowel.Notes.Single();
                    int ci = plan.Consonant.VowelIndex >= 0 ? VowelIndex(c.Phonemes, plan.Consonant.VowelIndex) : c.VowelIndex;
                    int vi = plan.Vowel!.VowelIndex >= 0 ? VowelIndex(v.Phonemes, plan.Vowel.VowelIndex) : v.VowelIndex;
                    if (ci <= 0) throw new InvalidOperationException("Select the source vowel entrance.");
                    if (vi < 0) throw new InvalidOperationException("Select the source vowel entrance.");
                    long cb = c.Boundaries[0], cv = c.Boundaries[ci], vv = v.Boundaries[vi];
                    var aligned = CompAlignment.AtVowel(cb, cv, target.Vowel);
                    int half = (int)Math.Min(captured.Rate * plan.SeamMs / 2000, Math.Min(cv - cb, target.End - target.Vowel) / 2);
                    long cOffset = Milliseconds(plan.Consonant.OffsetMs, captured.Rate);
                    long cLength = cv - cb + half, vLength = target.End - target.Vowel + half;
                    long vBegin = CompAlignment.Crop(Math.Max(0, vv - half), Math.Min(vowel.Samples, v.Boundaries[^1]), vLength,
                        plan.Vowel.Trim, Milliseconds(plan.Vowel.OffsetMs, captured.Rate));
                    consonant = await FollowControllers(state, consonant, [i], plan.Consonant, captured, aligned.ConsonantBegin - cb - cOffset, token).ConfigureAwait(false);
                    vowel = await FollowControllers(state, vowel, [i], plan.Vowel, captured, target.Vowel - half - vBegin, token).ConfigureAwait(false);
                    slices.Add(Trim(new(consonant.Path, cb + cOffset, aligned.ConsonantBegin, cLength, 0, half * 2, CompSourceRole.Consonant), plan.Consonant, captured.Rate));
                    slices.Add(Trim(new(vowel.Path, vBegin, target.Vowel - half, vLength, half * 2,
                        Fade(captured.Rate, plan.Isolation?.FadeMs ?? 5, vLength), CompSourceRole.Vowel), plan.Vowel, captured.Rate));
                    views[i] = [consonant, vowel];
                    AddSpans(consonant, slices[^2], spans, 0, ci);
                    AddSpans(vowel, slices[^1], spans, vi);
                }
                else if (plan.Body is { } recipe)
                {
                    var audio = await Source(state, [i], recipe, false, captured.Input, token,
                        minimumLength: target.End - target.Begin).ConfigureAwait(false);
                    var note = audio.Notes.Single(); long length = target.End - target.Begin;
                    int start = recipe.VowelIndex >= 0 ? VowelIndex(note.Phonemes, recipe.VowelIndex) : 0;
                    long begin = CompAlignment.Crop(note.Boundaries[start], Math.Min(audio.Samples, note.Boundaries[^1]), length,
                        recipe.Trim, Milliseconds(recipe.OffsetMs, captured.Rate));
                    audio = await FollowControllers(state, audio, [i], recipe, captured, target.Begin - begin, token).ConfigureAwait(false);
                    int fade = Fade(captured.Rate, plan.Isolation?.FadeMs ?? 5, length);
                    slices.Add(Trim(new(audio.Path, begin, target.Begin, length, fade, fade), recipe, captured.Rate)); views[i] = [audio];
                    AddSpans(audio, slices[^1], spans);
                }
            }
            token.ThrowIfCancellationRequested();
            long samples = Math.Max(captured.Samples, slices.Max(s => s.OutputEnd));
            var composition = new CompRenderPlan(captured.Rate, samples, slices.ToImmutableArray());
            output = Path.Combine(DirectoryPath, Guid.NewGuid().ToString("N") + ".wav");
            state.Files.TryAdd(output, 0);
            var (clipped, peaks) = await Task.Run(() => CompWave.MixWithPeaks(output, composition, token), token).ConfigureAwait(false);
            CompNativeSource.Ui(() =>
            {
                token.ThrowIfCancellationRequested();
                if (state.Generation != generation || !Parts.TryGetValue(state.Id, out var current) || current != state)
                    throw new OperationCanceledException();
                state.Wave = output; state.LastCompletedWave = output; state.Composition = composition; output = null; state.Views.Clear();
                state.ResultPeaks = peaks;
                state.Spans = spans.ToImmutableArray();
                foreach (var pair in views) state.Views[pair.Key] = pair.Value;
                state.Status = clipped == 0 ? "Ready" : "Clipped"; state.Error = null; state.Dirty = false;
                Refresh(state.Part); Changed?.Invoke(); return true;
            });
            Cleanup(state);
        }
        catch (OperationCanceledException) { }
        catch (Exception error)
        {
            Dispatch(() => { if (state.Generation == generation) { state.Status = "Failed"; state.Error = error.Message; Changed?.Invoke(); } });
            Debug.Print("Voice-comp source: " + error.GetType().Name);
        }
        finally
        {
            if (output != null) TryDelete(output);
            Dispatch(() =>
            {
                state.Running = false;
                if (Parts.GetValueOrDefault(state.Id) != state) Cleanup(state, true);
                else if (state.Generation != generation) Start(state);
            });
        }
    }
    private static void AddSpans(CompSourceAudio audio, SlicePlacement slice, List<CompPhonemeSpan> result, int first = 0, int endIndex = int.MaxValue)
    {
        foreach (var note in audio.Notes)
        {
            var phones = note.Phonemes.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            for (int i = first; i < Math.Min(phones.Length, endIndex); i++)
            {
                long begin = Math.Max(slice.SourceBegin, note.Boundaries[i]);
                long end = Math.Min(slice.SourceBegin + slice.Length, note.Boundaries[i + 1]);
                if (end > begin) result.Add(new(note.Index, phones[i], slice.OutputBegin + begin - slice.SourceBegin, slice.OutputBegin + end - slice.SourceBegin));
            }
        }
    }

    private sealed record Group(int[] Indices, bool Hold);
    private sealed record CaptureData(int Rate, long Samples, string Input, Target[] Targets, Group[] Groups, bool HasControllers, bool NeedAnchors);
    private static CaptureData Capture(PartState state)
    {
        var part = state.Part; var sequence = part.Sequence!; var notes = part.Notes.ToArray();
        int rate = (int)sequence.GetSamplingRate(); if (rate < 8000) rate = 44100;
        long Sample(VSMAbsTick tick) => (long)Math.Round((sequence.PresendTimeSec + sequence.GetTimeFromTick(part.AbsBeginTick, tick)) * rate);
        var targets = notes.Select((note, i) =>
        {
            var plan = Get(note) ?? new(); var positions = note.GetPhonemePositions();
            int vowel;
            try
            {
                vowel = Array.FindIndex(note.Phonemes.Split(' ', StringSplitOptions.RemoveEmptyEntries), note.IsVowel);
                if (vowel < 0) throw new InvalidDataException("Select the vowel entrance.");
            }
            catch (InvalidDataException) when (plan.Consonant == null) { vowel = 0; }
            long begin = positions.Count > 0 && positions[0] != int.MinValue ? Sample(note.GetAbsPositionFromNoteBaseTick(positions[0])) : Sample(note.AbsPosTick);
            long entry = positions.Count > vowel && vowel >= 0 && positions[vowel] != int.MinValue ? Sample(note.GetAbsPositionFromNoteBaseTick(positions[vowel])) : Sample(note.AbsPosTick);
            return new Target(i, Math.Max(0, begin), Sample(note.AbsEndTick) + Milliseconds(plan.Isolation?.EndOffsetMs ?? 0, rate), entry, plan);
        }).ToArray();
        var hold = targets.Select((t, i) => t.Plan.Active && t.Plan.Isolation?.After == true ||
            i + 1 < targets.Length && targets[i + 1].Plan.Active && targets[i + 1].Plan.HasSources).ToArray();
        var groups = new List<Group>(); var run = new List<int>();
        for (int i = 0; i < notes.Length; i++)
        {
            if (targets[i].Plan.Active && targets[i].Plan.HasSources)
            { if (run.Count != 0) { groups.Add(new(run.ToArray(), true)); run.Clear(); } continue; }
            run.Add(i);
            if (hold[i]) { groups.Add(new(run.ToArray(), true)); run.Clear(); }
        }
        if (run.Count != 0) groups.Add(new(run.ToArray(), false));
        string input = InputKey(state);
        bool controllers = Enum.GetValues<VSMControllerType>().Any(type => CompSourceFingerprint.IsSynthesisController(type) && part.GetNumController(type) > 0) ||
            notes.Any(n => n.VibratoDurationTick.Value > 0 || n.NumDirectPitches > 0);
        bool needAnchors = !part.HasValidRenderedScore || notes.Any(n => Get(n)?.HasSources == true &&
            n.GetPhonemePositions().Count != n.Phonemes.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length + 1);
        return new(rate, Sample(part.AbsEndTick) + rate / 2, input, targets, groups.ToArray(), controllers, needAnchors);
    }
    private static async Task<CompSourceAudio> Source(PartState state, int[] indices, SourceRecipe? recipe, bool hold, string input, CancellationToken token,
        long controllerShift = 0, long minimumLength = 0, double holdOffsetMs = 0, bool anchorsOnly = false, double holdLengthMs = 0)
    {
        token.ThrowIfCancellationRequested();
        var sourceInput = CompNativeSource.Ui(() =>
        {
            token.ThrowIfCancellationRequested(); var effective = recipe;
            if (recipe?.LengthMs == 0)
            {
                var first = state.Part.GetNote((ulong)indices[0])!; var sequence = state.Part.Sequence!;
                double duration = sequence.GetTimeFromTick(first.AbsPosTick, first.AbsEndTick) * 1000;
                effective = recipe with { LengthMs = Math.Max(recipe.Duration(duration), minimumLength > 0 ? minimumLength * 1000d / (int)sequence.GetSamplingRate() + 250 : 0) };
                effective.Validate();
            }
            return (Fingerprint: CompSourceFingerprint.Capture(state.Part, indices), Recipe: effective);
        });
        var renderRecipe = sourceInput.Recipe == null ? null : sourceInput.Recipe with
        { OffsetMs = 0, Trim = CompTrim.KeepHead, TrimBeginMs = 0, TrimEndMs = 0, VowelIndex = -1 };
        string key = sourceInput.Fingerprint + ":" + string.Join(",", indices) + ":" + JsonSerializer.Serialize(renderRecipe) + ":" + hold + ":" + controllerShift + ":" + holdOffsetMs + ":" + anchorsOnly + ":" + holdLengthMs;
        if (state.Cache.TryGetValue(key, out var cached) && File.Exists(cached.Path))
        {
            state.CacheOrder.Remove(key); state.CacheOrder.AddLast(key);
            return cached;
        }
        token.ThrowIfCancellationRequested();
        await SourceWorker.WaitAsync(token).ConfigureAwait(false);
        try
        {
            CompNativeSource? prepared = null;
            while (prepared == null)
            {
                try
                {
                    prepared = CompNativeSource.Ui(() =>
                    {
                        token.ThrowIfCancellationRequested();
                        if (Parts.GetValueOrDefault(state.Id) != state) throw new OperationCanceledException();
                        return new CompNativeSource(App.SequenceManager!, state.Part, indices, sourceInput.Recipe, hold, controllerShift, holdOffsetMs, anchorsOnly, holdLengthMs);
                    });
                }
                catch (CompNativeCloseBusyException) { await Task.Delay(25, token).ConfigureAwait(false); }
            }
            using var source = prepared;
            string path = Path.Combine(DirectoryPath, Guid.NewGuid().ToString("N") + ".wav");
            state.Files.TryAdd(path, 0);
            try
            {
                var result = await source.RenderAsync(path, token).ConfigureAwait(false); token.ThrowIfCancellationRequested();
                state.Cache[key] = result;
                state.CacheOrder.Remove(key); state.CacheOrder.AddLast(key);
                // Bound cached raw sources; current published compositions retain
                // their files until the owning Part closes. Dictionary slots are
                // reused after removal, so key enumeration is not recency order.
                while (state.Cache.Count > 32 && state.CacheOrder.First is { } oldest)
                { state.CacheOrder.RemoveFirst(); state.Cache.Remove(oldest.Value); }
                return result;
            }
            catch { TryDelete(path); throw; }
        }
        finally { SourceWorker.Release(); }
    }
    private static void Cleanup(PartState state, bool all = false)
    {
        var keep = all ? new HashSet<string>() : state.Cache.Values.Select(s => s.Path).Concat(state.Composition?.Slices.Select(s => s.Path) ?? [])
            .Concat(state.Views.Values.SelectMany(v => v).Select(s => s.Path)).Append(state.Wave ?? "")
            .Append(state.LastCompletedWave ?? "").ToHashSet();
        foreach (string path in state.Files.Keys.Where(p => !keep.Contains(p)))
            try { File.Delete(path); state.Files.TryRemove(path, out _); } catch (IOException) { }
    }
    private static async Task<CompSourceAudio> FollowControllers(PartState state, CompSourceAudio audio, int[] indices,
        SourceRecipe recipe, CaptureData captured, long shift, CancellationToken token)
    {
        if (shift == 0 || !captured.HasControllers) return audio;
        if (recipe.LengthMs == 0)
            recipe = recipe with { LengthMs = (audio.Notes[^1].NoteEnd - audio.Notes[0].NoteBegin) * 1000d / audio.Rate };
        var followed = await Source(state, indices, recipe, false, captured.Input, token, shift).ConfigureAwait(false);
        if (!audio.Notes.SelectMany(n => n.Boundaries).SequenceEqual(followed.Notes.SelectMany(n => n.Boundaries)))
            throw new InvalidOperationException("Source timing changed while aligning main-track controllers.");
        return followed;
    }
    private static string InputKey(PartState state)
    {
        var control = string.Join(";", state.Part.Notes.Select(n => JsonSerializer.Serialize(PhonemeVolumeHost.Get(n).Values) + ":" + BreathVolumeService.GetValue(n) +
            ":" + RegisterShift.RegisterShiftService.GetValue(n.CppObjPtr) + ":" + JsonSerializer.Serialize(Evec.EvecService.GetState(n))));
        var session = SampledTimingHost.Find(state.Part.Sequence!);
        string timing = "", transitions = "";
        if (session != null && state.Part.GetNote(0UL) is { } first)
        {
            var address = SampledTimingVsmBinding.Address(first);
            timing = string.Join(";", session.Document.Snapshot().Where(p => p.Key.Track == address.Track && p.Key.Part == address.Part)
                .OrderBy(p => p.Key.Note).Select(p => p.Key.Note + ":" + p.Value.Fingerprint));
            var archive = session.Transitions.Snapshot;
            transitions = archive.Data is { } data ? TransitionArchiveData.From(data.KeepNotes(n =>
                n.Address.Track == address.Track && n.Address.Part == address.Part)).Fingerprint : archive.Fingerprint;
        }
        string settings = Settings.EvecEnabled + ":" + Settings.ExtendedChinesePinyin + ":" + Settings.IndividualBreathVolume + ":" +
            Settings.IndividualPhonemeVolume + ":" + Settings.RegisterShift + ":" + Settings.TransitionSmoothingEnabled + ":" + JsonSerializer.Serialize(Settings.GetTransitionSmoothingConfig());
        return state.NativeVersion + ":" + settings + ":" + control + ":" + timing + ":" + transitions;
    }
    private static SlicePlacement Trim(SlicePlacement slice, SourceRecipe recipe, int rate)
    {
        long begin = Milliseconds(recipe.TrimBeginMs, rate), end = Milliseconds(recipe.TrimEndMs, rate);
        long length = slice.Length - begin - end;
        if (length < 1) throw new InvalidDataException("Trim handles removed the entire source window.");
        return slice with { SourceBegin = slice.SourceBegin + begin, OutputBegin = slice.OutputBegin + begin, Length = length,
            FadeIn = (int)Math.Min(slice.FadeIn, length / 2), FadeOut = (int)Math.Min(slice.FadeOut, length / 2) };
    }
    internal static int VowelIndex(string phonemes, int requested)
    {
        var phones = phonemes.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (requested >= 0) return requested < phones.Length ? requested : throw new InvalidDataException("Vowel index exceeds source phonemes.");
        // Language-neutral common VOCALOID vowels. Unknown alphabets expose the
        // explicit index control; do not invent equal-duration boundaries.
        for (int i = 0; i < phones.Length; i++)
        {
            string p = phones[i].Split('#')[0];
            if (new[] { "a", "e", "i", "o", "u", "M", "7", "@", "@r", "A", "E", "I", "O", "U", "V", "Q", "ae", "ei", "ai", "au", "ou", "ia", "ie", "ua", "uo", "y", "AN", "EN", "IN", "UN", "aI", "eI", "OI", "aU", "@U", "i:", "u:", "e@", "I@", "U@" }.Contains(p)) return i;
        }
        throw new InvalidDataException("Select a vowel entrance for this pronunciation.");
    }
    private static long Milliseconds(double value, int rate) => checked((long)Math.Round(value * rate / 1000));
    private static int Fade(int rate, double ms, long length) => (int)Math.Min(length / 2, rate * ms / 1000);
    private static void Refresh(WIVSMMidiPart part)
    {
        if (App.AudioPlayer != null) RegisterPlacement?.Invoke(App.AudioPlayer, [part.Sequence!, part]);
        WaveformSvState.Invalidate();
        RenderedWaveCachePatch.InvalidateAndRefreshPart(part);
        ConsonantTimingOverlay.SourceChanged(part.Sequence!);
        if (View(part) is { Dirty: false, Wave: not null }) AceParameters.AceAcousticHost.UpstreamReady(part);
        else AceParameters.AceAcousticHost.RenderCompleted(part);
    }
    private static void Dispatch(Action action)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher == null || dispatcher.CheckAccess()) action(); else dispatcher.BeginInvoke(action);
    }
    internal static void EnsureForExport(WIVSMSequence source)
    {
        if (!Settings.VoiceCompEnabled) return;
        if (Projects.TryGetValue((nint)source, out var project) && project.Unbound.Any(e => e.Plan.Active))
            throw new InvalidDataException("Unbound voice-comp recipes cannot be exported against different notes.");
        var parts = source.MidiTracks.SelectMany(t => t.MidiParts).Where(Owns).ToArray();
        foreach (var part in parts) Queue(part);
        CompExportWait.UntilReady(() =>
        {
            if (!source.IsOpen) throw new OperationCanceledException("Source closed during export preparation.");
            bool ready = true;
            foreach (var part in parts)
            {
                if (!Parts.TryGetValue((nint)part, out var state)) throw new InvalidOperationException("Voice-comp output was removed.");
                if (state.Status == "Failed") throw new InvalidOperationException(state.Error);
                ready &= !state.Dirty && !state.Running && state.Wave != null;
            }
            return ready;
        });
    }
    internal static VoiceCompArchive.Entry[] Snapshot(WIVSMSequence source)
    {
        if (!Projects.TryGetValue((nint)source, out var project)) return [];
        return source.MidiTracks.SelectMany(t => t.MidiParts).SelectMany(p => p.Notes).Where(n => Get(n) != null)
            .Select(n => new VoiceCompArchive.Entry(SampledTimingVsmBinding.Address(n), Get(n)!)).Concat(project.Unbound).ToArray();
    }
    internal static void Load(WIVSMSequence source, VoiceCompArchive.Entry[] entries)
    {
        if (entries.Length == 0) return;
        var project = For(source); var notes = source.MidiTracks.SelectMany(t => t.MidiParts).SelectMany(p => p.Notes)
            .ToDictionary(SampledTimingVsmBinding.Address);
        var unbound = new List<VoiceCompArchive.Entry>();
        foreach (var entry in entries)
            if (notes.TryGetValue(entry.Address, out var note)) project.Values[Key(note)] = entry.Plan; else unbound.Add(entry);
        project.Unbound = unbound.ToArray(); MarkSaved(source); NativeChanged(source);
    }
    private static string Fingerprint(Project project) => JsonSerializer.Serialize(Snapshot(project.Source));
    internal static bool IsDirty(WIVSMSequence source) => Projects.TryGetValue((nint)source, out var project) && Fingerprint(project) != project.Saved;
    internal static bool HasData(WIVSMSequence source) => Projects.TryGetValue((nint)source, out var p) && (p.Values.Count != 0 || p.Unbound.Length != 0);
    internal static void MarkSaved(WIVSMSequence source) { if (Projects.TryGetValue((nint)source, out var p)) p.Saved = Fingerprint(p); }
    internal static void Close(WIVSMSequence source)
    {
        if (NativeRenderSession.IsInternalSequence(source)) return;
        foreach (var state in Parts.Values.Where(s => s.Source == (nint)source).ToArray())
        { state.Cancellation?.Cancel(); Parts.TryRemove(state.Id, out _); if (!state.Running) Cleanup(state, true); }
        if (Projects.Remove((nint)source, out var project) && project.Observer != null)
        { source.RemoveUpdateObserver(project.Observer); project.Observer.Dispose(); }
    }
    internal static void DrainForManagerDisposal()
    {
        if (Projects.Count == 0 && SourceWorker.CurrentCount == 1 && SegmentedPhonemeRenderCoordinator.CompCleanupQuiescent) return;
        _stopping = true;
        foreach (var project in Projects.Values.ToArray())
        {
            if (project.Source.IsOpen) project.Source.StopAsyncRendering();
            NativeRenderSession.EndForeground(project.Source); Close(project.Source);
        }
        bool Ready() => SourceWorker.CurrentCount == 1 && SegmentedPhonemeRenderCoordinator.CompCleanupQuiescent;
        var dispatcher = Application.Current?.Dispatcher;
        while (!Ready())
        {
            if (dispatcher?.CheckAccess() == true && !dispatcher.HasShutdownStarted)
            {
                var frame = new DispatcherFrame();
                dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() => frame.Continue = false));
                Dispatcher.PushFrame(frame);
            }
            Thread.Sleep(5);
        }
    }
    private static void TryDelete(string path) { try { File.Delete(path); } catch (IOException) { } }
}
