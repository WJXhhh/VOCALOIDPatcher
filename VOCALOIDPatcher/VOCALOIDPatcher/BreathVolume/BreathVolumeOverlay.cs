using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
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
    private static readonly Brush FallbackBackgroundBrush = Freeze(new SolidColorBrush(Colors.Black));
    private static readonly Brush FallbackBarBrush = Freeze(new SolidColorBrush(Color.FromRgb(41, 171, 226)));
    private static readonly Brush FallbackSelectedBarBrush = Freeze(new SolidColorBrush(Color.FromRgb(174, 238, 255)));
    private static readonly Brush FallbackMeasureBrush = Freeze(new SolidColorBrush(Color.FromRgb(102, 102, 102)));
    private static readonly Brush FallbackBeatBrush = Freeze(new SolidColorBrush(Color.FromRgb(64, 64, 64)));
    private static readonly Brush FallbackQuantizeBrush = Freeze(new SolidColorBrush(Color.FromRgb(48, 48, 48)));
    private static readonly Brush TextBrush = Freeze(new SolidColorBrush(Color.FromRgb(190, 190, 195)));
    private static readonly Brush SelectionBrush = Freeze(new SolidColorBrush(Color.FromArgb(70, 90, 170, 240)));

    private readonly ParameterView _view;
    private readonly Canvas _canvas;
    private readonly UIControlParameterGridLine _gridLayer = new()
    {
        Focusable = false,
        IsHitTestVisible = false
    };
    private readonly Rectangle _selectionRectangle;
    private readonly Line _linePreview;
    private readonly Path _songPositionPath;
    private readonly TranslateTransform _songPositionTransform = new();
    private readonly TextBox _valueEditor;
    private readonly UIControlParameters? _nativeParameterCanvas;
    private readonly Path? _nativeSongPositionPath;
    private readonly TranslateTransform? _nativeSongPositionTransform;
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
            Focusable = true
        };
        _nativeParameterCanvas = AccessTools.Field(typeof(ParameterView), "xUIControlParameters")
            ?.GetValue(view) as UIControlParameters;
        _nativeSongPositionPath = AccessTools.Field(typeof(ParameterView), "pathSongPos")
            ?.GetValue(view) as Path;
        _nativeSongPositionTransform = AccessTools.Field(typeof(ParameterView), "songPosTranslate")
            ?.GetValue(view) as TranslateTransform;
        if (_nativeSongPositionTransform != null)
        {
            BindingOperations.SetBinding(
                _songPositionTransform,
                TranslateTransform.XProperty,
                new Binding(nameof(TranslateTransform.X))
                {
                    Source = _nativeSongPositionTransform,
                    Mode = BindingMode.OneWay
                });
        }
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
        _songPositionPath = new Path
        {
            Stroke = Brushes.White,
            StrokeThickness = 1,
            IsHitTestVisible = false,
            Focusable = false,
            RenderTransform = _songPositionTransform
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
            if (_nativeSongPositionPath != null)
                _nativeSongPositionPath.Visibility = Visibility.Visible;
            HideNativeGuides();
            return;
        }

        _canvas.Visibility = Visibility.Visible;
        if (_nativeParameterCanvas != null)
            _nativeParameterCanvas.Visibility = Visibility.Collapsed;
        _canvas.Width = Math.Max(vm.SongWidth, _view.ActualWidth);
        _canvas.Height = Math.Max(1, vm.ViewHeight);
        _canvas.Children.Clear();
        UpdateNativeSurface(vm, vm.VSMSequence);

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
        var notePositions = BuildNotePositions(vm, part);
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

    public void MoveSongPosition()
    {
        if (!_canvas.Dispatcher.CheckAccess())
        {
            _canvas.Dispatcher.BeginInvoke((Action)MoveSongPosition);
            return;
        }

        if (_nativeSongPositionTransform != null ||
            !IsVisible || _view.DataContext is not MusicalEditorViewModel vm)
            return;
        _songPositionTransform.X = _view.SongPosition.Value * vm.WidthPerTick;
    }

    public void Hide()
    {
        _gesture = GestureKind.None;
        _gestureBefore = null;
        _canvas.ReleaseMouseCapture();
        _canvas.Visibility = Visibility.Collapsed;
        if (_nativeParameterCanvas != null)
            _nativeParameterCanvas.Visibility = Visibility.Visible;
        if (_nativeSongPositionPath != null)
            _nativeSongPositionPath.Visibility = Visibility.Visible;
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
        var viewportLeft = vm.ParameterViewer?.HorizontalOffset ?? 0.0;
        Canvas.SetLeft(label, Math.Max(0, viewportLeft) + 12);
        Canvas.SetTop(label, 12);
        _canvas.Children.Add(label);
    }

    private void RestoreTransientObjects()
    {
        _canvas.Children.Add(_songPositionPath);
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
        var notePositions = BuildNotePositions(vm, part);
        var handles = BreathVolumeService.GetRegions(part).Where(region =>
        {
            var x1 = GetBarX(vm, region, notePositions);
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
        var notePositions = BuildNotePositions(vm, part);
        var values = BreathVolumeService.GetRegions(part).Select(region =>
        {
            var x = GetBarX(vm, region, notePositions);
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
        var notePositions = BuildNotePositions(vm, part);
        foreach (var region in BreathVolumeService.GetRegions(part))
        {
            var x = GetBarX(vm, region, notePositions);
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
        // VEL and OPE bars are anchored at the note start, not at an analysis
        // interval inside or before the note. A breath range may start earlier
        // than its note, and that tick difference grows visually with zoom.
        if (notePositions.TryGetValue(region.NoteHandle, out var noteX))
            return Math.Max(0, noteX);

        return Math.Max(0, vm.CalcTickToViewPosition((VSMAbsTick)region.BeginTick));
    }

    private static Dictionary<IntPtr, double> BuildNotePositions(
        MusicalEditorViewModel vm,
        WIVSMMidiPart part)
        => BuildNotes(part).ToDictionary(
            pair => pair.Key,
            pair => vm.CalcTickToViewPosition(pair.Value.AbsPosTick));

    private static Dictionary<IntPtr, WIVSMNote> BuildNotes(WIVSMMidiPart part)
    {
        var notes = new Dictionary<IntPtr, WIVSMNote>();
        for (ulong index = 0; index < part.NumNotes; index++)
        {
            var note = part.GetNote(index);
            if (note != null)
                notes[note.CppObjPtr] = note;
        }
        return notes;
    }

    private void UpdateNativeSurface(MusicalEditorViewModel vm, WIVSMSequence? sequence)
    {
        _canvas.Background = FindNativeBrush("Brush_TrackViewBackground", FallbackBackgroundBrush);
        UpdateNativeSongPosition(vm);
        if (sequence == null)
            return;

        var mainVm = Application.Current?.MainWindow?.DataContext as MainViewModel;
        _gridLayer.Width = _canvas.Width;
        _gridLayer.Height = _canvas.Height;
        _gridLayer.ViewHeight = vm.ViewHeight;
        _gridLayer.DefaultTimeSig = sequence.DefaultTimeSigValue;
        _gridLayer.ZSV = vm.ParameterViewer;
        _gridLayer.VSMSequence = sequence;
        _gridLayer.MainVM = mainVm;
        _gridLayer.MEVM = vm;
        _gridLayer.SongEndTick = mainVm?.TrackEditorVM?.SongEndTick ?? vm.SongEndTick;
        _gridLayer.WidthPerTick = vm.WidthPerTick;
        _gridLayer.BrushMeasure = FindNativeBrush("Brush_MeasureLine", FallbackMeasureBrush);
        _gridLayer.BrushBeat = FindNativeBrush("Brush_BeatLine", FallbackBeatBrush);
        _gridLayer.BrushQuantize = FindNativeBrush("Brush_GridLine", FallbackQuantizeBrush);
        _gridLayer.ControlParameterType = ControlParameterTypeEnum.Velocity;
        _gridLayer.QuantizeType = vm.Quantize;
        _gridLayer.WidthPerQuantize = vm.WidthPerQuantize;
        _gridLayer.InvalidateVisual();
        _canvas.Children.Add(_gridLayer);
    }

    private void UpdateNativeSongPosition(MusicalEditorViewModel vm)
    {
        if (_nativeSongPositionPath != null)
        {
            _songPositionPath.Style = _nativeSongPositionPath.Style;
            _nativeSongPositionPath.Visibility = Visibility.Collapsed;
        }
        _songPositionPath.Data = new LineGeometry(
            new Point(0, 0),
            new Point(0, _canvas.Height));
        MoveSongPosition();
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
