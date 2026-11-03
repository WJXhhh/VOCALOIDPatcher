using System;
using System.Globalization;
using System.Windows;
using System.Windows.Media;
using VOCALOIDPatcher.Translation;

namespace VOCALOIDPatcher.ParameterEnergy;

internal static class EnergyEnvelopeDrawing
{
    private static readonly Pen Outline = Frozen(new Pen(Brushes.White, 1));
    private static readonly Pen RmsOutline = Frozen(new Pen(new SolidColorBrush(Color.FromArgb(150, 255, 255, 255)), 1));

    internal static Drawing Create(EnergyEnvelope envelope, double left, double right, double height,
        double minimum, double maximum, Func<double, double> secondsAtX, double pixelsPerDip)
    {
        double Y(double db) => EnergyLevelScale.ToY(db, height, minimum, maximum);
        var geometry = new StreamGeometry();
        var rmsGeometry = new StreamGeometry();
        using (var context = geometry.Open())
        using (var rmsContext = rmsGeometry.Open())
        {
            bool peakStarted = false, rmsStarted = false;
            // Half-pixel sampling and open, unfilled paths. Do not draw a
            // horizontal floor through silent / out-of-range sections.
            double step = Math.Max(.5, (right - left) / 8192);
            double previousX = left, previousPeak = -120, previousRms = -120;
            void Add(StreamGeometryContext target, double x, double db, double previousDb, ref bool started)
            {
                var point = new Point(x, Y(db));
                if (db > minimum)
                {
                    if (!started)
                    {
                        double crossing = x == left || previousDb >= minimum ? x :
                            previousX + (x - previousX) * (minimum - previousDb) / (db - previousDb);
                        target.BeginFigure(new Point(crossing, x == left ? point.Y : Y(minimum)), isFilled: false, isClosed: false);
                        started = true;
                    }
                    target.LineTo(point, isStroked: true, isSmoothJoin: true);
                }
                else if (started)
                {
                    double crossing = previousX + (x - previousX) * (previousDb - minimum) / (previousDb - db);
                    target.LineTo(new Point(crossing, Y(minimum)), isStroked: true, isSmoothJoin: true);
                    started = false;
                }
            }
            double seconds = secondsAtX(left);
            for (double x = left; ; x = Math.Min(right, x + step))
            {
                double next = secondsAtX(Math.Min(right, x + step));
                double peak = envelope.GetPeakDecibels(seconds, next), rms = envelope.GetDecibels(seconds, next);
                Add(context, x, peak, previousPeak, ref peakStarted);
                Add(rmsContext, x, rms, previousRms, ref rmsStarted);
                previousX = x; previousPeak = peak; previousRms = rms;
                if (x >= right) break;
                seconds = next;
            }
        }
        geometry.Freeze();
        rmsGeometry.Freeze();
        var drawing = new DrawingGroup();
        using (var context = drawing.Open())
        {
            context.DrawGeometry(null, Outline, geometry);
            context.DrawGeometry(null, RmsOutline, rmsGeometry);
            foreach (double value in new[] { maximum, (minimum + maximum) / 2, minimum })
            {
                string unit = envelope.WeightingPercent > 0
                    ? TranslationManager.Tr("VOCALOIDPatcher_ParameterEnergy_WeightedUnit") : "dB";
                var text = new FormattedText(value.ToString("0.#", CultureInfo.InvariantCulture) + " " + unit,
                    CultureInfo.InvariantCulture, FlowDirection.LeftToRight, new Typeface("Segoe UI"), 10,
                    Brushes.White, pixelsPerDip);
                context.DrawText(text, new Point(Math.Max(left, right - text.Width - 5),
                    Math.Clamp(Y(value) - text.Height / 2, 0, Math.Max(0, height - text.Height))));
            }
        }
        drawing.Freeze();
        return drawing;
    }

    private static T Frozen<T>(T value) where T : Freezable { value.Freeze(); return value; }
}
