using System;
using System.Collections.Generic;
using System.Reflection;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using Stopwatch = System.Diagnostics.Stopwatch;
using HarmonyLib;
using VOCALOIDPatcher.Config;
using VOCALOIDPatcher.Translation;
using VOCALOIDPatcher.Utils;
using VOCALOIDPatcher.Utils.Audio;
using Yamaha.VOCALOID;
using Yamaha.VOCALOID.VAE;

namespace VOCALOIDPatcher.Patch.Patches;

public static class SmoothPlayhead
{
    private const double LogicalUpdatesPerSecond = 10.0;
    private const double VisualAnimationLookAheadSeconds = 0.35;
    private const double VisualAnimationRefreshSeconds = 0.10;

    private static readonly FieldInfo? FEvent =
        AccessTools.Field(typeof(AudioPlayer), "SongPositionProceeded");

    private static readonly MethodInfo? MTimerGetter =
        AccessTools.PropertyGetter(typeof(AudioPlayer), "songPositionUpdateTimer");

    private static readonly bool Resolved = FEvent != null && MTimerGetter != null;

    private static readonly List<PlayheadVisual> Visuals = new();

    private static EventHandler? _handler;
    private static DispatcherOperation? _logicalUpdateOperation;
    private static SongPositionProceedEventArgs? _pendingLogicalPosition;
    private static DispatcherTimer? _nativeTimer;
    private static AudioPlayer? _player;
    private static double _lastEngineTime = double.NaN;
    private static double _visualTime = double.NaN;
    private static double _averageFrameSeconds = 1.0 / 60.0;
    private static long _lastRenderTimestamp;
    private static long _lastLogicalUpdate;
    private static long _lastVisualAnimation;

    internal static void Begin(AudioPlayer player)
    {
        if (!Settings.SmoothPlayhead || !Resolved) return;
        if (player.ConnectMode != VEConnectMode.StandAlone) return;

        try
        {
            PlaybackLatencyCalibrator.Stop();
            Detach();

            if (MTimerGetter!.Invoke(player, null) is not DispatcherTimer timer)
                return;

            _nativeTimer = timer;
            _player = player;
            timer.Stop();

            _lastEngineTime = double.NaN;
            _visualTime = double.NaN;
            _averageFrameSeconds = 1.0 / 60.0;
            _lastRenderTimestamp = 0;
            _lastLogicalUpdate = 0;
            _lastVisualAnimation = 0;
            FindPlayheadVisuals();
            PlaybackLatencyCalibrator.Start(player.AudioDeviceManager.AudioDeviceConfig);
            _handler = (_, _) => OnRendering(player);
            CompositionTarget.Rendering += _handler;
        }
        catch (Exception e)
        {
            FallBackToNativeTimer();
            Debug.Print(TranslationManager.Tr("VOCALOIDPatcher_Debug_SmoothPlayhead_Failed", e.Message));
        }
    }

    internal static void End()
    {
        CancelLogicalUpdate();

        try
        {
            if (_player is { } player &&
                ((Application.Current?.MainWindow as MainWindow)?.DataContext as MainViewModel)?.VSMSequence is { } sequence)
            {
                var tick = sequence.GetTickFromTime(VEAudioEngine.GetCurrentTime());
                var handler = FEvent?.GetValue(player) as EventHandler<SongPositionProceedEventArgs>;
                handler?.Invoke(player, new SongPositionProceedEventArgs(tick));
                UpdatePlayheadVisuals((long) tick);
            }
        }
        catch (Exception e)
        {
            Debug.Print(TranslationManager.Tr("VOCALOIDPatcher_Debug_SmoothPlayhead_Failed", e.Message));
        }
        finally
        {
            PlaybackLatencyCalibrator.Stop();
            Detach();
        }
    }

    internal static void RefreshLatencyCalibration()
    {
        if (!Settings.AutoCalibratePlayheadLatency)
        {
            PlaybackLatencyCalibrator.Stop();
            return;
        }

        if (_player is { IsPlaying: true, ConnectMode: VEConnectMode.StandAlone } player)
            PlaybackLatencyCalibrator.Start(player.AudioDeviceManager.AudioDeviceConfig);
    }

    private static void Detach()
    {
        CancelLogicalUpdate();

        if (_handler != null)
            CompositionTarget.Rendering -= _handler;

        foreach (var visual in Visuals)
            visual.ClearAnimation();

        _handler = null;
        _player = null;
        _nativeTimer = null;
        Visuals.Clear();
    }

