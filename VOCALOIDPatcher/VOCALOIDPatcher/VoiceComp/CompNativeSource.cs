using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using VOCALOIDPatcher.BreathVolume;
using VOCALOIDPatcher.Config;
using VOCALOIDPatcher.ConsonantTiming;
using VOCALOIDPatcher.ConsonantTiming.Smoothing;
using VOCALOIDPatcher.Evec;
using VOCALOIDPatcher.Patch.Patches;
using VOCALOIDPatcher.PhonemeVolume;
using VOCALOIDPatcher.Utils;
using Yamaha.VOCALOID;
using Yamaha.VOCALOID.VSM;

namespace VOCALOIDPatcher.VoiceComp;

internal sealed record CompSourceNote(int Index, string Phonemes, long[] Boundaries, long NoteBegin, long NoteEnd, int VowelIndex = -1);
internal sealed record CompSourceAudio(string Path, int Rate, long Samples, CompSourceNote[] Notes, float[] Peaks);
internal sealed record CompPhonemeSpan(int NoteIndex, string Phoneme, long Begin, long End);
internal sealed class CompNativeCloseBusyException : Exception;

internal sealed class CompNativeSource : IDisposable
{
    internal WIVSMSequence Sequence { get; }
    internal WIVSMMidiPart Part { get; }
    private readonly List<(int Index, WIVSMNote Original, WIVSMNote Copy)> _notes;
    private readonly Dictionary<int, (long Begin, long End)> _ownership = new();
    private readonly Dictionary<int, (string Phones, float[] Gains, byte Breath)> _ownerControls = new();
    private readonly Dictionary<nint, EvecNoteState> _evec = new();
    private bool _started, _disposed;
    private readonly bool _anchorsOnly;

    internal static T Ui<T>(Func<T> action) => Application.Current?.Dispatcher is { } dispatcher && !dispatcher.CheckAccess()
        ? dispatcher.Invoke(action) : action();

