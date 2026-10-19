using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Diagnostics;
using System.Linq;
using System.Threading;

namespace VOCALOIDPatcher.ConsonantTiming;

internal sealed partial class LocalConsonantTimingBackend
{
    internal sealed partial class PreparedCoefficientTiming
    {
        private readonly Dictionary<(string Layout, TransitionIdentity Edge, int Offset), TransitionSegment> _onsetReferences = new();
        private readonly Dictionary<(string Plan, TransitionIdentity Edge), ImmutableArray<int>> _onsetRanges = new();

        internal ImmutableArray<int> QueryOnsetRange(TransitionLayout layout, TransitionControlPlan current, TransitionIdentity edge,
            long revision, CancellationToken cancellation = default, int nativeWaitBudgetMs = 50)
        {
            if (!Monitor.TryEnter(_owner._gate)) throw new TimeoutException("Transition backend busy.");
            try
            {
                CheckTransitionRevision(revision); RequireTransitionLayout(layout);
                var geometry = new TransitionControlPlan(layout.Fingerprint, current.Junctions, onsets: current.Onsets, phases: current.Phases, coverages: current.Coverages).WithOnset(edge, 0);
                var key = (geometry.Compile(layout).Fingerprint, edge);
                if (_onsetRanges.TryGetValue(key, out var hit)) return hit;
                int split = layout.Edges.Single(e => e.Identity == edge).Current.Split;
                int minimum = Math.Max(-4096, layout.MinimumOutputFrame - split);
                var values = ImmutableArray.CreateBuilder<int>();
                for (int offset = 0; offset >= minimum; offset--)
                {
                    cancellation.ThrowIfCancellationRequested();
                    try { QueryTransitionControls(layout, geometry.WithOnset(edge, offset), revision, cancellation, nativeWaitBudgetMs); values.Add(offset); }
                    catch (NotSupportedException) { }
                }
                if (_onsetRanges.Count >= 64) _onsetRanges.Clear();
                return _onsetRanges[key] = values.ToImmutable();
            }
            finally { Monitor.Exit(_owner._gate); }
        }

