using System;
using System.Collections.Generic;
using System.Reflection;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using Stopwatch = System.Diagnostics.Stopwatch;
using HarmonyLib;
using VOCALOIDPatcher.Config;
using VOCALOIDPatcher.Translation;
using VOCALOIDPatcher.Utils;
using VOCALOIDPatcher.Utils.Audio;
using Yamaha.VOCALOID;
using Yamaha.VOCALOID.VAE;
using Yamaha.VOCALOID.VSM;

namespace VOCALOIDPatcher.Patch.Patches;

public static class SmoothPlayhead
{
    private const double LogicalUpdatesPerSecond = 10.0;
    private const double MaximumClockHoldoverSeconds = 0.25;
    private const double VisualTreeRefreshSeconds = 5.0;
    private const double MissingVisualRefreshSeconds = 0.50;

    private static readonly FieldInfo? FEvent =
        AccessTools.Field(typeof(AudioPlayer), "SongPositionProceeded");

    private static readonly MethodInfo? MTimerGetter =
        AccessTools.PropertyGetter(typeof(AudioPlayer), "songPositionUpdateTimer");

    private static readonly bool Resolved = FEvent != null && MTimerGetter != null;

    private static readonly List<PlayheadVisual> Visuals = new();
    private static readonly Dictionary<Type, FieldInfo?> PlayheadTransformFields = new();
    private static readonly Dictionary<object, double> FrameWidthPerTick =
        new(ReferenceEqualityComparer.Instance);

    private static EventHandler? _handler;
    private static EventHandler<EventArgs>? _playbackStatusHandler;
    private static DispatcherOperation? _logicalUpdateOperation;
    private static DispatcherOperation? _visualTreeRefreshOperation;
    private static SongPositionProceedEventArgs? _pendingLogicalPosition;
    private static DispatcherTimer? _nativeTimer;
    private static AudioPlayer? _player;
    private static double _lastEngineTime = double.NaN;
    private static double _visualTime = double.NaN;
    private static double _lastCalibratedTime = double.NaN;
    private static double _lastObservedEngineTime = double.NaN;
    private static double _loopEndTime = double.NaN;
    private static double _averageFrameSeconds = 1.0 / 60.0;
    private static long _lastRenderTimestamp;
    private static long _lastCadenceTimestamp;
    private static long _lastLogicalUpdate;
    private static long _lastEngineAdvanceTimestamp;
    private static long _lastVisualTreeRefresh;
    private static int _expectedVisualCount;
    private static bool _nativeClockActive;
    private static bool _visualsSuspended;

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
            _lastCalibratedTime = double.NaN;
            _lastObservedEngineTime = double.NaN;
            _loopEndTime = double.NaN;
            _averageFrameSeconds = 1.0 / 60.0;
            _lastRenderTimestamp = 0;
            _lastCadenceTimestamp = 0;
            _lastLogicalUpdate = 0;
            _lastEngineAdvanceTimestamp = 0;
            _lastVisualTreeRefresh = 0;
            _expectedVisualCount = 0;
            _nativeClockActive = false;
            _visualsSuspended = true;
            RefreshPlayheadVisuals(true);
            _lastVisualTreeRefresh = Stopwatch.GetTimestamp();
            PlaybackLatencyCalibrator.Start(player.AudioDeviceManager.AudioDeviceConfig);
            _playbackStatusHandler = (_, _) => OnPlaybackStatusChanged(player);
            player.PlaybackStatusChanged += _playbackStatusHandler;
            _handler = (_, _) => OnRendering(player);
            CompositionTarget.Rendering += _handler;

            if (player.IsPlaying)
                ResumeVisualMotion(player);
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
                var now = Stopwatch.GetTimestamp();
                var engineTime = VEAudioEngine.GetCurrentTime();
                var displayLead = Settings.AutoCalibratePlayheadLatency
                    ? _averageFrameSeconds
                    : 0.0;
                var visualTime = ResolveVisualTime(engineTime, now, displayLead);
                var tick = sequence.GetTickFromTime(visualTime);
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

    internal static void NotifySeek(VSMAbsTick tick)
    {
        if (_player is not { } player || !Settings.SmoothPlayhead)
            return;

        try
        {
            CancelLogicalUpdate();
            UpdatePlayheadVisuals((long) tick);

            var now = Stopwatch.GetTimestamp();
            var engineTime = VEAudioEngine.GetCurrentTime();
            var displayLead = Settings.AutoCalibratePlayheadLatency
                ? _averageFrameSeconds
                : 0.0;
            ResetEngineObservation(engineTime, now);
            _lastCalibratedTime = Math.Max(0.0,
                engineTime - PlaybackLatencyCalibrator.LatencySeconds + displayLead);
            ResetManagedClock();
            _nativeClockActive = NativePlaybackClock.TryReset(engineTime, now,
                PlaybackLatencyCalibrator.LatencySeconds);
            _lastLogicalUpdate = now;
        }
        catch (Exception e)
        {
            Debug.Print(TranslationManager.Tr(
                "VOCALOIDPatcher_Debug_SmoothPlayhead_Failed", e.Message));
        }
    }

