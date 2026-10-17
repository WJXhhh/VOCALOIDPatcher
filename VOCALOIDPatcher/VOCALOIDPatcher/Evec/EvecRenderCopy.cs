using System;
using System.Collections.Generic;
using System.Linq;
using Yamaha.VOCALOID.VSM;

namespace VOCALOIDPatcher.Evec;

/// <summary>
/// Owns an isolated sequence with PPS-style articulation events. Callers must
/// render and dispose it; it is never substituted for the user's document.
/// The editor's asynchronous publication/cancellation path is not wired yet.
/// </summary>
internal sealed class EvecRenderCopy : IDisposable
{
    internal WIVSMSequence Sequence { get; }
    internal WIVSMMidiPart Part { get; }
    private bool _disposed;

    private EvecRenderCopy(WIVSMSequence sequence, WIVSMMidiPart part)
    {
        Sequence = sequence;
        Part = part;
    }

    internal static bool TryCreate(WIVSMSequenceManager manager, WIVSMMidiPart source,
        EvecVoicebankCapabilities capabilities, Func<WIVSMNote, EvecNoteState> getState,
        out EvecRenderCopy? copy, out string? error)
    {
        copy = null;
        error = null;
        WIVSMSequence? duplicate = null;
        try
        {
            var sequence = source.Sequence;
            if (source.IsAi || sequence == null || !sequence.IsOpen || sequence.IsStaged)
                return false;
            var tracks = sequence.MidiTracks.ToArray();
            int trackIndex = -1;
            int partIndex = -1;
            for (int index = 0; index < tracks.Length && partIndex < 0; index++)
            {
                partIndex = Array.FindIndex(tracks[index].MidiParts.ToArray(),
                    part => (IntPtr)part == (IntPtr)source);
                if (partIndex >= 0) trackIndex = index;
            }
            if (trackIndex < 0) return false;

            var notes = source.Notes.ToArray();
            if (!capabilities.IsSupported) return false;
            var plans = new List<(int Index, IReadOnlyList<EvecRenderSegment> Segments, long[] Ends)>();
            for (int index = 0; index < notes.Length; index++)
            {
                var note = notes[index];
                var state = getState(note);
                if (!state.HasAnyEvec) continue;
                var supported = capabilities.Normalize(note.Phonemes, state);
                if (note.LangID != 0 || supported.VoiceColorId != state.VoiceColorId ||
                    supported.AttackId != state.AttackId || supported.ReleaseId != state.ReleaseId ||
                    supported.ConsonantExtension != state.ConsonantExtension)
                    throw new InvalidOperationException("EVEC options are incompatible with this voicebank or language.");
                double duration = sequence.GetTimeFromTick(note.AbsPosTick, note.AbsEndTick) * 1000;
                var next = index + 1 < notes.Length ? notes[index + 1] : null;
                double? gap = next == null ? null :
                    sequence.GetTimeFromTick(note.AbsEndTick, next.AbsPosTick) * 1000;
                if (!EvecRenderTimingPlan.TryCreate(note.Phonemes, state, duration, gap,
                        next != null && next.LangID == note.LangID, out var segments))
                    throw new InvalidOperationException("Unsupported EVEC phoneme layout.");
                var ends = new long[segments.Count];
                long previous = note.RelPosTick.Value;
                for (int segment = 0; segment < segments.Count; segment++)
                {
                    ends[segment] = segment == segments.Count - 1
                        ? checked(note.RelPosTick.Value + note.DurationTick.Value)
                        : (sequence.GetTickFromTime(note.AbsPosTick, segments[segment].EndMs / 1000)
                           - source.AbsPosTick).Value;
                    if (ends[segment] <= previous)
                        throw new InvalidOperationException("EVEC segment collapsed at tick resolution.");
                    previous = ends[segment];
                }
                plans.Add((index, segments, ends));
            }
            if (plans.Count == 0) return false;

            duplicate = manager.DuplicateSequence(sequence, new VSMSequenceData
            {
                SamplingRate = sequence.GetSamplingRate(),
                MaxNumTracks = Math.Max(4UL, sequence.NumTrack),
                MaxUndoCount = 0,
            });
            if (duplicate == null || !duplicate.IsOpen)
                throw new InvalidOperationException("Cannot create EVEC render copy.");
            // Register before the first Commit: every trigger patch must already
            // know that this sequence is a patch-owned copy, never a user document.
            Utils.NativeRenderSession.RegisterInternalSequence(duplicate);
            var target = duplicate.MidiTracks.ToArray()[trackIndex].MidiParts.ToArray()[partIndex];
            var targets = target.Notes.ToArray();
            foreach (var plan in plans)
            {
                var current = targets[plan.Index];
                var expression = current.GetNoteExpression();
                // DivideNote keeps the native vibrato/controller split behavior;
                // rebuilding bare notes would discard authored expression data.
                for (int segment = 0; segment < plan.Segments.Count; segment++)
                {
                    current.IsProtected = false;
                    WIVSMNote? following = null;
                    if (segment + 1 < plan.Segments.Count)
                    {
                        following = target.DivideNote(current, new VSMRelTick(plan.Ends[segment]));
                        if (following == null)
                            throw new InvalidOperationException("Cannot divide EVEC render event.");
                    }
                    if (!current.SetPhonemes(plan.Segments[segment].Phonemes, true, current.LangID))
                        throw new InvalidOperationException("Cannot set EVEC render phonemes.");
                    if (segment > 0)
                    {
                        var continuation = expression;
                        continuation.Accent = 50;
                        continuation.Decay = 50;
                        continuation.RisePort = false;
                        continuation.FallPort = false;
                        current.NoteVelocity = 64;
                        if (!current.SetNoteExpression(continuation))
                            throw new InvalidOperationException("Cannot set EVEC continuation expression.");
                    }
                    current.IsProtected = true;
                    if (current.DurationTick.Value <= 0 ||
                        current.RelPosTick.Value + current.DurationTick.Value != plan.Ends[segment])
                        throw new InvalidOperationException("EVEC render event has an unexpected boundary.");
                    if (following != null) current = following;
                }
            }
            if (!duplicate.Commit(false))
                throw new InvalidOperationException("Cannot commit EVEC render copy.");
            copy = new EvecRenderCopy(duplicate, target);
            duplicate = null;
            return true;
        }
        catch (Exception exception)
        {
            error = exception.Message;
            return false;
        }
        finally
        {
            if (duplicate != null)
            {
                try { duplicate.Close(); }
                catch { error ??= "Cannot close failed EVEC render copy."; }
                finally { Utils.NativeRenderSession.UnregisterInternalSequence(duplicate); }
            }
        }
    }

    /// <summary>
    /// Stops the native renderer but leaves the duplicate sequence open so the
    /// editor-side close window can finish the teardown. Closing a sequence while
    /// the host renderer still owns it can take the editor down, so the editor
    /// coordinator never calls <see cref="Dispose"/> directly.
    /// </summary>
    internal void ReleaseToCloseWindow()
    {
        if (_disposed) return;
        _disposed = true;
        try { Sequence.StopAsyncRendering(); }
        catch { }
        // The registration stays until the shared close path actually closes the
        // sequence; an address must never be reused as a user document meanwhile.
    }

    public void Dispose()
    {
        if (_disposed) return;
        try { Sequence.StopAsyncRendering(); }
        finally
        {
            Sequence.Close();
            Utils.NativeRenderSession.UnregisterInternalSequence(Sequence);
            _disposed = true;
        }
    }
}
