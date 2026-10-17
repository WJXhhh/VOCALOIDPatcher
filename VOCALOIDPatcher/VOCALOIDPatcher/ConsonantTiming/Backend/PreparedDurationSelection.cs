using System;
using System.Linq;
using System.Threading;
using VOCALOIDPatcher.DurationSelection;

namespace VOCALOIDPatcher.ConsonantTiming;

internal sealed partial class LocalConsonantTimingBackend
{
    internal static string DurationInputKey(Yamaha.VOCALOID.VSM.WIVSMMidiPart part)
    {
        var source = part.Sequence ?? throw new InvalidOperationException("Missing duration source.");
        if (part.NumNotes is < 1 or > 33) throw new NotSupportedException("Independent duration requires a complete Part of at most 33 notes.");
        var target = part.GetNote(part.NumNotes / 2)!;
        var context = CaptureContext(target, 16);
        if (!context.Complete) throw new NotSupportedException("Incomplete duration input identity.");
        var notes = context.Notes;
        return ContextKey(source, part, notes, context.Target,
            CaptureTempo(source, notes[0].AbsTick, notes.Max(n => n.AbsTick + n.Duration)), target.NoteVelocity) +
            ":" + source.PresendTimeSec.ToString("R", System.Globalization.CultureInfo.InvariantCulture);
    }

    internal sealed partial class PreparedCoefficientTiming
    {
        // Capture pre-warp allocation with the current per-note coefficients.
        // A complete bounded context is required; never invent missing sections.
        internal NativeDurationSelection.TimeEvent[] CaptureDurationContract(NativeDurationSelection.Note[] notes,
            long revision, CancellationToken cancellation = default)
        {
            if (!Monitor.TryEnter(_owner._gate)) throw new InvalidOperationException("Timing backend busy.");
            try
            {
                ObjectDisposedException.ThrowIf(_windows.Length == 0 || _owner._disposed, this);
                if (revision != _sourceRevision) throw new InvalidOperationException("Duration source revision changed.");
                var window = _windows.LastOrDefault(w => w.Complete && w.Messages.Length == notes.Length)
                    ?? throw new NotSupportedException("Duration contract requires complete bounded Part context.");
                NativeDurationSelection.BeginCapture(notes);
                try
                {
                    _owner._engine.Begin(cancellation, 0);
                    try
                    {
                        _owner._engine.Plan(0, _singer);
                        foreach (var message in window.Messages)
                        {
                            cancellation.ThrowIfCancellationRequested();
                            _owner._engine.PlanNote(message.Frame + window.Origin, message.Bytes, message.Coefficient);
                        }
                    }
                    finally { _owner._engine.End(); }
                    return NativeDurationSelection.EndCapture();
                }
                finally { NativeDurationSelection.CancelCapture(); }
            }
            finally { Monitor.Exit(_owner._gate); }
        }
    }
}
