using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using VOCALOIDPatcher.BreathVolume;

namespace VOCALOIDPatcher.ConsonantTiming;

internal sealed class TransitionDocument
{
    private TransitionArchiveData _value = TransitionArchiveData.Empty;
    private string _saved;
    // Locations survive a control undo that temporarily removes its note IDs.
    private readonly Dictionary<Guid, TimingNoteAddress> _locations = new();
    internal TransitionArchiveData Snapshot => _value;
    internal bool IsDirty => _saved != _value.Fingerprint;
    internal event Action? Changed;
    internal TransitionDocument() => _saved = _value.Fingerprint;
    internal void MarkSaved() => _saved = _value.Fingerprint;
    internal void Load(TransitionArchiveData value)
    {
        _value = TransitionArchiveData.Parse(value.Payload.Span); _locations.Clear();
        if (_value.Data is { } data) foreach (var note in data.Notes) _locations.Add(note.Id, note.Address);
        MarkSaved(); Changed?.Invoke();
    }
    internal void Relocate(IReadOnlyDictionary<Guid, TimingNoteAddress> locations)
    {
        foreach (var address in locations.Values) address.Validate();
        TransitionArchiveData? relocated = null;
        if (_value.Data is { } data && data.Notes.Any(n => locations.TryGetValue(n.Id, out var a) && a != n.Address))
            relocated = TransitionArchiveData.From(data with { Notes = data.Notes.Select(n => locations.TryGetValue(n.Id, out var a) ? n with { Address = a } : n).ToImmutableArray() });
        foreach (var (id, address) in locations) _locations[id] = address;
        if (relocated != null) _value = relocated;
    }
    private void Apply(TransitionArchiveData archive)
    {
        var value = archive.Data ?? throw new NotSupportedException("Opaque transition data cannot be edited.");
        foreach (var note in value.Notes) _locations.TryAdd(note.Id, note.Address);
        _value = value.Notes.Any(n => n.Address != _locations[n.Id])
            ? TransitionArchiveData.From(value with { Notes = value.Notes.Select(n => n with { Address = _locations[n.Id] }).ToImmutableArray() })
            : archive;
    }
    internal void Edit(TransitionProjectData after, Action<ICustomParameterHistoryEdit> push)
    {
        if (!_value.Executable) throw new NotSupportedException("Transition schema is preserved but cannot be edited.");
        var next = TransitionArchiveData.From(after);
        if (next.Fingerprint == TransitionArchiveData.From(_value.Data!).Fingerprint) return;
        var edit = new EditCommand(this, _value, next);
        edit.ApplyAfter();
        try { push(edit); } catch { edit.ApplyBefore(); throw; }
        edit.AfterApply();
    }
    private sealed record EditCommand(TransitionDocument Document, TransitionArchiveData Before,
        TransitionArchiveData After) : ICustomParameterHistoryEdit
    {
        public void ApplyBefore() => Document.Apply(Before);
        public void ApplyAfter() => Document.Apply(After);
        public void AfterApply() => Document.Changed?.Invoke();
    }
}
