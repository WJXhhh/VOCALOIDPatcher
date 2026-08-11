using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using HarmonyLib;
using VOCALOIDPatcher.Translation;
using VOCALOIDPatcher.Utils;
using Yamaha.VOCALOID;
using Yamaha.VOCALOID.MusicalEditor;
using Yamaha.VOCALOID.VSM;

namespace VOCALOIDPatcher.BreathVolume;

internal sealed class BreathVolumeOverlay
{
    private static readonly Brush BackgroundBrush = Freeze(new SolidColorBrush(Color.FromRgb(35, 35, 38)));
    private static readonly Brush BarBrush = Freeze(new SolidColorBrush(Color.FromRgb(104, 185, 230)));
    private static readonly Brush SelectedBarBrush = Freeze(new SolidColorBrush(Color.FromRgb(255, 184, 76)));
    private static readonly Brush GridBrush = Freeze(new SolidColorBrush(Color.FromArgb(100, 120, 120, 125)));
    private static readonly Brush TextBrush = Freeze(new SolidColorBrush(Color.FromRgb(190, 190, 195)));
    private static readonly Brush SelectionBrush = Freeze(new SolidColorBrush(Color.FromArgb(70, 90, 170, 240)));

    private readonly ParameterView _view;
    private readonly Canvas _canvas;
    private readonly ScaleTransform _scale = new(1.0, 1.0);
    private readonly Rectangle _selectionRectangle;
    private readonly Line _linePreview;
    private Dictionary<IntPtr, byte>? _gestureBefore;
    private Point _gestureStart;
    private double _previousPaintX;
    private IntPtr _selectionAnchor;
    private GestureKind _gesture;
    private int _refreshPending;
    private int _refreshing;
    private bool _lastLoggedActive;
    private BreathRegionStatus _lastLoggedStatus = (BreathRegionStatus)(-1);
    private int _lastLoggedRegionCount = -1;

    private BreathVolumeOverlay(ParameterView view, Grid panel)
    {
        _view = view;
        _canvas = new Canvas
        {
            Background = BackgroundBrush,
            ClipToBounds = true,
            Visibility = Visibility.Collapsed,
            Focusable = true,
            RenderTransform = _scale,
            RenderTransformOrigin = new Point(0, 0)
        };
        Panel.SetZIndex(_canvas, 1000);
        panel.Children.Add(_canvas);

        _selectionRectangle = new Rectangle
        {
            Stroke = SelectedBarBrush,
            Fill = SelectionBrush,
            StrokeThickness = 1,
            Visibility = Visibility.Collapsed,
            IsHitTestVisible = false
        };
        _linePreview = new Line
        {
            Stroke = SelectedBarBrush,
            StrokeThickness = 1,
            Visibility = Visibility.Collapsed,
            IsHitTestVisible = false
        };

        _canvas.MouseLeftButtonDown += OnMouseLeftButtonDown;
        _canvas.MouseMove += OnMouseMove;
        _canvas.MouseLeftButtonUp += OnMouseLeftButtonUp;
        _canvas.MouseRightButtonDown += OnMouseRightButtonDown;
        _canvas.SizeChanged += (_, _) => Refresh();
        _view.DataContextChanged += (_, _) => Refresh();
        BreathVolumeService.Changed += OnServiceChanged;
    }

    public static BreathVolumeOverlay? Attach(ParameterView view)
    {
        try
        {
            var panel = AccessTools.Field(typeof(ParameterView), "xPanel")?.GetValue(view) as Grid;
            if (panel == null)
            {
                BreathVolumeDiagnosticsLog.Write("overlay attach failed: xPanel was not found");
                return null;
            }
            BreathVolumeDiagnosticsLog.Write("overlay attached");
            return new BreathVolumeOverlay(view, panel);
        }
        catch (Exception e)
        {
            BreathVolumeDiagnosticsLog.Write($"overlay attach failed: {e.GetType().Name}: {e.Message}");
            return null;
        }
    }

    public bool IsVisible => _canvas.Visibility == Visibility.Visible;