    private static void OnRendering(AudioPlayer player)
    {
        try
        {
            if (player.MixdownMode != MixdownMode.NotMixdownMode ||
                player.ConnectMode != VEConnectMode.StandAlone ||
                !player.IsPlaying)
                return;

            var sequence = ((Application.Current?.MainWindow as MainWindow)?.DataContext as MainViewModel)?.VSMSequence;
            if (sequence == null) return;

            var now = Stopwatch.GetTimestamp();
            var engineTime = VEAudioEngine.GetCurrentTime();
            var displayLead = Settings.AutoCalibratePlayheadLatency ? _averageFrameSeconds : 0.0;
            var calibratedTime = Math.Max(0.0,
                engineTime - PlaybackLatencyCalibrator.LatencySeconds + displayLead);
            var visualTime = EstimateVisualTime(calibratedTime, now);
            var engineTick = sequence.GetTickFromTime(engineTime);
            var visualTick = sequence.GetTickFromTime(visualTime);
            if (_lastVisualAnimation == 0 ||
                Stopwatch.GetElapsedTime(_lastVisualAnimation, now).TotalSeconds >= VisualAnimationRefreshSeconds)
            {
                _lastVisualAnimation = now;
                var duration = VisualAnimationLookAheadSeconds;

                if (player.LoopEnabled)
                {
                    var loopRange = VEAudioEngine.GetLoopRange();
                    if (visualTime < loopRange.End && visualTime + duration > loopRange.End)
                        duration = Math.Max(0.01, loopRange.End - visualTime);
                }

                var targetTick = sequence.GetTickFromTime(visualTime + duration);
                if ((long) targetTick >= (long) visualTick)
                    AnimatePlayheadVisuals((long) visualTick, (long) targetTick, duration);
                else
                    UpdatePlayheadVisuals((long) visualTick);
            }

            QueueLogicalUpdate(player, new SongPositionProceedEventArgs(engineTick), now);
        }
        catch (Exception e)
        {
            FallBackToNativeTimer();
            Debug.Print(TranslationManager.Tr("VOCALOIDPatcher_Debug_SmoothPlayhead_Failed", e.Message));
        }
    }

    private static void QueueLogicalUpdate(AudioPlayer player,
        SongPositionProceedEventArgs position, long now)
    {
        _pendingLogicalPosition = position;

        if (_logicalUpdateOperation != null ||
            _lastLogicalUpdate != 0 &&
            Stopwatch.GetElapsedTime(_lastLogicalUpdate, now).TotalSeconds < 1.0 / LogicalUpdatesPerSecond)
            return;

        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher == null) return;

