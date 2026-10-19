using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using HarmonyLib;
using VOCALOIDPatcher.Config;
using VOCALOIDPatcher.Translation;
using VOCALOIDPatcher.Utils;
using Yamaha.VOCALOID;
using Yamaha.VOCALOID.MusicalEditor;
using Yamaha.VOCALOID.VSM;

namespace VOCALOIDPatcher.AceParameters;

// Stage 0 host probe for the ACE carrier-controller strategy.
//
// Run with VOCALOID_ACE_PROBE=1 in a fresh, unsaved VOCALOID project. For every
// candidate carrier VSMControllerType it records native insert/read-back, native
// ParameterView breakpoint rendering, save-time JSON archive contents, and the
// runtime patch state. Results are written to
// %TEMP%\VOCALOIDPatcher\AceParameterProbe\<guid>\result.json.
// The probe is inert unless the environment variable is set, and all JSON
// writes are guarded so a probe bug can never terminate the editor.
internal static class AceParameterHostProbe
{
    private static readonly VSMControllerType[] Candidates =
    {
        VSMControllerType.DynamicsHmm,
        VSMControllerType.PitchBendHmm,
        VSMControllerType.WeightHmm,
        VSMControllerType.Expression,
        VSMControllerType.S5Air,
    };

    internal static void Queue()
    {
        if (Environment.GetEnvironmentVariable("VOCALOID_ACE_PROBE") == "1")
            Application.Current?.Dispatcher.BeginInvoke(new Action(Run));
    }

