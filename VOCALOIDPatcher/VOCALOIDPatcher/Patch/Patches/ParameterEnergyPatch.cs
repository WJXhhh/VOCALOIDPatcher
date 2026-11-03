using System;
using HarmonyLib;
using VOCALOIDPatcher.ParameterEnergy;
using Yamaha.VOCALOID.MusicalEditor;

namespace VOCALOIDPatcher.Patch.Patches;

public sealed class ParameterEnergyConstructorPatch : PatchBase
{
    public override string PatchName => nameof(ParameterEnergyConstructorPatch);
    public override Type TargetClass => typeof(ParameterView);
    public override string TargetMethodName => ".ctor";
    public override bool IsConstructor => true;
    public override Type[] ArgumentTypes => Type.EmptyTypes;

    [HarmonyPostfix]
    private static void Postfix(ParameterView __instance) => ParameterEnergyBackground.Attach(__instance);
}
