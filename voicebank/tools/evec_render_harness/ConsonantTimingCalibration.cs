using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using VOCALOIDPatcher.Utils;
using Yamaha.VOCALOID.VSM;

namespace VOCALOIDPatcher.ConsonantTiming;

/// <summary>Offline engine validation only. Never compile into the Editor patch.</summary>
internal sealed class ConsonantTimingCalibration : IDisposable
{
    private readonly WIVSMSequenceManager _manager;
    private readonly WIVSMNote _source;
    private readonly int _initialVelocity;
    private readonly long[] _initialBoundaries;
    private readonly Func<bool> _isCurrent;
    private readonly Action<WIVSMSequence> _close;
    private readonly Dictionary<int, ConsonantTimingSample> _samples = new();
    private WIVSMSequence? _sequence;
    private WIVSMMidiPart? _part;
    private WIVSMNote? _note;
    private string? _sourceWave;
    private string? _sourceScore;
    private bool _disposed;

    internal ConsonantTimingCalibration(WIVSMSequenceManager manager, WIVSMNote source,
        int initialVelocity, long[] initialBoundaries, Func<bool> isCurrent, Action<WIVSMSequence> close)
    {
        _manager = manager;
        _source = source;
        _initialVelocity = initialVelocity;
        _initialBoundaries = initialBoundaries;
        _isCurrent = isCurrent;
        _close = close;
    }

    private void CheckCurrent()
    {
        if (_disposed || !_isCurrent()) throw new OperationCanceledException();
    }

    private void CreateCopy()
    {
        CheckCurrent();
        var sourcePart = _source.Parent ?? throw new InvalidOperationException("Missing source Part.");
        var sourceTrack = sourcePart.Parent ?? throw new InvalidOperationException("Missing source track.");
        var sourceSequence = sourcePart.Sequence ?? throw new InvalidOperationException("Missing sequence.");
        if (!sourceSequence.IsOpen || sourceSequence.IsStaged || !sourcePart.HasValidRenderedScore)
            throw new InvalidOperationException("Source timing is not rendered.");
        _sourceWave = sourcePart.WaveFilePath;
        _sourceScore = sourcePart.ScoreFilePath;
        var tracks = sourceSequence.MidiTracks.ToArray();
        int trackIndex = Array.FindIndex(tracks, t => (nint)t == (nint)sourceTrack);
        int partIndex = trackIndex < 0 ? -1 : Array.FindIndex(tracks[trackIndex].MidiParts.ToArray(),
            p => (nint)p == (nint)sourcePart);
        int noteIndex = Array.FindIndex(sourcePart.Notes.ToArray(), n => (nint)n == (nint)_source);
        if (trackIndex < 0 || partIndex < 0 || noteIndex < 0)
            throw new InvalidOperationException("Missing source note.");
        _sequence = _manager.DuplicateSequence(sourceSequence, new VSMSequenceData
        {
            SamplingRate = sourceSequence.GetSamplingRate(),
            MaxNumTracks = Math.Max(4UL, sourceSequence.NumTrack), MaxUndoCount = 0,
        }) ?? throw new InvalidOperationException("Cannot duplicate sequence.");
        NativeRenderSession.RegisterInternalSequence(_sequence);
        var track = _sequence.MidiTracks.ToArray()[trackIndex];
        _part = track.MidiParts.ToArray()[partIndex];
        _note = _part.Notes.ToArray()[noteIndex];
        // Other Parts do not participate in this Part's native phoneme context.
        // Keep every note, controller, tempo and singer change inside the target Part.
        foreach (var other in _sequence.Tracks.ToArray())
            if ((nint)other != (nint)track && !_sequence.RemoveTrack(other))
                throw new InvalidOperationException("Cannot isolate timing Part.");
        foreach (var other in track.MidiParts.ToArray())
            if ((nint)other != (nint)_part && !track.RemovePart(other))
                throw new InvalidOperationException("Cannot isolate timing Part.");
    }

    internal async Task<ConsonantTimingSample> MeasureAsync(int velocity)
    {
        CheckCurrent();
        if (_samples.TryGetValue(velocity, out var cached)) return cached;
        while (!await NativeRenderSession.Gate.WaitAsync(50)) CheckCurrent();
        WIVSMRendererObserver? observer = null;
        bool started = false;
        try
        {
            CheckCurrent();
            if (_sequence == null) CreateCopy();
            var sequence = _sequence!;
            var part = _part!;
            var note = _note!;
            note.NoteVelocity = velocity;
            if (note.NoteVelocity != velocity || !sequence.Commit(false))
                throw new InvalidOperationException("Cannot stage timing probe.");
            if ((!string.IsNullOrEmpty(_sourceWave) && string.Equals(part.WaveFilePath, _sourceWave, StringComparison.OrdinalIgnoreCase)) ||
                (!string.IsNullOrEmpty(_sourceScore) && string.Equals(part.ScoreFilePath, _sourceScore, StringComparison.OrdinalIgnoreCase)))
                throw new InvalidOperationException("Timing copy shares the source cache.");
            int status = 0;
            observer = new WIVSMRendererObserver();
            observer.Completed += (_, args) => { if ((nint)args.MidiPart == (nint)part) status = 1; };
            observer.Canceled += (_, args) => { if ((nint)args.MidiPart == (nint)part) status = -1; };
            if (!sequence.AddRendererObserver(observer)) throw new InvalidOperationException("Cannot observe timing probe.");
            sequence.BlockRenderingEnabled = false;
            sequence.StartAsyncRendering();
            started = true;
            var timer = Stopwatch.StartNew();
            while (status == 0)
            {
                CheckCurrent();
                if (timer.Elapsed > TimeSpan.FromSeconds(30)) throw new TimeoutException("Timing probe timed out.");
                for (int count = 0; count < 8 && sequence.InvokeRendererObserver(); count++) { }
                if (status == 0) await Task.Delay(10);
            }
            CheckCurrent();
            if (status != 1 || !part.HasValidRenderedScore) throw new InvalidOperationException("Timing probe canceled.");
            var positions = note.GetPhonemePositions();
            if (positions.Count != _initialBoundaries.Length || positions.Contains(int.MinValue))
                throw new InvalidOperationException("Unavailable engine boundaries.");
            var boundaries = positions.Select(t => note.GetAbsPositionFromNoteBaseTick(t).Value).ToArray();
            if (boundaries.Zip(boundaries.Skip(1), (a, b) => a > b).Any(x => x))
                throw new InvalidOperationException("Invalid engine boundaries.");
            // A copy that does not reproduce the currently heard context is not a predictor.
            if (velocity == _initialVelocity && !boundaries.SequenceEqual(_initialBoundaries))
                throw new InvalidOperationException("Timing copy differs from source render.");
            var sample = new ConsonantTimingSample(velocity, boundaries);
            _samples.Add(velocity, sample);
            return sample;
        }
        finally
        {
            try { if (started) _sequence?.StopAsyncRendering(); }
            finally
            {
                try { if (observer != null && _sequence != null) _sequence.RemoveRendererObserver(observer); }
                finally { observer?.Dispose(); NativeRenderSession.Gate.Release(); }
            }
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_sequence == null) return;
        _close(_sequence); // The host's shared close window owns safe native teardown.
        _sequence = null;
        _part = null;
        _note = null;
    }
}