    private static async void Run()
    {
        string directory = Path.Combine(
            Path.GetTempPath(), "VOCALOIDPatcher", "AceParameterProbe", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(Path.GetDirectoryName(directory)!, "current.txt"), directory);
        var stages = new List<object>();
        void Record(string stage, object? data)
        {
            stages.Add(new { stage, data });
            try
            {
                File.WriteAllText(Path.Combine(directory, "result.json"),
                    JsonSerializer.Serialize(stages, new JsonSerializerOptions { WriteIndented = true }));
            }
            catch (Exception e)
            {
                try
                {
                    File.WriteAllText(Path.Combine(directory, "result-error.txt"), e.ToString());
                }
                catch
                {
                }
            }
        }

        try
        {
            Record("waiting_for_empty_document", null);
            for (int i = 0; i < 480 && App.Shared?.Document?.Sequence?.VSMSequence == null; i++)
                await Task.Delay(250);
            var document = App.Shared?.Document ?? throw new InvalidOperationException("No validation document.");
            var editor = document.Sequence!;
            var source = editor.VSMSequence!;
            for (int i = 0; i < 480 && Application.Current.Windows.OfType<Window>()
                     .Any(w => w.GetType().Name == "AddTrackDlg" && w.IsVisible); i++)
                await Task.Delay(250);

            bool empty = document.IsNew && document.DocumentUri == null;
            Record("startup", new
            {
                empty,
                source.IsDirty,
                tracks = source.NumTrack,
            });
            if (!empty)
                throw new InvalidOperationException("ACE probe requires a new, empty, unsaved document.");

            var track = (WIVSMMidiTrack)source.InsertTrackEx(source.NumTrack, VSMTrackType.Midi, "ACE probe")!;
            var part = track.InsertPart(VSMAbsTick.Zero, new VSMRelTick(2880), "ACE probe")!;
            try { part.SetVoiceBankID("BL8CEAM5N4XN3LFK"); } catch { }
            var note = part.InsertNote(new VSMRelTick(480), new VSMNoteEvent(480, 60, 64),
                part.GetDefaultNoteExpression(), part.GetDefaultAiNoteExpression(), "la", "l a", true, 4)!;
            source.InterpolatedMasterVolume();
            source.Commit(true);
            editor.SetActivePartAndTrack(part);
            ((MainWindow)Application.Current.MainWindow).ShowLowerZone(LowerZoneKindEnum.Musical, false);
            await Task.Delay(300);
            var vm = ((MainViewModel)Application.Current.MainWindow!.DataContext).MusicalEditorVM
                     ?? throw new InvalidOperationException("Musical editor is not initialized.");

            Record("runtime", new
            {
                aceEnabled = Settings.AceParameters,
                mappingEnabled = Settings.AceParameterMapping,
                definitions = AceParameterDefinitions.All.Select(d => new
                {
                    kind = d.Kind.ToString(),
                    parameterId = (int)d.ParameterType,
                    carrier = (int)d.CarrierType,
                    mapped = (int)MusicalEditorViewModel.CtrlParamTypeToVSMControllerType(d.ParameterType),
                    soundTarget = (int)d.SoundTarget,
                }).ToList(),
                patches = new
                {
                    carrierMap = PatchState(typeof(MusicalEditorViewModel), "CtrlParamTypeToVSMControllerType",
                        new[] { typeof(ControlParameterTypeEnum) }),
                    min = PatchState(typeof(MusicalEditorViewModel), "GetMinControllerValue",
                        new[] { typeof(ControlParameterTypeEnum) }),
                    commit = PatchState(typeof(WIVSMSequence), "Commit", new[] { typeof(bool) }),
                    save = PatchState(typeof(Yamaha.VOCALOID.Sequence), "SaveSequence",
                        new[] { typeof(WIVSMSequence), typeof(string), typeof(string), typeof(string) }),
                    load = PatchState(typeof(Yamaha.VOCALOID.Sequence), "LoadProjectSequenceFile",
                        new[] { typeof(string), typeof(WIVSMSequence).MakeByRefType() }),
                    combo = PatchState(typeof(ParameterHeaderControl), "UpdateControlParameterComboBox",
                        new[] { typeof(WIVSMMidiTrack), typeof(bool) }),
                },
                view = new
                {
                    activeTrack = vm.ActiveTrack != null,
                    activePart = vm.ActivePart != null,
                    partCount = vm.ActiveTrack?.MidiParts.Count,
                    parameterView = vm.ParameterView != null,
                },
            });

            string projectDirectory = Path.Combine(directory, "save");
            Directory.CreateDirectory(projectDirectory);

            foreach (var candidate in Candidates)
            {
                var result = new Dictionary<string, object?>
                {
                    ["candidateId"] = (int)candidate,
                    ["candidateName"] = candidate.ToString(),
                };
                try
                {
                    var first = part.InsertController(new VSMRelTick(120), candidate, 10);
                    var second = part.InsertController(new VSMRelTick(240), candidate, 50);
                    var third = part.InsertController(new VSMRelTick(360), candidate, 100);
                    ulong count = part.GetNumController(candidate);
                    var values = new List<int>();
                    for (ulong index = 0; index < count; index++)
                    {
                        var controller = part.GetController(candidate, index);
                        if (controller != null)
                            values.Add(controller.Value);
                    }
                    result["insertOk"] = first != null && second != null && third != null;
                    result["count"] = count;
                    result["values"] = values;
                    result["readBackOk"] = count >= 3 && values.Contains(10) && values.Contains(50) && values.Contains(100);
                    await Task.Delay(100);

                    // Native ParameterView rendering through an ACE custom type.
                    var definition = AceParameterDefinitions.All[0];
                    var previousCarrier = definition.CarrierType;
                    var previousType = vm.ControlParameterType;
                    try
                    {
                        AceParameterDefinitions.SetCarrierType(definition.Kind, candidate);
                        result["uiMappedCarrier"] =
                            (int)MusicalEditorViewModel.CtrlParamTypeToVSMControllerType(definition.ParameterType);
                        result["uiCarrierCountBefore"] = checked((int)part.GetNumController(candidate));
                        vm.ControlParameterType = definition.ParameterType;
                        try
                        {
                            AccessTools.Method(typeof(ParameterView), "UpdateDisplayControlParameter",
                                new[] { typeof(ControlParameterTypeEnum) })?.Invoke(
                                vm.ParameterView, new object[] { definition.ParameterType });
                        }
                        catch (Exception e)
                        {
                            result["uiInvokeError"] = e.InnerException?.Message ?? e.Message;
                        }
                        await Task.Delay(300);
                        var field = AccessTools.Field(typeof(ParameterView), "xUIControlParameters");
                        var parameters = field?.GetValue(vm.ParameterView) as UIControlParameters;
                        result["uiFieldFound"] = field != null;
                        result["uiParametersNull"] = parameters == null;
                        result["uiControlCount"] = parameters?.ControlParameters?.Count ?? -1;
                        var breakPoints = parameters?.ControlParameters
                            .SelectMany(p => p.Ctrls)
                            .OfType<BreakPoint>()
                            .ToList() ?? new List<BreakPoint>();
                        result["uiBreakPoints"] = breakPoints.Count;
                        result["uiValues"] = breakPoints.Select(b => b.Value).ToList();
                        result["uiOk"] = breakPoints.Count >= 3;
                    }
                    finally
                    {
                        AceParameterDefinitions.SetCarrierType(definition.Kind, previousCarrier);
                        vm.ControlParameterType = previousType;
                    }

                    // Save must append the ACE JSON archive (carrier curves are
                    // not persisted by the native sequence.json).
                    bool saved = editor.Save(projectDirectory, "ace-probe");
                    result["saveOk"] = saved;
                    var projectPath = Path.Combine(projectDirectory, "ace-probe.vpr");
                    if (saved && File.Exists(projectPath))
                    {
                        using var archive = ZipFile.OpenRead(projectPath);
                        var nativeEntry = archive.GetEntry("Project/sequence.json");
                        if (nativeEntry != null)
                        {
                            using var reader = new StreamReader(nativeEntry.Open());
                            string json = reader.ReadToEnd();
                            result["savedContainsNativeName"] =
                                json.Contains(candidate.ToString(), StringComparison.Ordinal);
                            result["savedNativeJsonBytes"] = json.Length;
                        }
                        var aceEntry = archive.GetEntry(AceParameterProjectArchive.EntryPath);
                        result["savedArchivePresent"] = aceEntry != null;
                        if (aceEntry != null)
                        {
                            using var reader = new StreamReader(aceEntry.Open());
                            result["savedArchiveBytes"] = reader.ReadToEnd().Length;
                        }
                    }
                }
                catch (Exception e)
                {
                    result["error"] = e.ToString();
                }
                finally
                {
                    RemoveControllers(part, candidate);
                }
                Record($"candidate_{(int)candidate}", result);
            }

            Record("done", new
            {
                notePos = note.RelPosTick.Value,
                partAbsPos = part.AbsPosTick.Value,
            });
        }
        catch (Exception e)
        {
            Record("failed", e.ToString());
        }
    }

    private static object PatchState(Type target, string method, Type[] args)
    {
        try
        {
            var info = AccessTools.Method(target, method, args);
            if (info == null)
                return new { found = false };
            var patch = Harmony.GetPatchInfo(info);
            return new
            {
                found = true,
                prefixes = patch?.Prefixes?.Count ?? 0,
                postfixes = patch?.Postfixes?.Count ?? 0,
                finalizers = patch?.Finalizers?.Count ?? 0,
            };
        }
        catch (Exception e)
        {
            return new { found = false, error = e.Message };
        }
    }

    private static void RemoveControllers(WIVSMMidiPart part, VSMControllerType type)
    {
        try
        {
            int count = checked((int)part.GetNumController(type));
            for (int index = count - 1; index >= 0; index--)
            {
                var controller = part.GetController(type, (ulong)index);
                if (controller != null)
                    part.RemoveController(controller);
            }
        }
        catch (Exception e)
        {
            Debug.Print(TranslationManager.Tr("VOCALOIDPatcher_Debug_AceParameter_Failed", e.Message));
        }
    }
}
