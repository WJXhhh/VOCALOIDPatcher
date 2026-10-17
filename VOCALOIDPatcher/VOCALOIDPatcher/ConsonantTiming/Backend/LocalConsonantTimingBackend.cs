using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using VOCALOIDPatcher.Utils;
using Yamaha.VOCALOID.DSE;
using Yamaha.VOCALOID.VDM;
using Yamaha.VOCALOID.VSM;

namespace VOCALOIDPatcher.ConsonantTiming;

internal sealed record LocalConsonantTimingResult(long[] Boundaries, int ContextNotes, int PlannerRuns,
    bool CacheHit, double Milliseconds);

// Standalone synchronous backend, validated in an isolated process only; not
// registered with the drag UI. Managers must outlive this instance. The caller
// must keep the source model stable for the query. All writes affect bounded
// private metadata sequences. Neither VSM renderer is called.
// Call ClearCache after other Part edits, including edits outside the captured
// window: the adaptive context rule is empirical and cannot fingerprint them.
internal sealed partial class LocalConsonantTimingBackend : IDisposable
{
    private const double MappingTicksPerSecond = 60000;
    private readonly object _gate = new();
    private readonly WIVSMSequenceManager _manager;
    private readonly NativeConsonantTimingEngine _engine;
    private readonly Dictionary<string, (int Language, int Singer)> _banks = new(StringComparer.Ordinal);
    private readonly WIVSMSequence _inputSequence;
    private readonly WIVSMSequence _mappingSequence;
    private readonly WIVSMMidiTrack _inputTrack;
    private readonly WIVSMMidiPart _inputPart;
    private readonly WIVSMMidiPart _mappingPart;
    private readonly Dictionary<string, LocalConsonantTimingResult> _cache = new(StringComparer.Ordinal);
    private readonly Queue<string> _keys = new();
    private readonly Dictionary<(nint Part, long Target, int Velocity), int> _radiusHints = new();
    private bool _disposed;

    internal LocalConsonantTimingBackend(WIVSMSequenceManager manager, DSEManager dse, DatabaseManager database)
    {
        ArgumentNullException.ThrowIfNull(manager);
        ArgumentNullException.ThrowIfNull(dse);
        ArgumentNullException.ThrowIfNull(database);
        _manager = manager;
        _engine = new NativeConsonantTimingEngine((nint)dse);
        try
        {
            _inputSequence = NewSequence();
            _mappingSequence = NewSequence();
            _inputTrack = (WIVSMMidiTrack)_inputSequence.InsertTrackEx(0, VSMTrackType.Midi, "timing")!;
            _inputPart = _inputTrack.InsertPart(VSMAbsTick.Zero, new VSMRelTick(2880), "timing")!;
            var mappingTrack = (WIVSMMidiTrack)_mappingSequence.InsertTrackEx(0, VSMTrackType.Midi, "timing")!;
            _mappingPart = mappingTrack.InsertPart(VSMAbsTick.Zero, new VSMRelTick(2880), "timing")!;
            for (ulong i = 0; i < database.NumVoiceBanks; i++)
            {
                var bank = database.GetVoiceBankByIndex(i);
                if (bank != null) _banks[bank.CompID] = (bank.NativeLangID, bank.SingerID);
            }
        }
        catch
        {
            try { ClosePrivate(_mappingSequence); }
            finally
            {
                try { ClosePrivate(_inputSequence); }
                finally { _engine.Dispose(); }
            }
            throw;
        }
    }