    public void Refresh()
    {
        if (!_canvas.Dispatcher.CheckAccess())
        {
            _canvas.Dispatcher.BeginInvoke((Action)Refresh);
            return;
        }

        if (Interlocked.Exchange(ref _refreshing, 1) != 0)
            return;

        try
        {
            RefreshCore();
        }
        catch (Exception e)
        {
            Debug.Print(TranslationManager.Tr("VOCALOIDPatcher_Debug_BreathVolume_UiFailed", e.Message));
            BreathVolumeDiagnosticsLog.Write($"overlay refresh failed: {e.GetType().Name}: {e.Message}");
            try
            {
                _canvas.Children.Clear();
                if (_view.DataContext is MusicalEditorViewModel vm)
                    DrawEmptyState("VOCALOIDPatcher_BreathVolume_NoBreaths", vm);
                RestoreTransientObjects();
            }
            catch
            {
                _canvas.Visibility = Visibility.Collapsed;
            }
        }
        finally
        {
            Interlocked.Exchange(ref _refreshing, 0);
        }
    }

    private void RefreshCore()
    {
        if (_view.DataContext is not MusicalEditorViewModel vm ||
            !BreathVolumeService.IsActive(vm.ControlParameterType))
        {
            LogOverlayState(active: false, BreathRegionStatus.Unknown, 0, 0);
            _canvas.Visibility = Visibility.Collapsed;
            return;
        }

        _canvas.Visibility = Visibility.Visible;
        _canvas.Width = Math.Max(vm.SongWidth, _view.ActualWidth);
        _canvas.Height = Math.Max(1, vm.ViewHeight);
        _scale.ScaleX = vm.ViewCanvasHorizontalZoom == 0 ? 1.0 : vm.ViewCanvasHorizontalZoom;
        _canvas.Children.Clear();

        DrawGrid(vm);
        var part = vm.ActivePart;
        var sequence = vm.VSMSequence;
        if (part == null || sequence == null)
        {
            DrawEmptyState("VOCALOIDPatcher_BreathVolume_NoActivePart", vm);
            RestoreTransientObjects();
            return;
        }

        var status = BreathVolumeService.GetRegionStatus(part);
        if (status == BreathRegionStatus.Unknown)
        {
            BreathVolumeService.EnsureRegionsAsync(sequence, part);
            status = BreathRegionStatus.Loading;
        }
        var regions = BreathVolumeService.GetRegions(part);
        LogOverlayState(active: true, status, regions.Count, part.NumNotes);
        if (regions.Count == 0)
        {
            DrawEmptyState(
                status == BreathRegionStatus.Loading
                    ? "VOCALOIDPatcher_BreathVolume_Loading"
                    : "VOCALOIDPatcher_BreathVolume_NoBreaths",
                vm);
            RestoreTransientObjects();
            return;
        }

        var height = Math.Max(1, vm.ViewHeight);
        var notePositions = new Dictionary<IntPtr, double>();
        for (ulong index = 0; index < part.NumNotes; index++)
        {
            var note = part.GetNote(index);
            if (note != null)
                notePositions[note.CppObjPtr] = vm.CalcTickToViewPosition(note.AbsPosTick);
        }
        foreach (var region in regions)
        {
            var x1 = vm.CalcTickToViewPosition((VSMAbsTick)region.BeginTick);
            var x2 = vm.CalcTickToViewPosition((VSMAbsTick)region.EndTick);
            if (x2 <= 0 && notePositions.TryGetValue(region.NoteHandle, out var noteX))
            {
                x1 = Math.Max(0, noteX);
                x2 = x1 + 5;
            }
            else
            {
                x1 = Math.Max(0, x1);
            }
            var width = Math.Max(5.0, x2 - x1);
            var value = BreathVolumeService.GetValue(region.NoteHandle);
            var top = ValueToY(value, height);
            var selected = BreathVolumeService.IsSelected(region.NoteHandle);
            var bar = new Rectangle
            {
                Width = width,
                Height = Math.Max(2, height - top),
                Fill = selected ? SelectedBarBrush : BarBrush,
                Stroke = selected ? Brushes.White : Brushes.Transparent,
                StrokeThickness = selected ? 1 : 0,
                Tag = region,
                ToolTip = $"BVL {value}"
            };
            Canvas.SetLeft(bar, x1);
            Canvas.SetTop(bar, top);
            _canvas.Children.Add(bar);
        }

        RestoreTransientObjects();
    }

