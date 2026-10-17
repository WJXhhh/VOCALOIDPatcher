using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using VOCALOIDPatcher.BreathVolume;

namespace VOCALOIDPatcher.ConsonantTiming;

// Owner-thread document model. No native handles or serialized predicted scores.
// Submit HistoryEdit through CustomParameterHistoryCoordinator in a host adapter.
internal sealed class SampledTimingDocument
{
    private readonly Dictionary<TimingNoteAddress, SampledTimingPlan> _plans = new();
    private readonly Dictionary<TimingNoteAddress, TimingNoteAddress> _historyAddresses = new();
    private string _saved = "";
    internal long Revision { get; private set; }
    internal event Action? Changed;
    internal bool IsDirty => Fingerprint != _saved;
    internal string Fingerprint => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join("\n",
        _plans.OrderBy(p => p.Key.Track).ThenBy(p => p.Key.Part).ThenBy(p => p.Key.Note)
            .Select(p => System.Text.Json.JsonSerializer.Serialize(p.Key) + ":" + p.Value.Fingerprint)))));
    internal IReadOnlyDictionary<TimingNoteAddress, SampledTimingPlan> Snapshot() => new Dictionary<TimingNoteAddress, SampledTimingPlan>(_plans);

    internal SampledTimingDocument() => MarkSaved();
    internal void MarkSaved() => _saved = Fingerprint;
    internal SampledTimingPlan? Get(TimingNoteAddress address) => _plans.GetValueOrDefault(address);

    // Only the host's live native-note generation may authorize a relocation.
    // Historical edits keep their semantic note when native position/pitch or
    // the surrounding note indices change; no second history item is created.
    internal void Relocate(IReadOnlyDictionary<TimingNoteAddress, TimingNoteAddress> changes)
    {
        if (changes.Count == 0) return;
        var replacement = _plans.Select(p => (Address: changes.GetValueOrDefault(p.Key) ?? p.Key, Plan: p.Value)).ToArray();
        foreach (var (address, plan) in replacement)
        {
            address.Validate();
            if (address.Topology != plan.Topology) throw new ArgumentException("Relocated timing topology changed.");
        }
        if (replacement.Select(p => p.Address).Distinct().Count() != replacement.Length)
            throw new InvalidOperationException("Timing identities collided during native edit.");
        foreach (var pair in _historyAddresses.ToArray())
            _historyAddresses[pair.Key] = changes.GetValueOrDefault(pair.Value) ?? pair.Value;
        foreach (var pair in changes) _historyAddresses[pair.Key] = pair.Value;
        _plans.Clear(); foreach (var (address, plan) in replacement) _plans.Add(address, plan);
        Revision++;
    }

    internal ICustomParameterHistoryEdit? Edit(TimingNoteAddress address, SampledTimingPlan? plan,
        Action<ICustomParameterHistoryEdit> pushHistory, bool replaceDetachedLocator = false)
    {
        address.Validate();
        var detached = _plans.Keys.SingleOrDefault(a => a != address && (a.Track, a.Part, a.Note) == (address.Track, address.Part, address.Note));
        if (detached != null && !replaceDetachedLocator)
            throw new ArgumentException("Timing locator already has a different note identity.");
        if (plan != null && plan.Topology != address.Topology) throw new ArgumentException("Timing topology mismatch.");
        if (detached != null)
        {
            if (plan == null) return null;
            // Explicitly editing this live note replaces a detached locator,
            // with both identities retained in the same undo command.
            var replacement = new ReplacementEdit(this, detached, _plans[detached], address, plan);
            replacement.ApplyAfter();
            try { pushHistory(replacement); }
            catch { replacement.ApplyBefore(); throw; }
            replacement.AfterApply();
            return replacement;
        }
        var before = Get(address);
        if (before?.Fingerprint == plan?.Fingerprint) return null;
        var edit = new HistoryEdit(this, address, before, plan);
        Set(address, plan);
        try { pushHistory(edit); }
        catch { Set(address, before); throw; }
        edit.AfterApply();
        return edit;
    }

    internal ICustomParameterHistoryEdit? EditMany(IReadOnlyDictionary<TimingNoteAddress, SampledTimingPlan?> plans,
        Action<ICustomParameterHistoryEdit> pushHistory, bool replaceDetachedLocator = false)
    {
        if (plans.Keys.GroupBy(a => (a.Track, a.Part, a.Note)).Any(g => g.Count() > 1))
            throw new ArgumentException("Duplicate timing locator.");
        var before = new Dictionary<TimingNoteAddress, SampledTimingPlan?>();
        var after = new Dictionary<TimingNoteAddress, SampledTimingPlan?>();
        foreach (var (address, plan) in plans)
        {
            address.Validate();
            if (plan != null && plan.Topology != address.Topology) throw new ArgumentException("Timing topology mismatch.");
            var detached = _plans.Keys.SingleOrDefault(a => a != address &&
                (a.Track, a.Part, a.Note) == (address.Track, address.Part, address.Note));
            if (detached != null)
            {
                if (!replaceDetachedLocator) throw new ArgumentException("Timing locator already has a different note identity.");
                if (plan == null) continue;
                before[detached] = _plans[detached]; after[detached] = null;
            }
            if (Get(address)?.Fingerprint == plan?.Fingerprint) continue;
            before[address] = Get(address); after[address] = plan;
        }
        if (after.Count == 0) return null;
        var edit = new BatchEdit(this, before, after);
        edit.ApplyAfter();
        try { pushHistory(edit); }
        catch { edit.ApplyBefore(); throw; }
        edit.AfterApply();
        return edit;
    }

    internal void Load(IReadOnlyDictionary<TimingNoteAddress, SampledTimingPlan> plans)
    {
        // Validate everything before changing document state.
        if (plans.Keys.GroupBy(a => (a.Track, a.Part, a.Note)).Any(g => g.Count() > 1))
            throw new ArgumentException("Duplicate timing locator.");
        foreach (var pair in plans)
        {
            pair.Key.Validate();
            if (pair.Key.Topology != pair.Value.Topology) throw new ArgumentException("Timing topology mismatch.");
        }
        _plans.Clear();
        _historyAddresses.Clear();
        foreach (var pair in plans) _plans.Add(pair.Key, pair.Value);
        Revision++; MarkSaved(); Changed?.Invoke();
    }

    private void Set(TimingNoteAddress address, SampledTimingPlan? value)
    {
        address = _historyAddresses.GetValueOrDefault(address) ?? address;
        if (value == null) _plans.Remove(address); else _plans[address] = value;
        Revision++; // Monotone even on undo/branch: old prepared requests stay invalid.
    }

    private sealed record HistoryEdit(SampledTimingDocument Document, TimingNoteAddress Address,
        SampledTimingPlan? Before, SampledTimingPlan? After) : ICustomParameterHistoryEdit
    {
        public void ApplyBefore() => Document.Set(Address, Before);
        public void ApplyAfter() => Document.Set(Address, After);
        public void AfterApply() => Document.Changed?.Invoke();
    }

    private sealed record ReplacementEdit(SampledTimingDocument Document, TimingNoteAddress OldAddress,
        SampledTimingPlan OldPlan, TimingNoteAddress NewAddress, SampledTimingPlan NewPlan) : ICustomParameterHistoryEdit
    {
        public void ApplyBefore() { Document.Set(NewAddress,null); Document.Set(OldAddress,OldPlan); }
        public void ApplyAfter() { Document.Set(OldAddress,null); Document.Set(NewAddress,NewPlan); }
        public void AfterApply() => Document.Changed?.Invoke();
    }

    private sealed record BatchEdit(SampledTimingDocument Document,
        IReadOnlyDictionary<TimingNoteAddress, SampledTimingPlan?> Before,
        IReadOnlyDictionary<TimingNoteAddress, SampledTimingPlan?> After) : ICustomParameterHistoryEdit
    {
        public void ApplyBefore() { foreach (var (address, plan) in Before) Document.Set(address, plan); }
        public void ApplyAfter() { foreach (var (address, plan) in After) Document.Set(address, plan); }
        public void AfterApply() => Document.Changed?.Invoke();
    }
}