    private static void Detach()
    {
        CancelLogicalUpdate();
        CancelVisualTreeRefresh();

        if (_handler != null)
            CompositionTarget.Rendering -= _handler;

        if (_player != null && _playbackStatusHandler != null)
            _player.PlaybackStatusChanged -= _playbackStatusHandler;

        foreach (var visual in Visuals)
            visual.ClearAnimation();

        _handler = null;
        _playbackStatusHandler = null;
        _player = null;
        _nativeTimer = null;
        Visuals.Clear();
    }

    private static void OnRendering(AudioPlayer player)
    {
        try
        {
            if (player.MixdownMode != MixdownMode.NotMixdownMode ||
                player.ConnectMode != VEConnectMode.StandAlone)
            {
                SuspendVisualMotion(player);
                return;
            }

            if (!player.IsPlaying)
            {
                SuspendVisualMotion(player);
                return;
            }

            if (_visualsSuspended)
                ResumeVisualMotion(player);

            var now = Stopwatch.GetTimestamp();
            UpdateRenderCadence(now);
            QueueVisualTreeRefresh(now,
                Visuals.Count == 0 || Visuals.Count < _expectedVisualCount);

            var sequence = ((Application.Current?.MainWindow as MainWindow)?.DataContext as MainViewModel)?.VSMSequence;
            if (sequence == null) return;

            var displayLead = Settings.AutoCalibratePlayheadLatency ? _averageFrameSeconds : 0.0;
            var engineTime = VEAudioEngine.GetCurrentTime();
            var visualTime = ResolveVisualTime(engineTime, now, displayLead);
            _loopEndTime = player.LoopEnabled
                ? VEAudioEngine.GetLoopRange().End
                : double.NaN;

            if (NeedsLogicalPosition(now))
            {
                var engineTick = sequence.GetTickFromTime(engineTime);
                QueueLogicalUpdate(player, new SongPositionProceedEventArgs(engineTick), now);
            }

            if (double.IsFinite(_loopEndTime))
                visualTime = Math.Min(visualTime, _loopEndTime);

            var visualTick = sequence.GetTickFromTime(visualTime);
            RenderPlayheadVisuals((long) visualTick);
        }
        catch (Exception e)
        {
            FallBackToNativeTimer();
            Debug.Print(TranslationManager.Tr("VOCALOIDPatcher_Debug_SmoothPlayhead_Failed", e.Message));
        }
    }