    private void LogOverlayState(
        bool active,
        BreathRegionStatus status,
        int regionCount,
        ulong noteCount)
    {
        if (_lastLoggedActive == active && _lastLoggedStatus == status &&
            _lastLoggedRegionCount == regionCount)
            return;
        _lastLoggedActive = active;
        _lastLoggedStatus = status;
        _lastLoggedRegionCount = regionCount;
        BreathVolumeDiagnosticsLog.Write(
            $"overlay active={active} status={status} regions={regionCount} notes={noteCount}");
    }

    public void Show() => Refresh();

    public void Hide()
    {
        _gesture = GestureKind.None;
        _gestureBefore = null;
        _canvas.ReleaseMouseCapture();
        _canvas.Visibility = Visibility.Collapsed;
    }

    private void DrawGrid(MusicalEditorViewModel vm)
    {
        var height = Math.Max(1, vm.ViewHeight);
        foreach (var value in new[] { 0, 32, 64, 96, 127 })
        {
            var y = ValueToY(value, height);
            var line = new Line
            {
                X1 = 0,
                X2 = _canvas.Width,
                Y1 = y,
                Y2 = y,
                Stroke = GridBrush,
                StrokeThickness = value is 0 or 127 ? 1 : 0.5,
                IsHitTestVisible = false
            };
            _canvas.Children.Add(line);
        }
    }

    private void DrawEmptyState(string key, MusicalEditorViewModel vm)
    {
        var label = new TextBlock
        {
            Text = TranslationManager.Tr(key),
            Foreground = TextBrush,
            FontSize = 12,
            IsHitTestVisible = false
        };
        var zoom = Math.Max(0.0001, Math.Abs(_scale.ScaleX));
        var viewportLeft = (vm.ParameterViewer?.HorizontalOffset ?? 0.0) / zoom;
        Canvas.SetLeft(label, Math.Max(0, viewportLeft) + 12);
        Canvas.SetTop(label, 12);
        _canvas.Children.Add(label);
    }

    private void RestoreTransientObjects()
    {
        _canvas.Children.Add(_selectionRectangle);
        _canvas.Children.Add(_linePreview);
    }

    private void OnMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (!TryGetContext(out var vm, out var sequence, out var part))
            return;

        _canvas.Focus();
        _gestureStart = e.GetPosition(_canvas);
        _previousPaintX = _gestureStart.X;
        var region = FindRegion(e.OriginalSource);
        var mode = vm.EditorMode.Mode;

        if (mode == EditModeME.Arrow && region.HasValue)
        {
            SelectClickedRegion(part, region.Value, Keyboard.Modifiers);
            var handles = BreathVolumeService.GetSelection();
            _gestureBefore = BreathVolumeService.Snapshot(handles);
            _gesture = GestureKind.Move;
        }
        else if (mode == EditModeME.Arrow)
        {
            if (!Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
                BreathVolumeService.ClearSelection();
            _gesture = GestureKind.Rectangle;
            ShowSelectionRectangle(_gestureStart, _gestureStart);
        }
        else if (mode is EditModeME.Pencil or EditModeME.Line)
        {
            var handles = BreathVolumeService.GetRegions(part).Select(item => item.NoteHandle).Distinct().ToArray();
            _gestureBefore = BreathVolumeService.Snapshot(handles);
            _gesture = mode == EditModeME.Line ? GestureKind.Line : GestureKind.Pencil;
            if (_gesture == GestureKind.Pencil)
                PaintBetween(vm, part, _gestureStart.X, _gestureStart.X, YToValue(_gestureStart.Y, vm.ViewHeight));
            else
                ShowLinePreview(_gestureStart, _gestureStart);
        }
        else
        {
            return;
        }

        _canvas.CaptureMouse();
        e.Handled = true;
    }

    private void OnMouseMove(object sender, MouseEventArgs e)
    {
        if (_gesture == GestureKind.None || e.LeftButton != MouseButtonState.Pressed ||
            !TryGetContext(out var vm, out _, out var part))
            return;

        var point = e.GetPosition(_canvas);
        switch (_gesture)
        {
            case GestureKind.Move:
                BreathVolumeService.SetPreviewValues(
                    BreathVolumeService.GetSelection(),
                    YToValue(point.Y, vm.ViewHeight));
                break;
            case GestureKind.Rectangle:
                ShowSelectionRectangle(_gestureStart, point);
                break;
            case GestureKind.Pencil:
                PaintBetween(vm, part, _previousPaintX, point.X, YToValue(point.Y, vm.ViewHeight));
                _previousPaintX = point.X;
                break;
            case GestureKind.Line:
                ShowLinePreview(_gestureStart, point);
                break;
        }
        e.Handled = true;
    }

