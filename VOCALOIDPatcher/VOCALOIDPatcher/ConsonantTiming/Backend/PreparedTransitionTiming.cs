using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using Yamaha.VOCALOID.VSM;

namespace VOCALOIDPatcher.ConsonantTiming;

internal sealed partial class LocalConsonantTimingBackend
{
    internal sealed record TransitionCapture(int WindowIndex, ImmutableArray<TransitionSegment> Original,
        ImmutableArray<TransitionSegment> Current, ImmutableArray<TransitionNoteFrames> Notes);

    internal sealed partial class PreparedCoefficientTiming
    {
        private readonly Guid _transitionScope = Guid.NewGuid();
        private readonly Dictionary<string, (TransitionLayout Layout, TransitionCapture Capture)> _transitionLayouts = new();
        private readonly Dictionary<string, TransitionExecutionPlan> _transitionResults = new();
        private readonly Dictionary<(string Layout, int Frame), TransitionReachableRange> _transitionRanges = new();
        private long _transitionNativePlans;
        private sealed record TransitionPublication(ulong Epoch, string Fingerprint, WIVSMMidiPart Part, nint Sequence);
        private readonly Dictionary<nint, TransitionPublication> _transitionPublications = new();

        internal TransitionLayout QueryTransitions(SampledTimingPlan plan, long revision,
            CancellationToken cancellation = default, int nativeWaitBudgetMs = 50)
        {
            if (!Monitor.TryEnter(_owner._gate)) throw new InvalidOperationException("Transition backend busy.");
            try
            {
                CheckTransitionRevision(revision); cancellation.ThrowIfCancellationRequested();
                if (_transitionLayouts.TryGetValue(plan.Fingerprint, out var hit)) return hit.Layout;
                var phonemes = QueryPhonemes(plan, revision, cancellation, nativeWaitBudgetMs, captureTransitions: true);
                var captured = phonemes.TransitionCapture ?? throw new InvalidOperationException("Missing transition snapshot.");
                var layout = new TransitionLayout(_transitionScope, revision, LocalTimingNative.Profile.Id + ":" + plan.Fingerprint + ":" + _windows[captured.WindowIndex].InputKey,
                    captured.Original, captured.Current, captured.Notes,
                    phonemes.Anchors.Select(a => new TransitionAnchor(a.From, a.To)),
                    minimumOutputFrame: checked(3087 + _windows[captured.WindowIndex].Origin));
                if (_transitionLayouts.Count >= 64) { _transitionLayouts.Clear(); _transitionResults.Clear(); _transitionRanges.Clear(); _transitionCoefficients.Clear(); }
                _transitionLayouts[plan.Fingerprint] = (layout, captured);
                _transitionCoefficients[layout.Fingerprint] = plan.PhonemeCoefficient(_topology) ?? _defaultCoefficient;
                return layout;
            }
            finally { Monitor.Exit(_owner._gate); }
        }

