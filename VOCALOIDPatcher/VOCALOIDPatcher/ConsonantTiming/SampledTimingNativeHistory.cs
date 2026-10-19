using System;
using System.Collections.Generic;
using System.Linq;
using VOCALOIDPatcher.BreathVolume;
using VOCALOIDPatcher.DurationSelection;
using Yamaha.VOCALOID.VSM;

namespace VOCALOIDPatcher.ConsonantTiming;

internal static partial class SampledTimingHost
{
    private sealed record NativeNode(DurationNoteKey Key, TimingNoteAddress Address);
    private static NativeNode[] CaptureNodes(WIVSMSequence source, NativeNode[] previous)
    {
        var reuse = previous.ToDictionary(n => n.Key);
        var result = new List<NativeNode>();
        for (ulong t = 0; t < source.NumTrack; t++)
        if (source.GetTrack(t) is WIVSMMidiTrack track)
        for (ulong p = 0; p < track.NumParts; p++)
        if (track.GetPart(p) is WIVSMMidiPart part)
        for (ulong n = 0; n < part.NumNotes; n++)
        if (part.GetNote(n) is { } note && !string.IsNullOrWhiteSpace(note.Phonemes))
        {
            var key = Key(note);
            var address = new TimingNoteAddress((int)t, (int)p, (int)n, note.RelPosTick.Value, (int)note.DurationTick.Value, note.NoteNumber, part.VoiceBankID, note.Phonemes);
            result.Add(reuse.TryGetValue(key, out var old) && old.Address == address ? old : new(key, address));
        }
        return result.ToArray();
    }
    private static IReadOnlyDictionary<TimingNoteAddress, SampledTimingPlan> SavePlans(Entry entry)
    {
        var live = CaptureNodes(entry.Source, entry.LastNodes).ToDictionary(n => n.Address, n => n.Key);
        // Unbound loaded controls are opaque project data and survive saves.
        // Deleted controls that were bound remain in memory for native Undo. They cannot be
        // serialized as parameters of whichever note now occupies that index.
        return entry.Session.Document.Snapshot().Where(p => !entry.Bindings.TryGetValue(p.Key, out var key) ||
            (live.TryGetValue(p.Key, out var current) && current == key)).ToDictionary(p => p.Key, p => p.Value);
    }
    internal static INativeParameterHistoryEdit? CaptureNativeHistory(WIVSMSequence source)
    {
        if (!Entries.TryGetValue((nint)source, out var entry) || entry.Rebuilding || entry.BeforeNativeCommit == null) return null;
        var before = entry.BeforeNativeCommit; entry.BeforeNativeCommit = null;
        var after = CaptureNodes(source, entry.LastNodes); entry.LastNodes = after;
        return new NativeHistory(entry, before, after);
    }
    private sealed record NativeHistory(Entry Entry, NativeNode[] Before, NativeNode[] After) : INativeParameterHistoryEdit
    {
        public void AfterNativeHistory(bool undo)
        {
            if (!Entry.Source.IsOpen) return;
            var expected = undo ? Before : After;
            var actual = CaptureNodes(Entry.Source, Entry.LastNodes);
            var lookup = actual.ToDictionary(n => n.Address);
            if (actual.Length != expected.Length || expected.Any(n => !lookup.ContainsKey(n.Address)))
                throw new InvalidOperationException("Native history restored a different note graph.");
            var keys = expected.Select(n => (Old: Entry.NativeAliases.GetValueOrDefault(n.Key, n.Key), New: lookup[n.Address].Key))
                .Where(p => p.Old != p.New).ToDictionary(p => p.Old, p => p.New);
            foreach (var pair in Entry.NativeAliases.ToArray()) Entry.NativeAliases[pair.Key] = keys.GetValueOrDefault(pair.Value, pair.Value);
            foreach (var old in expected) Entry.NativeAliases[old.Key] = lookup[old.Address].Key;
            foreach (var pair in keys) Entry.NativeAliases[pair.Key] = pair.Value;
            foreach (var binding in Entry.Bindings.ToArray()) Entry.Bindings[binding.Key] = keys.GetValueOrDefault(binding.Value, binding.Value);
            foreach (var binding in Entry.TransitionBindings.ToArray()) Entry.TransitionBindings[binding.Key] = keys.GetValueOrDefault(binding.Value, binding.Value);
            DurationSelectionHost.RebindNativeHistory(Entry.Source, keys);
            Entry.LastNodes = actual;
        }
    }
}
