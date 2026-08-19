using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Reflection;
using HarmonyLib;
using Yamaha.VOCALOID;
using Yamaha.VOCALOID.MusicalEditor;
using Yamaha.VOCALOID.SingerEditor;
using Yamaha.VOCALOID.StyleEditor;
using Yamaha.VOCALOID.TrackEditor;
using Yamaha.VOCALOID.VSM;

namespace VOCALOIDPatcher.Utils;

/// <summary>
/// Opt-in profiler for the "first track after new project" stall. It only
/// installs when first-track-profile.enabled exists in the config directory,
/// and writes a nested enter/exit timeline of the native V6 UI path so the
/// gap between Transaction.EndProc and the first render callback can be
/// attributed without running an unpatched editor.
/// </summary>
internal static class FirstTrackProfiler
{
    private static readonly object Sync = new();
    private static readonly long StartedTicks = Stopwatch.GetTimestamp();
    private static string? _logPath;

    [ThreadStatic]
    private static int _depth;

    public static string EnabledPath => Path.Combine(Patcher.ConfigDir, "first-track-profile.enabled");

    public static string LogPath => Path.Combine(Patcher.ConfigDir, "first-track-profile.log");

    public static bool IsEnabled
    {
        get
        {
            try { return File.Exists(EnabledPath); }
            catch { return false; }
        }
    }

    public static void Install(Harmony harmony)
    {
        if (!IsEnabled)
            return;

        try
        {
            _logPath = LogPath;
            File.WriteAllText(
                _logPath,
                "VOCALOID Patcher first-track profile" + Environment.NewLine +
                $"Started: {DateTime.Now:yyyy-MM-dd HH:mm:ss.fff zzz}" + Environment.NewLine +
                $"Editor: {typeof(App).Assembly.GetName().Version}" + Environment.NewLine +
                $"Patcher: {Patcher.Version}" + Environment.NewLine +
                Environment.NewLine);

            var prefix = new HarmonyMethod(typeof(FirstTrackProfiler), nameof(Prefix));
            var postfix = new HarmonyMethod(typeof(FirstTrackProfiler), nameof(Postfix));

            int patched = 0;
            foreach ((MethodBase? method, string label) in Targets())
            {
                if (method == null)
                {
                    WriteWarning($"target not found: {label}");
                    continue;
                }

                try
                {
                    harmony.Patch(method, prefix: prefix, postfix: postfix);
                    patched++;
                    Write($"installed {label}");
                }
                catch (Exception exception)
                {
                    WriteWarning($"install failed: {label}; {exception.GetType().Name}: {exception.Message}");
                }
            }

            Write($"profiler ready; targets installed: {patched}");
        }
        catch (Exception exception)
        {
            WriteWarning($"initialization failed; {exception.GetType().Name}: {exception.Message}");
        }
    }

