#if PATCHER_ISOLATED_VALIDATION
using System;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Yamaha.VOCALOID.MusicalEditor;
using Yamaha.VOCALOID.VSM;

namespace VOCALOIDPatcher.ConsonantTiming;

internal sealed partial class ConsonantTimingOverlay
{
    internal static TransitionControlPlan ValidateDiamondPlan(TransitionLayout layout, TransitionExecutionPlan execution,
        TransitionControlPlan controls, TransitionIdentity identity, WIVSMNote note, float target)
    {
        var edge = layout.Edges.Single(e => e.Identity == identity);
        var row = execution.Segments.Where(s => s.To != null).ElementAt(layout.Edges.IndexOf(edge));
        var range = TransitionExpressionControl.Range(row, identity, controls) ?? throw new InvalidOperationException("Missing diamond interval.");
        float before = controls.Phases.FirstOrDefault(p => p.Edge == identity)?.Amount ?? 0;
        double delta = TransitionExpressionControl.HandleFrame(range, target) - TransitionExpressionControl.HandleFrame(range, before);
        return PhaseDragPlan(controls, new(edge, row, new UIEmotionNote { Note = note }, 30, 230, 110, 25, 10, 0), delta, false);
    }
    // Exercise the production hit policy and WPF drawing with real ART rows,
    // without constructing or starting the Editor's application shell.
    internal static void ValidateTransitionBand(TransitionLayout layout, TransitionExecutionPlan execution, WIVSMNote note, string directory)
    {
        void Check(bool ok, string reason) { if (!ok) throw new InvalidOperationException("Transition band: " + reason); }
        var edge = layout.Edges[1]; var row = execution.Segments.Where(s => s.To != null).ElementAt(1);
        var item = new TransitionBandItem(edge, row, new UIEmotionNote { Note = note }, 30, 230, 110, 25, 10, -6,
            false, true, true, false);
        TransitionBandItem[] items = [item];
        Check(HitTransitionBand(items, edge.Identity, new(30, 29))?.Endpoint == TransitionEndpoint.Begin, "left endpoint priority");
        Check(HitTransitionBand(items, edge.Identity, new(230, 29))?.Endpoint == TransitionEndpoint.End, "locked endpoint selection");
        Check(HitTransitionBand(items, edge.Identity, new(130, 32.8))?.Gain == true, "gain line hit");
        Check(HitTransitionBand(items, edge.Identity, new(130, 25)) is { Endpoint: null, Gain: false }, "body selection");
        Check(HitTransitionBand(items, edge.Identity, new(130, 10)) == null, "phoneme row isolation");
        var original = new TransitionControlPlan(layout.Fingerprint);
        var initialRange = TransitionExpressionControl.Range(row, edge.Identity, original)!.Value;
        double PhaseX(double frame) => item.Left + (frame - row.Begin) * (item.Right - item.Left) / (row.End - row.Begin);
        double markerX = PhaseX(TransitionExpressionControl.HandleFrame(initialRange, 0));
        var onsetItem = item with { PhaseX = markerX, Split = PhaseX(row.Split) };
        Check(HitTransitionBand([onsetItem], edge.Identity, new(markerX, 19))?.Phase == true, "source progress diamond hit");
        Check(HitTransitionBand([onsetItem], edge.Identity, new(110, 29))?.Phase != true, "protected split is not source progress");
        foreach (double delta in new[] { -10000d, -2, 0, 2, 10000 })
        {
            var candidate = PhaseDragPlan(original, item, delta, false);
            Check(candidate.Compile(layout).Anchors.SequenceEqual(original.Compile(layout).Anchors), "diamond changed timing anchors");
            Check(candidate.Junctions.SequenceEqual(original.Junctions) && candidate.Onsets.SequenceEqual(original.Onsets), "diamond changed existing geometry controls");
            Check(delta == 0 ? candidate.Coverages.IsEmpty : candidate.Coverages.Length == 1, "diamond source activation/no-op policy");
            float amount = candidate.Phases.FirstOrDefault()?.Amount ?? 0;
            var range = TransitionExpressionControl.Range(row, edge.Identity, candidate)!.Value;
            double marker = TransitionExpressionControl.HandleFrame(range, amount);
            Check(marker >= range.Begin && marker <= range.End, "diamond escaped its timing range");
            if (delta != 0)
            {
                var covered = TransitionCoverageMapping.Apply(edge.Original, row, candidate.Coverages.Single().Sides);
                var frames = TransitionPhaseMapping.Apply(row with { Frames = covered }, amount);
                var material = range.Side == TransitionSourceSides.Left ? row.MaterialCoverage!.Left! : row.MaterialCoverage!.Right!;
                Check(BitConverter.Int32BitsToSingle(frames[range.Begin - row.Begin].SourceBits) == material.First &&
                    BitConverter.Int32BitsToSingle(frames[range.End - row.Begin].SourceBits) == material.Last, "diamond lost complete source endpoints");
            }
        }
        Check(PhaseDragPlan(original, item, 2, true).Phases.Single().Amount < PhaseDragPlan(original, item, 2, false).Phases.Single().Amount,
            "fine phase drag did not reduce movement");
        Check(PhaseDragPlan(original, item, 0, false).Phases.IsEmpty, "return-to-origin accumulated phase drift");
        Check(ReferenceEquals(PhaseDragPlan(original, item, 0, false), original), "cancel/return-to-origin enabled source coverage");
        var adjusted = PhaseDragPlan(original, item, 2, false);
        var cleared = TransitionExpressionControl.Reset(adjusted, edge.Identity);
        Check(cleared.Coverages.IsEmpty && cleared.Phases.IsEmpty && cleared.Junctions.SequenceEqual(adjusted.Junctions) &&
            cleared.Gains.SequenceEqual(adjusted.Gains), "reset diamond changed unrelated controls");
        var shortItem = item with { Right = 38 };
        Check(HitTransitionBand([shortItem], edge.Identity, new(34, 29)) is { Endpoint: null, Gain: false }, "short span must use precise selection");
        var drawing = new TransitionBandDrawing { Width = 270, Height = 65, Items = [onsetItem], Selected = edge.Identity,
            Normal = FrozenPen(Brushes.Gray, .7), Active = FrozenPen(Brushes.LightSkyBlue, 1.5),
            Reference = FrozenPen(Brushes.Gray, .7, true), Fill = Brushes.DarkSlateGray,
            Range = new Rect(20, 23, 40, 14), Reachable = [20, 30, 60], Requested = 50 };
        drawing.Measure(new(270, 65)); drawing.Arrange(new Rect(0, 0, 270, 65)); drawing.UpdateLayout();
        var bitmap = new RenderTargetBitmap(270, 65, 96, 96, PixelFormats.Pbgra32); bitmap.Render(drawing);
        byte[] pixels = new byte[270 * 65 * 4]; bitmap.CopyPixels(pixels, 270 * 4, 0);
        Check(pixels.Where((_, i) => i % 4 == 3).Any(v => v != 0), "WPF produced no band pixels");
        Check(Enumerable.Range(15, 9).Any(y => Enumerable.Range((int)Math.Round(markerX) - 4, 9).Any(x => pixels[(y * 270 + x) * 4 + 3] != 0)),
            "WPF did not draw the phase diamond above the selected band");
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var output = File.Create(Path.Combine(directory, "transition-band.png")); encoder.Save(output);
        Console.WriteLine("transition.ui=hit/fine/return/limit/complete-source/reset/fixed-timing; isolated WPF diamond render passed");
    }
}
#endif
