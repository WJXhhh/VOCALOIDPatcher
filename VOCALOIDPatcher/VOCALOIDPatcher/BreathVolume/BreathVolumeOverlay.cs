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
    private static readonly Brush FallbackBarBrush = Freeze(new SolidColorBrush(Color.FromRgb(104, 185, 230)));
    private static readonly Brush FallbackSelectedBarBrush = Freeze(new SolidColorBrush(Color.FromRgb(255, 184, 76)));
    private static readonly Brush FallbackGridBrush = Freeze(new SolidColorBrush(Color.FromArgb(150, 110, 110, 115)));
    private static readonly Brush FallbackQuantizeBrush = Freeze(new SolidColorBrush(Color.FromArgb(90, 90, 90, 95)));
    private static readonly Brush TextBrush = Freeze(new SolidColorBrush(Color.FromRgb(190, 190, 195)));
    private static readonly Brush SelectionBrush = Freeze(new SolidColorBrush(Color.FromArgb(70, 90, 170, 240)));

    private readonly ParameterView _view;
    private readonly Canvas _canvas;
    private readonly ScaleTransform _scale = new(1.0, 1.0);
    private readonly BreathVolumeGridLayer _gridLayer = new();
    private readonly Rectangle _selectionRectangle;
    private readonly Line _linePreview;
    private readonly TextBox _valueEditor;
    private readonly FrameworkElement? _nativeParameterCanvas;
    private readonly Label? _nativeToolTip;
    private readonly Label? _nativeCursorGuide;
    private Dictionary<IntPtr, byte>? _gestureBefore;
    private Point _gestureStart;
    private Point _previousPaintPoint;
    private IntPtr _selectionAnchor;
    private GestureKind _gesture;
    private int _refreshPending;
    private int _refreshing;
    private bool _lastLoggedActive;
    private BreathRegionStatus _lastLoggedStatus = (BreathRegionStatus)(-1);
    private int _lastLoggedRegionCount = -1;
    private bool _cancelValueEdit;

    private BreathVolumeOverlay(ParameterView view, Grid panel)
    {
        _view = view;
        _canvas = new Canvas
        {
            Background = Brushes.Transparent,
            ClipToBounds = true,
            Visibility = Visibility.Collapsed,
            Focusable = true,
            RenderTransform = _scale,
            RenderTransformOrigin = new Point(0, 0)
        };
        _nativeParameterCanvas = AccessTools.Field(typeof(ParameterView), "xUIControlParameters")
            ?.GetValue(view) as FrameworkElement;
        _nativeToolTip = AccessTools.Field(typeof(ParameterView), "xToolTip")
            ?.GetValue(view) as Label;
        _nativeCursorGuide = AccessTools.Field(typeof(ParameterView), "xMouseCursorGuide")
            ?.GetValue(view) as Label;
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
        _valueEditor = new TextBox
        {
            Width = 45,
            Height = 22,
            MaxLength = 3,
            Background = Brushes.White,
            Foreground = Brushes.Black,
            FontSize = 12,
            Visibility = Visibility.Collapsed
        };
        _valueEditor.PreviewKeyDown += OnValueEditorKeyDown;
        _valueEditor.LostKeyboardFocus += (_, _) => EndValueEdit(commit: !_cancelValueEdit);

        _canvas.MouseLeftButtonDown += OnMouseLeftButtonDown;
        _canvas.MouseMove += OnMouseMove;
        _canvas.MouseLeftButtonUp += OnMouseLeftButtonUp;
        _canvas.MouseRightButtonDown += OnMouseRightButtonDown;
        _canvas.MouseLeave += OnMouseLeave;
        _canvas.LostMouseCapture += OnLostMouseCapture;
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
            if (_nativeParameterCanvas != null)
                _nativeParameterCanvas.Visibility = Visibility.Visible;
            HideNativeGuides();
            return;
        }

        _canvas.Visibility = Visibility.Visible;
        if (_nativeParameterCanvas != null)
            _nativeParameterCanvas.Visibility = Visibility.Collapsed;
        _canvas.Width = Math.Max(vm.SongWidth, _view.ActualWidth);
        _canvas.Height = Math.Max(1, vm.ViewHeight);
        _scale.ScaleX = vm.ViewCanvasHorizontalZoom == 0 ? 1.0 : vm.ViewCanvasHorizontalZoom;
        _canvas.Children.Clear();

        _gridLayer.Width = _canvas.Width;
        _gridLayer.Height = _canvas.Height;
        _gridLayer.Update(
            vm,
            vm.VSMSequence,
            FindNativeBrush("Brush_MeasureLine", FallbackGridBrush),
            FindNativeBrush("Brush_BeatLine", FallbackGridBrush),
            FindNativeBrush("Brush_GridLine", FallbackQuantizeBrush));
        _canvas.Children.Add(_gridLayer);

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
            var x = GetBarX(vm, region, notePositions);
            var value = BreathVolumeService.GetValue(region.NoteHandle);
            var top = ValueToY(value, height);
            var selected = BreathVolumeService.IsSelected(region.NoteHandle);
            var bar = new Rectangle
            {
                Width = NativeBarWidth + (selected ? NativeSelectedAddWidth : 0),
                Height = Math.Max(1, ValueBottom(height) - top),
                Fill = selected ? SelectedBarBrush : BarBrush,
                Tag = region,
            };
            Canvas.SetLeft(bar, x);
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
        if (_nativeParameterCanvas != null)
            _nativeParameterCanvas.Visibility = Visibility.Visible;
        HideNativeGuides();
        _valueEditor.Visibility = Visibility.Collapsed;
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
        _canvas.Children.Add(_valueEditor);
    }

    private void OnMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (!TryGetContext(out var vm, out var sequence, out var part))
            return;

        _canvas.Focus();
        _gestureStart = e.GetPosition(_canvas);
        _previousPaintPoint = _gestureStart;
        var region = FindRegion(e.OriginalSource);
        var mode = vm.EditorMode.Mode;

        if (mode == EditModeME.Arrow && region.HasValue)
        {
            SelectClickedRegion(part, region.Value, Keyboard.Modifiers);
            if (e.ClickCount == 2)
            {
                BeginValueEdit(region.Value, _gestureStart);
                e.Handled = true;
                return;
            }
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
                PaintBetween(vm, part, _gestureStart, _gestureStart);
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
        if (!TryGetContext(out var vm, out _, out var part))
            return;

        var point = e.GetPosition(_canvas);
        if (_gesture == GestureKind.None || e.LeftButton != MouseButtonState.Pressed)
        {
            UpdateIdleFeedback(vm, point, FindRegion(e.OriginalSource));
            return;
        }

        switch (_gesture)
        {
            case GestureKind.Move:
                if (_gestureBefore != null)
                {
                    var delta = YToValue(point.Y, vm.ViewHeight) -
                                YToValue(_gestureStart.Y, vm.ViewHeight);
                    BreathVolumeService.SetPreviewValues(_gestureBefore.Select(pair =>
                        new KeyValuePair<IntPtr, byte>(pair.Key,
                            (byte)Math.Clamp(pair.Value + delta, MinValue, MaxValue))));
                    ShowNativeGuide(point, Math.Clamp(
                        YToValue(_gestureStart.Y, vm.ViewHeight) + delta, MinValue, MaxValue));
                }
                break;
            case GestureKind.Rectangle:
                ShowSelectionRectangle(_gestureStart, point);
                break;
            case GestureKind.Pencil:
                PaintBetween(vm, part, _previousPaintPoint, point);
                _previousPaintPoint = point;
                ShowNativeGuide(point, YToValue(point.Y, vm.ViewHeight));
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
        HideNativeGuides();
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
            var x2 = x1 + NativeBarWidth;
            var y = ValueToY(BreathVolumeService.GetValue(region.NoteHandle), vm.ViewHeight);
            return x2 >= left && x1 <= right && ValueBottom(vm.ViewHeight) >= top && y <= bottom;
        }).Select(region => region.NoteHandle);
        BreathVolumeService.SetSelection(handles, Keyboard.Modifiers.HasFlag(ModifierKeys.Control));
    }

    private static void PaintBetween(MusicalEditorViewModel vm, WIVSMMidiPart part, Point start, Point end)
    {
        var left = Math.Min(start.X, end.X) - NativeBarWidth;
        var right = Math.Max(start.X, end.X) + NativeBarWidth;
        var deltaX = end.X - start.X;
        var values = BreathVolumeService.GetRegions(part).Select(region =>
        {
            var x = vm.CalcTickToViewPosition((VSMAbsTick)region.BeginTick);
            var ratio = Math.Abs(deltaX) < 0.001 ? 1.0 : Math.Clamp((x - start.X) / deltaX, 0.0, 1.0);
            var y = start.Y + (end.Y - start.Y) * ratio;
            return new { Region = region, X = x, Value = (byte)YToValue(y, vm.ViewHeight) };
        }).Where(item => item.X >= left && item.X <= right)
            .ToDictionary(item => item.Region.NoteHandle, item => item.Value);
        BreathVolumeService.SetPreviewValues(values);
    }

    private static void ApplyLine(MusicalEditorViewModel vm, WIVSMMidiPart part, Point start, Point end)
    {
        var deltaX = end.X - start.X;
        foreach (var region in BreathVolumeService.GetRegions(part))
        {
            var x = vm.CalcTickToViewPosition((VSMAbsTick)region.BeginTick);
            if (x < Math.Min(start.X, end.X) || x > Math.Max(start.X, end.X))
                continue;
            var ratio = Math.Abs(deltaX) < 0.001 ? 0.0 : (x - start.X) / deltaX;
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
        => ValueBottom(height) - Math.Max(1,
            Math.Clamp(value, MinValue, MaxValue) / (double)MaxValue * ValueHeight(height));

    private static int YToValue(double y, double height)
        => Math.Clamp((int)Math.Round(MaxValue * (1.0 - (y - NativeTopOffset) / ValueHeight(height))), MinValue, MaxValue);

    private static double ValueHeight(double height)
        => Math.Max(1, height - NativeTopOffset - NativeBottomOffset);

    private static double ValueBottom(double height)
        => NativeTopOffset + ValueHeight(height);

    private static double GetBarX(
        MusicalEditorViewModel vm,
        BreathRegion region,
        IReadOnlyDictionary<IntPtr, double> notePositions)
    {
        var x = vm.CalcTickToViewPosition((VSMAbsTick)region.BeginTick);
        return x <= 0 && notePositions.TryGetValue(region.NoteHandle, out var noteX)
            ? Math.Max(0, noteX)
            : Math.Max(0, x);
    }

    private Brush BarBrush => FindNativeBrush("Brush_Parameter_Normal", FallbackBarBrush);

    private Brush SelectedBarBrush => FindNativeBrush("Brush_Parameter_Selected", FallbackSelectedBarBrush);

    private Brush FindNativeBrush(string key, Brush fallback)
        => _view.TryFindResource(key) as Brush ?? fallback;

    private void UpdateIdleFeedback(MusicalEditorViewModel vm, Point point, BreathRegion? region)
    {
        if (vm.EditorMode.Mode == EditModeME.Arrow)
        {
            _canvas.Cursor = region.HasValue ? Cursors.Hand : null;
            if (region.HasValue)
                ShowNativeToolTip(point, BreathVolumeService.GetValue(region.Value.NoteHandle));
            else if (_nativeToolTip != null)
                _nativeToolTip.Visibility = Visibility.Hidden;
            return;
        }

        _canvas.Cursor = _view.Cursor;
        if (vm.EditorMode.Mode is EditModeME.Pencil or EditModeME.Line)
            ShowNativeGuide(point, YToValue(point.Y, vm.ViewHeight));
    }

    private void ShowNativeToolTip(Point point, int value)
        => ShowNativeLabel(_nativeToolTip, point, value);

    private void ShowNativeGuide(Point point, int value)
        => ShowNativeLabel(_nativeCursorGuide, point, value);

    private static void ShowNativeLabel(Label? label, Point point, int value)
    {
        if (label == null)
            return;
        label.Content = value.ToString(System.Globalization.CultureInfo.InvariantCulture);
        label.Margin = new Thickness(point.X + 14, point.Y + 7, 0, 0);
        label.Visibility = Visibility.Visible;
    }

    private void HideNativeGuides()
    {
        if (_nativeToolTip != null)
            _nativeToolTip.Visibility = Visibility.Hidden;
        if (_nativeCursorGuide != null)
            _nativeCursorGuide.Visibility = Visibility.Hidden;
    }

    private void OnMouseLeave(object sender, MouseEventArgs e)
        => HideNativeGuides();

    private void OnLostMouseCapture(object sender, MouseEventArgs e)
    {
        if (_gesture == GestureKind.None)
            return;
        _gesture = GestureKind.None;
        _gestureBefore = null;
        _selectionRectangle.Visibility = Visibility.Collapsed;
        _linePreview.Visibility = Visibility.Collapsed;
        HideNativeGuides();
        Refresh();
    }

    private void BeginValueEdit(BreathRegion region, Point point)
    {
        _cancelValueEdit = false;
        _valueEditor.Tag = region.NoteHandle;
        _valueEditor.Text = BreathVolumeService.GetValue(region.NoteHandle)
            .ToString(System.Globalization.CultureInfo.InvariantCulture);
        Canvas.SetLeft(_valueEditor, point.X + 8);
        Canvas.SetTop(_valueEditor, Math.Max(0, point.Y - _valueEditor.Height / 2));
        _valueEditor.Visibility = Visibility.Visible;
        _valueEditor.Dispatcher.BeginInvoke(new Action(() =>
        {
            _valueEditor.Focus();
            _valueEditor.SelectAll();
        }));
    }

    private void OnValueEditorKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            _cancelValueEdit = true;
            EndValueEdit(commit: false);
            e.Handled = true;
        }
        else if (e.Key == Key.Enter)
        {
            EndValueEdit(commit: true);
            e.Handled = true;
        }
    }

    private void EndValueEdit(bool commit)
    {
        if (_valueEditor.Visibility != Visibility.Visible)
            return;

        var handle = _valueEditor.Tag is IntPtr value ? value : IntPtr.Zero;
        var text = _valueEditor.Text;
        _valueEditor.Visibility = Visibility.Collapsed;
        _valueEditor.Tag = null;
        if (commit && handle != IntPtr.Zero &&
            int.TryParse(text, System.Globalization.NumberStyles.Integer,
                System.Globalization.CultureInfo.InvariantCulture, out var parsed) &&
            TryGetContext(out _, out var sequence, out var part))
        {
            BreathVolumeService.SetValues(sequence, part, new[] { handle }, parsed);
        }
        _cancelValueEdit = false;
        _canvas.Focus();
    }

    private static T Freeze<T>(T freezable) where T : Freezable
    {
        if (freezable.CanFreeze)
            freezable.Freeze();
        return freezable;
    }

    private const int MinValue = BreathVolumeService.MinValue;
    private const int MaxValue = BreathVolumeService.MaxValue;
    private const double NativeBarWidth = 10.0;
    private const double NativeSelectedAddWidth = 2.0;
    private const double NativeTopOffset = 7.0;
    private const double NativeBottomOffset = 9.0;

    private enum GestureKind
    {
        None,
        Move,
        Rectangle,
        Pencil,
        Line
    }
}