    internal CompNativeSource(WIVSMSequenceManager manager, WIVSMMidiPart original,
        int[] indices, SourceRecipe? recipe = null, bool extendEnd = false, long controllerShiftSamples = 0, double holdOffsetMs = 0, bool anchorsOnly = false,
        double holdLengthMs = 0)
    {
        _anchorsOnly = anchorsOnly;
        var source = original.Sequence ?? throw new InvalidOperationException("Missing source sequence.");
        if (!SegmentedPhonemeRenderCoordinator.TryEnterCompSource()) throw new CompNativeCloseBusyException();
        try
        {
            Sequence = manager.CreateSequence(new VSMSequenceData { SamplingRate = source.GetSamplingRate(), MaxNumTracks = 4, MaxUndoCount = 0 })
                ?? throw new InvalidOperationException("Cannot create private voice-comp source.");
        }
        catch { SegmentedPhonemeRenderCoordinator.ExitCompSource(); throw; }
        NativeRenderSession.RegisterInternalSequence(Sequence);
        Sequence.StopAsyncRendering();
        try
        {
            bool sourceLengthChanged = false;
            Sequence.IsGlobalTempoEnabled = source.IsGlobalTempoEnabled; Sequence.GlobalTempo = source.GlobalTempo;
            Sequence.SetMainTuning(source.MainTuning);
            foreach (var tempo in source.Tempos)
                if (Sequence.DuplicateTempo(tempo.RelPosTick, tempo) == null) throw new InvalidOperationException("Cannot copy tempo map.");
            foreach (var signature in source.TimeSigs)
                if (Sequence.DuplicateTimeSig(signature.PosBar, signature) == null) throw new InvalidOperationException("Cannot copy time signatures.");
            var track = Sequence.InsertTrackEx(0, VSMTrackType.Midi, "Voice comp") as WIVSMMidiTrack
                ?? throw new InvalidOperationException("Cannot create private voice-comp track.");
            Part = track.DuplicatePart(original.AbsPosTick, original) ?? throw new InvalidOperationException("Cannot copy source Part.");
            var originals = original.Notes.ToArray(); var copies = Part.Notes.ToArray();
            _notes = indices.Select(i => (i, originals[i], copies[i])).ToList();
            foreach (var pair in _notes)
            {
                var volume = PhonemeVolumeHost.Get(pair.Original);
                _ownerControls[pair.Index] = (volume.Topology, volume.Values.ToArray(), BreathVolumeService.GetValue(pair.Original));
                _evec[(nint)pair.Copy] = recipe?.Evec is { } state ? new(state.Color, state.Attack, state.Release, state.Extension) : EvecService.GetState(pair.Original);
            }
            var keep = indices.ToHashSet();
            for (int i = copies.Length - 1; i >= 0; i--)
                if (!keep.Contains(i) && !Part.RemoveNote(copies[i])) throw new InvalidOperationException("Cannot isolate source context.");
            if (recipe != null || extendEnd)
            {
                var note = _notes[^1].Copy;
                double duration = source.GetTimeFromTick(note.AbsPosTick, note.AbsEndTick) * 1000;
                double generated = recipe?.Duration(duration) ?? new ContextBreak(EndOffsetMs: holdOffsetMs, SourceLengthMs: holdLengthMs).Duration(duration);
                long end = source.GetTickFromTime(note.AbsPosTick, generated / 1000).Value;
                sourceLengthChanged = end - note.AbsPosTick.Value != note.DurationTick.Value;
                if (!Part.SetDuration(new(Math.Max(Part.DurationTick.Value, end - Part.AbsPosTick.Value + 480))) ||
                    !note.SetDuration(new(end - note.AbsPosTick.Value))) throw new InvalidOperationException("Cannot extend source note.");
                if (recipe?.Lyric is { } lyric)
                {
                    if (recipe.Phonemes == null)
                    { if (!note.SetLyricsAndResetPhonemes(lyric, note.LangID)) throw new InvalidOperationException("Cannot pronounce source lyrics."); }
                    else note.Lyric = lyric;
                }
                if (recipe?.Phonemes is { } phones && !note.SetPhonemes(phones, true, note.LangID))
                    throw new InvalidOperationException("Cannot set source pronunciation.");
                if (recipe?.Expression is { } expression)
                {
                    note.NoteVelocity = expression.Velocity;
                    if (!note.SetNoteExpression(new(expression.Accent, expression.Decay, expression.BendDepth,
                        expression.BendLength, expression.Opening, expression.RisePort, expression.FallPort)))
                        throw new InvalidOperationException("Cannot restore donor expression snapshot.");
                }
            }
            if (recipe?.Context is { } context) AddContext(context);
            if (controllerShiftSamples != 0 || (recipe != null || extendEnd) && _notes.Any(n => n.Original.VibratoDurationTick.Value > 0))
                ShiftControllers(source, original, controllerShiftSamples);
            foreach (var note in _notes)
            {
                BreathVolumeService.CopyNoteValue(note.Original, note.Copy);
                RegisterShift.RegisterShiftService.CopyNoteValue(note.Original, note.Copy);
            }
            foreach (var note in _notes) _ownership[note.Index] = (note.Copy.RelPosTick.Value, note.Copy.RelEndTick.Value);
            EvecRenderCopy.ApplyOwnedPart(Part, Settings.EvecEnabled, n => _evec.GetValueOrDefault((nint)n) ?? EvecNoteState.Empty);
            foreach (var owner in _notes)
                foreach (var child in Part.Notes.Where(n => n.RelPosTick.Value >= _ownership[owner.Index].Begin && n.RelPosTick.Value < _ownership[owner.Index].End))
                    RegisterShift.RegisterShiftService.CopyNoteValue(owner.Original, child);
            if (!Sequence.Commit(false)) throw new InvalidOperationException("Cannot commit isolated source.");
            // EVEC can create extra native events. Bind unchanged semantic notes;
            // generated EVEC events retain their own native expression values.
            SampledTimingHost.BindCompSource(source, Sequence,
                _notes.SelectMany(n => Part.Notes.Where(child => child.RelPosTick.Value >= _ownership[n.Index].Begin &&
                    child.RelPosTick.Value < _ownership[n.Index].End).Select(child => (n.Original, child))).ToArray(),
                recipe != null || sourceLengthChanged ? _notes[^1].Original : null);
            // DuplicatePart can inherit a completed native renderer state. A
            // semantically unchanged baseline still needs a render request so
            // VSM hydrates its cached score and emits completion for this copy.
            SampledTimingVsmBinding.InvalidateRenderedPart(Part, [Part.GetNote(0UL)!]);
        }
        catch { Dispose(); throw; }
    }