        // A private boundary plan supplies source progress only. Its phoneme
        // anchors are never published to the real Part. Existing junction edits
        // remain independent; otherwise the same ART's reference extent is used.
        private TransitionExecutionPlan QueryOnsetProgress(TransitionLayout layout, TransitionControlPlan requested,
            TransitionCompiled compiled, long revision, CancellationToken cancellation, int budget, Stopwatch timer)
        {
            var captured = RequireTransitionLayout(layout);
            var window = _windows[captured.WindowIndex];
            var geometry = new TransitionControlPlan(layout.Fingerprint, requested.Junctions, requested.Gains);
            var references = new Dictionary<TransitionIdentity, TransitionSegment>();
            int runs = 0;
            foreach (var edit in compiled.Onsets)
            {
                var edge = layout.Edges.Single(e => e.Identity == edit.Edge);
                var key = (layout.Fingerprint, edit.Edge, edit.OffsetFrames);
                if (!_onsetReferences.TryGetValue(key, out var reference))
                {
                    var phone = edit.Edge.Right!;
                    int boundary = layout.Notes.Single(n => n.Address == phone.Note).Original[phone.Index];
                    var points = layout.Anchors.Select(p => p.From == boundary
                        ? new TransitionAnchor(p.From, checked(p.To + edit.OffsetFrames)) : p).ToImmutableArray();
                    TransitionLayout.ValidateAnchors(points);
                    _owner._engine.Begin(cancellation, budget); bool scope = false;
                    try
                    {
                        NativePhonemeTiming.Begin(points.Select(p => new NativePhonemeTiming.Point(
                            checked(p.From - window.Origin), checked(p.To - window.Origin))).ToArray()); scope = true;
                        _owner._engine.Plan(0, _singer);
                        foreach (var message in window.Messages)
                        {
                            cancellation.ThrowIfCancellationRequested();
                            _owner._engine.PlanNote(message.Frame, message.Bytes, message == window.Messages[window.Target]
                                ? _transitionCoefficients[layout.Fingerprint] : message.Coefficient);
                        }
                        var rows = _owner._engine.ReadTransitions(window.Origin).Where(s => s.To != null).ToArray();
                        if (rows.Length != layout.Edges.Length) throw new NotSupportedException("Onset progress changed transition topology.");
                        reference = rows[layout.Edges.IndexOf(edge)];
                        if ((reference.From, reference.To, reference.Stage, reference.Reverse, reference.MaterialMetadata) !=
                            (edge.Current.From, edge.Current.To, edge.Current.Stage, edge.Current.Reverse, edge.Current.MaterialMetadata) ||
                            reference.Frames.Length != reference.End - reference.Begin || reference.Frames.IsEmpty ||
                            reference.Frames.Any(f => !float.IsFinite(BitConverter.Int32BitsToSingle(f.SourceBits)) || BitConverter.Int32BitsToSingle(f.SourceBits) is < 0 or > 65535))
                            throw new NotSupportedException("Onset progress changed its source material or lacks complete source frames.");
                    }
                    finally { try { if (scope) NativePhonemeTiming.End(); } finally { _owner._engine.End(); } }
                    runs++; _transitionNativePlans++;
                    if (_onsetReferences.Count >= 256) _onsetReferences.Clear();
                    _onsetReferences[key] = reference;
                }
                references.Add(edit.Edge, reference);
                foreach (var side in new[] { TransitionEndpoint.Begin, TransitionEndpoint.End })
                {
                    var endpoint = new TransitionEndpointRef(edit.Edge, side);
                    int joint = layout.Junction(endpoint).OriginalFrame;
                    if (!requested.Junctions.Any(j => layout.Junction(j.Endpoint).OriginalFrame == joint))
                    {
                        // Source progress is independent of the protected
                        // split. The reference extent may cross it for a short
                        // consonant/large VEL; project that extent into this
                        // schedule's legal geometry, then resample progress
                        // across the actual allocation below. Do not move the
                        // phoneme or discard the source-progress control.
                        var bounds = layout.ConditionalJunction(endpoint, geometry);
                        geometry = geometry.WithJunction(layout, endpoint, Math.Clamp(TransitionLayout.Position(reference, side),
                            bounds.MinimumCandidate, bounds.MaximumCandidate));
                    }
                }
            }
            var allocation = QueryTransitionControls(layout, geometry, revision, cancellation, budget);
            var segments = allocation.Segments.ToArray();
            var progress = ImmutableArray.CreateBuilder<TransitionSourceProgress>();
            foreach (var pair in references)
            {
                var edge = layout.Edges.Single(e => e.Identity == pair.Key);
                int index = Array.FindIndex(segments, s => s.To != null && s.Begin ==
                    Interpolate(allocation.RenderAnchors, edge.Original.Begin));
                if (index < 0) throw new NotSupportedException("Onset allocation is missing.");
                var row = segments[index]; var reference = pair.Value;
                if (row.Frames.Length != row.End - row.Begin || row.Frames.IsEmpty)
                    throw new NotSupportedException("Onset allocation has an incomplete frame map.");
                var frames = row.Frames.Select(f =>
                {
                    // Exact reference positions when extents agree. Independent
                    // endpoint edits rescale the entire progression, not its two
                    // halves around the protected phoneme split.
                    double x = (f.Frame - (double)row.Begin) * (reference.Frames.Length - 1) / Math.Max(1, row.End - row.Begin - 1);
                    int a = (int)Math.Floor(x), b = Math.Min(a + 1, reference.Frames.Length - 1);
                    float source = (float)(BitConverter.Int32BitsToSingle(reference.Frames[a].SourceBits) * (1 - (x - a)) +
                        BitConverter.Int32BitsToSingle(reference.Frames[b].SourceBits) * (x - a));
                    return f with { SourceBits = BitConverter.SingleToInt32Bits(source) };
                }).ToImmutableArray();
                segments[index] = row with { Frames = frames };
                progress.Add(new(pair.Key, frames));
            }
            if (progress.Sum(p => p.Frames.Length) > 65536) throw new NotSupportedException("Part source progress exceeds the native snapshot bound.");
            var result = new TransitionExecutionPlan(layout, compiled, segments.ToImmutableArray(), runs + allocation.NativePlans,
                false, timer.Elapsed.TotalMilliseconds) { RenderAnchors = allocation.RenderAnchors, SourceProgress = progress.ToImmutable() };
            if (_transitionResults.Count >= 256) _transitionResults.Clear();
            _transitionResults[compiled.Fingerprint] = result;
            return result;
        }
    }
}
