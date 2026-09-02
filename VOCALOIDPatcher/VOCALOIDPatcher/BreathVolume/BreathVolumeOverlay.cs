using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using HarmonyLib;
using VOCALOIDPatcher.RegisterShift;
using VOCALOIDPatcher.Translation;
using VOCALOIDPatcher.Utils;
using Yamaha.VOCALOID;
using Yamaha.VOCALOID.MusicalEditor;
using Yamaha.VOCALOID.VSM;

namespace VOCALOIDPatcher.BreathVolume;

internal sealed class BreathVolumeOverlay
{
    private static readonly ConditionalWeakTable<UIControlParameters, BreathVolumeOverlay> NativeSurfaces = new();
    private static readonly MethodInfo? UpdateOutsideActivePartLayerMethod =
        AccessTools.Method(typeof(ParameterView), "UpdateOutsideActivePartLayer");
    private static readonly Brush FallbackBackgroundBrush = Freeze(new SolidColorBrush(Colors.Black));
    private static readonly Brush FallbackBarBrush = Freeze(new SolidColorBrush(Color.FromRgb(41, 171, 226)));
    private static readonly Brush FallbackSelectedBarBrush = Freeze(new SolidColorBrush(Color.FromRgb(174, 238, 255)));
    private static readonly Brush FallbackMeasureBrush = Freeze(new SolidColorBrush(Color.FromRgb(102, 102, 102)));
    private static readonly Brush FallbackBeatBrush = Freeze(new SolidColorBrush(Color.FromRgb(64, 64, 64)));
    private static readonly Brush FallbackQuantizeBrush = Freeze(new SolidColorBrush(Color.FromRgb(48, 48, 48)));
    private static readonly Brush FallbackBaseBrush = Freeze(new SolidColorBrush(Color.FromRgb(96, 96, 96)));
    private static readonly Brush FallbackNomineeBrush = Freeze(new SolidColorBrush(Color.FromRgb(174, 238, 255)));
    private static readonly Brush TextBrush = Freeze(new SolidColorBrush(Color.FromRgb(190, 190, 195)));

    private readonly ParameterView _view;
    private readonly Grid _panel;
    private readonly Canvas _canvas;
    private readonly UIControlParameterGridLine _gridLayer = new()
    {
        Focusable = false,
        IsHitTestVisible = false
    };
    private readonly UIControlParameters _parameterLayer = new()
    {
        Focusable = false,
        IsHitTestVisible = false
    };
    private readonly UINomineeControlParameters _nomineeLayer = new()
    {
        Focusable = false,
        IsHitTestVisible = false
    };
    private readonly RegexTextBox _valueEditor;
    private readonly TextBlock _emptyStateLabel = new()
    {
        Foreground = TextBrush,
        FontSize = 12,
        IsHitTestVisible = false,
        Visibility = Visibility.Collapsed
    };
    private readonly Rectangle _zeroLine = new()
    {
        Height = 1,
        Fill = FallbackBaseBrush,
        IsHitTestVisible = false,
        Visibility = Visibility.Collapsed
    };
    private readonly UIControlParameters? _nativeParameterCanvas;
    private readonly Canvas? _nativeGuideCanvas;
    private readonly Canvas? _nativeTempObjectCanvas;
    private readonly Canvas? _nativeOutsidePartCanvas;
    private readonly Rectangle? _nativeOutsidePartLeftLayer;
    private readonly Rectangle? _nativeOutsidePartRightLayer;
    private readonly Path? _nativeSongPositionPath;
    private readonly ScaleTransform? _nativeScaleTransform;
    private readonly TranslateTransform? _nativeSongPosTranslate;
    private readonly Label? _nativeToolTip;
    private readonly Label? _nativeCursorGuide;
    private readonly int _nativeGuideZIndex;
    private readonly bool _nativeGuideHitTestVisible;
    private readonly int _nativeTempObjectZIndex;
    private readonly bool _nativeTempObjectHitTestVisible;
    private readonly int _nativeOutsidePartZIndex;
    private readonly bool _nativeOutsidePartHitTestVisible;
    private readonly List<Point> _dragPoints = new();
    private Dictionary<IntPtr, byte>? _gestureBefore;
    private readonly Dictionary<IntPtr, byte> _gesturePreview = new();
    private Point _previousNomineePoint = new(-1, -1);
    private IntPtr _gestureTargetHandle;
    private Point _gestureStart;
    private long _gestureStartTick;
    private IntPtr _selectionAnchor;
    private GestureKind _gesture;
    private int _refreshPending;
    private int _refreshing;
    private int _refreshPosted;
    private int _nativeInjectionGeneration;
    private string? _lastObservedRenderSignature;
    private bool _lastLoggedActive;
    private BreathRegionStatus _lastLoggedStatus = (BreathRegionStatus)(-1);
    private int _lastLoggedRegionCount = -1;
    private bool _cancelValueEdit;

