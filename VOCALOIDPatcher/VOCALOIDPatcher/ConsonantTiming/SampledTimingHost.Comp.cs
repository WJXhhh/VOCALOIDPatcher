using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using VOCALOIDPatcher.BreathVolume;
using Yamaha.VOCALOID.VSM;

namespace VOCALOIDPatcher.ConsonantTiming;

internal static partial class SampledTimingHost
{
    internal static void ThrowIfCompRenderFailed(WIVSMSequence source)
    {
        if (CopyFaults.TryGetValue((nint)source, out var copyError))
            throw new System.InvalidOperationException("Voice-comp source preparation failed: " + copyError.Message, copyError);
        if (Entries.TryGetValue((nint)source, out var entry) && entry.SuspendedRevision == entry.Session.Revision && entry.Fault is { } error)
            throw new System.InvalidOperationException("Voice-comp source preparation failed: " + error.Message, error);
    }

    // Explicitly authorized internal source: normal auto-trigger hooks still
    // ignore it. Rebind by source identity, never by NOTE ordinal in a callback.
    internal static void BindCompSource(WIVSMSequence original, WIVSMSequence copy,
        IReadOnlyList<(WIVSMNote Original, WIVSMNote Copy)> notes, WIVSMNote? independentTimingOwner)
    {
        if (!Entries.TryGetValue((nint)original, out var owner)) return;
        var timing = new Dictionary<TimingNoteAddress, SampledTimingPlan>();
        var addresses = new Dictionary<TimingNoteAddress, TimingNoteAddress>();
        foreach (var pair in notes)
        {
            var before = SampledTimingVsmBinding.Address(pair.Original);
            var after = SampledTimingVsmBinding.Address(pair.Copy);
            bool same = before.Topology == after.Topology;
            if (same) addresses.TryAdd(before, after);
            if (owner.Session.Document.Get(before) is { } plan)
            {
                // Only the regenerated owner's natural duration is independent
                // of its output boundary edits. Other notes in this connected
                // source must retain their own timing, including shared edges.
                bool independent = independentTimingOwner != null && (nint)pair.Original == (nint)independentTimingOwner;
                var values = independent || !same ? plan.Adjustments.Where(a => a.Kind == TimingAdjustment.NoteCoefficient) : plan.Adjustments;
                if (values.Any()) timing[after] = new(after.Topology, values);
            }
        }
        var transitions = SaveTransitions(owner);
        if (!transitions.Executable && transitions.HasData)
            throw new System.NotSupportedException("Unknown transition controls cannot be executed in a voice-comp source.");
        var filtered = transitions.Data?.KeepNotes(n => addresses.ContainsKey(n.Address));
        if (filtered != null) filtered = filtered with
        { Notes = filtered.Notes.Select(n => n with { Address = addresses[n.Address] }).ToImmutableArray() };
        if (timing.Count == 0 && filtered?.HasControls != true) return;
        var entry = AttachEntry(copy); entry.CompSource = true; entry.Loading = true;
        try
        {
            entry.Session.Document.Load(timing);
            if (filtered?.HasControls == true) entry.Session.Transitions.Load(TransitionArchiveData.From(filtered));
        }
        finally { entry.Loading = false; }
        BindCurrentPlans(entry);
    }
}
