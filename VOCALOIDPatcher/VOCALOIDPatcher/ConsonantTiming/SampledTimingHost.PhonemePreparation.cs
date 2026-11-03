using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Yamaha.VOCALOID.VSM;

namespace VOCALOIDPatcher.ConsonantTiming;

internal static partial class SampledTimingHost
{
    // Capture/commit on Dispatcher; Query on an immutable backend preparation.
    // This is also the UI's preflight, including preservation of whole-Part ART.
    internal sealed class PhonemePreparation : IDisposable
    {
        internal readonly WIVSMNote Note;
        internal readonly SampledTimingSession Session;
        internal readonly long Revision;
        internal readonly TimingNoteAddress Address;
        internal readonly SampledTimingPlan Original;
        internal readonly LocalConsonantTimingBackend.PreparedCoefficientTiming Prepared;
        private readonly string _input;
        private readonly TransitionPreparation? _transitions;
        private readonly TransitionProjectData _all, _active;

        internal PhonemePreparation(WIVSMNote note, bool preserveTransitions = true)
        {
            Note = note; Session = ForPhonemeEditing(note.Sequence!); Revision = Session.Revision;
            Address = SampledTimingVsmBinding.Address(note);
            Original = Session.AllowsNote?.Invoke((nint)note) != false ? Session.Document.Get(Address) ?? new(note.Phonemes, []) : new(note.Phonemes, []);
            _input = LocalConsonantTimingBackend.PhonemeInputKey(note);
            var entry = Get(note.Sequence!);
            _active = preserveTransitions ? ActiveTransitions(entry) : TransitionProjectData.Empty;
            _all = Session.Transitions.Snapshot.Data ?? TransitionProjectData.Empty;
            Prepared = Session.Prepare(note);
            try { if (TransitionProjectBinding.HasPart(_active, Address.Track, Address.Part)) _transitions = PrepareTransitions(note.Parent!); }
            catch { Prepared.Dispose(); throw; }
        }
        internal sealed record Candidate(SampledTimingPlan? Plan, LocalConsonantTimingBackend.PhonemeLayout Layout,
            TransitionProjectData? Transitions);
        internal Candidate Query(SampledTimingPlan? plan, int? boundary, CancellationToken cancellation = default, bool resetAll = false)
        {
            var layout = Prepared.QueryPhonemes(plan ?? new(Address.Topology, []), Revision, cancellation, 0,
                editedBoundary: boundary, resetAllBoundaries: resetAll);
            TransitionProjectData? transitions = null;
            if (_transitions != null)
            {
                var before = _transitions.Prepared.QueryTransitions(_transitions.SourcePlan, Revision, cancellation, 0);
                transitions = TransitionTimingScale.Adopt(_all, _active, before);
            }
            return new(plan, layout, transitions);
        }
        internal TransitionPreparation? PrepareCandidate(Candidate candidate)
        {
            Check();
            return candidate.Transitions == null ? null : PrepareTransitions(Note.Parent!, candidate.Layout.Plans ??
                new Dictionary<TimingNoteAddress, SampledTimingPlan?> { [Address] = candidate.Plan });
        }
        internal TransitionExecutionPlan? ValidateCandidate(Candidate candidate, TransitionPreparation? next, CancellationToken cancellation = default)
        {
            if (next == null) return null;
            var liveIds = _active.Notes.Select(n => n.Id).ToHashSet();
            var active = candidate.Transitions!.KeepNotes(n => liveIds.Contains(n.Id));
            var layout = next.Prepared.QueryTransitions(next.SourcePlan, Revision, cancellation, 0);
            return next.Prepared.QueryTransitionControls(layout, TransitionProjectBinding.ResolveAvailable(active, layout), Revision, cancellation, 0);
        }
        internal void Check()
        {
            if (!Note.Sequence!.IsOpen || Note.Sequence.IsStaged || Session.Revision != Revision ||
                SampledTimingVsmBinding.Address(Note) != Address || LocalConsonantTimingBackend.PhonemeInputKey(Note) != _input)
                throw new InvalidOperationException("Phoneme candidate is stale; query it again.");
        }
        internal bool Commit(Candidate candidate, TransitionPreparation? next, TransitionExecutionPlan? execution)
        {
            Check();
            if (next != null) next.Prepared.ValidateTransitionCommit(Note.Parent!, execution!, Revision);
            var changes = candidate.Layout.Plans ?? new Dictionary<TimingNoteAddress, SampledTimingPlan?> { [Address] = candidate.Plan };
            if (changes.All(p => Session.Document.Get(p.Key)?.Fingerprint == p.Value?.Fingerprint)) return false;
            Note.Sequence!.StopAsyncRendering();
            Session.SetPlan(Note, candidate.Plan, candidate.Layout.Plans, candidate.Transitions);
            return true;
        }
        public void Dispose() { _transitions?.Dispose(); Prepared.Dispose(); }
    }
}

internal sealed partial class LocalConsonantTimingBackend
{
    internal static string PhonemeInputKey(WIVSMNote note)
    {
        var source = note.Sequence ?? throw new InvalidOperationException("Missing timing source.");
        var context = CaptureContext(note, 16); var notes = context.Notes;
        return ContextKey(source, note.Parent!, notes, context.Target,
            CaptureTempo(source, notes[0].AbsTick, notes.Max(n => n.AbsTick + n.Duration)), note.NoteVelocity) +
            ":" + source.PresendTimeSec.ToString("R", System.Globalization.CultureInfo.InvariantCulture) +
            ":" + source.GetSamplingRate() + ":" + source.NumSampleInFrame;
    }
}