    private BreathVolumeOverlay(ParameterView view, Grid panel)
    {
        _view = view;
        _panel = panel;
        _canvas = new Canvas
        {
            Background = Brushes.Transparent,
            ClipToBounds = true,
            Visibility = Visibility.Collapsed,
            Focusable = true,
            // Returning keyboard focus here after an inline value edit must not
            // apply VOCALOID's full-surface focus adorner to this song-wide canvas.
            FocusVisualStyle = null
        };
        _nativeParameterCanvas = AccessTools.Field(typeof(ParameterView), "xUIControlParameters")
            ?.GetValue(view) as UIControlParameters;
        NativeSurfaces.Add(_parameterLayer, this);
        _nativeGuideCanvas = AccessTools.Field(typeof(ParameterView), "xGuideCanvas")
            ?.GetValue(view) as Canvas;
        _nativeGuideZIndex = _nativeGuideCanvas == null ? 0 : Panel.GetZIndex(_nativeGuideCanvas);
        _nativeGuideHitTestVisible = _nativeGuideCanvas?.IsHitTestVisible ?? true;
        _nativeTempObjectCanvas = AccessTools.Field(typeof(ParameterView), "xTempObjectCanvas")
            ?.GetValue(view) as Canvas;
        _nativeTempObjectZIndex = _nativeTempObjectCanvas == null ? 0 : Panel.GetZIndex(_nativeTempObjectCanvas);
        _nativeTempObjectHitTestVisible = _nativeTempObjectCanvas?.IsHitTestVisible ?? true;
        _nativeOutsidePartCanvas = AccessTools.Field(typeof(ParameterView), "xOutsideActivePartCanvas")
            ?.GetValue(view) as Canvas;
        _nativeOutsidePartZIndex = _nativeOutsidePartCanvas == null ? 0 : Panel.GetZIndex(_nativeOutsidePartCanvas);
        _nativeOutsidePartHitTestVisible = _nativeOutsidePartCanvas?.IsHitTestVisible ?? true;
        _nativeOutsidePartLeftLayer = AccessTools.Field(typeof(ParameterView), "xOutsideActivePartLeftDarkLayer")
            ?.GetValue(view) as Rectangle;
        _nativeOutsidePartRightLayer = AccessTools.Field(typeof(ParameterView), "xOutsideActivePartRightDarkLayer")
            ?.GetValue(view) as Rectangle;
        _nativeSongPositionPath = AccessTools.Field(typeof(ParameterView), "pathSongPos")
            ?.GetValue(view) as Path;
        _nativeScaleTransform = AccessTools.Field(typeof(ParameterView), "scaleTransform")
            ?.GetValue(view) as ScaleTransform;
        _nativeSongPosTranslate = AccessTools.Field(typeof(ParameterView), "songPosTranslate")
            ?.GetValue(view) as TranslateTransform;
        _nativeToolTip = AccessTools.Field(typeof(ParameterView), "xToolTip")
            ?.GetValue(view) as Label;
        _nativeCursorGuide = AccessTools.Field(typeof(ParameterView), "xMouseCursorGuide")
            ?.GetValue(view) as Label;
        Panel.SetZIndex(_canvas, 1000);
        panel.Children.Add(_canvas);

        _valueEditor = new RegexTextBox
        {
            Width = 57,
            Height = 17,
            MaxLength = 3,
            Regex = new Regex("^[0-9]{0,3}$"),
            UseCreateMenu = true,
            Background = Brushes.White,
            Foreground = Brushes.Black,
            FontSize = 12,
            Visibility = Visibility.Collapsed
        };
        _valueEditor.PreviewKeyDown += OnValueEditorKeyDown;
        _valueEditor.LostKeyboardFocus += (_, _) => EndValueEdit(commit: !_cancelValueEdit);
        _canvas.Children.Add(_gridLayer);
        _canvas.Children.Add(_zeroLine);
        _canvas.Children.Add(_parameterLayer);
        _canvas.Children.Add(_nomineeLayer);
        _canvas.Children.Add(_emptyStateLabel);
        _canvas.Children.Add(_valueEditor);

        _canvas.MouseLeftButtonDown += OnMouseLeftButtonDown;
        _canvas.MouseMove += OnMouseMove;
        _canvas.MouseLeftButtonUp += OnMouseLeftButtonUp;
        _canvas.MouseRightButtonDown += OnMouseRightButtonDown;
        _canvas.MouseLeave += OnMouseLeave;
        _canvas.LostMouseCapture += OnLostMouseCapture;
        _canvas.PreviewKeyDown += OnCanvasPreviewKeyDown;
        _view.DataContextChanged += (_, _) => Refresh();
        BreathVolumeService.Changed += OnServiceChanged;
        RegisterShiftService.Changed += OnRegisterShiftChanged;
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

    public void ReassertCustomSurface()
    {
        if (_view.DataContext is not MusicalEditorViewModel vm ||
            !IsParameterActive(vm.ControlParameterType))
            return;

        _canvas.Visibility = Visibility.Visible;
        if (_nativeParameterCanvas != null)
            _nativeParameterCanvas.Visibility = Visibility.Collapsed;
        UpdateNativeSongPosition();
        ShowNativeGuideLayer();
        HideNativeOutsidePartLayer();
    }

    public void Refresh()
    {
        if (_canvas.Dispatcher.HasShutdownStarted)
            return;

        if (!_canvas.Dispatcher.CheckAccess())
        {
            QueueRefresh();
            return;
        }

        Interlocked.Exchange(ref _refreshPending, 1);
        if (Interlocked.CompareExchange(ref _refreshing, 1, 0) != 0)
            return;

        try
        {
            while (Interlocked.Exchange(ref _refreshPending, 0) != 0)
            {
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
                        ClearNativeBars();
                        if (_view.DataContext is MusicalEditorViewModel vm)
                            DrawEmptyState(IsRegisterMode
                                ? "VOCALOIDPatcher_RegisterShift_NoNotes"
                                : "VOCALOIDPatcher_BreathVolume_NoBreaths", vm);
                        RestoreTransientObjects();
                    }
                    catch
                    {
                        _canvas.Visibility = Visibility.Collapsed;
                    }
                }
            }
        }
        finally
        {
            Interlocked.Exchange(ref _refreshing, 0);
            if (Volatile.Read(ref _refreshPending) != 0)
                QueueRefresh();
        }
    }

    private void QueueRefresh()
    {
        if (_canvas.Dispatcher.HasShutdownStarted)
            return;
        Interlocked.Exchange(ref _refreshPending, 1);
        if (Interlocked.Exchange(ref _refreshPosted, 1) != 0)
            return;
        _canvas.Dispatcher.BeginInvoke(new Action(() =>
        {
            Interlocked.Exchange(ref _refreshPosted, 0);
            if (Interlocked.Exchange(ref _refreshPending, 0) == 0)
                return;
            if (Volatile.Read(ref _refreshing) != 0)
            {
                Interlocked.Exchange(ref _refreshPending, 1);
                return;
            }
            Refresh();
        }));
    }

    private void RefreshCore()
    {
        if (_view.DataContext is not MusicalEditorViewModel vm ||
            !IsParameterActive(vm.ControlParameterType))
        {
            LogOverlayState(active: false, BreathRegionStatus.Unknown, 0, 0);
            _canvas.Visibility = Visibility.Collapsed;
            if (_nativeParameterCanvas != null)
                _nativeParameterCanvas.Visibility = Visibility.Visible;
            UpdateNativeSongPosition();
            RestoreNativeGuideLayer();
            _view.EndDragRectangle();
            ClearNominees();
            ShowNativeOutsidePartLayer();
            HideNativeGuides();
            return;
        }

        _canvas.Visibility = Visibility.Visible;
        if (_nativeParameterCanvas != null)
            _nativeParameterCanvas.Visibility = Visibility.Collapsed;
        HideNativeOutsidePartLayer();
        if (_nativeSongPositionPath != null)
            _nativeSongPositionPath.Visibility = Visibility.Visible;
        ShowNativeGuideLayer();
        _canvas.Width = Math.Max(vm.SongWidth, _view.ActualWidth);
        _canvas.Height = Math.Max(1, vm.ViewHeight);
        _emptyStateLabel.Visibility = Visibility.Collapsed;
        UpdateNativeSurface(vm, vm.VSMSequence);

        var part = vm.ActivePart;
        var sequence = vm.VSMSequence;
        if (part == null || sequence == null)
        {
            ClearNativeBars();
            DrawEmptyState(IsRegisterMode
                ? "VOCALOIDPatcher_RegisterShift_NoActivePart"
                : "VOCALOIDPatcher_BreathVolume_NoActivePart", vm);
            RestoreTransientObjects();
            return;
        }

        var status = IsRegisterMode ? BreathRegionStatus.Ready : BreathVolumeService.GetRegionStatus(part);
        if (IsRegisterMode && !RegisterShiftService.IsSupportedForPart(part))
        {
            ClearNativeBars();
            DrawEmptyState("VOCALOIDPatcher_RegisterShift_Unsupported", vm);
            RestoreTransientObjects();
            return;
        }
        var regions = GetRegions(part);
        if (!IsRegisterMode &&
            (status == BreathRegionStatus.Unknown ||
             regions.Count == 0 && status is BreathRegionStatus.Ready or BreathRegionStatus.Faulted))
        {
            if (BreathVolumeService.EnsureRegionsAsync(sequence, part))
            {
                status = BreathRegionStatus.Loading;
                regions = GetRegions(part);
            }
        }
        LogOverlayState(active: true, status, regions.Count, part.NumNotes);
        if (regions.Count == 0)
        {
            ClearNativeBars();
            DrawEmptyState(IsRegisterMode
                ? "VOCALOIDPatcher_RegisterShift_NoNotes"
                : status == BreathRegionStatus.Loading
                    ? "VOCALOIDPatcher_BreathVolume_Loading"
                    : "VOCALOIDPatcher_BreathVolume_NoBreaths",
                vm);
            RestoreTransientObjects();
            return;
        }

        UpdateNativeBars(vm, part, regions);

        _zeroLine.Visibility = IsRegisterMode ? Visibility.Visible : Visibility.Collapsed;
        _zeroLine.Width = _canvas.Width;
        Canvas.SetLeft(_zeroLine, 0);
        Canvas.SetTop(_zeroLine,
            ValueToY(RegisterShiftService.DefaultValue + RegisterShiftService.DisplayOffset,
                vm.ViewHeight));

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
        _view.EndDragRectangle();
        ClearNominees();
        _canvas.Visibility = Visibility.Collapsed;
        _canvas.Cursor = null;
        if (_nativeParameterCanvas != null)
            _nativeParameterCanvas.Visibility = Visibility.Visible;
        UpdateNativeSongPosition();
        RestoreNativeGuideLayer();
        ShowNativeOutsidePartLayer();
        HideNativeGuides();
        _valueEditor.Visibility = Visibility.Collapsed;
    }

    private void DrawEmptyState(string key, MusicalEditorViewModel vm)
    {
        _emptyStateLabel.Text = TranslationManager.Tr(key);
        var viewportLeft = vm.ParameterViewer?.HorizontalOffset ?? 0.0;
        Canvas.SetLeft(_emptyStateLabel, Math.Max(0, viewportLeft) + 12);
        Canvas.SetTop(_emptyStateLabel, 12);
        EnsureCanvasChild(_emptyStateLabel);
        _emptyStateLabel.Visibility = Visibility.Visible;
    }

    private void RestoreTransientObjects()
    {
        EnsureCanvasChild(_valueEditor);
    }

    private void EnsureCanvasChild(UIElement child)
    {
        if (!ReferenceEquals(VisualTreeHelper.GetParent(child), _canvas))
            _canvas.Children.Add(child);
    }

    private void OnMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (!TryGetContext(out var vm, out var sequence, out var part))
            return;

        _canvas.Focus();
        _gestureStart = e.GetPosition(_canvas);
        var region = FindRegion(vm, part, _gestureStart);
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
            var handles = GetSelection();
            _gestureBefore = Snapshot(handles);
            _gesturePreview.Clear();
            _gestureTargetHandle = region.Value.NoteHandle;
            _gesture = GestureKind.MoveWait;
        }
        else if (mode == EditModeME.Arrow)
        {
            _gesture = GestureKind.SongPositionJump;
        }
        else if (mode is EditModeME.Pencil or EditModeME.Line)
        {
            var tick = vm.CalcViewPositionToTick(_gestureStart.X, QuantizeStrategy.None);
            if (tick < part.AbsBeginTick || tick > part.AbsEndTick)
            {
                e.Handled = true;
                return;
            }
            var handles = GetRegions(part).Select(item => item.NoteHandle).Distinct().ToArray();
            _gestureBefore = Snapshot(handles);
            _gesturePreview.Clear();
            _gestureStartTick = vm.CalcViewPositionToTick(_gestureStart.X, QuantizeStrategy.Nearest).Value;
            _gesture = mode == EditModeME.Line ? GestureKind.LineWait : GestureKind.PencilWait;
            ClearNominees();
        }
        else
        {
            // BVL does not expose native VSM controllers. Do not let the
            // underlying ParameterView behaviors reinterpret this input as a
            // breakpoint edit for the synthetic parameter enum.
            e.Handled = true;
            return;
        }

        _canvas.CaptureMouse();
        vm.EditorMode.IsIdleMouseOperation = false;
        e.Handled = true;
    }

    private void OnMouseMove(object sender, MouseEventArgs e)
    {
        if (!TryGetContext(out var vm, out _, out var part))
            return;

        var point = e.GetPosition(_canvas);
        if (_gesture == GestureKind.None || e.LeftButton != MouseButtonState.Pressed)
        {
            UpdateIdleFeedback(vm, point, FindRegion(vm, part, point));
            e.Handled = true;
            return;
        }

        switch (_gesture)
        {
            case GestureKind.SongPositionJump:
                if (!Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
                    ClearSelection();
                _gesture = GestureKind.Rectangle;
                _view.BeginDragRectangle(_gestureStart);
                _view.DragRectangle(point, _gestureStart);
                break;
            case GestureKind.MoveWait:
                _gesture = GestureKind.Move;
                goto case GestureKind.Move;
            case GestureKind.Move:
                if (_gestureBefore != null)
                {
                    var delta = YToValue(point.Y, vm.ViewHeight) -
                                YToValue(_gestureStart.Y, vm.ViewHeight);
                    _gesturePreview.Clear();
                    foreach (var pair in _gestureBefore)
                        _gesturePreview[pair.Key] = (byte)Math.Clamp(pair.Value + delta, MinValue, MaxValue);
                    UpdateMoveNominee(vm, part, _gesturePreview);
                    _canvas.Cursor = Keyboard.Modifiers.HasFlag(ModifierKeys.Alt)
                        ? VocCursors.Duplicate
                        : Cursors.Hand;
                    if (_gesturePreview.TryGetValue(_gestureTargetHandle, out var targetValue))
                        ShowNativeGuide(vm, point, targetValue);
                }
                break;
            case GestureKind.Rectangle:
                _view.DragRectangle(point, _gestureStart);
                break;
            case GestureKind.PencilWait:
                _gesture = GestureKind.Pencil;
                UpdateNominee(vm, part, point, GestureKind.Pencil, quantizeEnd: true);
                ApplyDragPoints(part, _dragPoints, _gesturePreview);
                _canvas.Cursor = VocCursors.Pencil;
                ShowNativeGuide(vm, point, YToValue(point.Y, vm.ViewHeight));
                break;
            case GestureKind.Pencil:
                UpdateNominee(vm, part, point, GestureKind.Pencil);
                ApplyDragPoints(part, _dragPoints, _gesturePreview);
                _canvas.Cursor = VocCursors.Pencil;
                ShowNativeGuide(vm, point, YToValue(point.Y, vm.ViewHeight));
                break;
            case GestureKind.LineWait:
                _gesture = GestureKind.Line;
                UpdateNominee(vm, part, point, GestureKind.Line, quantizeEnd: true);
                _canvas.Cursor = VocCursors.LinePencil;
                ShowNativeGuide(vm, point, YToValue(point.Y, vm.ViewHeight));
                break;
            case GestureKind.Line:
                UpdateNominee(vm, part, point, GestureKind.Line);
                _canvas.Cursor = VocCursors.LinePencil;
                ShowNativeGuide(vm, point, YToValue(point.Y, vm.ViewHeight));
                break;
        }
        vm.HorizontalDragScroll = point.X;
        e.Handled = true;
    }

    private void OnMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (_gesture == GestureKind.None || !TryGetContext(out var vm, out var sequence, out var part))
            return;

        var point = e.GetPosition(_canvas);
        if (_gesture == GestureKind.SongPositionJump)
        {
            if (!Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
                ClearSelection();
            if (App.AudioPlayer?.IsPlaying != true && !vm.IsRecording &&
                Application.Current?.MainWindow?.DataContext is MainViewModel mainVm)
            {
                mainVm.SetCurrentPosition(vm.CalcViewPositionToTick(point.X, QuantizeStrategy.Nearest));
            }
        }
        else if (_gesture == GestureKind.Rectangle)
            CompleteRectangleSelection(vm, part, _gestureStart, point);
        else if (_gesture == GestureKind.Line)
            ApplyDragPoints(part, _dragPoints, _gesturePreview);

        if (_gestureBefore != null && _gesture is GestureKind.Move or GestureKind.Pencil or GestureKind.Line)
        {
            SetPreviewValues(_gesturePreview);
            CommitValues(sequence, part, _gestureBefore);
        }

        _gesture = GestureKind.None;
        _gestureBefore = null;
        _gestureTargetHandle = IntPtr.Zero;
        _gesturePreview.Clear();
        _view.EndDragRectangle();
        ClearNominees();
        _canvas.ReleaseMouseCapture();
        vm.EditorMode.IsIdleMouseOperation = true;
        HideNativeGuides();
        e.Handled = true;
    }

    private void OnMouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (!TryGetContext(out var vm, out var sequence, out var part))
            return;

        var region = FindRegion(vm, part, e.GetPosition(_canvas));
        if (region.HasValue && !IsSelected(region.Value.NoteHandle))
            SetSelection(new[] { region.Value.NoteHandle });

        var menu = new ContextMenu();
        var reset = new MenuItem { Header = TranslationManager.Tr(IsRegisterMode
            ? "VOCALOIDPatcher_RegisterShift_Reset" : "VOCALOIDPatcher_BreathVolume_Reset") };
        reset.Click += (_, _) => ResetSelected(sequence, part);
        menu.Items.Add(reset);
        _canvas.ContextMenu = menu;
        menu.IsOpen = true;
        e.Handled = true;
    }

    private void SelectClickedRegion(WIVSMMidiPart part, BreathRegion clicked, ModifierKeys modifiers)
    {
        var regions = GetRegions(part);
        if (modifiers.HasFlag(ModifierKeys.Shift) && _selectionAnchor != IntPtr.Zero)
        {
            var first = IndexOf(regions, _selectionAnchor);
            var second = IndexOf(regions, clicked.NoteHandle);
            if (first >= 0 && second >= 0)
            {
                var handles = regions.Skip(Math.Min(first, second)).Take(Math.Abs(first - second) + 1)
                    .Select(region => region.NoteHandle);
                SetSelection(handles, modifiers.HasFlag(ModifierKeys.Control));
            }
            else
            {
                SetSelection(new[] { clicked.NoteHandle }, modifiers.HasFlag(ModifierKeys.Control));
            }
        }
        else if (modifiers.HasFlag(ModifierKeys.Control))
        {
            ToggleSelection(clicked.NoteHandle);
        }
        else if (!IsSelected(clicked.NoteHandle))
        {
            SetSelection(new[] { clicked.NoteHandle });
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
        var handles = GetRegions(part).Where(region =>
        {
            var x1 = GetBarX(vm, region, notePositions);
            var x2 = x1 + NativeBarWidth;
            var y = ValueToY(GetDisplayValue(region.NoteHandle), vm.ViewHeight);
            return x2 >= left && x1 <= right && ValueBottom(vm.ViewHeight) >= top && y <= bottom;
        }).Select(region => region.NoteHandle);
        SetSelection(handles, Keyboard.Modifiers.HasFlag(ModifierKeys.Control));
    }

    private void ApplyDragPoints(
        WIVSMMidiPart part,
        IReadOnlyList<Point> dragPoints,
        IDictionary<IntPtr, byte> preview)
    {
        preview.Clear();
        if (dragPoints.Count == 0)
            return;
        var first = dragPoints[0];
        var last = dragPoints[^1];
        var notes = BuildNotes(part);
        foreach (var region in GetRegions(part))
        {
            if (!notes.TryGetValue(region.NoteHandle, out var note))
                continue;
            var tick = note.AbsPosTick.Value;
            if (tick < first.X || tick > last.X)
                continue;

            var value = (int)first.Y;
            var previous = first;
            foreach (var current in dragPoints)
            {
                if (current.X == tick)
                {
                    value = (int)current.Y;
                    break;
                }
                if (tick < current.X && previous.X >= 0 && current.X != previous.X)
                {
                    value = (int)(previous.Y + (current.Y - previous.Y) *
                        (tick - previous.X) / (current.X - previous.X));
                    break;
                }
                previous = current;
            }
            preview[region.NoteHandle] = (byte)Math.Clamp(value, MinValue, MaxValue);
        }
    }

    private bool TryGetContext(
        out MusicalEditorViewModel vm,
        out WIVSMSequence sequence,
        out WIVSMMidiPart part)
    {
        vm = _view.DataContext as MusicalEditorViewModel ?? null!;
        sequence = vm?.VSMSequence ?? null!;
        part = vm?.ActivePart ?? null!;
        return vm != null && sequence != null && part != null && IsParameterActive(vm.ControlParameterType);
    }

    private BreathRegion? FindRegion(MusicalEditorViewModel vm, WIVSMMidiPart part, Point point)
    {
        var notePositions = BuildNotePositions(vm, part);
        var regions = GetRegions(part);
        for (var index = regions.Count - 1; index >= 0; index--)
        {
            var region = regions[index];
            var left = GetBarX(vm, region, notePositions);
            var width = NativeBarWidth +
                        (IsSelected(region.NoteHandle) ? NativeSelectedAddWidth : 0);
            var top = ValueToY(GetDisplayValue(region.NoteHandle), vm.ViewHeight);
            if (point.X >= left && point.X <= left + width &&
                point.Y >= top && point.Y <= ValueBottom(vm.ViewHeight))
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
        QueueRefresh();
    }

    private void OnRegisterShiftChanged(WIVSMMidiPart? part)
        => OnServiceChanged(BreathVolumeChangeKind.Values, part);

    private static int IndexOf(IReadOnlyList<BreathRegion> regions, IntPtr handle)
    {
        for (var index = 0; index < regions.Count; index++)
            if (regions[index].NoteHandle == handle)
                return index;
        return -1;
    }

    private double ValueToY(int value, double height)
        => ValueBottom(height) - Math.Max(1,
            Math.Clamp(value, MinValue, MaxValue) / (double)MaxValue * ValueHeight(height));

    private int YToValue(double y, double height)
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
        _panel.Background = FindNativeBrush("Brush_TrackViewBackground", FallbackBackgroundBrush);
        _canvas.Background = FindNativeBrush("Brush_TrackViewBackground", FallbackBackgroundBrush);
        UpdateNativeSongPosition();
        UpdateNativeOutsidePartLayer(vm);
        if (sequence != null)
        {
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
            _gridLayer.BrushBase = FindNativeBrush("Brush_Parameter_BaseLine", FallbackBaseBrush);
            _gridLayer.ControlParameterType = ControlParameterTypeEnum.Velocity;
            _gridLayer.QuantizeType = vm.Quantize;
            _gridLayer.WidthPerQuantize = vm.WidthPerQuantize;
            _gridLayer.InvalidateVisual();
            _gridLayer.Visibility = Visibility.Visible;
            EnsureCanvasChild(_gridLayer);
        }
        else
        {
            _gridLayer.Visibility = Visibility.Collapsed;
        }

        _parameterLayer.Visibility = Visibility.Visible;
        _parameterLayer.Width = _canvas.Width;
        _parameterLayer.Height = _canvas.Height;
        _parameterLayer.ControlParameterType = ControlParameterTypeEnum.Velocity;
        _parameterLayer.ZSV = vm.ParameterViewer;
        _parameterLayer.Minimum = MinValue;
        _parameterLayer.Maximum = MaxValue;
        _parameterLayer.WidthPerTick = vm.WidthPerTick;
        _parameterLayer.ViewHeight = vm.ViewHeight;
        EnsureCanvasChild(_parameterLayer);

        _nomineeLayer.Visibility = Visibility.Visible;
        _nomineeLayer.Width = _canvas.Width;
        _nomineeLayer.Height = _canvas.Height;
        _nomineeLayer.ControlParameterType = ControlParameterTypeEnum.Velocity;
        _nomineeLayer.ZSV = vm.ParameterViewer;
        _nomineeLayer.Minimum = MinValue;
        _nomineeLayer.Maximum = MaxValue;
        _nomineeLayer.WidthPerTick = vm.WidthPerTick;
        _nomineeLayer.ViewHeight = vm.ViewHeight;
        EnsureCanvasChild(_nomineeLayer);
    }

    private void UpdateNativeBars(
        MusicalEditorViewModel vm,
        WIVSMMidiPart part,
        IReadOnlyList<BreathRegion> regions)
    {
        var notes = BuildNotes(part);
        var matchedNotes = 0;
        var nativePart = new UIControlParameter
        {
            Part = part,
            BrushControl = BarBrush,
            BrushControlSelected = SelectedBarBrush
        };
        foreach (var region in regions)
        {
            if (!notes.TryGetValue(region.NoteHandle, out var note))
                continue;

            matchedNotes++;

            nativePart.Ctrls.Add(new Bar
            {
                Note = note,
                Minimum = MinValue,
                Maximum = MaxValue,
                RelPosTick = (VSMRelTick)note.AbsPosTick.Value,
                Value = GetDisplayValue(region.NoteHandle),
                ViewHeight = vm.ViewHeight,
                IsSelected = IsSelected(region.NoteHandle)
            });
        }

        _parameterLayer.ControlParameters.Clear();
        _parameterLayer.ControlParameters.Add(nativePart);
        Interlocked.Increment(ref _nativeInjectionGeneration);
        _parameterLayer.InvalidateVisual();
    }

    private void ClearNativeBars()
    {
        _zeroLine.Visibility = Visibility.Collapsed;
        _parameterLayer.ControlParameters.Clear();
        _parameterLayer.InvalidateVisual();
    }

    private void ClearNominees()
    {
        _dragPoints.Clear();
        _previousNomineePoint = new Point(-1, -1);
        _nomineeLayer.ControlParameters.Clear();
        _nomineeLayer.InvalidateVisual();
    }

    private void UpdateNominee(
        MusicalEditorViewModel vm,
        WIVSMMidiPart part,
        Point point,
        GestureKind gesture,
        bool quantizeEnd = false)
    {
        var rawTick = vm.CalcViewPositionToTick(
            point.X,
            quantizeEnd ? QuantizeStrategy.Nearest : QuantizeStrategy.None).Value;
        var tick = Math.Clamp(rawTick, part.AbsBeginTick.Value, part.AbsEndTick.Value);
        var value = YToValue(point.Y, vm.ViewHeight);
        if (gesture == GestureKind.Line)
        {
            var startTick = _gestureStartTick;
            var endY = point.Y;
            if (rawTick != tick && rawTick != startTick)
            {
                endY = _gestureStart.Y + (point.Y - _gestureStart.Y) *
                    (tick - startTick) / (double)(rawTick - startTick);
                value = YToValue(endY, vm.ViewHeight);
            }
            _dragPoints.Clear();
            _dragPoints.Add(new Point(
                Math.Clamp(startTick, part.AbsBeginTick.Value, part.AbsEndTick.Value),
                YToValue(_gestureStart.Y, vm.ViewHeight)));
            if (startTick != tick)
            {
                _dragPoints.Add(new Point(tick, value));
                _dragPoints.Sort((left, right) => left.X.CompareTo(right.X));
            }
        }
        else
        {
            var candidate = new Point(tick, value);
            if (_previousNomineePoint.X >= 0 && _previousNomineePoint.Y >= 0 && _dragPoints.Count != 0)
            {
                for (var index = _dragPoints.Count - 1; index >= 0; index--)
                {
                    var existing = _dragPoints[index];
                    if (_previousNomineePoint.X < tick)
                    {
                        if (_previousNomineePoint.X < existing.X && existing.X <= tick)
                            _dragPoints.RemoveAt(index);
                    }
                    else if (tick < _previousNomineePoint.X)
                    {
                        if (tick <= existing.X && existing.X < _previousNomineePoint.X)
                            _dragPoints.RemoveAt(index);
                    }
                    else if (existing.X == tick)
                    {
                        _dragPoints.RemoveAt(index);
                    }
                }
            }
            _dragPoints.Add(candidate);
            _dragPoints.Sort((left, right) => left.X.CompareTo(right.X));
            _previousNomineePoint = candidate;
        }

        var brush = FindNativeBrush("Brush_Parameter_Nominee", FallbackNomineeBrush);
        var pen = Freeze(new Pen(brush, 1));
        var nativePart = new UINomineeControlParameter
        {
            Part = part,
            BrushControl = brush,
            PenControl = pen
        };
        NomineeBreakPoint? previousBreakPoint = null;
        foreach (var dragPoint in _dragPoints)
        {
            var breakPoint = new NomineeBreakPoint
            {
                RelPosTick = (VSMRelTick)(long)dragPoint.X,
                Value = (int)dragPoint.Y,
                Minimum = MinValue,
                Maximum = MaxValue,
                ViewHeight = vm.ViewHeight
            };
            if (gesture == GestureKind.Line || previousBreakPoint == null || previousBreakPoint.Value != breakPoint.Value)
                nativePart.Ctrls.Add(breakPoint);
            previousBreakPoint = breakPoint;
        }

        _nomineeLayer.NomineeType = gesture == GestureKind.Line
            ? NomineeEditToolType.Line
            : NomineeEditToolType.Pencil;
        _nomineeLayer.ControlParameters.Clear();
        _nomineeLayer.ControlParameters.Add(nativePart);
        _nomineeLayer.InvalidateVisual();
    }

    private void UpdateMoveNominee(
        MusicalEditorViewModel vm,
        WIVSMMidiPart part,
        IReadOnlyDictionary<IntPtr, byte> preview)
    {
        var notes = BuildNotes(part);
        var drawHeight = Math.Max(1, vm.DrawHeight);
        var brush = FindNativeBrush("Brush_Parameter_Nominee", FallbackNomineeBrush);
        var nativePart = new UINomineeControlParameter
        {
            Part = part,
            BrushControl = brush
        };
        foreach (var pair in preview)
        {
            if (!notes.TryGetValue(pair.Key, out var note))
                continue;
            var scaledValue = (int)(pair.Value * (drawHeight / MaxValue));
            nativePart.Ctrls.Add(new NomineeBar
            {
                Note = note,
                RelPosTick = (VSMRelTick)note.AbsPosTick.Value,
                Value = scaledValue,
                OriginalValue = scaledValue,
                Minimum = 0,
                Maximum = (int)drawHeight,
                ViewHeight = drawHeight
            });
        }

        _nomineeLayer.NomineeType = NomineeEditToolType.Other;
        _nomineeLayer.ControlParameters.Clear();
        _nomineeLayer.ControlParameters.Add(nativePart);
        _nomineeLayer.InvalidateVisual();
    }

    private void UpdateNativeSongPosition()
    {
        if (_nativeScaleTransform != null)
        {
            if (_nativeScaleTransform.ScaleX != 1.0)
                _nativeScaleTransform.ScaleX = 1.0;
            if (_nativeScaleTransform.ScaleY != 1.0)
                _nativeScaleTransform.ScaleY = 1.0;
        }

        if (_nativeSongPositionPath != null)
        {
            _nativeSongPositionPath.Data = new LineGeometry(
                new Point(0, 0),
                new Point(0, _canvas.Height));
            _nativeSongPositionPath.Visibility = Visibility.Visible;
        }

        if (_nativeSongPosTranslate != null && _view.DataContext is MusicalEditorViewModel vm)
        {
            _nativeSongPosTranslate.X = (double)(int)(long)_view.SongPosition * vm.WidthPerTick;
        }
    }

    private void ShowNativeGuideLayer()
    {
        if (_nativeOutsidePartCanvas != null)
        {
            Panel.SetZIndex(_nativeOutsidePartCanvas, 1001);
            _nativeOutsidePartCanvas.IsHitTestVisible = false;
        }
        if (_nativeTempObjectCanvas != null)
        {
            Panel.SetZIndex(_nativeTempObjectCanvas, 1002);
            _nativeTempObjectCanvas.IsHitTestVisible = false;
        }
        if (_nativeGuideCanvas != null)
        {
            Panel.SetZIndex(_nativeGuideCanvas, 1003);
            _nativeGuideCanvas.IsHitTestVisible = false;
        }
    }

    private void RestoreNativeGuideLayer()
    {
        if (_nativeOutsidePartCanvas != null)
        {
            Panel.SetZIndex(_nativeOutsidePartCanvas, _nativeOutsidePartZIndex);
            _nativeOutsidePartCanvas.IsHitTestVisible = _nativeOutsidePartHitTestVisible;
        }
        if (_nativeTempObjectCanvas != null)
        {
            Panel.SetZIndex(_nativeTempObjectCanvas, _nativeTempObjectZIndex);
            _nativeTempObjectCanvas.IsHitTestVisible = _nativeTempObjectHitTestVisible;
        }
        if (_nativeGuideCanvas != null)
        {
            Panel.SetZIndex(_nativeGuideCanvas, _nativeGuideZIndex);
            _nativeGuideCanvas.IsHitTestVisible = _nativeGuideHitTestVisible;
        }
    }

    private void UpdateNativeOutsidePartLayer(MusicalEditorViewModel vm)
    {
        if (_nativeOutsidePartCanvas == null)
            return;
        UpdateOutsideActivePartLayerMethod?.Invoke(_view, new object[] { vm });
        // BVL/REG already renders the active-part content itself. Keep the
        // native outside-part dark layer hidden so it cannot dim the custom
        // parameter panel or cover it with a gray mask.
        HideNativeOutsidePartLayer();
    }

    private void HideNativeOutsidePartLayer()
    {
        if (_nativeOutsidePartCanvas == null)
            return;
        _nativeOutsidePartCanvas.Visibility = Visibility.Collapsed;
        _nativeOutsidePartCanvas.IsHitTestVisible = false;
        if (_nativeOutsidePartLeftLayer != null)
            _nativeOutsidePartLeftLayer.Visibility = Visibility.Collapsed;
        if (_nativeOutsidePartRightLayer != null)
            _nativeOutsidePartRightLayer.Visibility = Visibility.Collapsed;
    }

    private void ShowNativeOutsidePartLayer()
    {
        if (_nativeOutsidePartCanvas == null)
            return;

        if (_view.DataContext is MusicalEditorViewModel vm)
            UpdateOutsideActivePartLayerMethod?.Invoke(_view, new object[] { vm });
        else
        {
            if (_nativeOutsidePartLeftLayer != null)
                _nativeOutsidePartLeftLayer.Visibility = Visibility.Visible;
            if (_nativeOutsidePartRightLayer != null)
                _nativeOutsidePartRightLayer.Visibility = Visibility.Collapsed;
        }

        _nativeOutsidePartCanvas.Visibility = Visibility.Visible;
        _nativeOutsidePartCanvas.IsHitTestVisible = _nativeOutsidePartHitTestVisible;
    }

    private void ObserveNativeRenderCore(UIControlParameters surface)
    {
        var generation = Volatile.Read(ref _nativeInjectionGeneration);
        if (generation == 0 || !IsVisible ||
            _view.DataContext is not MusicalEditorViewModel vm || vm.ActivePart is not { } part)
            return;

        var viewportLeft = surface.ZSV?.HorizontalOffset ?? -1;
        var viewportRight = viewportLeft + (surface.ZSV?.ViewportWidth ?? 0);
        var visibleParts = 0;
        var nativeBars = 0;
        var visibleBars = 0;
        var firstBarX = double.NaN;
        var lastBarX = double.NaN;
        foreach (var nativePart in surface.ControlParameters)
        {
            if (nativePart.Part != null)
            {
                var partLeft = nativePart.Part.AbsPosTick.Value * surface.WidthPerTick;
                var partRight = nativePart.Part.AbsEndTick.Value * surface.WidthPerTick;
                if (partRight >= viewportLeft && partLeft <= viewportRight)
                    visibleParts++;
            }

            foreach (var control in nativePart.Ctrls)
            {
                if (control is not Bar bar)
                    continue;
                nativeBars++;
                var x = bar.RelPosTick.Value * surface.WidthPerTick;
                if (double.IsNaN(firstBarX) || x < firstBarX)
                    firstBarX = x;
                if (double.IsNaN(lastBarX) || x > lastBarX)
                    lastBarX = x;
                if (x + NativeBarWidth >= viewportLeft && x <= viewportRight)
                    visibleBars++;
            }
        }

        var parent = surface.Parent as Canvas;
        var signature = string.Join('|', generation, (int)surface.ControlParameterType,
            surface.ControlParameters.Count, nativeBars, visibleBars, surface.Visibility,
            surface.Opacity, parent?.Visibility, parent?.Opacity, _canvas.Visibility,
            _nativeOutsidePartCanvas?.Visibility);
        if (string.Equals(signature, _lastObservedRenderSignature, StringComparison.Ordinal))
            return;
        _lastObservedRenderSignature = signature;

        BreathVolumeDiagnosticsLog.WriteUiState("nativeRender", new Dictionary<string, object?>
        {
            ["generation"] = generation,
            ["parameterType"] = (int)surface.ControlParameterType,
            ["nativeParts"] = surface.ControlParameters.Count,
            ["visibleParts"] = visibleParts,
            ["nativeBars"] = nativeBars,
            ["visibleBars"] = visibleBars,
            ["firstBarX"] = firstBarX,
            ["lastBarX"] = lastBarX,
            ["viewportLeft"] = viewportLeft,
            ["viewportRight"] = viewportRight,
            ["surfaceWidth"] = surface.Width,
            ["surfaceActualWidth"] = surface.ActualWidth,
            ["surfaceHeight"] = surface.Height,
            ["surfaceActualHeight"] = surface.ActualHeight,
            ["surfaceVisibility"] = surface.Visibility.ToString(),
            ["surfaceIsVisible"] = surface.IsVisible,
            ["surfaceOpacity"] = surface.Opacity,
            ["surfaceParentVisibility"] = parent?.Visibility.ToString(),
            ["surfaceParentIsVisible"] = parent?.IsVisible ?? false,
            ["surfaceParentOpacity"] = parent?.Opacity ?? -1,
            ["surfaceParentClip"] = DescribeGeometry(parent?.Clip),
            ["surfaceClip"] = DescribeGeometry(surface.Clip),
            ["overlayVisibility"] = _canvas.Visibility.ToString(),
            ["overlayOpacity"] = _canvas.Opacity,
            ["overlayBackground"] = DescribeBrush(_canvas.Background),
            ["overlayActualWidth"] = _canvas.ActualWidth,
            ["overlayActualHeight"] = _canvas.ActualHeight,
            ["panelBackground"] = DescribeBrush(_panel.Background),
            ["normalBrush"] = DescribeBrush(BarBrush),
            ["selectedBrush"] = DescribeBrush(SelectedBarBrush),
            ["outsideVisibility"] = _nativeOutsidePartCanvas?.Visibility.ToString(),
            ["partId"] = RuntimeObservationLog.ObjectId("part", (IntPtr)part),
        });
    }

    private void WriteNativeUiState(
        string stage,
        MusicalEditorViewModel vm,
        WIVSMMidiPart part,
        int regions,
        int matchedNotes,
        int generation)
    {
        var nativeParent = _parameterLayer.Parent as Canvas;
        var scale = nativeParent?.RenderTransform as ScaleTransform;
        BreathVolumeDiagnosticsLog.WriteUiState(stage, new Dictionary<string, object?>
        {
            ["generation"] = generation,
            ["partId"] = RuntimeObservationLog.ObjectId("part", (IntPtr)part),
            ["regions"] = regions,
            ["partNotes"] = part.NumNotes,
            ["matchedNotes"] = matchedNotes,
            ["nativeParts"] = _parameterLayer.ControlParameters.Count,
            ["nativeBars"] = _parameterLayer.ControlParameters.Sum(item => item.Ctrls.Count),
            ["parameterType"] = (int)_parameterLayer.ControlParameterType,
            ["widthPerTick"] = vm.WidthPerTick,
            ["viewZoom"] = vm.ViewCanvasHorizontalZoom,
            ["songWidth"] = vm.SongWidth,
            ["viewHeight"] = vm.ViewHeight,
            ["viewportLeft"] = vm.ParameterViewer?.HorizontalOffset ?? -1,
            ["viewportWidth"] = vm.ParameterViewer?.ViewportWidth ?? -1,
            ["partBeginTick"] = part.AbsPosTick.Value,
            ["partEndTick"] = part.AbsEndTick.Value,
            ["surfaceWidth"] = _parameterLayer.Width,
            ["surfaceActualWidth"] = _parameterLayer.ActualWidth,
            ["surfaceHeight"] = _parameterLayer.Height,
            ["surfaceActualHeight"] = _parameterLayer.ActualHeight,
            ["surfaceVisibility"] = _parameterLayer.Visibility.ToString(),
            ["surfaceIsVisible"] = _parameterLayer.IsVisible,
            ["surfaceOpacity"] = _parameterLayer.Opacity,
            ["normalBrush"] = DescribeBrush(BarBrush),
            ["selectedBrush"] = DescribeBrush(SelectedBarBrush),
            ["panelBackground"] = DescribeBrush(_panel.Background),
            ["surfaceParentWidth"] = nativeParent?.ActualWidth ?? -1,
            ["surfaceParentHeight"] = nativeParent?.ActualHeight ?? -1,
            ["surfaceParentVisibility"] = nativeParent?.Visibility.ToString(),
            ["surfaceParentIsVisible"] = nativeParent?.IsVisible ?? false,
            ["surfaceParentOpacity"] = nativeParent?.Opacity ?? -1,
            ["surfaceScaleX"] = scale?.ScaleX ?? -1,
            ["surfaceScaleY"] = scale?.ScaleY ?? -1,
            ["gridWidth"] = _gridLayer.ActualWidth,
            ["gridHeight"] = _gridLayer.ActualHeight,
            ["gridVisibility"] = _gridLayer.Visibility.ToString(),
            ["gridIsVisible"] = _gridLayer.IsVisible,
            ["outsideLeftWidth"] = _nativeOutsidePartLeftLayer?.Width ?? -1,
            ["outsideRightLeft"] = _nativeOutsidePartRightLayer?.Margin.Left ?? -1,
            ["outsideRightWidth"] = _nativeOutsidePartRightLayer?.Width ?? -1,
            ["outsideVisibility"] = _nativeOutsidePartCanvas?.Visibility.ToString(),
            ["nativeLayerZ"] = nativeParent == null ? -1 : Panel.GetZIndex(nativeParent),
            ["outsideLayerZ"] = _nativeOutsidePartCanvas == null ? -1 : Panel.GetZIndex(_nativeOutsidePartCanvas),
            ["overlayLayerZ"] = Panel.GetZIndex(_canvas),
        });
    }

    private static string DescribeBrush(Brush? brush)
    {
        if (brush is SolidColorBrush solid)
            return $"solid:{solid.Color.A:X2}{solid.Color.R:X2}{solid.Color.G:X2}{solid.Color.B:X2}:opacity={solid.Opacity:0.###}";
        return brush == null ? "null" : $"{brush.GetType().Name}:opacity={brush.Opacity:0.###}";
    }

    private static string DescribeGeometry(Geometry? geometry)
        => geometry == null ? "null" : $"{geometry.GetType().Name}:{geometry.Bounds}";

    private Brush BarBrush => FindNativeBrush("Brush_Parameter_Normal", FallbackBarBrush);

    private Brush SelectedBarBrush => FindNativeBrush("Brush_Parameter_Selected", FallbackSelectedBarBrush);

    private Brush FindNativeBrush(string key, Brush fallback)
        => _view.TryFindResource(key) as Brush ?? fallback;

    private void UpdateIdleFeedback(MusicalEditorViewModel vm, Point point, BreathRegion? region)
    {
        var viewport = vm.ParameterViewer?.ViewportRect();
        if (!viewport.HasValue || !viewport.Value.Contains(point))
        {
            HideNativeGuides();
            return;
        }

        if (vm.EditorMode.Mode == EditModeME.Arrow)
        {
            _canvas.Cursor = region.HasValue ? Cursors.Hand : null;
            if (_nativeCursorGuide != null)
                _nativeCursorGuide.Visibility = Visibility.Hidden;
            if (region.HasValue)
                ShowNativeToolTip(vm, point, GetDisplayValue(region.Value.NoteHandle));
            else if (_nativeToolTip != null)
                _nativeToolTip.Visibility = Visibility.Hidden;
            return;
        }

        if (_nativeToolTip != null)
            _nativeToolTip.Visibility = Visibility.Hidden;
        if (vm.EditorMode.Mode == EditModeME.Pencil)
        {
            _canvas.Cursor = VocCursors.Pencil;
            ShowNativeGuide(vm, point, YToValue(point.Y, vm.ViewHeight));
        }
        else if (vm.EditorMode.Mode == EditModeME.Line)
        {
            _canvas.Cursor = VocCursors.LinePencil;
            ShowNativeGuide(vm, point, YToValue(point.Y, vm.ViewHeight));
        }
        else
        {
            _canvas.Cursor = null;
            if (_nativeCursorGuide != null)
                _nativeCursorGuide.Visibility = Visibility.Hidden;
        }
    }

    private void ShowNativeToolTip(MusicalEditorViewModel vm, Point point, int value)
        => ShowNativeLabel(_nativeToolTip, vm, point, value, includeHorizontalOffsetWhenFlipped: true);

    private void ShowNativeGuide(MusicalEditorViewModel vm, Point point, int value)
        => ShowNativeLabel(_nativeCursorGuide, vm, point, value, includeHorizontalOffsetWhenFlipped: true);

    private void ShowNativeLabel(
        Label? label,
        MusicalEditorViewModel vm,
        Point point,
        int value,
        bool includeHorizontalOffsetWhenFlipped)
    {
        if (label == null || vm.ParameterViewer == null)
            return;
        label.Content = FormatDisplayValue(value);
        label.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        var width = Math.Max(label.ActualWidth, label.DesiredSize.Width);
        var height = Math.Max(label.ActualHeight, label.DesiredSize.Height);
        var viewport = vm.ParameterViewer.ViewportRect();
        var left = point.X + 14;
        var top = point.Y + 7;
        top = Math.Clamp(top, viewport.Top, Math.Max(viewport.Top, viewport.Bottom - height));
        left = Math.Max(viewport.Left, left);
        if (left + width > viewport.Right)
            left = point.X - width - (includeHorizontalOffsetWhenFlipped ? 14 : 0);
        label.Margin = new Thickness(left, top, 0, 0);
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
        _gestureTargetHandle = IntPtr.Zero;
        _gesturePreview.Clear();
        _view.EndDragRectangle();
        ClearNominees();
        HideNativeGuides();
        if (_view.DataContext is MusicalEditorViewModel vm)
            vm.EditorMode.IsIdleMouseOperation = true;
    }

    private void OnCanvasPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape || !_canvas.IsMouseCaptured)
            return;
        _canvas.ReleaseMouseCapture();
        e.Handled = true;
    }

    private void BeginValueEdit(BreathRegion region, Point point)
    {
        _cancelValueEdit = false;
        _valueEditor.Regex = new Regex(IsRegisterMode ? "^-?[0-9]{0,2}$" : "^[0-9]{0,3}$");
        _valueEditor.Tag = region.NoteHandle;
        _valueEditor.Text = FormatDisplayValue(GetDisplayValue(region.NoteHandle));
        var viewport = (_view.DataContext as MusicalEditorViewModel)?.ParameterViewer?.ViewportRect()
                       ?? new Rect(0, 0, _canvas.ActualWidth, _canvas.ActualHeight);
        var left = Math.Max(viewport.Left, point.X + 14);
        var top = Math.Clamp(point.Y + 7, viewport.Top,
            Math.Max(viewport.Top, viewport.Bottom - _valueEditor.Height));
        if (left + _valueEditor.Width > viewport.Right)
            left = point.X - _valueEditor.Width - 14;
        Canvas.SetLeft(_valueEditor, left);
        Canvas.SetTop(_valueEditor, top);
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
            SetValues(sequence, part, new[] { handle }, parsed);
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

    private bool IsRegisterMode
        => _view.DataContext is MusicalEditorViewModel vm &&
           vm.ControlParameterType.Equals(RegisterShiftService.ParameterType);

    private bool IsParameterActive(ControlParameterTypeEnum type)
    {
        if (BreathVolumeService.IsActive(type))
            return true;
        if (!RegisterShiftService.IsActive(type))
            return false;

        // ActivePart can be temporarily null while the header commits a value.
        // Treat REG as active until the track is known to be an AI track; otherwise
        // the native outside-part mask can flash over the custom panel.
        return _view.DataContext is not MusicalEditorViewModel vm ||
               vm.ActiveTrack?.Type != VSMTrackType.MidiAi;
    }

    private IReadOnlyList<BreathRegion> GetRegions(WIVSMMidiPart part)
        => IsRegisterMode ? RegisterShiftService.GetRegions(part) : BreathVolumeService.GetRegions(part);

    private int GetDisplayValue(IntPtr handle)
        => IsRegisterMode
            ? RegisterShiftService.GetValue(handle) + RegisterShiftService.DisplayOffset
            : BreathVolumeService.GetValue(handle);

    private IReadOnlyCollection<IntPtr> GetSelection()
        => IsRegisterMode ? RegisterShiftService.GetSelection() : BreathVolumeService.GetSelection();

    private Dictionary<IntPtr, byte> Snapshot(IEnumerable<IntPtr> handles)
        => IsRegisterMode ? RegisterShiftService.Snapshot(handles) : BreathVolumeService.Snapshot(handles);

    private bool IsSelected(IntPtr handle)
        => IsRegisterMode ? RegisterShiftService.IsSelected(handle) : BreathVolumeService.IsSelected(handle);

    private void ClearSelection()
    {
        if (IsRegisterMode) RegisterShiftService.ClearSelection();
        else BreathVolumeService.ClearSelection();
    }

    private void SetSelection(IEnumerable<IntPtr> handles, bool additive = false)
    {
        if (IsRegisterMode) RegisterShiftService.SetSelection(handles, additive);
        else BreathVolumeService.SetSelection(handles, additive);
    }

    private void ToggleSelection(IntPtr handle)
    {
        if (IsRegisterMode) RegisterShiftService.ToggleSelection(handle);
        else BreathVolumeService.ToggleSelection(handle);
    }

    private void SetPreviewValues(IEnumerable<KeyValuePair<IntPtr, byte>> values)
    {
        if (IsRegisterMode) RegisterShiftService.SetPreviewValues(values);
        else BreathVolumeService.SetPreviewValues(values);
    }

    private void CommitValues(WIVSMSequence sequence, WIVSMMidiPart part,
        IReadOnlyDictionary<IntPtr, byte> before)
    {
        if (IsRegisterMode) RegisterShiftService.CommitValues(sequence, part, before);
        else BreathVolumeService.CommitValues(sequence, part, before);
    }

    private void ResetSelected(WIVSMSequence sequence, WIVSMMidiPart part)
    {
        if (IsRegisterMode) RegisterShiftService.ResetSelected(sequence, part);
        else BreathVolumeService.ResetSelected(sequence, part);
    }

    private void SetValues(WIVSMSequence sequence, WIVSMMidiPart part,
        IEnumerable<IntPtr> handles, int value)
    {
        if (IsRegisterMode) RegisterShiftService.SetValues(sequence, part, handles, value);
        else BreathVolumeService.SetValues(sequence, part, handles, value);
    }

    private string FormatDisplayValue(int value)
        => (IsRegisterMode ? value - RegisterShiftService.DisplayOffset : value)
            .ToString(System.Globalization.CultureInfo.InvariantCulture);

    private int MinValue => IsRegisterMode
        ? RegisterShiftService.MinValue + RegisterShiftService.DisplayOffset
        : BreathVolumeService.MinValue;
    private int MaxValue => IsRegisterMode
        ? RegisterShiftService.MaxValue + RegisterShiftService.DisplayOffset
        : BreathVolumeService.MaxValue;
    private const double NativeBarWidth = 10.0;
    private const double NativeSelectedAddWidth = 2.0;
    private const double NativeTopOffset = 7.0;
    private const double NativeBottomOffset = 9.0;

    private enum GestureKind
    {
        None,
        SongPositionJump,
        MoveWait,
        Move,
        Rectangle,
        PencilWait,
        Pencil,
        LineWait,
        Line
    }
}