    private void OnMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (_gesture == GestureKind.None || !TryGetContext(out var vm, out var sequence, out var part))
            return;

        var point = e.GetPosition(_canvas);
        if (_gesture == GestureKind.Rectangle)
            CompleteRectangleSelection(vm, part, _gestureStart, point);
        else if (_gesture == GestureKind.Line)
            ApplyLine(vm, part, _gestureStart, point);

        if (_gestureBefore != null && _gesture is GestureKind.Move or GestureKind.Pencil or GestureKind.Line)
            BreathVolumeService.CommitValues(sequence, part, _gestureBefore);

        _gesture = GestureKind.None;
        _gestureBefore = null;
        _selectionRectangle.Visibility = Visibility.Collapsed;
        _linePreview.Visibility = Visibility.Collapsed;
        _canvas.ReleaseMouseCapture();
        Refresh();
        e.Handled = true;
    }

    private void OnMouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (!TryGetContext(out _, out var sequence, out var part))
            return;

        var region = FindRegion(e.OriginalSource);
        if (region.HasValue && !BreathVolumeService.IsSelected(region.Value.NoteHandle))
            BreathVolumeService.SetSelection(new[] { region.Value.NoteHandle });

        var menu = new ContextMenu();
        var reset = new MenuItem { Header = TranslationManager.Tr("VOCALOIDPatcher_BreathVolume_Reset") };
        reset.Click += (_, _) => BreathVolumeService.ResetSelected(sequence, part);
        menu.Items.Add(reset);
        _canvas.ContextMenu = menu;
        menu.IsOpen = true;
        e.Handled = true;
    }

    private void SelectClickedRegion(WIVSMMidiPart part, BreathRegion clicked, ModifierKeys modifiers)
    {
        var regions = BreathVolumeService.GetRegions(part);
        if (modifiers.HasFlag(ModifierKeys.Shift) && _selectionAnchor != IntPtr.Zero)
        {
            var first = IndexOf(regions, _selectionAnchor);
            var second = IndexOf(regions, clicked.NoteHandle);
            if (first >= 0 && second >= 0)
            {
                var handles = regions.Skip(Math.Min(first, second)).Take(Math.Abs(first - second) + 1)
                    .Select(region => region.NoteHandle);
                BreathVolumeService.SetSelection(handles, modifiers.HasFlag(ModifierKeys.Control));
            }
            else
            {
                BreathVolumeService.SetSelection(new[] { clicked.NoteHandle }, modifiers.HasFlag(ModifierKeys.Control));
            }
        }
        else if (modifiers.HasFlag(ModifierKeys.Control))
        {
            BreathVolumeService.ToggleSelection(clicked.NoteHandle);
        }
        else if (!BreathVolumeService.IsSelected(clicked.NoteHandle))
        {
            BreathVolumeService.SetSelection(new[] { clicked.NoteHandle });
        }

        _selectionAnchor = clicked.NoteHandle;
    }

    private void CompleteRectangleSelection(MusicalEditorViewModel vm, WIVSMMidiPart part, Point start, Point end)
    {
        var left = Math.Min(start.X, end.X);
        var right = Math.Max(start.X, end.X);
        var top = Math.Min(start.Y, end.Y);
        var bottom = Math.Max(start.Y, end.Y);
        var handles = BreathVolumeService.GetRegions(part).Where(region =>
        {
            var x1 = vm.CalcTickToViewPosition((VSMAbsTick)region.BeginTick);
            var x2 = vm.CalcTickToViewPosition((VSMAbsTick)region.EndTick);
            var y = ValueToY(BreathVolumeService.GetValue(region.NoteHandle), vm.ViewHeight);
            return Math.Max(x1 + 5, x2) >= left && x1 <= right && vm.ViewHeight >= top && y <= bottom;
        }).Select(region => region.NoteHandle);
        BreathVolumeService.SetSelection(handles, Keyboard.Modifiers.HasFlag(ModifierKeys.Control));
    }

    private static void PaintBetween(MusicalEditorViewModel vm, WIVSMMidiPart part, double x1, double x2, int value)
    {
        var left = Math.Min(x1, x2);
        var right = Math.Max(x1, x2);
        var handles = BreathVolumeService.GetRegions(part).Where(region =>
        {
            var center = (vm.CalcTickToViewPosition((VSMAbsTick)region.BeginTick) +
                          vm.CalcTickToViewPosition((VSMAbsTick)region.EndTick)) / 2;
            return center >= left - 3 && center <= right + 3;
        }).Select(region => region.NoteHandle);
        BreathVolumeService.SetPreviewValues(handles, value);
    }

    private static void ApplyLine(MusicalEditorViewModel vm, WIVSMMidiPart part, Point start, Point end)
    {
        var deltaX = end.X - start.X;
        foreach (var region in BreathVolumeService.GetRegions(part))
        {
            var center = (vm.CalcTickToViewPosition((VSMAbsTick)region.BeginTick) +
                          vm.CalcTickToViewPosition((VSMAbsTick)region.EndTick)) / 2;
            if (center < Math.Min(start.X, end.X) || center > Math.Max(start.X, end.X))
                continue;
            var ratio = Math.Abs(deltaX) < 0.001 ? 0.0 : (center - start.X) / deltaX;
            var y = start.Y + (end.Y - start.Y) * ratio;
            BreathVolumeService.SetPreviewValues(new[] { region.NoteHandle }, YToValue(y, vm.ViewHeight));
        }
    }

    private void ShowSelectionRectangle(Point start, Point end)
    {
        _selectionRectangle.Visibility = Visibility.Visible;
        Canvas.SetLeft(_selectionRectangle, Math.Min(start.X, end.X));
        Canvas.SetTop(_selectionRectangle, Math.Min(start.Y, end.Y));
        _selectionRectangle.Width = Math.Abs(end.X - start.X);
        _selectionRectangle.Height = Math.Abs(end.Y - start.Y);
    }

    private void ShowLinePreview(Point start, Point end)
    {
        _linePreview.Visibility = Visibility.Visible;
        _linePreview.X1 = start.X;
        _linePreview.Y1 = start.Y;
        _linePreview.X2 = end.X;
        _linePreview.Y2 = end.Y;
    }

    private bool TryGetContext(
        out MusicalEditorViewModel vm,
        out WIVSMSequence sequence,
        out WIVSMMidiPart part)
    {
        vm = _view.DataContext as MusicalEditorViewModel ?? null!;
        sequence = vm?.VSMSequence ?? null!;
        part = vm?.ActivePart ?? null!;
        return vm != null && sequence != null && part != null && BreathVolumeService.IsActive(vm.ControlParameterType);
    }

    private static BreathRegion? FindRegion(object source)
    {
        for (var current = source as DependencyObject; current != null; current = VisualTreeHelper.GetParent(current))
        {
            if (current is FrameworkElement { Tag: BreathRegion region })
                return region;
        }
        return null;
    }

    private void OnServiceChanged(BreathVolumeChangeKind kind, WIVSMMidiPart? part)
    {
        if (_canvas.Dispatcher.HasShutdownStarted)
            return;
        if (part != null && _view.DataContext is MusicalEditorViewModel { ActivePart: { } activePart } &&
            !activePart.Equals(part))
            return;
        if (Interlocked.Exchange(ref _refreshPending, 1) != 0)
            return;
        _canvas.Dispatcher.BeginInvoke(new Action(() =>
        {
            Interlocked.Exchange(ref _refreshPending, 0);
            Refresh();
        }));
    }

    private static int IndexOf(IReadOnlyList<BreathRegion> regions, IntPtr handle)
    {
        for (var index = 0; index < regions.Count; index++)
            if (regions[index].NoteHandle == handle)
                return index;
        return -1;
    }

    private static double ValueToY(int value, double height)
        => (MaxValue - Math.Clamp(value, MinValue, MaxValue)) / (double)MaxValue * Math.Max(1, height - 2);

    private static int YToValue(double y, double height)
        => Math.Clamp((int)Math.Round(MaxValue * (1.0 - y / Math.Max(1, height - 2))), MinValue, MaxValue);

    private static T Freeze<T>(T freezable) where T : Freezable
    {
        if (freezable.CanFreeze)
            freezable.Freeze();
        return freezable;
    }

    private const int MinValue = BreathVolumeService.MinValue;
    private const int MaxValue = BreathVolumeService.MaxValue;

    private enum GestureKind
    {
        None,
        Move,
        Rectangle,
        Pencil,
        Line
    }
}