internal sealed class BreathVolumeGridLayer : FrameworkElement
{
    private MusicalEditorViewModel? _viewModel;
    private WIVSMSequence? _sequence;
    private Brush? _measureBrush;
    private Brush? _beatBrush;
    private Brush? _quantizeBrush;

    public BreathVolumeGridLayer()
    {
        Focusable = false;
        IsHitTestVisible = false;
    }

    public void Update(
        MusicalEditorViewModel viewModel,
        WIVSMSequence? sequence,
        Brush measureBrush,
        Brush beatBrush,
        Brush quantizeBrush)
    {
        _viewModel = viewModel;
        _sequence = sequence;
        _measureBrush = measureBrush;
        _beatBrush = beatBrush;
        _quantizeBrush = quantizeBrush;
        InvalidateVisual();
    }

    protected override void OnRender(DrawingContext drawingContext)
    {
        base.OnRender(drawingContext);
        if (_viewModel is not { ParameterViewer: { } viewer } vm ||
            _sequence == null || ActualWidth <= 0 || ActualHeight <= 0)
            return;

        var left = viewer.HorizontalOffset;
        var right = left + viewer.ViewportWidth;
        var (beginIndex, endIndex) = _sequence.GetBarIndex(
            vm.CalcViewPositionToTick(left, QuantizeStrategy.None),
            vm.CalcViewPositionToTick(right, QuantizeStrategy.None));
        if (beginIndex < 0 || endIndex < 0)
            return;

        var mainVm = Application.Current?.MainWindow?.DataContext as MainViewModel;
        var timeSignature = _sequence.DefaultTimeSigValue;
        var accumulatedMeasureWidth = 0.0;

        for (var barIndex = beginIndex; barIndex <= endIndex + 1; barIndex++)
        {
            var beginTick = _sequence.GetTickFromBar(barIndex);
            var endTick = _sequence.GetTickFromBar(barIndex + 1);
            var beginX = beginTick.Value * vm.WidthPerTick;
            var endX = endTick.Value * vm.WidthPerTick;
            if (beginX > right + 2 || beginX > ActualWidth)
                break;

            var current = mainVm?.GetTimeSigBeforePosBar(barIndex);
            if (current != null)
                timeSignature = current.Value;

            var measureWidth = Math.Max(0, endX - beginX);
            var beatWidth = timeSignature.Numer > 0
                ? measureWidth / timeSignature.Numer
                : measureWidth;

            if (measureWidth >= MinMeasureSpacing || accumulatedMeasureWidth >= MinMeasureSpacing)
            {
                DrawVertical(drawingContext, _measureBrush, beginX, 2);
                accumulatedMeasureWidth = 0;
            }
            else
            {
                accumulatedMeasureWidth += measureWidth;
            }

            if (beatWidth < MinBeatSpacing)
                continue;

            var quantizeWidth = vm.WidthPerQuantize;
            if (quantizeWidth >= MinQuantizeSpacing)
            {
                for (var x = beginX + quantizeWidth; x < endX - 0.5; x += quantizeWidth)
                {
                    var beatNumber = beatWidth <= 0 ? 0 : Math.Round((x - beginX) / beatWidth);
                    var beatX = beginX + beatNumber * beatWidth;
                    if (Math.Abs(x - beatX) >= 1)
                        DrawVertical(drawingContext, _quantizeBrush, x, 1);
                }
            }

            for (var beat = 1; beat < timeSignature.Numer; beat++)
                DrawVertical(drawingContext, _beatBrush, beginX + beat * beatWidth, 2);
        }
    }

    private void DrawVertical(DrawingContext drawingContext, Brush? brush, double x, double width)
    {
        if (brush == null || x + width < 0 || x > ActualWidth)
            return;
        drawingContext.DrawRectangle(brush, null, new Rect(x, 0, width, ActualHeight));
    }

    private const double MinMeasureSpacing = 50.0;
    private const double MinBeatSpacing = 25.0;
    private const double MinQuantizeSpacing = 12.5;
}