    private void AddContext(CompSourceContext context)
    {
        var note = _notes.Single().Copy;
        if (context.Before != null)
        {
            long before = Sequence.GetTickFromTime(note.AbsPosTick, -context.NoteMs / 1000).Value - Part.AbsPosTick.Value;
            if (before < 0)
            {
                long shift = -before;
                if (!Part.SetDuration(new(Part.DurationTick.Value + shift)) || !Part.MoveNote(new(note.RelPosTick.Value + shift), note))
                    throw new InvalidOperationException("Cannot create source context pre-roll.");
                before = 0;
            }
            Insert(before, note.RelPosTick.Value - before, context.Before);
        }
        if (context.After != null)
        {
            long end = Sequence.GetTickFromTime(note.AbsEndTick, context.NoteMs / 1000).Value - Part.AbsPosTick.Value;
            if (!Part.SetDuration(new(Math.Max(Part.DurationTick.Value, end + 480)))) throw new InvalidOperationException("Cannot extend context.");
            Insert(note.RelEndTick.Value, end - note.RelEndTick.Value, context.After);
        }
        void Insert(long position, long duration, string phones)
        {
            if (Part.InsertNote(new(position), new VSMNoteEvent(checked((int)duration), note.NoteNumber, note.NoteVelocity),
                Part.GetDefaultNoteExpression(), Part.GetDefaultAiNoteExpression(), "context", phones, true, note.LangID) == null)
                throw new InvalidOperationException("Cannot create pronunciation context.");
        }
    }

    private void ShiftControllers(WIVSMSequence source, WIVSMMidiPart original, long shift)
    {
        double seconds = shift / (double)(int)source.GetSamplingRate();
        foreach (var type in Enum.GetValues<VSMControllerType>())
        {
            var points = Part.GetControlParametersEnumerator(type).ToArray();
            if (points.Length == 0) continue;
            var shifted = points.Select(p =>
            {
                double time = source.GetTimeFromTick(original.AbsBeginTick,
                    new VSMAbsTick(original.AbsBeginTick.Value + p.RelPosTick.Value)) - seconds;
                long tick = source.GetTickFromTime(original.AbsBeginTick, time).Value - original.AbsBeginTick.Value;
                return (Tick: tick, p.Value);
            }).ToArray();
            foreach (var point in points) if (!Part.RemoveController(point)) throw new InvalidOperationException("Cannot remap source controller.");
            var values = new SortedDictionary<long, int>();
            foreach (var point in shifted) values[Math.Max(0, point.Tick)] = point.Value;
            foreach (var point in values)
                if (Part.InsertController(new VSMRelTick(point.Key), type, point.Value) == null) throw new InvalidOperationException("Cannot follow main-track controllers.");
        }
        foreach (var pair in _notes)
        {
            if (shift != 0 && pair.Original.NumDirectPitches > 0)
            {
                if (!pair.Copy.ClearDirectPitches(WIVSMNote.DirectPitchClearingBeginTick, WIVSMNote.DirectPitchClearingEndTick))
                    throw new InvalidOperationException("Cannot remap source pitch curve.");
                foreach (var point in pair.Original.DirectPitches)
                {
                    double pointTime = source.GetTimeFromTick(original.AbsBeginTick, pair.Original.GetAbsPositionFromNoteBaseTick(point.Tick)) - seconds;
                    long absolute = source.GetTickFromTime(original.AbsBeginTick, pointTime).Value;
                    if (!pair.Copy.SetDirectPitch(pair.Copy.GetNoteBaseTickFromAbsPosition(new(absolute)), point.Value))
                        throw new InvalidOperationException("Cannot follow main-note pitch curve.");
                }
            }
            if (pair.Original.VibratoDurationTick.Value <= 0) continue;
            var originalStart = new VSMAbsTick(pair.Original.AbsEndTick.Value - pair.Original.VibratoDurationTick.Value);
            double time = source.GetTimeFromTick(original.AbsBeginTick, originalStart) - seconds;
            long begin = source.GetTickFromTime(original.AbsBeginTick, time).Value;
            long duration = Math.Clamp(pair.Copy.AbsEndTick.Value - begin, 0, pair.Copy.DurationTick.Value);
            if (!pair.Copy.SetVibratoDuration(new VSMRelTick(duration))) throw new InvalidOperationException("Cannot follow main-note vibrato.");
        }
    }

