using System;
using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using VOCALOIDPatcher.Translation;
using VOCALOIDPatcher.Utils;

namespace VOCALOIDPatcher.ParameterEnergy;

internal sealed class EnergyTargetGuide : FrameworkElement
{
    private static readonly Brush GuideBrush = CreateBrush(Color.FromRgb(235, 183, 92));
    private static readonly Pen GuidePen = CreatePen(1, dashed: true);
    private static readonly Pen GripPen = CreatePen(2, dashed: false);
    private readonly Action<double> _commit;
    private readonly Action? _suppressHints;
    private Drawing? _drawing;
    private Rect _grip;
    private Rect _viewport;
    private double _minimum, _maximum, _persistedValue, _previewValue;
    private double _dragOffset;
    private bool _dragging;

    internal EnergyTargetGuide(Action<double> commit, Action? suppressHints = null)
    {
        _commit = commit;
        _suppressHints = suppressHints;
        ClipToBounds = true;
        Visibility = Visibility.Collapsed;
        // Draw primitives directly: no styled label/border, background, popup,
        // template or focus adorner can cover the parameter area.
        Style = null;
        OverridesDefaultStyle = true;
        Focusable = true;
        FocusVisualStyle = null;
        Cursor = Cursors.SizeNS;
        PreviewMouseLeftButtonDown += OnMouseDown;
        PreviewMouseMove += OnMouseMove;
        PreviewMouseLeftButtonUp += OnMouseUp;
        MouseEnter += (_, _) => SuppressHints();
        LostMouseCapture += (_, _) => EndDrag(commit: false);
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key != Key.Escape || !_dragging) return;
            e.Handled = true;
            EndDrag(commit: false);
        };
        Unloaded += (_, _) => EndDrag(commit: false);
    }

    internal void Update(Rect viewport, double width, double height, double minimum, double maximum, double value, bool enabled)
    {
        if (!enabled || !double.IsFinite(viewport.Left + viewport.Width + width + height + value) ||
            viewport.Width <= 0 || height <= 8 || value < minimum || value > maximum)
        {
            Hide();
            return;
        }
        if (_dragging && (_minimum != minimum || _maximum != maximum || Height != height || _persistedValue != value))
            EndDrag(commit: false);
        _viewport = viewport;
        _minimum = minimum; _maximum = maximum;
        _persistedValue = value;
        if (!_dragging) _previewValue = value;
        Width = Math.Max(1, width);
        Height = height;
        Visibility = Visibility.Visible;
        DrawGuide();
    }

    internal void Hide()
    {
        EndDrag(commit: false);
        Visibility = Visibility.Collapsed;
    }

    private void DrawGuide()
    {
        double y = EnergyLevelScale.ToY(_previewValue, Height, _minimum, _maximum);
        double left = Math.Max(0, _viewport.Left), right = Math.Min(Width, _viewport.Right);
        double available = Math.Max(0, right - left);
        _grip = new Rect(Math.Max(left, right - 6), Math.Clamp(y - 9, 0, Math.Max(0, Height - 18)),
            Math.Min(6, available), Math.Min(18, Height));
        var drawing = new DrawingGroup();
        using (var context = drawing.Open())
        {
            context.PushClip(new RectangleGeometry(new Rect(left, 0, available, Height)));
            context.PushOpacity(.65);
            context.DrawLine(GuidePen, new Point(left, y), new Point(right, y));
            context.DrawLine(GripPen, new Point(right - 1, Math.Max(0, y - 5)), new Point(right - 1, Math.Min(Height, y + 5)));
            context.Pop();
            string value = TranslationManager.Tr("VOCALOIDPatcher_ParameterEnergy_TargetValue", _previewValue.ToString("0.0", CultureInfo.CurrentCulture));
            var label = new FormattedText(value, CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
                new Typeface("Segoe UI"), 11, GuideBrush, VisualTreeHelper.GetDpi(this).PixelsPerDip);
            double top = y >= label.Height + 2 ? y - label.Height - 2 : y + 2;
            context.PushOpacity(.8);
            context.DrawText(label, new Point(Math.Max(left, right - label.Width - 10), Math.Clamp(top, 0, Math.Max(0, Height - label.Height))));
            context.Pop();
            context.Pop();
        }
        drawing.Freeze();
        _drawing = drawing;
        InvalidateVisual();
    }

    protected override void OnRender(DrawingContext drawingContext)
    {
        if (_drawing != null) drawingContext.DrawDrawing(_drawing);
    }

    protected override HitTestResult? HitTestCore(PointHitTestParameters hitTestParameters)
        => _grip.Contains(hitTestParameters.HitPoint) ? new PointHitTestResult(this, hitTestParameters.HitPoint) : null;

    private void SuppressHints()
    {
        try { _suppressHints?.Invoke(); }
        catch (Exception e) { Debug.Print("Energy target hints: " + e.GetType().Name); }
    }

    private void OnMouseDown(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        if (_dragging || Mouse.Captured != null) return;
        SuppressHints();
        Focus();
        if (!CaptureMouse()) return;
        _dragging = true;
        _previewValue = _persistedValue;
        _dragOffset = e.GetPosition(this).Y - EnergyLevelScale.ToY(_previewValue, Height, _minimum, _maximum);
    }

    private void OnMouseMove(object sender, MouseEventArgs e)
    {
        // Native Pencil/Line behaviors also listen for bubbling mouse moves.
        e.Handled = true;
        if (!_dragging) return;
        _previewValue = EnergyLevelScale.FromY(e.GetPosition(this).Y - _dragOffset, Height, _minimum, _maximum);
        DrawGuide();
    }

    private void OnMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (!_dragging) return;
        e.Handled = true;
        EndDrag(commit: true);
    }

    private void EndDrag(bool commit)
    {
        if (!_dragging) return;
        _dragging = false;
        if (IsMouseCaptured) ReleaseMouseCapture();
        try
        {
            if (commit && _persistedValue != _previewValue)
            {
                _commit(_previewValue);
                _persistedValue = _previewValue;
            }
        }
        catch (Exception e) { Debug.Print("Energy target guide: " + e.GetType().Name); }
        _previewValue = _persistedValue;
        DrawGuide();
    }

    private static Brush CreateBrush(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }

    private static Pen CreatePen(double thickness, bool dashed)
    {
        var pen = new Pen(GuideBrush, thickness);
        if (dashed) pen.DashStyle = new DashStyle(new double[] { 4, 4 }, 0);
        pen.Freeze();
        return pen;
    }
}