    internal LocalConsonantTimingResult Query(WIVSMNote target, int velocity, CancellationToken cancellation = default,
        int nativeWaitBudgetMs = 50)
    {
        ArgumentNullException.ThrowIfNull(target);
        if (velocity is < 0 or > 127) throw new ArgumentOutOfRangeException(nameof(velocity));
        if (nativeWaitBudgetMs is < 0 or > 50) throw new ArgumentOutOfRangeException(nameof(nativeWaitBudgetMs));
        if (!Monitor.TryEnter(_gate)) throw new InvalidOperationException("Timing query is already running.");
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            cancellation.ThrowIfCancellationRequested();
            var timer = Stopwatch.StartNew();
            var part = target.Parent ?? throw new InvalidOperationException("Missing source Part.");
            var source = target.Sequence ?? throw new InvalidOperationException("Missing source sequence.");
            if (source.GetSamplingRate() != VSMSamplingRate._44100)
                throw new NotSupportedException("Standalone timing currently requires 44100 Hz.");
            if (part.IsAi || !string.IsNullOrEmpty(part.SecondaryVoiceBankID))
                throw new NotSupportedException("Only single-bank sampled Parts are supported by this probe.");
            if (!_banks.TryGetValue(part.VoiceBankID, out var bank))
                throw new NotSupportedException("Missing sampled bank.");
            var hintKey = ((nint)part, target.AbsPosTick.Value, velocity);
            if (_radiusHints.TryGetValue(hintKey, out int knownRadius))
            {
                var known = CaptureContext(target, knownRadius);
                int[] knownTempo = CaptureTempo(source, known.Notes[0].AbsTick,
                    known.Notes.Max(note => note.AbsTick + note.Duration));
                string knownKey = ContextKey(source, part, known.Notes, known.Target, knownTempo, velocity);
                if (_cache.TryGetValue(knownKey, out var hit))
                    return hit with { Boundaries = hit.Boundaries.ToArray(), CacheHit = true,
                        PlannerRuns = 0, Milliseconds = timer.Elapsed.TotalMilliseconds };
            }
            long[]? previous = null;
            int stable = 0, runs = 0;
            foreach (int radius in new[] { 1, 2, 4, 8, 16 })
            {
                cancellation.ThrowIfCancellationRequested();
                var context = CaptureContext(target, radius);
                int[] tempo = CaptureTempo(source, context.Notes[0].AbsTick,
                    context.Notes.Max(note => note.AbsTick + note.Duration));
                string key = ContextKey(source, part, context.Notes, context.Target, tempo, velocity);
                if (_cache.TryGetValue(key, out var cached))
                    return cached with { Boundaries = cached.Boundaries.ToArray(), CacheHit = true,
                        PlannerRuns = runs, Milliseconds = timer.Elapsed.TotalMilliseconds };
                runs++;
                long[] boundaries;
                try { boundaries = QueryWindow(source, part, context.Notes, context.Target, velocity, bank, tempo, cancellation, nativeWaitBudgetMs); }
                catch (IncompleteTimingContextException) when (context.Complete)
                {
                    // Native VSM may leave an ambiguous repeated-phoneme note
                    // without independent boundaries. Preserve that outcome.
                    boundaries = [];
                }
                catch (IncompleteTimingContextException) when (!context.Complete)
                {
                    previous = null; stable = 0; continue;
                }
                stable = previous != null && previous.SequenceEqual(boundaries) ? stable + 1 : 0;
                if (context.Complete || stable >= 2)
                {
                    cancellation.ThrowIfCancellationRequested();
                    var result = new LocalConsonantTimingResult(boundaries, context.Notes.Count, runs, false,
                        timer.Elapsed.TotalMilliseconds);
                    // Store only complete or twice-stable windows. Stability
                    // is a measured stopping rule, not a universal radius proof.
                    _cache[key] = result;
                    if (_radiusHints.Count >= 128) _radiusHints.Clear();
                    _radiusHints[hintKey] = radius;
                    _keys.Enqueue(key);
                    while (_keys.Count > 128) _cache.Remove(_keys.Dequeue());
                    return result with { Boundaries = result.Boundaries.ToArray() };
                }
                previous = boundaries;
            }
            throw new NotSupportedException("Timing context did not stabilize within 33 notes.");
        }
        finally { Monitor.Exit(_gate); }
    }

    private long[] QueryWindow(WIVSMSequence source, WIVSMMidiPart sourcePart,
        List<TimingNoteSnapshot> notes, int targetIndex, int velocity,
        (int Language, int Singer) bank, int[] tempo, CancellationToken cancellation, int nativeWaitBudgetMs)
    {
        var inputSequence = _inputSequence;
        var mappingSequence = _mappingSequence;
        var inputPart = _inputPart;
        var mappingPart = _mappingPart;
        ClearNotes(inputPart);
        ClearNotes(mappingPart);
        long partStart = sourcePart.AbsPosTick.Value;
        long[] samples = notes.Select(note => source.GetSampleFromTime(
            source.GetTimeFromTick(new VSMAbsTick(partStart), new VSMAbsTick(note.AbsTick)),
            VSMSamplingRate._44100)).ToArray();
        long originFrame = Math.Max(0, samples[0] / 256 - 86);
        if (inputPart.AbsPosTick.Value != partStart &&
            !_inputTrack.MovePart(new VSMAbsTick(partStart), _inputTrack, inputPart))
            throw new InvalidOperationException("Cannot position private timing metadata.");
        double lastEnd = notes.Select(note => source.GetTimeFromTick(new VSMAbsTick(partStart),
            new VSMAbsTick(note.AbsTick + note.Duration))).Max() - originFrame * 256d / 44100;
        if (!double.IsFinite(lastEnd) || lastEnd > 22 || samples[0] < 0)
            throw new NotSupportedException("Timing window exceeds its supported time range.");
        if (!inputPart.SetDuration(new VSMRelTick(checked((int)(notes.Max(note => note.AbsTick + note.Duration) - partStart + 480)))) ||
            !mappingPart.SetDuration(new VSMRelTick((long)Math.Ceiling((lastEnd + 2) * MappingTicksPerSecond))))
            throw new InvalidOperationException("Cannot resize private timing metadata.");
        if ((inputPart.VoiceBankID != sourcePart.VoiceBankID && !inputPart.SetVoiceBankID(sourcePart.VoiceBankID)) ||
            (mappingPart.VoiceBankID != sourcePart.VoiceBankID && !mappingPart.SetVoiceBankID(sourcePart.VoiceBankID)))
            throw new InvalidOperationException("Cannot assign private timing bank.");
        WIVSMNote? mappingTarget = null;
        var messages = new List<(int Frame, byte[] Bytes)>();
        for (int i = 0; i < notes.Count; i++)
        {
            cancellation.ThrowIfCancellationRequested();
            var note = notes[i];
            int currentVelocity = i == targetIndex ? velocity : note.Velocity;
            var inputNote = inputPart.InsertNote(new VSMRelTick(note.AbsTick - partStart),
                new VSMNoteEvent(checked((int)note.Duration), note.Pitch, currentVelocity), note.Expression,
                inputPart.GetDefaultAiNoteExpression(), "timing", note.Phonemes, true, note.Language)
                ?? throw new InvalidOperationException("Cannot insert private timing input.");
            messages.Add((checked((int)(samples[i] / 256 - originFrame)),
                ConsonantTimingInputEncoder.EncodeNote(inputNote, (int)(samples[i] % 256), tempo)));
            double beginTime = source.GetTimeFromTick(new VSMAbsTick(partStart), new VSMAbsTick(note.AbsTick))
                - originFrame * 256d / 44100;
            double endTime = source.GetTimeFromTick(new VSMAbsTick(partStart),
                new VSMAbsTick(note.AbsTick + note.Duration)) - originFrame * 256d / 44100;
            long mappingPosition = (long)Math.Round(beginTime * MappingTicksPerSecond);
            long mappingDuration = Math.Max(1, (long)Math.Round(endTime * MappingTicksPerSecond) - mappingPosition);
            var mapped = mappingPart.InsertNote(new VSMRelTick(mappingPosition),
                new VSMNoteEvent(checked((int)mappingDuration), note.Pitch, currentVelocity), note.Expression,
                mappingPart.GetDefaultAiNoteExpression(), "timing", note.Phonemes, true, note.Language)
                ?? throw new InvalidOperationException("Cannot insert private mapping note.");
            if (i == targetIndex) mappingTarget = mapped;
        }
        if (!inputSequence.Commit(false) || !mappingSequence.Commit(false))
            throw new InvalidOperationException("Cannot commit private timing metadata.");
        _engine.Begin(cancellation, nativeWaitBudgetMs);
        try
        {
            _engine.Plan(0, ConsonantTimingInputEncoder.EncodeSinger(bank.Language, bank.Singer));
            foreach (var message in messages)
            {
                cancellation.ThrowIfCancellationRequested();
                _engine.Plan(message.Frame, message.Bytes);
            }
            using var adapter = new ConsonantTimingScoreAdapter(_engine.CreateBoundaryScore());
            adapter.Map(mappingPart, [0, 750000]);
            if (_engine.CurrentFrame != 0) throw new InvalidOperationException("Query advanced synthesis frames.");
            var positions = mappingTarget!.GetPhonemePositions();
            if (positions.Count < 2) throw new IncompleteTimingContextException();
            return positions.Select(position =>
            {
                double frame = ((mappingTarget.RelPosTick.Value + position) / MappingTicksPerSecond + 0.5) * 44100 / 256;
                long recovered = (long)Math.Round(frame);
                if (Math.Abs(frame - recovered) > 0.01)
                    throw new InvalidOperationException("Boundary could not be recovered on the native frame grid.");
                double seconds = (recovered + originFrame) * 256d / 44100 - 0.5;
                return source.GetTickFromTime(new VSMAbsTick(partStart), seconds).Value - notes[targetIndex].AbsTick;
            }).ToArray();
        }
        finally { _engine.End(); }
    }

    private static void ClearNotes(WIVSMMidiPart part)
    {
        if (part.NumNotes > 33) throw new InvalidOperationException("Private note limit exceeded.");
        while (part.NumNotes != 0)
            if (!part.RemoveNote(part.GetNote(0UL)!)) throw new InvalidOperationException("Cannot clear private timing notes.");
    }

    private WIVSMSequence NewSequence()
    {
        var sequence = _manager.CreateSequence(new VSMSequenceData
        { SamplingRate = VSMSamplingRate._44100, MaxNumTracks = 4, MaxUndoCount = 0 })
            ?? throw new InvalidOperationException("Cannot create private timing metadata.");
        NativeRenderSession.RegisterInternalSequence(sequence);
        try
        {
            if (sequence.InsertTimeSig(0, new VSMTimeSigEvent(4, 4)) == null ||
                sequence.InsertTempo(VSMRelTick.Zero, 12000) == null)
                throw new InvalidOperationException("Cannot initialize private timing metadata.");
            return sequence;
        }
        catch { ClosePrivate(sequence); throw; }
    }

    internal void ClearCache()
    {
        lock (_gate) { _cache.Clear(); _keys.Clear(); _radiusHints.Clear(); InvalidatePrepared(); }
    }

    private static (List<TimingNoteSnapshot> Notes, int Target, bool Complete) CaptureContext(WIVSMNote target, int radius)
    {
        var left = new List<WIVSMNote>();
        var right = new List<WIVSMNote>();
        var current = target.Prev;
        while (current != null && left.Count < radius) { left.Add(current); current = current.Prev; }
        bool leftComplete = current == null;
        current = target.Next;
        while (current != null && right.Count < radius) { right.Add(current); current = current.Next; }
        bool rightComplete = current == null;
        left.Reverse();
        var nativeNotes = left.Append(target).Concat(right).ToArray();
        foreach (var note in nativeNotes)
        {
            if (note.HasDvqm(true) || note.HasDvqm(false))
                throw new NotSupportedException("DVQM timing inputs have not been validated.");
            // Sampled tracks do not support native SetEditedPhonemePosition.
            // Original/current scores can differ while invalidated or missing;
            // that is not a manual edit. Never compare a projected UI accessor.
            _ = NativeTimingPositions.Read(note,true);
            _ = NativeTimingPositions.Read(note,false);
        }
        var notes = nativeNotes.Select(note => new TimingNoteSnapshot(
            note.AbsPosTick.Value, note.DurationTick.Value, note.NoteNumber, note.NoteVelocity,
            note.Phonemes, note.LangID, note.GetNoteExpression())).ToList();
        if (notes.Any(note => note.Duration <= 0 || string.IsNullOrWhiteSpace(note.Phonemes) || note.Phonemes.Length > 60))
            throw new NotSupportedException("Invalid timing context note.");
        if (notes.Zip(notes.Skip(1), (a, b) => a.AbsTick + a.Duration > b.AbsTick).Any(overlap => overlap))
            throw new NotSupportedException("Overlapping timing notes have not been validated.");
        return (notes, left.Count, leftComplete && rightComplete);
    }

    private static int[] CaptureTempo(WIVSMSequence source, long begin, long end)
    {
        var first = source.GetTempo(new VSMAbsTick(begin), true, false);
        var result = new List<int> { 0, first?.Value ?? 12000 };
        var current = first?.Next;
        while (current != null && current.RelPosTick.Value <= end)
        {
            if (result.Count >= 512) throw new NotSupportedException("Timing tempo context exceeds bound.");
            result.Add(checked((int)current.RelPosTick.Value)); result.Add(current.Value);
            current = current.Next;
        }
        return result.ToArray();
    }

    private static string ContextKey(WIVSMSequence source, WIVSMMidiPart part, List<TimingNoteSnapshot> notes, int targetIndex,
        int[] tempo, int velocity)
    {
        using var stream = new MemoryStream();
        using (var writer = new BinaryWriter(stream, Encoding.UTF8, true))
        {
            writer.Write(part.VoiceBankID); writer.Write(part.AbsPosTick.Value); writer.Write(velocity); writer.Write(targetIndex);
            writer.Write(part.NumNotes);
            foreach (int value in tempo) writer.Write(value);
            writer.Write(-1);
            for (int index = 0; index < notes.Count; index++)
            {
                var note = notes[index];
                writer.Write(note.AbsTick); writer.Write(note.Duration); writer.Write(note.Pitch);
                writer.Write(index == targetIndex ? velocity : note.Velocity);
                // Earlier tempo edits change the physical frame phase even
                // when the tempo inside this window stays the same.
                writer.Write(source.GetTimeFromTick(part.AbsPosTick, new VSMAbsTick(note.AbsTick)));
                writer.Write(source.GetTimeFromTick(part.AbsPosTick, new VSMAbsTick(note.AbsTick + note.Duration)));
                writer.Write(note.Phonemes); writer.Write(note.Language);
                writer.Write(note.Expression.Accent); writer.Write(note.Expression.Decay);
                writer.Write(note.Expression.BendDepth); writer.Write(note.Expression.BendLength);
                writer.Write(note.Expression.Opening); writer.Write(note.Expression.RisePort); writer.Write(note.Expression.FallPort);
            }
        }
        return Convert.ToHexString(SHA256.HashData(stream.ToArray()));
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            InvalidatePrepared();
            _cache.Clear();
            _keys.Clear();
            _radiusHints.Clear();
            try { ClosePrivate(_mappingSequence); }
            finally
            {
                try { ClosePrivate(_inputSequence); }
                finally { _engine.Dispose(); }
            }
        }
    }

    private static void ClosePrivate(WIVSMSequence? sequence)
    {
        if (sequence == null) return;
        nint pointer = (nint)sequence;
        try { sequence.Close(); }
        finally { if (!sequence.IsOpen) NativeRenderSession.UnregisterInternalSequence(pointer); }
    }
}

internal sealed record TimingNoteSnapshot(long AbsTick, long Duration, int Pitch, int Velocity,
    string Phonemes, int Language, VSMNoteExpression Expression);

internal sealed class IncompleteTimingContextException : InvalidOperationException
{
    internal IncompleteTimingContextException() : base("No target boundaries in timing context.") { }
}
