using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Windows.Input;
using VOCALOIDPatcher.BreathVolume;
using VOCALOIDPatcher.ConsonantTiming;
using VOCALOIDPatcher.Utils;
using Yamaha.VOCALOID.VSM;

namespace VOCALOIDPatcher.DurationSelection;

internal static class DurationSelectionHost
{
    private sealed class Entry(WIVSMSequence source)
    {
        internal readonly WIVSMSequence Source = source;
        internal readonly int Thread = Environment.CurrentManagedThreadId;
        internal readonly DurationSelectionDocument Document = new();
        internal readonly List<DurationSelectionArchive.Entry> Unbound = new();
        internal readonly Dictionary<DurationNoteKey, WIVSMNote> Notes = new();
        internal readonly Dictionary<nint, HashSet<DurationNoteKey>> Scopes = new();
        internal string? Fault;
        internal long FaultRevision = -1;
    }
    private static readonly Dictionary<nint, Entry> Entries = new();
    private static readonly Dictionary<DurationNoteKey, DurationSelectionPlan> DetachedCopies = new();
    private static readonly Dictionary<nint, DurationSelectionArchive.Entry[]> Copies = new();
    internal static event Action? Changed;

    static DurationSelectionHost()
    {
        BreathVolumeService.NoteCopied += (source, target) =>
        {
            SampledTimingHost.TrackCopiedTarget(target);
            var plan = Get(source);
            var key = Key(target);
            if (plan.IsDefault || target.Phonemes != plan.Topology) return;
            // Clipboard and internal renderer objects have no user document.
            // The copy generation still prevents later pointer reuse matching.
            if (target.Sequence is not { } sequence || NativeRenderSession.IsInternalSequence(sequence))
                DetachedCopies[key] = plan;
            else
            {
                var entry = For(sequence); entry.Notes[key] = target; entry.Document.Restore(key, plan);
                if (target.Parent is { IsAi: false } part) RememberScope(entry, part);
                SampledTimingHost.ForCopiedDuration(target);
            }
        };
    }
    private static DurationNoteKey Key(WIVSMNote note) => new(note.CppObjPtr, BreathVolumeService.NoteGeneration(note));
    private static Entry For(WIVSMSequence source)
    {
        if (!Entries.TryGetValue((nint)source, out var entry))
        {
            if (NativeRenderSession.IsInternalSequence(source) && !Copies.ContainsKey((nint)source)) throw new InvalidOperationException("Internal renderer cannot own duration edits.");
            entry = new(source); Entries.Add((nint)source, entry);
            entry.Document.Changed += key =>
            {
                if (entry.Source.IsOpen && entry.Notes.TryGetValue(key, out var note) &&
                    BreathVolumeService.IsNoteGenerationActive(key.Handle, key.Generation))
                    SampledTimingHost.DurationChanged(note);
                entry.Fault = null; Changed?.Invoke(); ConsonantTimingOverlay.DurationChanged(); CommandManager.InvalidateRequerySuggested();
            };
        }
        if (entry.Thread != Environment.CurrentManagedThreadId) throw new InvalidOperationException("Duration document requires its owner thread.");
        return entry;
    }
    internal static DurationSelectionPlan Get(WIVSMNote note)
    {
        var key = Key(note);
        var plan = note.Sequence is { } source && Entries.TryGetValue((nint)source, out var entry) ? entry.Document.Get(key) : null;
        plan ??= DetachedCopies.GetValueOrDefault(key);
        return plan?.Topology == note.Phonemes ? plan : new(note.Phonemes);
    }
    internal static void Set(WIVSMNote note, DurationSelectionPlan? plan)
    {
        if (note.Sequence is not { IsOpen: true, IsStaged: false } source || note.Parent is not { IsAi: false } part ||
            NativeRenderSession.IsInternalSequence(source)) throw new InvalidOperationException("Duration preference requires an editable sampled note.");
        if (plan != null && plan.Topology != note.Phonemes) throw new ArgumentException("Duration topology changed.");
        var entry = For(source); var key = Key(note);
        var address = SampledTimingVsmBinding.Address(note);
        if (entry.Unbound.Any(e => (e.Address.Track, e.Address.Part, e.Address.Note) == (address.Track, address.Part, address.Note)))
            throw new NotSupportedException("An unbound saved duration control occupies this locator; it is preserved until its note identity can be resolved.");
        if (entry.Document.Get(key)?.Fingerprint == (plan?.IsDefault == true ? null : plan?.Fingerprint) && entry.Fault == null) return;
        NativeDurationSelection.Initialize(Path.Combine(Patcher.DataDir, "native", "v6patch_clock.dll"));
        NativeDurationSelection.ValidateBudget(part, note, plan);
        SampledTimingHost.ValidateDurationPart(part);
        RememberScope(entry, part);
        entry.Notes[key] = note;
        source.StopAsyncRendering();
        if (entry.Document.Get(key)?.Fingerprint == (plan?.IsDefault == true ? null : plan?.Fingerprint))
        {
            // Explicitly reapplying a failed value retries only this Part. It
            // changes no saved parameter and adds no duplicate history item.
            SampledTimingHost.DurationChanged(note); return;
        }
        entry.Document.Edit(key, plan, edit => CustomParameterHistoryCoordinator.Push(source, edit));
    }
    internal static IReadOnlyDictionary<nint, (float Ratio, uint Policy)> Controls(WIVSMMidiPart part) => part.NotesEnumerator
        .Select(n => (Note: n, Plan: Get(n))).Where(p => !p.Plan.IsDefault)
        .ToDictionary(p => (nint)p.Note, p => (p.Plan.Ratio, (uint)p.Plan.Policy));
    private static void RememberScope(Entry entry, WIVSMMidiPart part) => entry.Scopes[(nint)part] = part.NotesEnumerator.Select(Key).ToHashSet();
    internal static bool HasScope(WIVSMMidiPart part) => part.Sequence is { } source && Entries.TryGetValue((nint)source, out var entry) &&
        entry.Scopes.TryGetValue((nint)part, out var keys) && keys.Any(k => BreathVolumeService.IsNoteGenerationActive(k.Handle, k.Generation));
    internal static bool HasUnbound(WIVSMSequence source) => Entries.GetValueOrDefault((nint)source)?.Unbound.Count > 0;
    internal static bool HasState(WIVSMSequence source) => Entries.ContainsKey((nint)source) || Copies.ContainsKey((nint)source);
    internal static bool IsDirty(WIVSMSequence source) => Entries.TryGetValue((nint)source, out var entry) && entry.Document.IsDirty;
    internal static string? Error(WIVSMSequence source) => Entries.GetValueOrDefault((nint)source)?.Fault;
    internal static void RenderFailed(WIVSMSequence source, Exception error)
    {
        if (Entries.TryGetValue((nint)source, out var entry))
        {
            entry.Fault = error.GetType().Name + ": " + error.Message;
            entry.FaultRevision = SampledTimingHost.Find(source)?.Revision ?? -1;
        }
        Changed?.Invoke();
        ConsonantTimingOverlay.DurationChanged();
    }
    internal static void RenderSucceeded(WIVSMSequence source)
    {
        if (!Entries.TryGetValue((nint)source, out var entry) || entry.Fault == null ||
            SampledTimingHost.Find(source)?.Revision <= entry.FaultRevision) return;
        entry.Fault = null; Changed?.Invoke(); ConsonantTimingOverlay.DurationChanged();
    }
    internal static DurationSelectionArchive.Entry[] Snapshot(WIVSMSequence source)
    {
        if (Copies.TryGetValue((nint)source, out var copied)) return copied.ToArray();
        var result = Entries.TryGetValue((nint)source, out var stored) ? new List<DurationSelectionArchive.Entry>(stored.Unbound) : new();
        for (ulong t = 0; t < source.NumTrack; t++)
        foreach (var part in source.GetTrack(t) is WIVSMMidiTrack track ? track.PartsEnumerator.OfType<WIVSMMidiPart>() : [])
        {
            int first = result.Count;
            foreach (var note in part.NotesEnumerator)
            {
                if (string.IsNullOrWhiteSpace(note.Phonemes)) continue;
                var plan = Get(note);
                if (!plan.IsDefault) result.Add(new(SampledTimingVsmBinding.Address(note), plan.Ratio, plan.Policy, DurationSelectionPlan.AlgorithmVersion));
            }
            // The native timbre/layer histories need a render lifetime even
            // after the final override is undone. Persist that execution mode
            // independently; it carries no sample choice or predicted timing.
            if (result.Count == first && HasScope(part) && part.GetNote(0UL) is { } anchor)
                result.Add(new(SampledTimingVsmBinding.Address(anchor), 1, DurationCandidatePolicy.Native, DurationSelectionPlan.AlgorithmVersion, ScopeOnly: true));
        }
        return result.ToArray();
    }
    internal static void Load(WIVSMSequence source, DurationSelectionArchive.Entry[] values)
    {
        if (values.Length == 0) return;
        var resolved = new List<(WIVSMNote Note, DurationSelectionPlan Plan)>();
        var unbound = new List<DurationSelectionArchive.Entry>();
        foreach (var value in values)
        {
            var a = value.Address;
            var track = (ulong)a.Track < source.NumTrack ? source.GetTrack((ulong)a.Track) as WIVSMMidiTrack : null;
            var part = track != null && (ulong)a.Part < track.NumParts ? track.GetPart((ulong)a.Part) as WIVSMMidiPart : null;
            var note = part != null && (ulong)a.Note < part.NumNotes ? part.GetNote((ulong)a.Note) : null;
            if (note == null || note.RelPosTick.Value != a.Position || note.DurationTick.Value != a.Duration ||
                note.NoteNumber != a.Pitch || part!.VoiceBankID != a.Bank || note.Phonemes != a.Topology)
            { unbound.Add(value); continue; }
            resolved.Add((note, new(a.Topology, value.Ratio, value.Policy)));
        }
        var entry = For(source);
        entry.Unbound.AddRange(unbound);
        foreach (var (note, plan) in resolved)
        {
            var key = Key(note); entry.Notes[key] = note; entry.Document.Restore(key, plan);
            RememberScope(entry, note.Parent!);
        }
        entry.Document.MarkSaved();
        SampledTimingHost.ForDuration(source);
    }
    internal static void MarkSaved(WIVSMSequence source) { if (Entries.TryGetValue((nint)source, out var entry)) entry.Document.MarkSaved(); }
    internal static void RebindNativeHistory(WIVSMSequence source, IReadOnlyDictionary<DurationNoteKey, DurationNoteKey> keys)
    {
        if (!Entries.TryGetValue((nint)source, out var entry)) return;
        entry.Document.Rekey(keys);
        for (ulong t = 0; t < source.NumTrack; t++)
        foreach (var part in source.GetTrack(t) is WIVSMMidiTrack track ? track.PartsEnumerator.OfType<WIVSMMidiPart>() : [])
        foreach (var note in part.NotesEnumerator)
        {
            var key = Key(note);
            if (!keys.Values.Contains(key)) continue;
            entry.Notes[key] = note;
            if (keys.Keys.Any(old => entry.Scopes.Values.Any(scope => scope.Contains(old)))) RememberScope(entry, part);
        }
    }
    internal static void Copy(WIVSMSequence source, WIVSMSequence copy)
    {
        var values = Snapshot(source); if (values.Length != 0) Copies[(nint)copy] = values;
    }
    internal static void ValidateCopy(WIVSMSequence source)
    {
        if (!Copies.TryGetValue((nint)source, out var values)) return;
        foreach (var value in values)
        {
            var a = value.Address;
            var track = (ulong)a.Track < source.NumTrack ? source.GetTrack((ulong)a.Track) as WIVSMMidiTrack : null;
            var part = track != null && (ulong)a.Part < track.NumParts ? track.GetPart((ulong)a.Part) as WIVSMMidiPart : null;
            var note = part != null && (ulong)a.Note < part.NumNotes ? part.GetNote((ulong)a.Note) : null;
            if (note == null || SampledTimingVsmBinding.Address(note) != a)
                throw new NotSupportedException("The render copy changed a duration-controlled note's identity or phoneme topology.");
        }
    }
    internal static void Close(nint source)
    {
        Copies.Remove(source);
        if (Entries.Remove(source, out var entry)) foreach (var key in entry.Document.Snapshot().Keys) DetachedCopies.Remove(key);
        foreach (var key in DetachedCopies.Keys.Where(k => !BreathVolumeService.IsNoteGenerationActive(k.Handle, k.Generation)).ToArray()) DetachedCopies.Remove(key);
    }
}
