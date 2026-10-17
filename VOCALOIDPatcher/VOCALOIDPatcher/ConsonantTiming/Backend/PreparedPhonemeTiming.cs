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
        TransitionCapture? TransitionCapture = null);

    internal sealed partial class PreparedCoefficientTiming
    {
        private readonly Dictionary<(string Plan, int? Boundary, bool Transitions), PhonemeLayout> _phonemeResults = new();

        internal PhonemeLayout QueryPhonemes(SampledTimingPlan plan, long revision,
            CancellationToken cancellation = default, int nativeWaitBudgetMs = 50, int? editedBoundary = null,
            bool captureTransitions = false)
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
                var cacheKey = (plan.Fingerprint, editedBoundary, captureTransitions);
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
                    _owner._engine.Begin(cancellation, nativeWaitBudgetMs);
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
                                throw new NotSupportedException("Native baseline phoneme topology is unavailable.");
                            baseline[n] = positions.Select(v => Frame(note.RelPosTick.Value + v)).ToArray();
                        }
                        if (captureTransitions) originalTransitions = _owner._engine.ReadTransitions(window.Origin);
                        if (editedBoundary is { } boundary)
                            resolvedPlans = PhonemeBoundaryEdit.Resolve(window.Notes.Select((_, n) =>
                                new PhonemeBoundaryEdit.Note(window.Messages[n].Address ??
                                    throw new InvalidOperationException("Missing source boundary identity."), baseline[n], window.Messages[n].Plan)).ToArray(),
                                window.Target, boundary, plan);
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
                    var points = anchors.Select(p => new NativePhonemeTiming.Point(p.Key, p.Value.To)).ToArray();
                    if (points.Zip(points.Skip(1), (a, b) => a.To >= b.To).Any(v => v))
                        throw new NotSupportedException("Phoneme boundaries cannot cross or collapse.");
                    int[] requested = baseline[window.Target].Select(f => anchors[f].To).ToArray();
                    string[] phones = _topology.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                    _owner._engine.Begin(cancellation, nativeWaitBudgetMs);
                    bool scope = false;
                    try
                    {
                        NativePhonemeTiming.Begin(points); scope = true; Feed();
                        for (int i = 0; i < requested.Length; i++) _owner._engine.VerifyPhonemeBoundary(requested[i],
                            i == 0 ? null : phones[i - 1], i == phones.Length ? null : phones[i]);
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
                                    baseline[i].Select(f => checked(f + window.Origin)).ToImmutableArray())).ToImmutableArray()));
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
