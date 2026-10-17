using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using HarmonyLib;
using Yamaha.VOCALOID;
using Yamaha.VOCALOID.MusicalEditor;
using Yamaha.VOCALOID.VSM;

namespace VOCALOIDPatcher.ConsonantTiming;

internal static class TimingFeedbackHostValidation
{
    internal static void Queue()
    {
        if (Environment.GetEnvironmentVariable("VOCALOID_TIMING_FEEDBACK_VALIDATION") == "1")
            Application.Current?.Dispatcher.BeginInvoke(new Action(Run));
    }

    private static async void Run()
    {
        string directory = Path.Combine(Path.GetTempPath(), "VOCALOIDPatcher", "TimingFeedbackValidation", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(Path.GetDirectoryName(directory)!, "current.txt"), directory);
        var stages = new List<object>();
        void Record(string stage, object? data)
        {
            stages.Add(new { stage, data });
            File.WriteAllText(Path.Combine(directory, "result.json"), JsonSerializer.Serialize(stages, new JsonSerializerOptions { WriteIndented = true }));
        }
        try
        {
            Record("waiting_for_empty_document", null);
            for (int i = 0; i < 480 && App.Shared?.Document?.Sequence?.VSMSequence == null; i++) await Task.Delay(250);
            // Native initialization and the standard New command must finish
            // first; constructing a Sequence during module startup is unsafe.
            var document = App.Shared?.Document ?? throw new InvalidOperationException("No validation document.");
            var editor = document.Sequence!;
            var source = editor.VSMSequence!;
            for (int i = 0; i < 480 && Application.Current.Windows.OfType<Window>().Any(w => w.GetType().Name == "AddTrackDlg" && w.IsVisible); i++) await Task.Delay(250);
            Record("startup", new { document.IsNew, source.IsDirty, source.NumTrack,
                notes = source.Tracks.OfType<WIVSMMidiTrack>().SelectMany(t => t.MidiParts).Sum(p => (long)p.NumNotes) });
            if (!document.IsNew || document.DocumentUri != null || source.Tracks.OfType<WIVSMMidiTrack>()
                .SelectMany(t => t.MidiParts).Any(p => p.NumNotes != 0)) throw new InvalidOperationException("Validation requires an empty new unsaved document.");
            var track = (WIVSMMidiTrack)source.InsertTrackEx(source.NumTrack, VSMTrackType.Midi, "Timing feedback validation")!;
            var part = track.InsertPart(VSMAbsTick.Zero, new VSMRelTick(2880), "Timing feedback validation")!;
            if (!part.SetVoiceBankID("BL8CEAM5N4XN3LFK")) throw new InvalidOperationException("Missing fixture voicebank.");
            var note = part.InsertNote(new VSMRelTick(780), new VSMNoteEvent(780, 63, 64), part.GetDefaultNoteExpression(),
                part.GetDefaultAiNoteExpression(), "la", "l a", true, 4)!;
            source.InterpolatedMasterVolume(); source.Commit(true);
            editor.SetActivePartAndTrack(part);
            ((MainWindow)Application.Current.MainWindow).ShowLowerZone(LowerZoneKindEnum.Musical, false);
            await Task.Delay(250);
            var vm = ((MainViewModel)Application.Current.MainWindow!.DataContext).MusicalEditorVM
                ?? throw new InvalidOperationException("Musical editor is not initialized.");
            vm.EditorMode.Mode = EditModeME.Emotion;
            vm.ControlParameterType = ControlParameterTypeEnum.Velocity;
            source.StartAsyncRendering();
            for (int i = 0; i < 160 && !part.HasValidRenderedScore; i++) { source.InvokeRendererObserver(); await Task.Delay(50); }
            AccessTools.Method(typeof(PianorollView), "DrawNoteInsideActiveTrack", [typeof(MusicalEditorViewModel)])!.Invoke(vm.PianorollView, [vm]);
            AccessTools.Method(typeof(PianorollView), "DrawPhonemeCanvas", [typeof(MusicalEditorViewModel)])!.Invoke(vm.PianorollView, [vm]);
            AccessTools.Method(typeof(ParameterView), "CreateControlParameter", [typeof(MusicalEditorViewModel), typeof(ControlParameterTypeEnum), typeof(WIVSMMidiTrack)])!.Invoke(vm.ParameterView, [vm, ControlParameterTypeEnum.Velocity, null]);
            vm.UpdateViewport(); await Task.Delay(150);
            Record("before", new { hover = ConsonantTimingOverlay.ProbeHover(note), snapshot = ConsonantTimingOverlay.Diagnostics(note) });

            var parameters = (UIControlParameters)AccessTools.Field(typeof(ParameterView), "xUIControlParameters")!.GetValue(vm.ParameterView)!;
            var bar = parameters.ControlParameters.SelectMany(p => p.Ctrls).OfType<Bar>().Single(b => b.Note != null && (nint)b.Note == (nint)note);
            var textBox = (RegexTextBox)AccessTools.Field(typeof(ParameterView), "parameterTextBox")!.GetValue(vm.ParameterView)!;
            textBox.Target = bar; textBox.Text = "0";
            AccessTools.Field(typeof(ParameterView), "hideWhenLostFocus")!.SetValue(vm.ParameterView, true);
            AccessTools.Method(typeof(ParameterView), "OnTextBoxLostKeyboardFocus")!.Invoke(vm.ParameterView, [textBox]);
            await Task.Delay(150);
            Record("after_parameter_zero", new { hover = ConsonantTimingOverlay.ProbeHover(note), snapshot = ConsonantTimingOverlay.Diagnostics(note) });
            for (int i = 0; i < 160 && !part.HasValidRenderedScore; i++) { source.InvokeRendererObserver(); await Task.Delay(50); }
            await Task.Delay(150);
            Record("after_render", new { hover = ConsonantTimingOverlay.ProbeHover(note), snapshot = ConsonantTimingOverlay.Diagnostics(note) });
        }
        catch (Exception e) { Record("failed", e.ToString()); }
    }
}
