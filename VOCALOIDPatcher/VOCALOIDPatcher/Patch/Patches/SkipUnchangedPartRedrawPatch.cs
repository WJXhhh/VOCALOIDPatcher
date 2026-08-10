using System;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Windows.Threading;
using HarmonyLib;
using VOCALOIDPatcher.Config;
using VOCALOIDPatcher.Translation;
using VOCALOIDPatcher.Utils;
using Yamaha.VOCALOID;
using Yamaha.VOCALOID.MusicalEditor;
using Yamaha.VOCALOID.VSM;
using UpdateViewTypeFlag = Yamaha.VOCALOID.MusicalEditor.UpdateViewTypeFlag;

namespace VOCALOIDPatcher.Patch.Patches;

public class SkipUnchangedPartRedrawPatch : PatchBase
{
    public override string PatchName        => "SkipUnchangedPartRedrawPatch";
    public override Type   TargetClass      => typeof(PianorollView);
    public override string TargetMethodName => "UpdateView";

    public override Type[] ArgumentTypes => new[]
    {
        typeof(object),
        typeof(UpdateViewTypeFlag),
        typeof(UpdateObserverNotifyEventArgs),
        typeof(object)
    };

    private static readonly MethodInfo? MDrawNoteInside =
        AccessTools.Method(typeof(PianorollView), "DrawNoteInsideActiveTrack", new[] { typeof(MusicalEditorViewModel) });

    private static readonly MethodInfo? MDrawRenderedWave =
        AccessTools.Method(typeof(PianorollView), "DrawRenderedWaveCanvas", new[] { typeof(MusicalEditorViewModel) });

    private static readonly MethodInfo? MDrawVibrato =
        AccessTools.Method(typeof(PianorollView), "DrawVibratoPitchCurveCanvas", Type.EmptyTypes);

    private static readonly MethodInfo? MDrawPitchBend =
        AccessTools.Method(typeof(PianorollView), "DrawPitchBendPitchCurveCanvas", Type.EmptyTypes);

    private static readonly MethodInfo? MDrawAmplitude =
        AccessTools.Method(typeof(PianorollView), "DrawAmplitudeCurveCanvas", new[] { typeof(MusicalEditorViewModel) });

    private static readonly MethodInfo? MDrawPartName =
        AccessTools.Method(typeof(PianorollView), "DrawPartName", new[] { typeof(MusicalEditorViewModel) });

    private static readonly MethodInfo? MUpdateOutsideLayer =
        AccessTools.Method(typeof(PianorollView), "UpdateOutsideActivePartLayer", new[] { typeof(MusicalEditorViewModel) });

    private static readonly MethodInfo? MRedrawSelectedNotes =
        AccessTools.Method(typeof(PianorollView), "RedrawSelectChangedNotes", Type.EmptyTypes);

    private static readonly MethodInfo? MUpdateView =
        AccessTools.Method(typeof(PianorollView), "UpdateView", new[]
        {
            typeof(object),
            typeof(UpdateViewTypeFlag),
            typeof(UpdateObserverNotifyEventArgs),
            typeof(object)
        });

    private static readonly bool MethodsResolved =
        MDrawNoteInside != null && MDrawRenderedWave != null && MDrawVibrato != null
        && MDrawPitchBend != null && MDrawAmplitude != null && MDrawPartName != null
        && MUpdateOutsideLayer != null && MRedrawSelectedNotes != null && MUpdateView != null;

    private sealed class TrackBox
    {
        public WIVSMMidiTrack? Track;
        public bool FirstShowCompleted;
        public bool FirstShowScheduled;
        public bool BypassFirstShowDefer;
    }

    private static readonly ConditionalWeakTable<PianorollView, TrackBox> LastTrack = new();

