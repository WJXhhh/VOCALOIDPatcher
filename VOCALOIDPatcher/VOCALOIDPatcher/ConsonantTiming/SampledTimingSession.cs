using System;
using System.Collections.Generic;
using System.Linq;
using VOCALOIDPatcher.BreathVolume;
using Yamaha.VOCALOID.VSM;

namespace VOCALOIDPatcher.ConsonantTiming;

// Explicit opt-in host adapter. No static instance, patches, drag handlers or
// render hook are installed by constructing/compiling this type.
internal sealed class SampledTimingSession : IDisposable
{
    private readonly WIVSMSequence _source;
    private readonly TimingBackendLease<LocalConsonantTimingBackend> _backend;
    internal bool HasBackend => _backend.IsCreated;
    internal Exception? BackendFault => _backend.Fault;
    private bool _disposed;
    private readonly int _ownerThread = Environment.CurrentManagedThreadId;
    private readonly TimingRenderRevision _renderRevision = new();
    internal SampledTimingDocument Document { get; } = new();
    internal TransitionDocument Transitions { get; } = new();
    internal long Revision => _renderRevision.Revision;
    internal event Action? Changed;
    internal Func<nint, bool>? AllowsNote { get; set; }
    private int _notificationDepth;
    private bool _notificationPending;

    internal SampledTimingSession(WIVSMSequence source, LocalConsonantTimingBackend backend, bool ownsBackend = false)
        : this(source, backend.CreateReplacement, ownsBackend, backend) { }

    internal SampledTimingSession(WIVSMSequence source, Func<LocalConsonantTimingBackend> backendFactory, bool ownsBackend = true, LocalConsonantTimingBackend? initialBackend = null)
    {
        ArgumentNullException.ThrowIfNull(source); ArgumentNullException.ThrowIfNull(backendFactory);
        _source = source; _backend = new(backendFactory, ownsBackend, initialBackend);
        Document.Changed += OnChanged;
        Transitions.Changed += OnChanged;
    }

    internal void SetCoefficient(WIVSMNote note, float? coefficient)
    {
        EnsureOpen();
        if ((nint)note.Sequence! != (nint)_source || _source.IsStaged)
            throw new InvalidOperationException("Timing edit requires a stable source note.");
        if (note.Parent is not { IsAi: false } part || !string.IsNullOrEmpty(part.SecondaryVoiceBankID) ||
            _source.GetSamplingRate() != VSMSamplingRate._44100)
            throw new NotSupportedException("Only single-bank sampled timing at 44100 Hz is supported.");
        LocalTimingNative.VerifyImages();
        var address = SampledTimingVsmBinding.Address(note);
        var values = Document.Get(address)?.Adjustments.Where(a => a.Kind != TimingAdjustment.NoteCoefficient).ToList() ?? [];
        if (coefficient.HasValue) values.Add(new(TimingAdjustment.NoteCoefficient, -1, coefficient.Value));
        Document.Edit(address, values.Count == 0 ? null : new SampledTimingPlan(address.Topology, values),
            edit => CustomParameterHistoryCoordinator.Push(_source, edit),replaceDetachedLocator:true);
    }

    internal LocalConsonantTimingBackend.PreparedCoefficientTiming Prepare(WIVSMNote target,
        IReadOnlyDictionary<TimingNoteAddress, SampledTimingPlan?>? candidates = null)
    {
        EnsureOpen();
        if ((nint)target.Sequence! != (nint)_source) throw new ArgumentException("Timing target belongs to another source.");
        var values = SampledTimingVsmBinding.ResolveActivePlans(_source, Document)
            .Where(p => AllowsNote?.Invoke(p.Key) != false).ToDictionary(p => p.Key, p => p.Value);
        if (candidates != null)
            foreach (var note in target.Parent!.NotesEnumerator)
                if (candidates.TryGetValue(SampledTimingVsmBinding.Address(note), out var plan))
                {
                    if (plan == null) values.Remove((nint)note);
                    else values[(nint)note] = plan;
                }
        return _backend.Require().PrepareCoefficient(target, Revision, note => values.GetValueOrDefault((nint)note)?.PhonemeCoefficient(note.Phonemes),
            note => values.GetValueOrDefault((nint)note));
    }