    internal async Task<CompSourceAudio> RenderAsync(string path, CancellationToken token)
    {
        Ui(() =>
        {
            // A completed Chinese source may otherwise reuse its cached PCM
            // before the native Start path gets a chance to reject this fault.
            SampledTimingHost.ThrowIfCompRenderFailed(Sequence);
            BreathVolumeService.StartRender(Sequence, Part); return true;
        });
        SegmentedPhonemeRenderCoordinator.OwnedCompAudio? chinese = null;
        if (Settings.ExtendedChinesePinyin)
            chinese = await Task.Run(() => SegmentedPhonemeRenderCoordinator.RenderOwnedCompSource(Sequence, Part, token), token).ConfigureAwait(false);
        if (chinese != null) File.Copy(chinese.Path, path, true);
        else
        {
            await NativeRenderSession.Gate.WaitAsync(token).ConfigureAwait(false);
            WIVSMRendererObserver? observer = null; int status = 0; string canceled = "";
            try
            {
                Ui(() =>
                {
                    // Construction commits can enqueue native cache notices.
                    // Discard them before observing this explicit request.
                    Sequence.StopAsyncRendering();
                    for (int i = 0; i < 256 && Sequence.InvokeRendererObserver(); i++) { }
                    observer = new WIVSMRendererObserver();
                    observer.Completed += (_, e) => { if ((nint)e.MidiPart == (nint)Part) Volatile.Write(ref status, 1); };
                    observer.Canceled += (_, e) => { if ((nint)e.MidiPart == (nint)Part) { canceled = e.CancelReason.ToString(); Volatile.Write(ref status, -1); } };
                    if (!Sequence.AddRendererObserver(observer)) throw new InvalidOperationException("Cannot observe isolated source.");
                    RegisterShift.RegisterShiftService.PublishBeforeRendering(Sequence);
                    Sequence.BlockRenderingEnabled = false; return true;
                });
                // Prepare before entering the Start prefix: that prefix must
                // reject an unprepared private source, but transient contention
                // belongs to this cancellable async owner, not a permanent fault.
                var preparationRetry = new TimingPublicationRetryBudget();
                while (true)
                {
                    token.ThrowIfCancellationRequested();
                    try
                    {
                        Ui(() =>
                        {
                            token.ThrowIfCancellationRequested();
                            SampledTimingHost.BeforeRendering(Sequence);
                            Sequence.StartAsyncRendering(); _started = true;
                            SampledTimingHost.ThrowIfCompRenderFailed(Sequence); return true;
                        });
                        break;
                    }
                    catch (TimingPlannerBusyException)
                    {
                        if (!preparationRetry.TrySchedule(0, Environment.TickCount64, out int delay)) throw;
                        await Task.Delay(delay, token).ConfigureAwait(false);
                    }
                }
                var timer = Stopwatch.StartNew();
                while (Volatile.Read(ref status) == 0)
                {
                    token.ThrowIfCancellationRequested();
                    if (timer.Elapsed > TimeSpan.FromSeconds(90)) throw new TimeoutException("Voice-comp source timed out.");
                    Ui(() => { for (int i = 0; i < 8 && Sequence.InvokeRendererObserver(); i++) { } SampledTimingHost.ThrowIfCompRenderFailed(Sequence); return true; });
                    if (Ui(() => Sequence.IsFinishedRendering && Part.HasValidRenderedWave && Part.HasValidRenderedScore &&
                        Part.Notes.All(n => n.GetPhonemePositions() is { Count: > 1 } positions && !positions.Contains(int.MinValue))))
                        Volatile.Write(ref status, 1);
                    await Task.Delay(15, token).ConfigureAwait(false);
                }
                if (status != 1) throw new InvalidOperationException("Voice-comp source render canceled: " + canceled);
                string input = Ui(() => Part.WaveFilePath);
                File.Copy(input, path, true);
            }
            finally
            {
                Ui(() => { Sequence.StopAsyncRendering(); if (observer != null) { Sequence.RemoveRendererObserver(observer); observer.Dispose(); } return true; });
                NativeRenderSession.Gate.Release();
            }
        }
        try
        {
            var data = Ui(() => { token.ThrowIfCancellationRequested(); return Capture(path, chinese?.Spans); });
            if (data.Breath.Length > 0)
            {
                string adjusted = path + ".breath.wav"; BreathWaveProcessor.CreateAdjustedWave(path, adjusted, data.Breath); File.Move(adjusted, path, true);
            }
            if (data.Gains.Length > 0)
            {
                string adjusted = path + ".gain.wav"; PhonemeGainWaveProcessor.Create(path, adjusted, data.Gains);
                File.Move(adjusted, path, true);
            }
            var junctions = Ui(() =>
            {
                var plan = SampledTimingHost.TransitionView(Part);
                return Settings.TransitionSmoothingEnabled && plan != null ?
                    TransitionSmoothingHost.CollectJunctions(Part, plan, Settings.GetTransitionSmoothingConfig()) : null;
            });
            if (junctions?.Count > 0)
            {
                string adjusted = path + ".smooth.wav";
                WaveJunctionProcessor.ProcessWaveFile(path, adjusted, junctions, Ui(Settings.GetTransitionSmoothingConfig));
                File.Move(adjusted, path, true);
            }
            return new(path, data.Rate, CompWave.Inspect(path).Frames, data.Notes, CompWave.Peaks(path));
        }
        catch
        {
#if PATCHER_ISOLATED_VALIDATION
            if (Environment.GetEnvironmentVariable("VOCALOID_COMP_FAILURE_DIRECTORY") is { } diagnostic)
            {
                Directory.CreateDirectory(diagnostic);
                File.Copy(path, Path.Combine(diagnostic, "failed-source.wav"), true);
                File.Copy(Ui(() => Part.ScoreFilePath), Path.Combine(diagnostic, "failed-source.score"), true);
            }
#endif
            throw;
        }
        finally { if (chinese != null) File.Delete(chinese.Path); }
    }