        _lastLogicalUpdate = now;
        _logicalUpdateOperation = dispatcher.BeginInvoke(new Action(() =>
        {
            var pending = _pendingLogicalPosition;
            _pendingLogicalPosition = null;
            _logicalUpdateOperation = null;

            if (pending == null || !ReferenceEquals(_player, player) || !player.IsPlaying)
                return;

            try
            {
                var handler = FEvent!.GetValue(player) as EventHandler<SongPositionProceedEventArgs>;
                handler?.Invoke(player, pending);
            }
            catch (Exception e)
            {
                FallBackToNativeTimer();
                Debug.Print(TranslationManager.Tr("VOCALOIDPatcher_Debug_SmoothPlayhead_Failed", e.Message));
            }
        }), DispatcherPriority.Background);
    }

    private static void CancelLogicalUpdate()
    {
        if (_logicalUpdateOperation?.Status == DispatcherOperationStatus.Pending)
            _logicalUpdateOperation.Abort();

        _logicalUpdateOperation = null;
        _pendingLogicalPosition = null;
    }

    private static double EstimateVisualTime(double engineTime, long now)
    {
        if (!double.IsFinite(engineTime))
            return 0.0;

        if (_lastRenderTimestamp == 0 || !double.IsFinite(_visualTime))
        {
            _lastEngineTime = engineTime;
            _visualTime = engineTime;
            _lastRenderTimestamp = now;
            return engineTime;
        }

        var frameSeconds = Stopwatch.GetElapsedTime(_lastRenderTimestamp, now).TotalSeconds;
        if (frameSeconds is > 0.0 and < 0.1)
            _averageFrameSeconds = _averageFrameSeconds * 0.9 + frameSeconds * 0.1;

        _visualTime += frameSeconds;
        _lastRenderTimestamp = now;

        if (engineTime != _lastEngineTime)
        {
            var error = engineTime - _visualTime;
            if (engineTime < _lastEngineTime || Math.Abs(error) >= 0.08)
                _visualTime = engineTime;
            else
                _visualTime += error * 0.35;

            _lastEngineTime = engineTime;
        }

        return _visualTime;
    }

    private static void FindPlayheadVisuals()
    {
        Visuals.Clear();

        if (Application.Current?.MainWindow is not DependencyObject root)
            return;

        var pending = new Stack<DependencyObject>();
        pending.Push(root);

        while (pending.Count > 0)
        {
            var current = pending.Pop();
            if (current is FrameworkElement element)
            {
                var field = AccessTools.Field(current.GetType(), "songPosTranslate");
                if (field?.GetValue(current) is TranslateTransform transform)
                    Visuals.Add(new PlayheadVisual(element, transform));
            }

            var childCount = VisualTreeHelper.GetChildrenCount(current);
            for (var i = 0; i < childCount; i++)
                pending.Push(VisualTreeHelper.GetChild(current, i));
        }
    }

    private static void UpdatePlayheadVisuals(long tick)
    {
        for (var i = Visuals.Count - 1; i >= 0; i--)
        {
            var visual = Visuals[i];
            visual.ClearAnimation();
            if (!visual.TryMove(tick))
                Visuals.RemoveAt(i);
        }
    }

    private static void AnimatePlayheadVisuals(long currentTick, long targetTick, double durationSeconds)
    {
        for (var i = Visuals.Count - 1; i >= 0; i--)
        {
            var visual = Visuals[i];
            if (!visual.TryAnimate(currentTick, targetTick, durationSeconds))
                Visuals.RemoveAt(i);
        }
    }

    private static void FallBackToNativeTimer()
    {
        var timer = _nativeTimer;
        var player = _player;
        PlaybackLatencyCalibrator.Stop();
        Detach();

        if (timer != null && player?.IsPlaying == true)
            timer.Start();
    }

    private sealed class PlayheadVisual
    {
        private readonly FrameworkElement _owner;
        private readonly TranslateTransform _transform;
        private object? _dataContext;
        private PropertyInfo? _widthPerTick;
        private bool _animated;

        internal PlayheadVisual(FrameworkElement owner, TranslateTransform transform)
        {
            _owner = owner;
            _transform = transform;
        }

        internal bool TryMove(long tick)
        {
            if (!_owner.IsLoaded)
                return false;

            var dataContext = _owner.DataContext;
            if (dataContext == null)
                return true;

            if (!ReferenceEquals(dataContext, _dataContext))
            {
                _dataContext = dataContext;
                _widthPerTick = AccessTools.Property(dataContext.GetType(), "WidthPerTick");
            }

            if (_widthPerTick?.GetValue(dataContext) is not double widthPerTick ||
                !double.IsFinite(widthPerTick))
                return true;

            _transform.X = tick * widthPerTick;
            return true;
        }

        internal bool TryAnimate(long currentTick, long targetTick, double durationSeconds)
        {
            if (!TryGetWidthPerTick(out var widthPerTick))
                return _owner.IsLoaded;

            var currentX = currentTick * widthPerTick;
            if (!_animated)
            {
                _transform.X = currentX;
                _animated = true;
            }
            else
            {
                currentX = _transform.X;
            }

            var animation = new DoubleAnimation
            {
                From = currentX,
                To = targetTick * widthPerTick,
                Duration = TimeSpan.FromSeconds(durationSeconds),
                FillBehavior = FillBehavior.HoldEnd
            };
            _transform.BeginAnimation(TranslateTransform.XProperty, animation,
                HandoffBehavior.SnapshotAndReplace);
            return true;
        }

        internal void ClearAnimation()
        {
            if (!_animated) return;
            try
            {
                _transform.BeginAnimation(TranslateTransform.XProperty, null);
            }
            catch
            {
                // The owner may be unloading while playback is stopping.
            }
            _animated = false;
        }

        private bool TryGetWidthPerTick(out double widthPerTick)
        {
            widthPerTick = 0.0;
            if (!_owner.IsLoaded)
                return false;

            var dataContext = _owner.DataContext;
            if (dataContext == null)
                return false;

            if (!ReferenceEquals(dataContext, _dataContext))
            {
                _dataContext = dataContext;
                _widthPerTick = AccessTools.Property(dataContext.GetType(), "WidthPerTick");
            }

            if (_widthPerTick?.GetValue(dataContext) is not double value ||
                !double.IsFinite(value))
                return false;

            widthPerTick = value;
            return true;
        }
    }
}

public class SmoothPlayheadBeginPatch : PatchBase
{
    public override string PatchName        => "SmoothPlayheadBeginPatch";
    public override Type   TargetClass      => typeof(AudioPlayer);
    public override string TargetMethodName => "BeginAudioPlayObserving";

    [HarmonyPostfix]
    private static void Postfix(AudioPlayer __instance)
    {
        SmoothPlayhead.Begin(__instance);
    }
}

public class SmoothPlayheadEndPatch : PatchBase
{
    public override string PatchName        => "SmoothPlayheadEndPatch";
    public override Type   TargetClass      => typeof(AudioPlayer);
    public override string TargetMethodName => "EndAudioPlayObserving";

    [HarmonyPostfix]
    private static void Postfix()
    {
        SmoothPlayhead.End();
    }
}