    internal void SetPlan(WIVSMNote note, SampledTimingPlan? plan,
        IReadOnlyDictionary<TimingNoteAddress, SampledTimingPlan?>? related = null, TransitionProjectData? transitions = null)
    {
        EnsureOpen();
        if ((nint)note.Sequence! != (nint)_source || _source.IsStaged)
            throw new InvalidOperationException("Timing edit requires a stable source.");
        if (plan != null) _ = plan.PhonemeCoefficient(note.Phonemes);
        var address = SampledTimingVsmBinding.Address(note);
        if (related != null && (!related.ContainsKey(address) || related.Keys.Any(a => a.Track != address.Track || a.Part != address.Part)))
            throw new ArgumentException("Shared timing edit belongs to another Part.");
        if (transitions != null)
        {
            TimingCompositeEdit.Execute(push =>
            {
                if (related != null) Document.EditMany(related, push, replaceDetachedLocator: true);
                else Document.Edit(address, plan, push, replaceDetachedLocator: true);
                Transitions.Edit(transitions, push);
            }, edit => CustomParameterHistoryCoordinator.Push(_source, edit), BatchNotifications);
            return;
        }
        if (related != null)
        {
            Document.EditMany(related, edit => CustomParameterHistoryCoordinator.Push(_source, edit), replaceDetachedLocator: true);
            return;
        }
        Document.Edit(address, plan, edit => CustomParameterHistoryCoordinator.Push(_source, edit),replaceDetachedLocator:true);
    }

    internal void SetTransitions(TransitionProjectData data)
    {
        EnsureOpen();
        if (_source.IsStaged) throw new InvalidOperationException("Transition edit requires a committed source.");
        Transitions.Edit(data, edit => CustomParameterHistoryCoordinator.Push(_source, edit));
    }

    // Called by the source owner for native commit/undo/redo/structure changes.
    internal void SourceChanged()
    {
        EnsureOpen(); _renderRevision.Advance(); _backend.IfCreated(b => b.ClearCache()); Changed?.Invoke();
    }
    internal void SelectionChanged()
    {
        EnsureOpen(); _renderRevision.Advance(); Changed?.Invoke();
    }

    internal void LoadProjectData(string path)
    {
        EnsureOpen(); Document.Load(SampledTimingProjectArchive.Read(path)); Transitions.Load(TransitionProjectArchive.Read(path));
    }

    // Call only after native save has succeeded. Failure leaves timing dirty.
    internal void WriteProjectData(string path)
    {
        EnsureOpen();
        SampledTimingProjectArchive.Write(path, Document.Snapshot()); TransitionProjectArchive.Write(path, Transitions.Snapshot);
        Document.MarkSaved(); Transitions.MarkSaved();
    }

    private void OnChanged()
    {
        if (_notificationDepth != 0) { _notificationPending = true; return; }
        _renderRevision.Advance(); _backend.IfCreated(b => b.ClearCache());
        Changed?.Invoke();
    }

    private void BatchNotifications(Action action)
    {
        _notificationDepth++;
        try { action(); }
        finally
        {
            if (--_notificationDepth == 0 && _notificationPending)
            { _notificationPending = false; OnChanged(); }
        }
    }

    private void EnsureOpen()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (Environment.CurrentManagedThreadId != _ownerThread) throw new InvalidOperationException("Timing document access requires its owner thread.");
        if (!_source.IsOpen) throw new InvalidOperationException("Timing source has closed.");
    }

    internal TimingRenderStamp CaptureRenderStamp()
    {
        EnsureOpen(); return _renderRevision.Capture(Document.Fingerprint + ":" + Transitions.Snapshot.Fingerprint);
    }

    internal void ResetRenderBackend() { EnsureOpen(); _backend.Reset(); _renderRevision.Advance(); }

    // Marshal completion back to the source owner, then recheck immediately
    // before publication. A background callback cannot publish directly.
    internal bool CanPublish(TimingRenderStamp stamp) => Environment.CurrentManagedThreadId == _ownerThread && !_disposed && _source.IsOpen &&
        _renderRevision.IsCurrent(stamp, Document.Fingerprint + ":" + Transitions.Snapshot.Fingerprint);

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true; _renderRevision.Close(); Document.Changed -= OnChanged; Transitions.Changed -= OnChanged;
        _backend.IfCreated(b => b.ClearCache()); _backend.Dispose();
    }
}