    private static IEnumerable<(MethodBase? Method, string Label)> Targets()
    {
        // Add Track dialog lifecycle.
        yield return (AccessTools.Constructor(typeof(AddTrackDlg)), "AddTrackDlg..ctor");
        yield return (AccessTools.Method(typeof(AddTrackDlg), "OnLoaded", new[] { typeof(object), typeof(System.Windows.RoutedEventArgs) }),
            "AddTrackDlg.OnLoaded");
        yield return (AccessTools.Method(typeof(AddTrackDlg), "InitializeVoiceBanks"), "AddTrackDlg.InitializeVoiceBanks");
        yield return (AccessTools.Method(typeof(AddTrackDlg), "OnClickCreation", new[] { typeof(object), typeof(System.Windows.RoutedEventArgs) }),
            "AddTrackDlg.OnClickCreation");

        // TrackEditorViewModel add-track path and update dispatch.
        yield return (AccessTools.Method(typeof(TrackEditorViewModel), "AddTrack", new[] { typeof(VSMTrackType), typeof(bool) }),
            "TrackEditorViewModel.AddTrack");
        yield return (AccessTools.Method(typeof(TrackEditorViewModel), "AddTrackByIndex", new[] { typeof(VSMTrackType), typeof(ulong) }),
            "TrackEditorViewModel.AddTrackByIndex");
        yield return (AccessTools.Method(typeof(TrackEditorViewModel), "AddMultiTrack", new[] { typeof(bool), typeof(MultiTrackAdditionParam) }),
            "TrackEditorViewModel.AddMultiTrack(bool,param)");
        yield return (AccessTools.Method(typeof(TrackEditorViewModel), "AddMultiTrack", new[] { typeof(MultiTrackAdditionParam) }),
            "TrackEditorViewModel.AddMultiTrack(param)");
        yield return (AccessTools.Method(typeof(TrackEditorViewModel), "DoUpdateView",
                new[] { typeof(object), typeof(Yamaha.VOCALOID.TrackEditor.UpdateViewTypeFlag), typeof(UpdateObserverNotifyEventArgs), typeof(object) }),
            "TrackEditorViewModel.DoUpdateView");
        yield return (AccessTools.Method(typeof(TrackEditorViewModel), "UpdateViewModelChanged",
                new[] { typeof(UpdateViewModelKind), typeof(object) }),
            "TrackEditorViewModel.UpdateViewModelChanged");

        // Transaction / selection notification boundary.
        yield return (AccessTools.Method(typeof(Transaction), "EndProc"), "Transaction.EndProc");
        yield return (AccessTools.Constructor(typeof(SelectionNotifier), new[] { typeof(Sequence) }), "SelectionNotifier..ctor");
        yield return (AccessTools.Method(typeof(SelectionNotifier), "Dispose"), "SelectionNotifier.Dispose");
        yield return (AccessTools.Method(typeof(SelectionNotifier), "Notify"), "SelectionNotifier.Notify");
        yield return (AccessTools.Method(typeof(Sequence), "SetActivePartAndTrack", new[] { typeof(WIVSMPart) }),
            "Sequence.SetActivePartAndTrack");
        yield return (AccessTools.Method(typeof(WIVSMMidiPart), "CreatePartialRenderer"),
            "WIVSMMidiPart.CreatePartialRenderer");

        // MainViewModel selection fan-out.
        yield return (AccessTools.Method(typeof(MainViewModel), "SelectionStateDidChangeHandler", new[] { typeof(Sequence) }),
            "MainViewModel.SelectionStateDidChangeHandler");
        yield return (AccessTools.Method(typeof(MainViewModel), "SelectionStateChangedHandler", Type.EmptyTypes),
            "MainViewModel.SelectionStateChangedHandler");
        yield return (AccessTools.Method(typeof(MainViewModel), "UpdateViewModelChanged",
                new[] { typeof(UpdateViewModelKind), typeof(object) }),
            "MainViewModel.UpdateViewModelChanged");

        // MainWindow inspector switch and refresh.
        yield return (AccessTools.Method(typeof(MainWindow), "RightZoneTypePropertyChanged",
                new[] { typeof(System.Windows.DependencyPropertyChangedEventArgs) }),
            "MainWindow.RightZoneTypePropertyChanged");
        yield return (AccessTools.Method(typeof(MainWindow), "UpdateRightZoneViews", new[] { typeof(RightZoneTypeEnum) }),
            "MainWindow.UpdateRightZoneViews");
        yield return (AccessTools.Method(typeof(MainWindow), "RefreshInspector"), "MainWindow.RefreshInspector");
        yield return (AccessTools.Method(typeof(MainWindow), "ShowLowerZone",
                new[] { typeof(LowerZoneKindEnum), typeof(bool) }),
            "MainWindow.ShowLowerZone");

        // Inspector data reloads.
        yield return (AccessTools.Method(typeof(MidiPartInspector), "UpdateView",
                new[] { typeof(object), typeof(Yamaha.VOCALOID.MusicalEditor.UpdateViewTypeFlag), typeof(UpdateObserverNotifyEventArgs), typeof(object) }),
            "MidiPartInspector.UpdateView");
        yield return (AccessTools.Method(typeof(SingerDivision), "UpdateViews"), "SingerDivision.UpdateViews");
        yield return (AccessTools.Method(typeof(SingerViewModel), "ReloadLanguageAndVoiceBank"),
            "SingerViewModel.ReloadLanguageAndVoiceBank");
        yield return (AccessTools.Method(typeof(SingerViewModel), "GetAllVoiceBanks", new[] { typeof(Yamaha.VOCALOID.VDM.VDMVoiceBankType) }),
            "SingerViewModel.GetAllVoiceBanks");
        yield return (AccessTools.Method(typeof(SingerViewModel), "GetLanguageTagList", new[] { typeof(WIVSMMidiTrack) }),
            "SingerViewModel.GetLanguageTagList");
        yield return (AccessTools.Method(typeof(StyleSearch), "UpdateViews"), "StyleSearch.UpdateViews");
        yield return (AccessTools.Method(typeof(StyleCustomize), "UpdateViews"), "StyleCustomize.UpdateViews");

        // First track/header visual tree construction.
        yield return (AccessTools.Constructor(typeof(MidiHeaderControl)), "MidiHeaderControl..ctor");
        yield return (AccessTools.Constructor(typeof(MidiTrackControl)), "MidiTrackControl..ctor");
        yield return (AccessTools.Constructor(typeof(VolumeLaneControl)), "VolumeLaneControl..ctor");
        yield return (AccessTools.Constructor(typeof(PanpotLaneControl)), "PanpotLaneControl..ctor");
        yield return (AccessTools.Method(typeof(HeaderView), "InsertHeaderControls",
                new[] { typeof(TrackEditorViewModel), typeof(List<WIVSMTrack>) }),
            "HeaderView.InsertHeaderControls");
        yield return (AccessTools.Method(typeof(TrackView), "InsertTrackControls",
                new[] { typeof(TrackEditorViewModel), typeof(List<WIVSMTrack>) }),
            "TrackView.InsertTrackControls");
        yield return (AccessTools.Method(typeof(MidiTrackControl), "InitDrawCanvas"), "MidiTrackControl.InitDrawCanvas");
        yield return (AccessTools.Method(typeof(MidiTrackControl), "UpdateView",
                new[] { typeof(object), typeof(Yamaha.VOCALOID.TrackEditor.UpdateViewTypeFlag), typeof(UpdateObserverNotifyEventArgs), typeof(object) }),
            "MidiTrackControl.UpdateView");

        // Native renderer start boundary.
        yield return (AccessTools.Method(typeof(WIVSMSequence), "StartAsyncRendering", Type.EmptyTypes),
            "WIVSMSequence.StartAsyncRendering");
        yield return (AccessTools.Method(typeof(MusicalEditorViewModel), "OnRendererStarted",
                new[] { typeof(object), typeof(RendererObserverStartEventArgs) }),
            "MusicalEditorViewModel.OnRendererStarted");
    }

