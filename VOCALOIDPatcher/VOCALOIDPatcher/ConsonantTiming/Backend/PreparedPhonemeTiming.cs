using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;

namespace VOCALOIDPatcher.ConsonantTiming;

internal sealed partial class LocalConsonantTimingBackend
{
    internal sealed record PhonemeLayout(LocalConsonantTimingResult Result, NativePhonemeTiming.Point[] Anchors,
        IReadOnlyDictionary<TimingNoteAddress, SampledTimingPlan?>? Plans = null,
        IReadOnlyDictionary<TimingNoteAddress, long[]>? RelatedBoundaries = null,
        TransitionCapture? TransitionCapture = null,
        IReadOnlyDictionary<TimingNoteAddress, ImmutableArray<PhonemeBoundaryLimit>>? Limits = null);

    internal sealed partial class PreparedCoefficientTiming
    {
        private readonly Dictionary<(string Plan, int? Boundary, bool Transitions, bool Reset), PhonemeLayout> _phonemeResults = new();

        internal PhonemeLayout QueryPhonemes(SampledTimingPlan plan, long revision,
            CancellationToken cancellation = default, int nativeWaitBudgetMs = 50, int? editedBoundary = null,
            bool captureTransitions = false, bool resetAllBoundaries = false)
        {
            float coefficient = plan.PhonemeCoefficient(_topology) ?? _defaultCoefficient;
            if (nativeWaitBudgetMs is < 0 or > 50) throw new ArgumentOutOfRangeException(nameof(nativeWaitBudgetMs));
            if (!Monitor.TryEnter(_owner._gate)) throw new InvalidOperationException("Timing backend busy.");
            try
            {
                ObjectDisposedException.ThrowIf(_windows.Length == 0 || _owner._disposed, this);
                if (revision != _sourceRevision) throw new InvalidOperationException("Prepared source revision changed.");
                cancellation.ThrowIfCancellationRequested();
                var timer = Stopwatch.StartNew();
                if (captureTransitions && (!_transitionCompatible || !_windows.Any(w => w.Complete)))
                    throw new NotSupportedException("Transition planning requires a complete captured context (at most 33 notes), 44100/256 and native presend.");
                var cacheKey = (plan.Fingerprint, editedBoundary, captureTransitions, resetAllBoundaries);
                if (_phonemeResults.TryGetValue(cacheKey, out var hit)) return hit with
                {
                    Result = hit.Result with
                    {
                        Boundaries = hit.Result.Boundaries.ToArray(),
                        CacheHit = true,
                        PlannerRuns = 0,
                        Milliseconds = timer.Elapsed.TotalMilliseconds
                    },
                    Anchors = hit.Anchors.ToArray()
                };
                long[]? previous = null; int stable = 0, runs = 0;
                foreach (var window in _windows)
                {
                    if (captureTransitions && !window.Complete) continue;
                    if (!window.Complete && ContinuationPhonemes.IsContinuation(window.Messages[0].Address!.Topology)) continue;
                    var owners = ContinuationPhonemes.Owners(window.Messages.Select(m => m.Address!).ToArray());
                    string? continuationPhone = owners[window.Target] == window.Target ? null :
                        window.Messages[owners[window.Target]].Address!.Topology.Split(' ', StringSplitOptions.RemoveEmptyEntries)[^1];
                    bool continuesAfter = window.Target + 1 < owners.Length && owners[window.Target + 1] == owners[window.Target];
                    ContinuationPhonemes.ValidateBoundaryEdits(plan, continuesAfter);
                    cancellation.ThrowIfCancellationRequested();
                    void Feed()
                    {
                        _owner._engine.Plan(0, _singer);
                        for (int i = 0; i < window.Messages.Length; i++)
                        {
                            var message = window.Messages[i];
                            _owner._engine.PlanNote(message.Frame, message.Bytes, i == window.Target ? coefficient : message.Coefficient);
                        }
                    }
                    int Frame(long tick) => checked((int)Math.Round((tick / MappingTicksPerSecond + .5) * 44100 / 256) + 3087);
                    var anchors = new SortedDictionary<int, (int To, bool Edited)> { [0] = (0, false) };
                    var baseline = new int[window.Notes.Length][];
                    ImmutableArray<TransitionSegment> originalTransitions = [], currentTransitions = [];
                    IReadOnlyDictionary<TimingNoteAddress, SampledTimingPlan?>? resolvedPlans = null;
                    _owner._engine.Begin(cancellation, nativeWaitBudgetMs, window.RegisterNotes);
                    try
                    {
                        Feed();
                        foreach (var note in window.Notes) Marshal.WriteIntPtr((nint)note, LocalTimingNative.Profile.NotePositionsEnd, Marshal.ReadIntPtr((nint)note, LocalTimingNative.Profile.NotePositionsBegin));
                        using var adapter = new ConsonantTimingScoreAdapter(_owner._engine.CreateBoundaryScore());
                        adapter.Map(window.Part, [0, 750000]);
                        for (int n = 0; n < window.Notes.Length; n++)
                        {
                            var note = window.Notes[n]; var positions = note.GetPhonemePositions();
                            string[] tokens = note.Phonemes.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                            if (positions.Count != tokens.Length + 1 || positions.Any(v => v == int.MinValue))
#if PATCHER_ISOLATED_VALIDATION
                                throw new NotSupportedException($"Native baseline phoneme topology is unavailable: expected={tokens.Length + 1}; positions={string.Join(',', positions)}; feed={window.Messages[n].Frame}; note={note.RelPosTick.Value}/{note.DurationTick.Value}; original={string.Join(',', NativeTimingPositions.Read(note, true))}; rows=" +
                                    System.Text.Json.JsonSerializer.Serialize(_owner._engine.ReadTransitions(window.Origin).Select(s => new { s.Begin, s.Split, s.End, s.From, s.To })));
#else
                                throw new NotSupportedException("Native baseline phoneme topology is unavailable.");
#endif
                            baseline[n] = positions.Select(v => Frame(note.RelPosTick.Value + v)).ToArray();
                        }
                        if (captureTransitions) originalTransitions = _owner._engine.ReadTransitions(window.Origin);
                        if (editedBoundary.HasValue || resetAllBoundaries)
                            resolvedPlans = PhonemeBoundaryEdit.ResolveMany(window.Notes.Select((_, n) =>
                                new PhonemeBoundaryEdit.Note(window.Messages[n].Address ??
                                    throw new InvalidOperationException("Missing source boundary identity."), baseline[n], window.Messages[n].Plan)).ToArray(),
                                window.Target, resetAllBoundaries ? Enumerable.Range(0, baseline[window.Target].Length).ToArray() : [editedBoundary!.Value], plan);
                        for (int n = 0; n < window.Notes.Length; n++)
                        {
                            var note = window.Notes[n];
                            var currentPlan = n == window.Target ? plan : window.Messages[n].Plan;
                            if (window.Messages[n].Address is { } address && resolvedPlans?.TryGetValue(address, out var synchronized) == true)
                                currentPlan = synchronized;
                            if (currentPlan != null) _ = currentPlan.PhonemeCoefficient(note.Phonemes);
                            for (int i = 0; i < baseline[n].Length; i++)
                            {
                                int from = baseline[n][i];
                                var edit = currentPlan?.Adjustments.SingleOrDefault(a => a.Kind == TimingAdjustment.PhonemeBoundary && a.Index == i);
                                int delta = edit == null ? 0 : checked((int)Math.Round(edit.Value * (44100d / 256)));
                                int to = checked(from + delta);
                                if (anchors.TryGetValue(from, out var existing))
                                {
                                    if (delta != 0 && existing.Edited && existing.To != to)
                                        throw new NotSupportedException("Conflicting controls at a shared phoneme boundary.");
                                    if (delta == 0 || existing.Edited) continue;
                                }
                                anchors[from] = (to, delta != 0);
                            }
                        }
                    }
                    finally { _owner._engine.End(); }
                    // Keep the captured context fixed outside its guard anchors.
                    int guard = checked(anchors.Keys.Max() + 86);
                    anchors[guard] = (guard, false);
                    var projected = PhonemeWarpProjection.Resolve(anchors.Select(p => new PhonemeWarpRequest(p.Key, p.Value.To, p.Value.Edited)).ToArray(), 3087);
                    var points = projected.Select(p => new NativePhonemeTiming.Point(p.From, p.To)).ToArray();
                    var limits = new Dictionary<TimingNoteAddress, ImmutableArray<PhonemeBoundaryLimit>>();
                    var applied = projected.ToDictionary(p => p.From, p => p.To);
                    for (int n = 0; n < baseline.Length; n++)
                    {
                        var limited = baseline[n].Select((from, index) => new PhonemeBoundaryLimit(index, anchors[from].To - from, applied[from] - from))
                            .Where(l => l.RequestedFrames != l.AppliedFrames).ToImmutableArray();
                        if (!limited.IsEmpty) limits.Add(window.Messages[n].Address!, limited);
                    }
                    foreach (var point in projected) anchors[point.From] = (point.To, anchors[point.From].Edited);
                    int[] requested = baseline[window.Target].Select(f => anchors[f].To).ToArray();
                    string[] phones = _topology.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                    _owner._engine.Begin(cancellation, nativeWaitBudgetMs, window.RegisterNotes);
                    bool scope = false;
                    try
                    {
                        NativePhonemeTiming.Begin(points); scope = true; Feed();
                        for (int i = 0; i < requested.Length; i++)
                        {
                            // An owner's last boundary is also the next '-'
                            // note's pitch anchor, not the end of its vowel.
                            // Require the same two-row continuation proof used
                            // when querying that following note directly.
                            bool continuationEnd = i == phones.Length && continuesAfter;
                            _owner._engine.VerifyPhonemeBoundary(requested[i], i == 0 ? null : phones[i - 1],
                                continuationEnd ? "-" : i == phones.Length ? null : phones[i],
                                continuationEnd ? continuationPhone ?? phones[^1] : continuationPhone);
                        }
                        if (captureTransitions) currentTransitions = _owner._engine.ReadTransitions(window.Origin);
                    }
                    finally
                    {
                        try { if (scope) NativePhonemeTiming.End(); }
                        finally { _owner._engine.End(); }
                    }
                    runs += 2;
                    long Tick(int frame)
                    {
                        int index = frame - 3087;
                        if (index < 0 || index >= window.Ticks.Length) throw new NotSupportedException("Phoneme timing exceeds its captured context.");
                        return window.Ticks[index];
                    }
                    long[] boundaries = requested.Select(Tick).ToArray();
                    stable = previous != null && previous.SequenceEqual(boundaries) ? stable + 1 : 0;
                    if (window.Complete || stable >= 2)
                    {
                        var related = new Dictionary<TimingNoteAddress, long[]>();
                        if (resolvedPlans != null)
                            for (int n = 0; n < window.Notes.Length; n++)
                                if (window.Messages[n].Address is { } address && resolvedPlans.ContainsKey(address))
                                    related[address] = baseline[n].Select(f => Tick(anchors[f].To) +
                                        window.Messages[window.Target].Address!.Position - address.Position).ToArray();
                        var result = new PhonemeLayout(new(boundaries, window.Notes.Length, runs, false, timer.Elapsed.TotalMilliseconds),
                            points.Select(p => new NativePhonemeTiming.Point(checked(p.From + window.Origin), checked(p.To + window.Origin))).ToArray(),
                            resolvedPlans, related, !captureTransitions ? null : new TransitionCapture(Array.IndexOf(_windows, window),
                                originalTransitions, currentTransitions,
                                window.Messages.Select((m, i) => new TransitionNoteFrames(m.Address!,
                                    baseline[i].Select(f => checked(f + window.Origin)).ToImmutableArray())).ToImmutableArray()), limits);
                        if (_phonemeResults.Count >= 256) _phonemeResults.Clear();
                        _phonemeResults[cacheKey] = result;
                        return result with { Result = result.Result with { Boundaries = boundaries.ToArray() }, Anchors = result.Anchors.ToArray() };
                    }
                    previous = boundaries;
                }
                throw new NotSupportedException("Phoneme timing context did not stabilize.");
            }
            finally { Monitor.Exit(_owner._gate); }
        }
    }
}
