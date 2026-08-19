using System;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Windows.Threading;
using HarmonyLib;
using VOCALOIDPatcher.Config;
using VOCALOIDPatcher.Translation;
using VOCALOIDPatcher.Utils;
using Yamaha.VOCALOID;

namespace VOCALOIDPatcher.Patch.Patches;

/// <summary>
/// V6 refreshes the MIDI-part inspector synchronously as part of the first
/// track/part selection, even while the right zone is hidden. The hidden path
/// still updates style/expression controls and fires repeated TrackEditor
/// updates, which measurably delays the first track from appearing.
/// Hidden refreshes are skipped (ShowInspector refreshes on show), and visible
/// refreshes are coalesced onto Dispatcher.Background so the track editor
/// becomes responsive first.
/// </summary>
public class InspectorRefreshDeferPatch : PatchBase
{
    public override string PatchName => nameof(InspectorRefreshDeferPatch);
    public override Type TargetClass => typeof(MainWindow);
    public override string TargetMethodName => "RefreshInspector";

    private static readonly MethodInfo? Original =
        AccessTools.Method(typeof(MainWindow), "RefreshInspector");

    private sealed class PendingState
    {
        public bool Passthrough;
        public bool Scheduled;
    }

    private static readonly ConditionalWeakTable<MainWindow, PendingState> Pending = new();

    [HarmonyPrefix]
    private static bool Prefix(MainWindow __instance)
    {
        var state = Pending.GetOrCreateValue(__instance);
        if (!Settings.DeferInspectorRefresh || state.Passthrough || Original == null)
            return true;

        // When the right zone is hidden the native method performs no useful UI
        // work; it only reloads data that will be refreshed again by
        // ShowRightZoneViews when the inspector is actually shown.
        if (!__instance.IsInspectorShown)
            return false;

        if (state.Scheduled)
            return false;

        state.Scheduled = true;
        var dispatcher = __instance.Dispatcher;
        dispatcher.BeginInvoke(new Action(() =>
        {
            state.Scheduled = false;
            if (!__instance.IsInspectorShown || dispatcher.HasShutdownStarted)
                return;

            try
            {
                state.Passthrough = true;
                Original.Invoke(__instance, null);
            }
            catch (Exception exception)
            {
                string message = exception is TargetInvocationException { InnerException: { } inner }
                    ? inner.Message
                    : exception.Message;
                Debug.Print(TranslationManager.Tr("VOCALOIDPatcher_Debug_InspectorRefreshDefer_Failed", message));
            }
            finally
            {
                state.Passthrough = false;
            }
        }), DispatcherPriority.Background);

        return false;
    }
}