    [HarmonyPrefix]
    private static void Prefix(MethodBase __originalMethod, out long __state)
    {
        __state = Stopwatch.GetTimestamp();
        _depth++;
        Write("->", Format(__originalMethod), __state, null);
    }

    [HarmonyPostfix]
    private static void Postfix(MethodBase __originalMethod, long __state)
    {
        double elapsed = Stopwatch.GetElapsedTime(__state).TotalMilliseconds;
        Write("<-", Format(__originalMethod), __state, elapsed);
        if (_depth > 0)
            _depth--;
    }

    private static string Format(MethodBase method)
    {
        string? parameters = method switch
        {
            MethodInfo info => "(" + string.Join(", ", Array.ConvertAll(info.GetParameters(), parameter => parameter.ParameterType.Name)) + ")",
            ConstructorInfo ctor => "(" + string.Join(", ", Array.ConvertAll(ctor.GetParameters(), parameter => parameter.ParameterType.Name)) + ")",
            _ => string.Empty,
        };
        return $"{method.DeclaringType?.FullName}.{method.Name}{parameters}";
    }

    private static void Write(string arrow, string label, long timestamp, double? elapsedMilliseconds)
    {
        if (_logPath == null)
            return;

        try
        {
            double total = Stopwatch.GetElapsedTime(StartedTicks).TotalMilliseconds;
            string duration = elapsedMilliseconds.HasValue
                ? $" duration={elapsedMilliseconds.Value.ToString("F3", CultureInfo.InvariantCulture)}ms"
                : string.Empty;
            string line =
                $"[{total,10:F2} ms] [T{Environment.CurrentManagedThreadId}] [{_depth,2}] " +
                $"{arrow} {label}{duration}";
            lock (Sync)
                File.AppendAllText(_logPath, line + Environment.NewLine);
        }
        catch
        {
        }
    }

    private static void Write(string message)
    {
        if (_logPath == null)
            return;

        try
        {
            lock (Sync)
                File.AppendAllText(_logPath, $"[{Stopwatch.GetElapsedTime(StartedTicks).TotalMilliseconds,10:F2} ms] {message}" + Environment.NewLine);
        }
        catch
        {
        }
    }

    private static void WriteWarning(string message)
    {
        if (_logPath == null)
            return;

        try
        {
            lock (Sync)
                File.AppendAllText(_logPath, $"[{Stopwatch.GetElapsedTime(StartedTicks).TotalMilliseconds,10:F2} ms] [warning] {message}" + Environment.NewLine);
        }
        catch
        {
        }
    }
}
