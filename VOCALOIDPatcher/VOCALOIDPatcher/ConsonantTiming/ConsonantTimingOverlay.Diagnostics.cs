using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using HarmonyLib;
using VOCALOIDPatcher.Config;
using Yamaha.VOCALOID.MusicalEditor;
using Yamaha.VOCALOID.VSM;

namespace VOCALOIDPatcher.ConsonantTiming;

internal sealed partial class ConsonantTimingOverlay
{
    // Explicit, bounded diagnostic projection; never called by drawing or
    // mouse-move logging. No lyrics, paths or raw native handles are returned.
    internal static object Diagnostics(WIVSMNote note) => new {
        module = typeof(ConsonantTimingOverlay).Module.ModuleVersionId,
        modelVelocity = note.NoteVelocity,
        boundaries = note.GetPhonemePositions().Take(65).ToArray(),
        nativeBoundaries = NativeTimingPositions.Read(note,false),
        renderServiceEnabled = note.Sequence?.CanAsyncRendering(),
        renderFinished = note.Sequence?.IsFinishedRendering,
        validRenderedScore = note.Parent?.HasValidRenderedScore,
        validRenderedWave = note.Parent?.HasValidRenderedWave,
        revision = ConsonantTimingRuntime.Revision,
        enabled = Settings.IntuitiveConsonantTiming,
        backendReady = ConsonantTimingRuntime.Backend != null,
        timingHost = SampledTimingHost.Diagnostics(note.Sequence),
        phonemeVolume = PhonemeVolume.PhonemeVolumeHost.Diagnostics(note),
        timingControls = note.Sequence is { } source ? SampledTimingHost.Find(source)?.Document
            .Get(SampledTimingVsmBinding.Address(note))?.Adjustments.Take(65)
            .Select(a => new { a.Kind, a.Index, a.Value }).ToArray() : null,
        commitPatches = Harmony.GetPatchInfo(AccessTools.Method(typeof(WIVSMSequence), nameof(WIVSMSequence.Commit), [typeof(bool)]))?
            .Postfixes.Select(p => p.PatchMethod.DeclaringType?.Name).ToArray(),
        views = States.Take(4).Select(pair => {
            var state = pair.Value;
            var vm = pair.Key.DataContext as MusicalEditorViewModel;
            var parameters = vm?.ParameterView == null ? null :
                AccessTools.Field(typeof(ParameterView), "xUIControlParameters")?.GetValue(vm.ParameterView) as UIControlParameters;
            return new {
                loaded = pair.Key.IsLoaded, root = state._root.Visibility.ToString(),
                sourceMatches = vm?.VSMSequence != null && (nint)vm.VSMSequence == (nint)note.Sequence!,
                onsets = state._onsets.Take(20).Select(o => new {
                    matches = o.Visual.Note != null && (nint)o.Visual.Note == (nint)note,
                    velocity = o.Visual.Note?.NoteVelocity,
                    o.Milliseconds, o.X, o.Right,
                    hovered = ReferenceEquals(o, state._hover)
                }).ToArray(),
                bars = parameters?.ControlParameters.SelectMany(p => p.Ctrls).OfType<Bar>().Take(20).Select(b => new {
                    b.Value, velocity = b.Note?.NoteVelocity,
                    matches = b.Note != null && (nint)b.Note == (nint)note
                }).ToArray(),
                tipOpen = state._tip.IsOpen, tip = state._tip.Content?.ToString(),
                tipVisualText = ReadTipVisualText(state._tip),
                feedbackVelocity = state._feedbackVelocity, feedbackRevision = state._feedbackRevision,
                feedbackMilliseconds = state._feedbackMilliseconds, feedbackMessage = state._feedbackMessage,
                phonemeFailure = state._phonemeFailure,
                dragVelocity = state._drag?.Search.Best.Velocity
            };
        }).ToArray()
    };

    private static string[] ReadTipVisualText(ToolTip tip)
    {
        var pending = new Queue<DependencyObject>();
        var text = new List<string>();
        pending.Enqueue(tip);
        for (int visited = 0; pending.Count > 0 && visited < 64; visited++)
        {
            var node = pending.Dequeue();
            if (node is TextBlock block) text.Add(block.Text);
            for (int i = 0; i < VisualTreeHelper.GetChildrenCount(node); i++)
                pending.Enqueue(VisualTreeHelper.GetChild(node, i));
        }
        return text.ToArray();
    }

    internal static string? ProbeHover(WIVSMNote note)
    {
        foreach (var pair in States)
        {
            var state = pair.Value;
            var onset = state._onsets.FirstOrDefault(o => o.Visual.Note != null && (nint)o.Visual.Note == (nint)note);
            if (onset == null) continue;
            var point = new System.Windows.Point(onset.X, onset.Y + onset.Height / 2);
            Search(pair.Key, point, out _);
            return state._tip.Content?.ToString();
        }
        return null;
    }
}
