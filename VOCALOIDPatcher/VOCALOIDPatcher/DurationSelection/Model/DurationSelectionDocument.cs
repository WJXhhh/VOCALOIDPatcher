using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using VOCALOIDPatcher.BreathVolume;

namespace VOCALOIDPatcher.DurationSelection;

// Runtime identities never leave this owner-thread document. Archive adapters
// resolve its live keys to current source locators at the save boundary.
internal sealed class DurationSelectionDocument
{
    private readonly Dictionary<DurationNoteKey, DurationSelectionPlan> _values = new();
    private readonly Dictionary<DurationNoteKey, DurationNoteKey> _historyKeys = new();
    private readonly Dictionary<DurationNoteKey, DurationNoteKey> _origins = new();
    private string _saved = "";
    internal event Action<DurationNoteKey>? Changed;
    internal long Revision { get; private set; }
    internal DurationSelectionDocument() => MarkSaved();
    internal DurationSelectionPlan? Get(DurationNoteKey key) => _values.GetValueOrDefault(key);
    internal IReadOnlyDictionary<DurationNoteKey, DurationSelectionPlan> Snapshot() => new Dictionary<DurationNoteKey, DurationSelectionPlan>(_values);
    internal string Fingerprint => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join("\n", _values
        .Select(p => (Key: _origins.GetValueOrDefault(p.Key, p.Key), p.Value))
        .OrderBy(p => p.Key.Generation).ThenBy(p => p.Key.Handle)
        .Select(p => p.Key.Handle.ToInt64() + ":" + p.Key.Generation + ":" + p.Value.Fingerprint)))));
    internal bool IsDirty => Fingerprint != _saved;
    internal void MarkSaved() => _saved = Fingerprint;
    internal void Restore(DurationNoteKey key, DurationSelectionPlan? plan)
    {
        key = _historyKeys.GetValueOrDefault(key, key);
        _origins.TryAdd(key, key);
        if (plan is { IsDefault: false }) _values[key] = plan; else _values.Remove(key);
        Revision++;
    }
    internal void Rekey(IReadOnlyDictionary<DurationNoteKey, DurationNoteKey> keys)
    {
        if (keys.Count == 0) return;
        var values = _values.Select(p => (Key: keys.GetValueOrDefault(p.Key, p.Key), p.Value)).ToArray();
        if (values.Select(p => p.Key).Distinct().Count() != values.Length) throw new InvalidOperationException("Duration native identities collided.");
        foreach (var pair in _historyKeys.ToArray()) _historyKeys[pair.Key] = keys.GetValueOrDefault(pair.Value, pair.Value);
        foreach (var pair in keys) _historyKeys[pair.Key] = pair.Value;
        foreach (var pair in keys) _origins[pair.Value] = _origins.GetValueOrDefault(pair.Key, pair.Key);
        _values.Clear(); foreach (var pair in values) _values.Add(pair.Key, pair.Value);
        Revision++;
    }
    internal void Edit(DurationNoteKey key, DurationSelectionPlan? value, Action<ICustomParameterHistoryEdit> push)
    {
        if (value?.IsDefault == true) value = null;
        var before = Get(key);
        if (before?.Fingerprint == value?.Fingerprint) return;
        var edit = new EditCommand(this, key, before, value);
        edit.ApplyAfter();
        try { push(edit); }
        catch { edit.ApplyBefore(); throw; }
        edit.AfterApply();
    }
    private sealed record EditCommand(DurationSelectionDocument Document, DurationNoteKey Key,
        DurationSelectionPlan? Before, DurationSelectionPlan? After) : ICustomParameterHistoryEdit
    {
        public void ApplyBefore() => Document.Restore(Key, Before);
        public void ApplyAfter() => Document.Restore(Key, After);
        public void AfterApply() => Document.Changed?.Invoke(Document._historyKeys.GetValueOrDefault(Key, Key));
    }
}