    [HarmonyPrefix]
    private static bool Prefix(PianorollView __instance, UpdateViewTypeFlag typeFlags)
    {
        if (!Settings.SkipUnchangedPartRedraw || !MethodsResolved)
            return true;

        try
        {
            if (__instance.DataContext is not MusicalEditorViewModel vm)
                return true;

            var box = LastTrack.GetOrCreateValue(__instance);

            if (box.BypassFirstShowDefer)
                return true;

            // The editor rebuilds all pianoroll layers even while its lower zone is hidden.
            // ShowMusicalEditor will rebuild them with the final viewport a moment later.
            if (IsActivePartOrTrackChange(typeFlags) && !__instance.IsVisible)
                return false;

            if (!box.FirstShowCompleted && typeFlags == UpdateViewTypeFlag.ShowMusicalEditor)
            {
                ScheduleFirstShow(__instance, box);
                return false;
            }

            if (box.FirstShowScheduled && IsActivePartOrTrackChange(typeFlags))
                return false;

            if (typeFlags == UpdateViewTypeFlag.NoteSelectionChanged)
            {
                MRedrawSelectedNotes!.Invoke(__instance, null);

                switch (vm.EditorMode.Mode)
                {
                    case EditModeME.Pitch:
                    case EditModeME.PitchPencil:
                    case EditModeME.PitchEraser:
                    case EditModeME.Vibrato:
                        MDrawVibrato!.Invoke(__instance, null);
                        MDrawPitchBend!.Invoke(__instance, null);
                        MDrawAmplitude!.Invoke(__instance, new object?[] { vm });
                        break;
                    case EditModeME.Amplitude:
                        MDrawAmplitude!.Invoke(__instance, new object?[] { vm });
                        MDrawRenderedWave!.Invoke(__instance, new object?[] { vm });
                        break;
                }

                vm.UpdateViewport();
                return false;
            }

            if (typeFlags != UpdateViewTypeFlag.ActivePartChanged)
                return true;

            var current = vm.ActiveTrack;
            bool sameTrack = current != null && box.Track != null && current.Equals(box.Track);
            box.Track = current;

            if (!sameTrack)
                return true;

            var args = new object?[] { vm };

            if (__instance.EditMode == EditModeME.Emotion)
                MDrawNoteInside!.Invoke(__instance, args);

            MDrawRenderedWave!.Invoke(__instance, args);
            MDrawVibrato!.Invoke(__instance, null);
            MDrawPitchBend!.Invoke(__instance, null);
            MDrawAmplitude!.Invoke(__instance, args);
            MDrawPartName!.Invoke(__instance, args);
            MUpdateOutsideLayer!.Invoke(__instance, args);
            vm.UpdateViewport();

            return false;
        }
        catch (Exception e)
        {
            Debug.Print(TranslationManager.Tr("VOCALOIDPatcher_Debug_SkipPartRedraw_Failed", e.Message));
            return true;
        }
    }

    private static bool IsActivePartOrTrackChange(UpdateViewTypeFlag typeFlags) =>
        typeFlags == UpdateViewTypeFlag.ActiveTrackChanged ||
        typeFlags == UpdateViewTypeFlag.ActivePartChanged;

    private static void ScheduleFirstShow(PianorollView view, TrackBox box)
    {
        if (box.FirstShowScheduled)
            return;

        box.FirstShowScheduled = true;
        try
        {
            view.Dispatcher.BeginInvoke(new Action(() =>
            {
                box.FirstShowScheduled = false;
                if (!view.IsVisible)
                    return;

                try
                {
                    box.BypassFirstShowDefer = true;
                    MUpdateView!.Invoke(view, new object?[]
                    {
                        view,
                        UpdateViewTypeFlag.ShowMusicalEditor,
                        null,
                        null
                    });
                    box.FirstShowCompleted = true;
                }
                catch (Exception e)
                {
                    Debug.Print(TranslationManager.Tr("VOCALOIDPatcher_Debug_SkipPartRedraw_Failed", e.Message));
                }
                finally
                {
                    box.BypassFirstShowDefer = false;
                }
            }), DispatcherPriority.Background);
        }
        catch
        {
            box.FirstShowScheduled = false;
            throw;
        }
    }
}
