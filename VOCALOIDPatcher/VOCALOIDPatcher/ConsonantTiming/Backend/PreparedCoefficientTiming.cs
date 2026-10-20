using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using Yamaha.VOCALOID.VSM;

namespace VOCALOIDPatcher.ConsonantTiming;

internal sealed partial class LocalConsonantTimingBackend
{
    private readonly HashSet<PreparedCoefficientTiming> _prepared = new();

    // Capture on the source owner thread while stable. Once prepared, queries
    // access only owned metadata, event buffers and a bounded exact tick table.
    internal PreparedCoefficientTiming PrepareCoefficient(WIVSMNote target, long sourceRevision,
        Func<WIVSMNote, float?>? coefficientForNote = null, Func<WIVSMNote, SampledTimingPlan?>? planForNote = null)
    {
        ArgumentNullException.ThrowIfNull(target);
        if (!Monitor.TryEnter(_gate)) throw new InvalidOperationException("Timing backend busy.");
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_prepared.Count >= 4) throw new InvalidOperationException("Dispose unused timing preparations.");
            var source = target.Sequence ?? throw new InvalidOperationException("Missing source.");
            var part = target.Parent ?? throw new InvalidOperationException("Missing Part.");
            var targetAddress = SampledTimingVsmBinding.Address(target);
            if (source.GetSamplingRate() != VSMSamplingRate._44100 || part.IsAi ||
                !string.IsNullOrEmpty(part.SecondaryVoiceBankID) || !_banks.TryGetValue(part.VoiceBankID, out var bank))
                throw new NotSupportedException("Unsupported sampled timing source.");
            var timer = Stopwatch.StartNew();
            var windows = new List<CoefficientWindow>();
            int encodedNotes = 0;
            try
            {
                foreach (int radius in new[] { 1, 2, 4, 8, 16 })
                {
                    var context = CaptureContext(target, radius);
                    var notes = context.Notes;
                    long partStart = part.AbsPosTick.Value;
                    int[] tempo = CaptureTempo(source, notes[0].AbsTick, notes.Max(n => n.AbsTick + n.Duration));
                    long[] samples = notes.Select(n => source.GetSampleFromTime(source.GetTimeFromTick(
                        new VSMAbsTick(partStart), new VSMAbsTick(n.AbsTick)), VSMSamplingRate._44100)).ToArray();
                    long origin = Math.Max(0, samples[0] / 256 - 86);
                    double end = notes.Max(n => source.GetTimeFromTick(new VSMAbsTick(partStart),
                        new VSMAbsTick(n.AbsTick + n.Duration))) - origin * 256d / 44100;
                    if (samples[0] < 0 || !double.IsFinite(end) || end > 22)
                        throw new NotSupportedException("Timing window exceeds its bound.");
                    ClearNotes(_inputPart);
                    if (_inputPart.AbsPosTick.Value != partStart && !_inputTrack.MovePart(new VSMAbsTick(partStart), _inputTrack, _inputPart))
                        throw new InvalidOperationException("Cannot position timing input.");
                    if (!_inputPart.SetDuration(new VSMRelTick(notes.Max(n => n.AbsTick + n.Duration) - partStart + 480)))
                        throw new InvalidOperationException("Cannot resize timing input.");
                    if (!_inputPart.SetVoiceBankID(part.VoiceBankID)) throw new InvalidOperationException("Cannot select bank.");
                    var mapping = NewSequence();
                    try
                    {
                        var track = (WIVSMMidiTrack)mapping.InsertTrackEx(0, VSMTrackType.Midi, "timing")!;
                        var mappingPart = track.InsertPart(VSMAbsTick.Zero, new VSMRelTick((long)Math.Ceiling((end + 2) * MappingTicksPerSecond)), "timing")!;
                        if (!mappingPart.SetVoiceBankID(part.VoiceBankID)) throw new InvalidOperationException("Cannot select mapping bank.");
                        var messages = new List<CoefficientMessage>();
                        var mapped = new List<WIVSMNote>();
                        // Resolve optional coefficients against the same captured source order.
                        var sourceNotes = new List<WIVSMNote>();
                        var first = target;
                        for (int i = 0; i < context.Target; i++) first = first.Prev!;
                        for (var current = first; current != null && sourceNotes.Count < notes.Count; current = current.Next) sourceNotes.Add(current);
                        for (int i = 0; i < notes.Count; i++)
                        {
                            var n = notes[i];
                            var input = _inputPart.InsertNote(new VSMRelTick(n.AbsTick - partStart),
                                new VSMNoteEvent(checked((int)n.Duration), n.Pitch, n.Velocity), n.Expression,
                                _inputPart.GetDefaultAiNoteExpression(), "timing", n.Phonemes, true, n.Language)!;
                            float coefficient = coefficientForNote?.Invoke(sourceNotes[i]) ?? DecodeVelocity(n.Velocity);
                            if (!float.IsFinite(coefficient) || coefficient is < 0 or > 1) throw new ArgumentOutOfRangeException(nameof(coefficientForNote));
                            messages.Add(new(checked((int)(samples[i] / 256 - origin)),
                                ConsonantTimingInputEncoder.EncodeNote(input, (int)(samples[i] % 256), tempo), coefficient, planForNote?.Invoke(sourceNotes[i]),
                                // Prev/Next captured a contiguous, unfiltered
                                // note window. Resolve the Part locator once,
                                // rather than rescan it for every context note.
                                new TimingNoteAddress(targetAddress.Track, targetAddress.Part, checked(targetAddress.Note - context.Target + i),
                                    n.AbsTick - partStart, checked((int)n.Duration), n.Pitch, part.VoiceBankID, n.Phonemes)));
                            encodedNotes++;
                            double startTime = source.GetTimeFromTick(new VSMAbsTick(partStart), new VSMAbsTick(n.AbsTick)) - origin * 256d / 44100;
                            double endTime = source.GetTimeFromTick(new VSMAbsTick(partStart), new VSMAbsTick(n.AbsTick + n.Duration)) - origin * 256d / 44100;
                            long position = (long)Math.Round(startTime * MappingTicksPerSecond);
                            mapped.Add(mappingPart.InsertNote(new VSMRelTick(position),
                                new VSMNoteEvent(checked((int)Math.Max(1, Math.Round(endTime * MappingTicksPerSecond) - position)), n.Pitch, n.Velocity),
                                n.Expression, mappingPart.GetDefaultAiNoteExpression(), "timing", n.Phonemes, true, n.Language)!);
                        }
                        if (!_inputSequence.Commit(false) || !mapping.Commit(false)) throw new InvalidOperationException("Timing prepare commit failed.");
                        // Exact conversion phase/tempo captured once; no live source call at query time.
                        var ticks = new long[4096];
                        long targetAbsoluteTick = target.AbsPosTick.Value;
                        for (int frame = 0; frame < ticks.Length; frame++) ticks[frame] = source.GetTickFromTime(
                            new VSMAbsTick(partStart), (frame + origin) * 256d / 44100 - 0.5).Value - targetAbsoluteTick;
                        windows.Add(new(mapping, mappingPart, mapped.ToArray(), messages.ToArray(), context.Target, context.Complete, ticks, checked((int)origin),
                            ContextKey(source, part, notes, context.Target, tempo, target.NoteVelocity),
                            CaptureRegisterPlanning(source, part, notes, checked((int)origin))));
                    }
                    catch { ClosePrivate(mapping); throw; }
                    if (context.Complete) break;
                }
                var result = new PreparedCoefficientTiming(this, sourceRevision,
                    ConsonantTimingInputEncoder.EncodeSinger(bank.Language, bank.Singer), windows.ToArray(), encodedNotes,
                    timer.Elapsed.TotalMilliseconds, target.Phonemes, DecodeVelocity(target.NoteVelocity),
                    source.NumSampleInFrame == 256 && Math.Abs(source.PresendTimeSec - .5) < 1e-9, source, part);
                _prepared.Add(result);
                return result;
            }
            catch { foreach (var window in windows) ClosePrivate(window.Sequence); throw; }
        }
        finally { Monitor.Exit(_gate); }
    }

    private static unsafe float DecodeVelocity(int velocity) =>
        ((delegate* unmanaged<byte, float>)(LocalTimingNative.Dse + LocalTimingNative.Profile.DseVelocityCoefficient))(checked((byte)velocity));

    private void InvalidatePrepared()
    {
        foreach (var item in _prepared.ToArray()) item.Dispose();
    }

    internal sealed record CoefficientMessage(int Frame, byte[] Bytes, float Coefficient, SampledTimingPlan? Plan = null,
        TimingNoteAddress? Address = null);
    internal sealed record CoefficientWindow(WIVSMSequence Sequence, WIVSMMidiPart Part, WIVSMNote[] Notes,
        CoefficientMessage[] Messages, int Target, bool Complete, long[] Ticks, int Origin, string InputKey,
        RegisterShift.NativeRegisterShift.PlanningNote[] RegisterNotes);

    internal sealed partial class PreparedCoefficientTiming : IDisposable
    {
        private readonly LocalConsonantTimingBackend _owner;
        private readonly long _sourceRevision;
        private readonly byte[] _singer;
        private readonly string _topology;
        private readonly float _defaultCoefficient;
        private readonly bool _transitionCompatible;
        private readonly WIVSMSequence _transitionSourceSequence;
        private readonly WIVSMMidiPart _transitionSourcePart;
        private CoefficientWindow[] _windows;
        private readonly Dictionary<int, LocalConsonantTimingResult> _results = new();
        internal int EncodedNotes { get; }
        internal double PreparationMs { get; }
        internal int WindowCount => _windows.Length;

        internal PreparedCoefficientTiming(LocalConsonantTimingBackend owner, long sourceRevision, byte[] singer,
            CoefficientWindow[] windows, int encodedNotes, double preparationMs, string topology, float defaultCoefficient,
            bool transitionCompatible, WIVSMSequence sourceSequence, WIVSMMidiPart sourcePart)
        {
            _owner = owner; _sourceRevision = sourceRevision; _singer = singer; _windows = windows;
            EncodedNotes = encodedNotes; PreparationMs = preparationMs;
            _topology = topology; _defaultCoefficient = defaultCoefficient;
            _transitionCompatible = transitionCompatible;
            _transitionSourceSequence = sourceSequence; _transitionSourcePart = sourcePart;
        }

        // Stable plan-level entry for later per-phoneme/transition executors.
        // Unknown axes are preserved by storage and rejected by this executor.
        internal LocalConsonantTimingResult Query(SampledTimingPlan plan, long currentSourceRevision,
            CancellationToken cancellation = default, int nativeWaitBudgetMs = 50) =>
            plan.HasPhonemeControls || _windows.Any(w => w.Messages.Any(m => m.Plan?.HasPhonemeControls == true))
                ? QueryPhonemes(plan, currentSourceRevision, cancellation, nativeWaitBudgetMs).Result
                : Query(plan.PhonemeCoefficient(_topology) ?? _defaultCoefficient,
                    currentSourceRevision, cancellation, nativeWaitBudgetMs);

        internal LocalConsonantTimingResult Query(float coefficient, long currentSourceRevision,
            CancellationToken cancellation = default, int nativeWaitBudgetMs = 50)
        {
            if (!float.IsFinite(coefficient) || coefficient is < 0 or > 1) throw new ArgumentOutOfRangeException(nameof(coefficient));
            if (nativeWaitBudgetMs is < 0 or > 50) throw new ArgumentOutOfRangeException(nameof(nativeWaitBudgetMs));
            if (_windows.Any(w => w.Messages.Any(m => m.Plan?.HasPhonemeControls == true)))
            {
                var current = _windows[0].Messages[_windows[0].Target].Plan;
                var axes = current?.Adjustments.Where(a => a.Kind != TimingAdjustment.NoteCoefficient).ToList() ?? [];
                axes.Add(new(TimingAdjustment.NoteCoefficient, -1, coefficient));
                return QueryPhonemes(new(_topology, axes), currentSourceRevision, cancellation, nativeWaitBudgetMs).Result;
            }
            if (!System.Threading.Monitor.TryEnter(_owner._gate)) throw new InvalidOperationException("Timing backend busy.");
            try
            {
                cancellation.ThrowIfCancellationRequested();
                ObjectDisposedException.ThrowIf(_windows.Length == 0 || _owner._disposed, this);
                if (currentSourceRevision != _sourceRevision) throw new InvalidOperationException("Prepared source revision changed.");
                int key = BitConverter.SingleToInt32Bits(coefficient == 0 ? 0 : coefficient);
                var watch = Stopwatch.StartNew();
                if (_results.TryGetValue(key, out var cached)) return cached with { Boundaries = cached.Boundaries.ToArray(), CacheHit = true, PlannerRuns = 0, Milliseconds = watch.Elapsed.TotalMilliseconds };
                long[]? previous = null;
                int stable = 0, runs = 0;
                foreach (var window in _windows)
                {
                    cancellation.ThrowIfCancellationRequested();
                    long[] boundaries;
                    _owner._engine.Begin(cancellation, nativeWaitBudgetMs, window.RegisterNotes);
                    try
                    {
                        _owner._engine.Plan(0, _singer);
                        for (int i = 0; i < window.Messages.Length; i++)
                        {
                            var message = window.Messages[i];
                            _owner._engine.PlanNote(message.Frame, message.Bytes, i == window.Target ? coefficient : message.Coefficient);
                        }
                        foreach (var note in window.Notes)
                            Marshal.WriteIntPtr((nint)note, LocalTimingNative.Profile.NotePositionsEnd, Marshal.ReadIntPtr((nint)note, LocalTimingNative.Profile.NotePositionsBegin));
                        using var adapter = new ConsonantTimingScoreAdapter(_owner._engine.CreateBoundaryScore());
                        adapter.Map(window.Part, [0, 750000]);
                        var target = window.Notes[window.Target];
                        boundaries = target.GetPhonemePositions().Select(p =>
                        {
                            double frame = ((target.RelPosTick.Value + p) / MappingTicksPerSecond + 0.5) * 44100 / 256;
                            int index = checked((int)Math.Round(frame));
                            if (Math.Abs(frame - index) > .01 || index < 0 || index >= window.Ticks.Length)
                                throw new InvalidOperationException("Invalid prepared boundary frame.");
                            return window.Ticks[index];
                        }).ToArray();
                    }
                    finally { _owner._engine.End(); }
                    runs++;
                    if (boundaries.Length < 2)
                    {
                        if (!window.Complete) { previous = null; stable = 0; continue; }
                        boundaries = [];
                    }
                    stable = previous != null && previous.SequenceEqual(boundaries) ? stable + 1 : 0;
                    if (window.Complete || stable >= 2)
                    {
                        var result = new LocalConsonantTimingResult(boundaries, window.Notes.Length, runs, false, watch.Elapsed.TotalMilliseconds);
                        if (_results.Count >= 256) _results.Clear();
                        _results[key] = result;
                        return result with { Boundaries = boundaries.ToArray() };
                    }
                    previous = boundaries;
                }
                throw new NotSupportedException("Coefficient timing context did not stabilize.");
            }
            finally { System.Threading.Monitor.Exit(_owner._gate); }
        }

        public void Dispose()
        {
            lock (_owner._gate)
            {
                ClearTransitionCache();
                foreach (var window in _windows) ClosePrivate(window.Sequence);
                _windows = []; _results.Clear(); _phonemeResults.Clear(); _owner._prepared.Remove(this);
            }
        }
    }
}
