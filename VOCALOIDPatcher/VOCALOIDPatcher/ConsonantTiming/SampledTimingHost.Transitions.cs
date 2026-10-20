using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.IO;
using Yamaha.VOCALOID.VSM;

namespace VOCALOIDPatcher.ConsonantTiming;

internal static partial class SampledTimingHost
{
    private static IEnumerable<TimingNoteAddress> ChangedTransitionAddresses(TransitionProjectData before, TransitionProjectData after)
    {
        foreach (var owner in before.Notes.Concat(after.Notes).Select(n => (n.Address.Track, n.Address.Part)).Distinct())
        {
            var a = before.KeepNotes(n => (n.Address.Track, n.Address.Part) == owner);
            var b = after.KeepNotes(n => (n.Address.Track, n.Address.Part) == owner);
            if (TransitionArchiveData.From(a).Fingerprint != TransitionArchiveData.From(b).Fingerprint)
                foreach (var address in a.Notes.Concat(b.Notes).Select(n => n.Address).Distinct()) yield return address;
        }
    }
    private static int MapTransitionFrame(ImmutableArray<TransitionAnchor> points, int frame)
    {
        int i = 0; while (i < points.Length && points[i].From <= frame) i++;
        if (i == 0 || i == points.Length) return frame;
        var a = points[i - 1]; var b = points[i];
        return checked(a.To + (int)Math.Round((frame - (double)a.From) * (b.To - (double)a.To) / (b.From - (double)a.From), MidpointRounding.AwayFromZero));
    }
    internal static TransitionExecutionPlan? TransitionView(WIVSMMidiPart part)
        => part.Sequence is { } source && Entries.TryGetValue((nint)source, out var entry) &&
            entry.TransitionViews.TryGetValue((nint)part, out var view) && view.Revision == entry.Session.Revision ? view.Plan : null;
    private static void BindTransitions(Entry entry)
    {
        if (!entry.Session.Transitions.Snapshot.Executable || entry.Session.Transitions.Snapshot.Data is not { } data) return;
        var live = CaptureNodes(entry.Source, entry.LastNodes).ToDictionary(n => n.Address, n => n.Key);
        foreach (var note in data.Notes)
            if (!entry.TransitionBindings.ContainsKey(note.Id) && live.TryGetValue(note.Address, out var key))
                entry.TransitionBindings.Add(note.Id, key);
    }
    private static void RelocateTransitions(Entry entry)
    {
        if (!entry.Session.Transitions.Snapshot.Executable || entry.Session.Transitions.Snapshot.Data is not { } data) return;
        var live = CaptureNodes(entry.Source, entry.LastNodes).ToDictionary(n => n.Key, n => n.Address);
        var prior = data.Notes.ToDictionary(n => n.Id, n => n.Address);
        var moved = entry.TransitionBindings.Where(p => live.TryGetValue(p.Value, out var address) &&
            (!prior.TryGetValue(p.Key, out var old) || (old.Topology == address.Topology && old.Bank == address.Bank))).ToDictionary(p => p.Key, p => live[p.Value]);
        entry.Session.Transitions.Relocate(moved);
    }
    private static void ValidateTransitionBindings(Entry entry)
    {
        var archive = entry.Session.Transitions.Snapshot;
        if (!archive.Executable) throw new NotSupportedException("Transition project schema/fields are preserved but not executable.");
        if (archive.Data!.Notes.Any(n => !entry.TransitionBindings.ContainsKey(n.Id)))
            throw new NotSupportedException("Stored transition notes are unbound; data is preserved.");
    }
    private static TransitionProjectData ActiveTransitions(Entry entry)
    {
        ValidateTransitionBindings(entry);
        var live = CaptureNodes(entry.Source, entry.LastNodes).ToDictionary(n => n.Key, n => n.Address);
        return entry.Session.Transitions.Snapshot.Data!.KeepNotes(note =>
        {
            if (!live.TryGetValue(entry.TransitionBindings[note.Id], out var actual)) return false;
            // A native pronunciation/bank edit replaces the controlled target,
            // just as deletion does. Keep its data for native Undo, but never
            // publish old phoneme controls against the replacement topology.
            return actual == note.Address;
        });
    }
    private static TransitionArchiveData SaveTransitions(Entry entry)
    {
        var archive = entry.Session.Transitions.Snapshot;
        if (!archive.Executable || archive.Data is not { } data) return archive;
        var live = CaptureNodes(entry.Source, entry.LastNodes).ToDictionary(n => n.Key, n => n.Address);
        var filtered = data.KeepNotes(n => !entry.TransitionBindings.TryGetValue(n.Id, out var key) ||
            (live.TryGetValue(key, out var address) && address == n.Address));
        // Do not canonicalize an unchanged payload: retain future fields and
        // exact opaque bytes on save even without a working backend.
        return data.Notes.Length == filtered.Notes.Length ? archive : TransitionArchiveData.From(filtered);
    }

