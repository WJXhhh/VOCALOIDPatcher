using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using System.Windows;
using HarmonyLib;
using VOCALOIDPatcher.Config;
using VOCALOIDPatcher.Utils;
using Yamaha.VOCALOID.MusicalEditor;
using Yamaha.VOCALOID.VSM;

namespace VOCALOIDPatcher.Patch.Patches;

internal static class ParameterPreviewPool
{
    private sealed class State
    {
        internal readonly BoundedPreviewPool<NomineeBreakPoint> Points = new(() => new(), 8192);
        internal readonly UINomineeControlParameter Group = new();
    }

    internal sealed class Frame(ParameterView view, Frame? previous)
    {
        internal readonly ParameterView View = view;
        internal readonly Frame? Previous = previous;
        private State? _pool;
        private bool _restored;

        internal void Enable()
        {
            _pool = States.GetValue(View, _ => new());
            _pool.Points.BeginUpdate();
        }

        internal NomineeBreakPoint Point()
        {
            var point = _pool?.Points.Rent() ?? new();
            point.IsSelected = false;
            return point;
        }

        internal UINomineeControlParameter Group()
        {
            if (_pool == null) return new();
            _pool.Group.Ctrls.Clear();
            return _pool.Group;
        }

        internal void Restore()
        {
            if (_restored) return;
            _restored = true;
            Active = Previous;
        }
    }

    private static readonly ConditionalWeakTable<ParameterView, State> States = new();
    [ThreadStatic] private static Frame? Active;

    internal static Frame? Begin(ParameterView view)
    {
        bool enabled = Settings.DeferParameterViewUpdate && view.Dispatcher.CheckAccess();
        if (!enabled && Active == null) return null;
        var frame = new Frame(view, Active);
        // A reentrant preview must not reuse the outer call's active objects.
        if (enabled && Active == null) frame.Enable();
        Active = frame;
        return frame;
    }

    internal static NomineeBreakPoint RentPoint() => Active?.Point() ?? new();
    internal static UINomineeControlParameter RentGroup() => Active?.Group() ?? new();

    internal static void Cleared(ParameterView view)
    {
        if (Active?.View == view || !States.TryGetValue(view, out var state)) return;
        state.Group.Ctrls.Clear();
        state.Group.Part = null;
    }

    internal static IEnumerable<CodeInstruction> Rewrite(IEnumerable<CodeInstruction> instructions)
    {
        var code = instructions.ToList();
        var point = AccessTools.Constructor(typeof(NomineeBreakPoint), Type.EmptyTypes);
        var group = AccessTools.Constructor(typeof(UINomineeControlParameter), Type.EmptyTypes);
        // Only replace constructors on the audited native preview methods.
        // An SDK with a different shape keeps its original allocation path.
        if (code.Count(i => i.opcode == OpCodes.Newobj && Equals(i.operand, point)) != 1 ||
            code.Count(i => i.opcode == OpCodes.Newobj && Equals(i.operand, group)) != 1)
        {
            Debug.Print("Parameter preview pooling: unsupported method shape.");
            return code;
        }
        foreach (var instruction in code)
        {
            if (instruction.opcode != OpCodes.Newobj) continue;
            MethodInfo? replacement = Equals(instruction.operand, point) ?
                AccessTools.Method(typeof(ParameterPreviewPool), nameof(RentPoint)) :
                Equals(instruction.operand, group) ? AccessTools.Method(typeof(ParameterPreviewPool), nameof(RentGroup)) : null;
            if (replacement == null) continue;
            instruction.opcode = OpCodes.Call;
            instruction.operand = replacement;
        }
        return code;
    }
}

public sealed class ParameterPencilPreviewPoolPatch : PatchBase
{
    public override string PatchName => nameof(ParameterPencilPreviewPoolPatch);
    public override Type TargetClass => typeof(ParameterView);
    public override string TargetMethodName => nameof(ParameterView.UpdateDragPencilNominee);
    public override Type[] ArgumentTypes => [typeof(WIVSMMidiPart), typeof(Point)];
    [HarmonyPrefix] private static void Prefix(ParameterView __instance, out ParameterPreviewPool.Frame? __state) => __state = ParameterPreviewPool.Begin(__instance);
    [HarmonyTranspiler] private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions) => ParameterPreviewPool.Rewrite(instructions);
    [HarmonyPostfix] private static void Postfix(ParameterPreviewPool.Frame? __state) => __state?.Restore();
    [HarmonyFinalizer] private static Exception? Finalizer(Exception? __exception, ParameterPreviewPool.Frame? __state) { __state?.Restore(); return __exception; }
}

public sealed class ParameterLinePreviewPoolPatch : PatchBase
{
    public override string PatchName => nameof(ParameterLinePreviewPoolPatch);
    public override Type TargetClass => typeof(ParameterView);
    public override string TargetMethodName => nameof(ParameterView.UpdateDragLineNominee);
    public override Type[] ArgumentTypes => [typeof(WIVSMMidiPart), typeof(long), typeof(double), typeof(long), typeof(double)];
    [HarmonyPrefix] private static void Prefix(ParameterView __instance, out ParameterPreviewPool.Frame? __state) => __state = ParameterPreviewPool.Begin(__instance);
    [HarmonyTranspiler] private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions) => ParameterPreviewPool.Rewrite(instructions);
    [HarmonyPostfix] private static void Postfix(ParameterPreviewPool.Frame? __state) => __state?.Restore();
    [HarmonyFinalizer] private static Exception? Finalizer(Exception? __exception, ParameterPreviewPool.Frame? __state) { __state?.Restore(); return __exception; }
}

public sealed class ParameterPreviewClearPoolPatch : PatchBase
{
    public override string PatchName => nameof(ParameterPreviewClearPoolPatch);
    public override Type TargetClass => typeof(ParameterView);
    public override string TargetMethodName => nameof(ParameterView.ClearNominees);
    public override Type[] ArgumentTypes => Type.EmptyTypes;
    [HarmonyPostfix] private static void Postfix(ParameterView __instance) => ParameterPreviewPool.Cleared(__instance);
}
