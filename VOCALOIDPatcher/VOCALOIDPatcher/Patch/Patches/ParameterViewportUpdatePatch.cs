using System;
using System.Collections.Generic;
using System.Reflection.Emit;
using HarmonyLib;
using VOCALOIDPatcher.Config;
using Yamaha.VOCALOID.MusicalEditor;
using Yamaha.VOCALOID.VSM;

namespace VOCALOIDPatcher.Patch.Patches;

// Parameter controls are retained drawings, not FastCanvas children. A pure
// controller notification does not change membership of the ten piano canvases
// scanned by MusicalEditorDivision.UpdateViewport. Keep native parameter data
// rebuilding synchronous, while omitting that unrelated viewport traversal.
public sealed class ParameterViewportUpdatePatch : PatchBase
{
    public override string PatchName => nameof(ParameterViewportUpdatePatch);
    public override Type TargetClass => typeof(ParameterView);
    public override string TargetMethodName => "UpdateView";
    public override Type[] ArgumentTypes => [typeof(object), typeof(UpdateViewTypeFlag), typeof(UpdateObserverNotifyEventArgs), typeof(object)];

    [HarmonyTranspiler]
    private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
    {
        var update = AccessTools.Method(typeof(MusicalEditorViewModel), nameof(MusicalEditorViewModel.UpdateViewport), Type.EmptyTypes);
        var selective = AccessTools.Method(typeof(ParameterViewportUpdatePatch), nameof(UpdateViewport));
        foreach (var instruction in instructions)
        {
            if (instruction.Calls(update))
            {
                var flag = new CodeInstruction(OpCodes.Ldarg_2);
                flag.labels.AddRange(instruction.labels);
                instruction.labels.Clear();
                yield return flag;
                instruction.opcode = OpCodes.Call;
                instruction.operand = selective;
            }
            yield return instruction;
        }
    }

    private static void UpdateViewport(MusicalEditorViewModel vm, UpdateViewTypeFlag flag)
    {
        if (flag == UpdateViewTypeFlag.ControlParameterChanged && Settings.DeferParameterViewUpdate) return;
        vm.UpdateViewport();
    }
}