        internal TransitionExecutionPlan QueryTransitionControls(TransitionLayout layout, TransitionControlPlan plan,
            long revision, CancellationToken cancellation = default, int nativeWaitBudgetMs = 50)
        {
            if (!Monitor.TryEnter(_owner._gate)) throw new InvalidOperationException("Transition backend busy.");
            try
            {
                CheckTransitionRevision(revision); cancellation.ThrowIfCancellationRequested();
                if (nativeWaitBudgetMs is < 0 or > 50) throw new ArgumentOutOfRangeException(nameof(nativeWaitBudgetMs));
                var captured = RequireTransitionLayout(layout);
                var timer = Stopwatch.StartNew();
                var compiled = plan.Compile(layout);
                if (_transitionResults.TryGetValue(compiled.Fingerprint, out var hit))
                    return hit with { CacheHit = true, NativePlans = 0, Milliseconds = timer.Elapsed.TotalMilliseconds };
                // Gains cannot affect planning. Reuse the exact timing result
                // rather than plan again for every dB change.
                var geometry = _transitionResults.Values.FirstOrDefault(r => r.Layout == layout && r.Controls.Anchors.SequenceEqual(compiled.Anchors));
                ImmutableArray<TransitionSegment> segments;
                int runs = 0;
                if (compiled.Anchors.SequenceEqual(layout.Anchors)) segments = captured.Current;
                else if (geometry != null) segments = geometry.Segments;
                else
                {
                    var window = _windows[captured.WindowIndex];
                    _owner._engine.Begin(cancellation, nativeWaitBudgetMs);
                    bool scope = false;
                    try
                    {
                        NativePhonemeTiming.Begin(compiled.Anchors.Select(a => new NativePhonemeTiming.Point(
                            checked(a.From - window.Origin), checked(a.To - window.Origin))).ToArray()); scope = true;
                        _transitionNativePlans++;
                        _owner._engine.Plan(0, _singer);
                        // Translate both event and anchor coordinates by the
                        // same integer origin. Never invert a rounded warp or
                        // allocate all leading silence for a late Part note.
                        foreach (var message in window.Messages)
                        {
                            cancellation.ThrowIfCancellationRequested();
                            float coefficient = message == window.Messages[window.Target]
                                ? _transitionCoefficients[layout.Fingerprint] : message.Coefficient;
                            _owner._engine.PlanNote(message.Frame, message.Bytes, coefficient);
                        }
                        segments = _owner._engine.ReadTransitions(window.Origin);
                        foreach (var note in layout.Notes)
                        {
                            string[] tokens = note.Address.Topology.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                            for (int i = 0; i < note.Original.Length; i++)
                                _owner._engine.VerifyPhonemeBoundary(checked(Interpolate(layout.Anchors, note.Original[i]) - window.Origin),
                                    i == 0 ? null : tokens[i - 1], i == tokens.Length ? null : tokens[i]);
                        }
                    }
                    finally { try { if (scope) NativePhonemeTiming.End(); } finally { _owner._engine.End(); } }
                    runs = 1;
                }
                var actual = segments.Where(s => s.To != null).ToArray();
                if (actual.Length != layout.Edges.Length) throw new NotSupportedException("Transition control changed ART topology.");
                for (int i = 0; i < actual.Length; i++)
                {
                    var edge = layout.Edges[i]; var row = actual[i];
                    if ((row.From, row.To, row.Stage, row.Reverse, row.MaterialMetadata) !=
                        (edge.Current.From, edge.Current.To, edge.Current.Stage, edge.Current.Reverse, edge.Current.MaterialMetadata) ||
                        row.Begin != Map(edge.Original.Begin) || row.Split != Map(edge.Original.Split) || row.End != Map(edge.Original.End))
                        throw new NotSupportedException("Native transition allocation differs from requested junctions or source identity.");
                }
                // Existing phoneme boundaries remain authoritative, including
                // joins represented by ART/STA boundaries instead of a split.
                foreach (var note in layout.Notes)
                    foreach (int frame in note.Original)
                        if (Map(frame) != MapBase(frame)) throw new NotSupportedException("Transition control moved a protected phoneme boundary.");
                var result = new TransitionExecutionPlan(layout, compiled, segments, runs, false, timer.Elapsed.TotalMilliseconds);
                if (_transitionResults.Count >= 256) _transitionResults.Clear();
                _transitionResults[compiled.Fingerprint] = result;
                return result;

                int Map(int frame) => Interpolate(compiled.Anchors, frame);
                int MapBase(int frame) => Interpolate(layout.Anchors, frame);
            }
            finally { Monitor.Exit(_owner._gate); }
        }

