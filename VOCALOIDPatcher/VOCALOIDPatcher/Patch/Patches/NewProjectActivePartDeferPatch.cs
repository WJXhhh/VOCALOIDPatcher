using System;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Threading;
using HarmonyLib;
using VOCALOIDPatcher.Config;
using VOCALOIDPatcher.Translation;
using VOCALOIDPatcher.Utils;
using Yamaha.VOCALOID;
using Yamaha.VOCALOID.VSM;

namespace VOCALOIDPatcher.Patch.Patches;

/// <summary>
/// Marks the native new-project setup call. The Add Track dialog runs a nested
/// dispatcher loop inside this method, so the scope remains active until V6
/// performs its automatic first-part selection after the dialog closes.
/// </summary>
public class NewProjectSetupScopePatch : PatchBase
{
    public override string PatchName => nameof(NewProjectSetupScopePatch);
    public override Type TargetClass => typeof(MainWindow);
    public override string TargetMethodName => "UpdateView";

    public override Type[] ArgumentTypes =>
    [
        typeof(object),
        typeof(UpdateViewTypeFlag),
        typeof(UpdateObserverNotifyEventArgs),
        typeof(object)
    ];

    [ThreadStatic]
    private static int _depth;

    internal static bool IsActive => _depth > 0;

    [HarmonyPrefix]
    private static void Prefix(UpdateViewTypeFlag typeFlags, out bool __state)
    {
        __state = typeFlags == UpdateViewTypeFlag.SetupAfterSequenceChange;
        if (__state)
            _depth++;
    }

    [HarmonyFinalizer]
    private static Exception? Finalizer(bool __state, Exception? __exception)
    {
        if (__state && _depth > 0)
            _depth--;
        return __exception;
    }
}

/// <summary>
/// Creating the first active MIDI part synchronously creates V6's native
/// partial renderer. On a cold project this takes place before WPF can present
/// the newly-created track. Queue only that automatic new-project activation
/// below Render/Input priority so the track is painted first.
/// </summary>
public class NewProjectActivePartDeferPatch : PatchBase
{
    public override string PatchName => nameof(NewProjectActivePartDeferPatch);
    public override Type TargetClass => typeof(Sequence);
    public override string TargetMethodName => nameof(Sequence.SetActivePartAndTrack);
    public override Type[] ArgumentTypes => [typeof(WIVSMPart)];

    private static readonly MethodInfo? Original =
        AccessTools.Method(typeof(Sequence), nameof(Sequence.SetActivePartAndTrack), [typeof(WIVSMPart)]);

    private sealed class PendingState
    {
        public bool Passthrough;
        public bool Scheduled;
        public WIVSMPart? Part;
    }

    private static readonly ConditionalWeakTable<Sequence, PendingState> Pending = new();

    [HarmonyPrefix]
    private static bool Prefix(Sequence __instance, WIVSMPart part, ref bool __result)
    {
        var state = Pending.GetOrCreateValue(__instance);
        if (!Settings.FastProjectLoad || !NewProjectSetupScopePatch.IsActive || state.Passthrough ||
            Original == null || __instance.ActivePart != null || part is not WIVSMMidiPart)
            return true;

        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher == null || dispatcher.HasShutdownStarted)
            return true;

        state.Part = part;
        __result = false;
        if (state.Scheduled)
            return false;

        state.Scheduled = true;
        dispatcher.BeginInvoke(new Action(() => Activate(__instance, state, dispatcher)),
            DispatcherPriority.Background);
        return false;
    }

    private static void Activate(Sequence sequence, PendingState state, Dispatcher dispatcher)
    {
        state.Scheduled = false;
        WIVSMPart? part = state.Part;
        state.Part = null;

        if (part == null || dispatcher.HasShutdownStarted || sequence.VSMSequence == null ||
            !ReferenceEquals(App.Shared?.Document?.Sequence, sequence))
            return;

        try
        {
            state.Passthrough = true;
            Original!.Invoke(sequence, [part]);
        }
        catch (Exception exception)
        {
            string message = exception is TargetInvocationException { InnerException: { } inner }
                ? inner.Message
                : exception.Message;
            Debug.Print(TranslationManager.Tr("VOCALOIDPatcher_Debug_NewProjectActivePartDefer_Failed", message));
        }
        finally
        {
            state.Passthrough = false;
        }
    }
}