    internal sealed class TransitionPreparation : IDisposable
    {
        internal readonly SampledTimingSession Session;
        internal readonly WIVSMMidiPart Part;
        internal readonly long Revision;
        internal readonly nint Source;
        internal readonly SampledTimingPlan SourcePlan;
        internal readonly TransitionProjectData Document;
        internal readonly LocalConsonantTimingBackend.PreparedCoefficientTiming Prepared;
        internal TransitionPreparation(SampledTimingSession session, WIVSMMidiPart part, SampledTimingPlan sourcePlan,
            TransitionProjectData document, LocalConsonantTimingBackend.PreparedCoefficientTiming prepared)
        { Session = session; Part = part; Source = (nint)part.Sequence!; SourcePlan = sourcePlan; Document = document; Prepared = prepared; Revision = session.Revision; }
        internal (TransitionLayout Layout, TransitionControlPlan Controls, TransitionExecutionPlan Execution) Query(System.Threading.CancellationToken cancellation = default)
        {
            var layout = Prepared.QueryTransitions(SourcePlan, Revision, cancellation, 0);
            var controls = TransitionProjectBinding.ResolveAvailable(Document, layout);
            return (layout, controls, Prepared.QueryTransitionControls(layout, controls, Revision, cancellation, 0));
        }
        public void Dispose() => Prepared.Dispose();
    }
    internal static TransitionPreparation PrepareTransitions(WIVSMMidiPart part)
    {
        var source = part.Sequence ?? throw new InvalidOperationException("Missing transition source.");
        if (source.IsStaged || part.IsAi || part.NumNotes is < 1 or > 33) throw new NotSupportedException("Transition planning requires one complete bounded sampled Part.");
        NativeTransitionGain.Initialize(Path.Combine(Patcher.DataDir, "native", "v6patch_clock.dll"));
        var entry = Get(source); var document = ActiveTransitions(entry);
        var target = part.GetNote(part.NumNotes / 2)!;
        var address = SampledTimingVsmBinding.Address(target);
        // Match the generation filter used by Session.Prepare and Publish.
        // A vacant locator can still carry an old note's undo-only controls.
        var plan = entry.Session.AllowsNote?.Invoke((nint)target) != false
            ? entry.Session.Document.Get(address) ?? new(target.Phonemes, []) : new(target.Phonemes, []);
        return new(entry.Session, part, plan, document, entry.Session.Prepare(target));
    }
    internal static void SetTransitions(WIVSMMidiPart part, TransitionLayout layout, TransitionControlPlan controls, long revision)
    {
        var source = part.Sequence ?? throw new InvalidOperationException("Missing transition source.");
        var entry = Get(source);
        if (entry.Session.Revision != revision || source.IsStaged || layout.Revision != revision)
            throw new InvalidOperationException("Transition candidate is stale.");
        var first = layout.Notes[0].Address;
        if (PartFor(source, first) is not { } actual || (nint)actual != (nint)part)
            throw new InvalidOperationException("Transition candidate belongs to a different Part.");
        // Reprepare from the live owner immediately before the document edit.
        // A background candidate cannot authorize a stale source publication.
        using (var check = PrepareTransitions(part))
        {
            var now = check.Query();
            if (now.Layout.Fingerprint != layout.Fingerprint) throw new InvalidOperationException("Transition input changed.");
            var execution = check.Prepared.QueryTransitionControls(now.Layout, controls, revision, nativeWaitBudgetMs: 0);
            CommitTransitions(check, execution);
        }
    }
    internal static void CommitTransitions(TransitionPreparation preparation, TransitionExecutionPlan execution)
    {
        var source = preparation.Part.Sequence ?? throw new InvalidOperationException("Transition source closed.");
        var entry = Get(source);
        if (entry.Session != preparation.Session || entry.Session.Revision != preparation.Revision)
            throw new InvalidOperationException("Transition candidate source changed.");
        preparation.Prepared.ValidateTransitionCommit(preparation.Part, execution, preparation.Revision);
        var controls = execution.RequestedControls ?? TransitionControlPlan.FromCompiled(execution.Layout, execution.Controls);
        var data = entry.Session.Transitions.Snapshot.Data!;
        var live = CaptureNodes(source, entry.LastNodes).ToDictionary(n => n.Address, n => n.Key);
        var ids = execution.Layout.Notes.ToDictionary(n => n.Address, n => data.Notes.FirstOrDefault(old => old.Address == n.Address &&
            entry.TransitionBindings.TryGetValue(old.Id, out var key) && live.TryGetValue(n.Address, out var actual) && key == actual)?.Id ?? Guid.NewGuid());
        var next = TransitionProjectBinding.Encode(data, execution.Layout, controls, ids);
        if (TransitionArchiveData.From(next).Fingerprint == TransitionArchiveData.From(data).Fingerprint) return;
        source.StopAsyncRendering(); entry.Session.SetTransitions(next);
        Smoothing.TransitionSmoothingHost.RequestPart(preparation.Part);
    }
    internal static TransitionArchiveData TransitionData(WIVSMSequence source)
        => Find(source)?.Transitions.Snapshot ?? TransitionArchiveData.Empty;
}