        private readonly Dictionary<string, float> _transitionCoefficients = new();
        // Call on the source model's owner thread, just like PrepareCoefficient.
        // One atomic native publication contains coefficients, warp and gain;
        // invalidation targets only this Part and is omitted for identical edits.
        internal void PublishTransitions(WIVSMMidiPart part, ulong epoch, TransitionExecutionPlan result, long revision)
        {
            lock (_owner._gate)
            {
                CheckTransitionRevision(revision);
                var captured = RequireTransitionLayout(result.Layout);
                if (!_transitionSourceSequence.IsOpen || (nint)part != (nint)_transitionSourcePart ||
                    (nint)part.Sequence! != (nint)_transitionSourceSequence)
                    throw new InvalidOperationException("Transition publication belongs to a different or closed source Part.");
                if (_transitionSourceSequence.IsStaged)
                    throw new InvalidOperationException("Transition source must be committed before publication.");
                var window = _windows[captured.WindowIndex];
                var address = window.Messages[window.Target].Address!;
                var note = part.GetNote(checked((ulong)address.Note)) ?? throw new InvalidOperationException("Transition source note disappeared.");
                var context = CaptureContext(note, 16);
                if (!context.Complete || context.Notes.Count != window.Notes.Length || part.Sequence == null ||
                    ContextKey(part.Sequence, part, context.Notes, context.Target,
                        CaptureTempo(part.Sequence, context.Notes[0].AbsTick, context.Notes.Max(n => n.AbsTick + n.Duration)), note.NoteVelocity) != window.InputKey ||
                    part.Sequence.GetSamplingRate() != VSMSamplingRate._44100 || part.IsAi || !string.IsNullOrEmpty(part.SecondaryVoiceBankID) ||
                    Math.Abs(part.Sequence.PresendTimeSec - .5) > 1e-9 || part.Sequence.NumSampleInFrame != 256)
                    throw new InvalidOperationException("Transition input changed; prepare it again before publication.");
                var notes = part.NotesEnumerator.ToArray();
                if (notes.Length != window.Messages.Length || !notes.Select(SampledTimingVsmBinding.Address).SequenceEqual(window.Messages.Select(m => m.Address)))
                    throw new InvalidOperationException("Transition source/Part identity changed.");
                var coefficients = notes.Select((n, i) => ((nint)n, Value: i == window.Target
                    ? _transitionCoefficients[result.Layout.Fingerprint] : window.Messages[i].Coefficient)).ToDictionary(p => p.Item1, p => p.Value);
                NativeSampledTiming.Publish(part, epoch, coefficients,
                    result.Controls.Anchors.Select(a => new NativePhonemeTiming.Point(a.From, a.To)).ToArray(), NativeTransitionGain.Gains(result));
                bool same = _transitionPublications.TryGetValue((nint)part, out var prior) && prior.Fingerprint == result.Controls.Fingerprint;
                _transitionPublications[(nint)part] = new(epoch, result.Controls.Fingerprint, part, (nint)part.Sequence);
                if (!same) SampledTimingVsmBinding.InvalidateRenderedPart(part, notes);
            }
        }
        internal TransitionReachableRange QueryTransitionRange(TransitionLayout layout, TransitionEndpointRef endpoint,
            long revision, CancellationToken cancellation = default, int nativeWaitBudgetMs = 50)
        {
            if (!Monitor.TryEnter(_owner._gate)) throw new InvalidOperationException("Transition backend busy.");
            try
            {
                CheckTransitionRevision(revision); cancellation.ThrowIfCancellationRequested(); RequireTransitionLayout(layout);
                var joint = layout.Junction(endpoint);
                var key = (layout.Fingerprint, joint.OriginalFrame);
                if (_transitionRanges.TryGetValue(key, out var hit)) return hit with { CacheHit = true, NativePlans = 0, Milliseconds = 0 };
                var timer = Stopwatch.StartNew();
                var reachable = ImmutableArray.CreateBuilder<int>();
                long priorPlans = _transitionNativePlans;
                if (joint.MaximumCandidate - joint.MinimumCandidate > 4096) throw new NotSupportedException("Transition range exceeds bounded scan.");
                for (int frame = joint.MinimumCandidate; frame <= joint.MaximumCandidate; frame++)
                {
                    cancellation.ThrowIfCancellationRequested();
                    try
                    {
                        var candidate = QueryTransitionControls(layout, new(layout.Fingerprint, [new(endpoint, frame)]), revision, cancellation, nativeWaitBudgetMs);
                        reachable.Add(frame);
                    }
                    catch (NotSupportedException) { /* A native-rejected point is not certified as reachable. */ }
                }
                if (!reachable.Contains(joint.CurrentFrame)) throw new InvalidOperationException("Native baseline endpoint is not reachable.");
                var result = new TransitionReachableRange(joint, reachable.ToImmutable(), checked((int)(_transitionNativePlans - priorPlans)), false, timer.Elapsed.TotalMilliseconds);
                if (_transitionRanges.Count >= 256) _transitionRanges.Clear();
                _transitionRanges[key] = result;
                return result;
            }
            finally { Monitor.Exit(_owner._gate); }
        }

        private void CheckTransitionRevision(long revision)
        {
            ObjectDisposedException.ThrowIf(_windows.Length == 0 || _owner._disposed, this);
            if (revision != _sourceRevision) throw new InvalidOperationException("Transition source revision changed.");
        }
        private TransitionCapture RequireTransitionLayout(TransitionLayout layout)
        {
            if (layout.Scope != _transitionScope || layout.Revision != _sourceRevision)
                throw new InvalidOperationException("Foreign or stale transition preparation.");
            return _transitionLayouts.Values.SingleOrDefault(p => ReferenceEquals(p.Layout, layout)).Capture
                ?? throw new InvalidOperationException("Transition layout was invalidated or evicted.");
        }
        private void ClearTransitionCache()
        {
            foreach (var item in _transitionPublications)
            {
                // Native Close hashes and publishes valid audio before freeing
                // the source. If we detach earlier, first invalidate controlled
                // PCM so it cannot be published under the ordinary cache key.
                if (NativeSampledTiming.ReadStatus(item.Key).Epoch == item.Value.Epoch &&
                    _owner._manager.HasSequence(item.Value.Sequence) && item.Value.Part.HasValidRenderedWave)
                {
                    // Source edits may have added or removed notes since this
                    // snapshot. One bounded set dirties the entire Part. An
                    // emptied Part was already dirtied by note deletion.
                    var remaining = item.Value.Part.NotesEnumerator.Take(33).ToArray();
                    if (remaining.Length != 0) SampledTimingVsmBinding.InvalidateRenderedPart(item.Value.Part, remaining);
                }
                NativeTransitionGain.Remove(item.Key, item.Value.Epoch);
            }
            _transitionPublications.Clear(); _transitionLayouts.Clear(); _transitionResults.Clear(); _transitionRanges.Clear(); _transitionCoefficients.Clear();
        }
        private static int Interpolate(ImmutableArray<TransitionAnchor> points, int frame)
        {
            int index = 0; while (index < points.Length && points[index].From <= frame) index++;
            if (index == 0 || index == points.Length) return frame;
            var a = points[index - 1]; var b = points[index];
            return checked(a.To + (int)Math.Round((frame - (double)a.From) * (b.To - (double)a.To) / (b.From - (double)a.From), MidpointRounding.AwayFromZero));
        }
    }
}