    private static void OnPlaybackStatusChanged(AudioPlayer player)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher != null && !dispatcher.CheckAccess())
        {
            dispatcher.BeginInvoke(new Action(() => OnPlaybackStatusChanged(player)),
                DispatcherPriority.Send);
            return;
        }

        if (!ReferenceEquals(_player, player))
            return;

        if (player.IsPlaying)
            ResumeVisualMotion(player);
        else
            SuspendVisualMotion(player);
    }

    private static void ResumeVisualMotion(AudioPlayer player)
    {
        if (!ReferenceEquals(_player, player))
            return;

        foreach (var visual in Visuals)
            visual.ClearAnimation();

        var now = Stopwatch.GetTimestamp();
        var engineTime = VEAudioEngine.GetCurrentTime();
        var displayLead = Settings.AutoCalibratePlayheadLatency
            ? _averageFrameSeconds
            : 0.0;
        ResetEngineObservation(engineTime, now);
        _lastCalibratedTime = Math.Max(0.0,
            engineTime - PlaybackLatencyCalibrator.LatencySeconds + displayLead);
        ResetManagedClock();
        _nativeClockActive = NativePlaybackClock.TryReset(engineTime, now,
            PlaybackLatencyCalibrator.LatencySeconds);
        _lastCadenceTimestamp = 0;
        _visualsSuspended = false;
    }

    private static void SuspendVisualMotion(AudioPlayer player)
    {
        if (_visualsSuspended || !ReferenceEquals(_player, player))
            return;

        _visualsSuspended = true;
        CancelLogicalUpdate();

        foreach (var visual in Visuals)
            visual.ClearAnimation();

        try
        {
            if (((Application.Current?.MainWindow as MainWindow)?.DataContext as MainViewModel)?.VSMSequence
                is not { } sequence)
                return;

            var now = Stopwatch.GetTimestamp();
            var engineTime = VEAudioEngine.GetCurrentTime();
            var displayLead = Settings.AutoCalibratePlayheadLatency
                ? _averageFrameSeconds
                : 0.0;
            var visualTime = ResolveVisualTime(engineTime, now, displayLead);
            _nativeClockActive = false;
            var tick = sequence.GetTickFromTime(visualTime);
            var handler = FEvent?.GetValue(player) as EventHandler<SongPositionProceedEventArgs>;
            handler?.Invoke(player, new SongPositionProceedEventArgs(tick));
            UpdatePlayheadVisuals((long) tick);
        }
        catch (Exception e)
        {
            Debug.Print(TranslationManager.Tr(
                "VOCALOIDPatcher_Debug_SmoothPlayhead_Failed", e.Message));
        }
    }

    private static void ResetManagedClock()
    {
        _lastEngineTime = double.NaN;
        _visualTime = double.NaN;
        _lastRenderTimestamp = 0;
    }

    private static double ResolveVisualTime(double engineTime, long now, double displayLead)
    {
        RecordEngineObservation(engineTime, now);
        _lastCalibratedTime = Math.Max(0.0,
            engineTime - PlaybackLatencyCalibrator.LatencySeconds + displayLead);

        if (_nativeClockActive && NativePlaybackClock.TryUpdate(engineTime, now,
                PlaybackLatencyCalibrator.LatencySeconds, displayLead, 0.0,
                out var nativeClock))
            return nativeClock.IsStale
                ? _lastCalibratedTime
                : nativeClock.CurrentTime;

        _nativeClockActive = false;
        return IsEngineFeedbackStale(now)
            ? _lastCalibratedTime
            : EstimateVisualTime(_lastCalibratedTime, now);
    }

    private static void RecordEngineObservation(double engineTime, long now)
    {
        if (!double.IsFinite(engineTime))
            return;

        if (!double.IsFinite(_lastObservedEngineTime) ||
            Math.Abs(engineTime - _lastObservedEngineTime) > double.Epsilon)
        {
            _lastObservedEngineTime = engineTime;
            _lastEngineAdvanceTimestamp = now;
        }
    }

    private static void ResetEngineObservation(double engineTime, long now)
    {
        _lastObservedEngineTime = engineTime;
        _lastEngineAdvanceTimestamp = now;
    }

    private static bool IsEngineFeedbackStale(long now)
    {
        return _lastEngineAdvanceTimestamp != 0 &&
            Stopwatch.GetElapsedTime(_lastEngineAdvanceTimestamp, now).TotalSeconds >
            MaximumClockHoldoverSeconds;
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

    private static bool NeedsLogicalPosition(long now)
    {
        return _logicalUpdateOperation != null || _lastLogicalUpdate == 0 ||
            Stopwatch.GetElapsedTime(_lastLogicalUpdate, now).TotalSeconds >=
            1.0 / LogicalUpdatesPerSecond;
    }

    private static void CancelLogicalUpdate()
    {
        if (_logicalUpdateOperation?.Status == DispatcherOperationStatus.Pending)
            _logicalUpdateOperation.Abort();

        _logicalUpdateOperation = null;
        _pendingLogicalPosition = null;
    }

    private static void CancelVisualTreeRefresh()
    {
        if (_visualTreeRefreshOperation?.Status == DispatcherOperationStatus.Pending)
            _visualTreeRefreshOperation.Abort();

        _visualTreeRefreshOperation = null;
    }

    private static void UpdateRenderCadence(long now)
    {
        if (_lastCadenceTimestamp != 0)
        {
            var frameSeconds = Stopwatch.GetElapsedTime(_lastCadenceTimestamp, now).TotalSeconds;
            if (frameSeconds is > 0.0 and < 0.1)
                _averageFrameSeconds = _averageFrameSeconds * 0.9 + frameSeconds * 0.1;
        }

        _lastCadenceTimestamp = now;
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

    private static void QueueVisualTreeRefresh(long now, bool urgent)
    {
        if (_visualTreeRefreshOperation != null)
            return;

        var refreshInterval = urgent ? MissingVisualRefreshSeconds : VisualTreeRefreshSeconds;
        if (_lastVisualTreeRefresh != 0 &&
            Stopwatch.GetElapsedTime(_lastVisualTreeRefresh, now).TotalSeconds <
            refreshInterval)
            return;

        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher == null) return;

        _lastVisualTreeRefresh = now;
        _visualTreeRefreshOperation = dispatcher.BeginInvoke(new Action(() =>
        {
            _visualTreeRefreshOperation = null;
            if (_player?.IsPlaying != true)
                return;

            try
            {
                RefreshPlayheadVisuals(false);
            }
            catch (Exception e)
            {
                Debug.Print(TranslationManager.Tr(
                    "VOCALOIDPatcher_Debug_SmoothPlayhead_Failed", e.Message));
            }
        }), DispatcherPriority.ContextIdle);
    }

    private static void RefreshPlayheadVisuals(bool clearExisting)
    {
        if (clearExisting)
            Visuals.Clear();
        else
            for (var i = Visuals.Count - 1; i >= 0; i--)
                if (!Visuals[i].IsAlive)
                    Visuals.RemoveAt(i);

        if (Application.Current?.MainWindow is not DependencyObject root)
            return;

        var knownTransforms = new HashSet<TranslateTransform>(ReferenceEqualityComparer.Instance);
        foreach (var visual in Visuals)
            knownTransforms.Add(visual.Transform);

        var pending = new Stack<DependencyObject>();
        pending.Push(root);

        while (pending.Count > 0)
        {
            var current = pending.Pop();
            if (current is FrameworkElement element)
            {
                var type = current.GetType();
                if (!PlayheadTransformFields.TryGetValue(type, out var field))
                {
                    field = AccessTools.Field(type, "songPosTranslate");
                    PlayheadTransformFields[type] = field;
                }

                if (element.IsLoaded &&
                    field?.GetValue(current) is TranslateTransform transform &&
                    knownTransforms.Add(transform))
                    Visuals.Add(new PlayheadVisual(element, transform));
            }

            var childCount = VisualTreeHelper.GetChildrenCount(current);
            for (var i = 0; i < childCount; i++)
                pending.Push(VisualTreeHelper.GetChild(current, i));
        }

        _expectedVisualCount = Visuals.Count;
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

    private static void RenderPlayheadVisuals(long tick)
    {
        FrameWidthPerTick.Clear();
        for (var i = Visuals.Count - 1; i >= 0; i--)
        {
            var visual = Visuals[i];
            if (!visual.TryRender(tick, FrameWidthPerTick))
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

        internal PlayheadVisual(FrameworkElement owner, TranslateTransform transform)
        {
            _owner = owner;
            _transform = transform;
        }

        internal bool IsAlive => _owner.IsLoaded;

        internal TranslateTransform Transform => _transform;

        internal bool TryMove(long tick)
        {
            if (!_owner.IsLoaded)
                return false;

            if (!TryGetWidthPerTick(null, out var widthPerTick))
                return true;

            _transform.X = tick * widthPerTick;
            return true;
        }

        internal bool TryRender(long tick, Dictionary<object, double> frameWidthPerTick)
        {
            if (!_owner.IsLoaded)
                return false;

            if (!_owner.IsVisible)
                return true;

            if (!TryGetWidthPerTick(frameWidthPerTick, out var widthPerTick))
                return true;

            _transform.X = tick * widthPerTick;
            return true;
        }

        internal void ClearAnimation()
        {
            try
            {
                _transform.BeginAnimation(TranslateTransform.XProperty, null);
            }
            catch
            {
                // The owner may be unloading while playback is stopping.
            }
        }

        private bool TryGetWidthPerTick(Dictionary<object, double>? frameWidthPerTick,
            out double widthPerTick)
        {
            widthPerTick = 0.0;
            if (!_owner.IsLoaded)
                return false;

            var dataContext = _owner.DataContext;
            if (dataContext == null)
                return false;

            if (frameWidthPerTick != null &&
                frameWidthPerTick.TryGetValue(dataContext, out widthPerTick))
                return true;

            if (!ReferenceEquals(dataContext, _dataContext))
            {
                _dataContext = dataContext;
                _widthPerTick = AccessTools.Property(dataContext.GetType(), "WidthPerTick");
            }

            if (_widthPerTick?.GetValue(dataContext) is not double value ||
                !double.IsFinite(value))
                return false;

            widthPerTick = value;
            frameWidthPerTick?.Add(dataContext, value);
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

public class SmoothPlayheadSeekPatch : PatchBase
{
    public override string PatchName        => "SmoothPlayheadSeekPatch";
    public override Type   TargetClass      => typeof(MainViewModel);
    public override string TargetMethodName => "SetCurrentPosition";
    public override Type[] ArgumentTypes    => [typeof(VSMAbsTick)];

    [HarmonyPostfix]
    private static void Postfix(VSMAbsTick posTick)
    {
        SmoothPlayhead.NotifySeek(posTick);
    }
}