    private (int Rate, CompSourceNote[] Notes, PhonemeGainRegion[] Gains, BreathGainRegion[] Breath) Capture(string path,
        Dictionary<int, SegmentedPhonemeRenderCoordinator.WavePhonemeSpan[]>? overrides)
    {
        var format = CompWave.Inspect(path); var notes = new List<CompSourceNote>(); var gains = new List<PhonemeGainRegion>();
        long Sample(WIVSMNote note, int tick) => (long)Math.Round((Sequence.PresendTimeSec +
            Sequence.GetTimeFromTick(Part.AbsBeginTick, note.GetAbsPositionFromNoteBaseTick(tick))) * format.Rate);
        var rendered = Part.Notes.ToArray();
        var breath = new List<BreathGainRegion>();
        if (Settings.IndividualBreathVolume && _ownerControls.Values.Any(n => n.Breath != BreathVolumeService.DefaultValue))
        {
            BreathVolumeService.RefreshOwnedRenderRegions(Sequence, Part);
            if (BreathVolumeService.GetRegionStatus(Part) == BreathRegionStatus.Faulted)
                throw new InvalidOperationException("Breath source regions are not ready.");
            foreach (var region in BreathVolumeService.GetRegions(Part))
            {
                var owner = _notes.FirstOrDefault(n => rendered.Any(r => r.CppObjPtr == region.NoteHandle &&
                    r.RelPosTick.Value >= _ownership[n.Index].Begin && r.RelPosTick.Value < _ownership[n.Index].End));
                if (owner.Original != null) breath.Add(new(region.BeginSample, region.EndSample, _ownerControls[owner.Index].Breath));
            }
        }
        foreach (var pair in _notes)
        {
            var spans = new List<(string Phone, long Begin, long End, bool Vowel)>();
            for (int child = 0; child < rendered.Length; child++)
            {
                var note = rendered[child]; var range = _ownership[pair.Index];
                if (note.RelPosTick.Value < range.Begin || note.RelPosTick.Value >= range.End) continue;
                if (overrides?.TryGetValue(child, out var custom) == true)
                {
                    long At(long tick) => (long)Math.Round((Sequence.PresendTimeSec + Sequence.GetTimeFromTick(Part.AbsBeginTick,
                        new VSMAbsTick(Part.AbsPosTick.Value + tick))) * format.Rate);
                    spans.AddRange(custom.Select(s => (s.Phoneme, At(s.StartRelTick), At(s.EndRelTick), note.IsVowel(s.Phoneme))));
                }
                else
                {
                    var positions = SampledTimingHost.VerifiedPositions(note) ?? note.GetPhonemePositions(); var tokens = note.Phonemes.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                    // A connected native phrase may emit no separate vector
                    // for an absorbed note. This pass supplies anchors only;
                    // its PCM is never used as an output stem.
                    if (_anchorsOnly && positions.Count == 0) continue;
                    if (positions.Count != tokens.Length + 1 || positions.Contains(int.MinValue))
                        throw new InvalidOperationException("Source phoneme boundaries are not ready: note=" + pair.Index + ", count=" + positions.Count +
                            ", positions=" + string.Join(",", positions) + ", finished=" + Sequence.IsFinishedRendering + ", score=" + Part.HasValidRenderedScore +
                            ", phones=" + note.Phonemes + ", all=" + string.Join(";", rendered.Select(n => n.Phonemes + ":" + string.Join(",", n.GetPhonemePositions()))) +
                            ", path=" + path + ", timing=" + System.Text.Json.JsonSerializer.Serialize(SampledTimingHost.Diagnostics(Sequence)));
                    for (int p = 0; p < tokens.Length; p++) spans.Add((tokens[p], Sample(note, positions[p]), Sample(note, positions[p + 1]), note.IsVowel(tokens[p])));
                }
            }
            if (_anchorsOnly && spans.Count == 0) continue;
            if (spans.Count == 0 || spans.Any(s => s.End < s.Begin)) throw new InvalidOperationException("Source boundaries are invalid.");
            string[] phones = spans.Select(s => s.Phone).ToArray(); long[] boundaries = spans.Select(s => s.Begin).Append(spans[^1].End).ToArray();
            var ownerRange = _ownership[pair.Index];
            long AtTick(long tick) => (long)Math.Round((Sequence.PresendTimeSec + Sequence.GetTimeFromTick(Part.AbsBeginTick, new VSMAbsTick(Part.AbsPosTick.Value + tick))) * format.Rate);
            notes.Add(new(pair.Index, string.Join(" ", phones), boundaries, AtTick(ownerRange.Begin), AtTick(ownerRange.End), spans.FindIndex(s => s.Vowel)));
            var volume = _ownerControls[pair.Index];
            for (int p = 0; p < phones.Length; p++)
            {
                var originalPhones = volume.Phones.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                int originalIndex = p < originalPhones.Length && originalPhones[p] == phones[p] ? p : Array.FindIndex(originalPhones, s => s.Split('#')[0] == phones[p].Split('#')[0]);
                float gain = Settings.IndividualPhonemeVolume && originalIndex >= 0 ? volume.Gains[originalIndex] : 0;
                if (gain != 0 && spans[p].End > spans[p].Begin)
                    gains.Add(new(spans[p].Begin / (double)format.Rate, spans[p].End / (double)format.Rate, gain));
            }
        }
        return (format.Rate, notes.ToArray(), gains.ToArray(), breath.ToArray());
    }

    public void Dispose()
    {
        if (_disposed) return; _disposed = true;
        try { Ui(() =>
        {
            if (_started) Sequence.StopAsyncRendering();
            SegmentedPhonemeRenderCoordinator.CloseSequenceSafely(Sequence); return true;
        }); }
        finally { SegmentedPhonemeRenderCoordinator.ExitCompSource(); }
    }
}
