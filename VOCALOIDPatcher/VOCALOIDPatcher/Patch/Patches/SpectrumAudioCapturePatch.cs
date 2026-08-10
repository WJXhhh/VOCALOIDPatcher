using System;
using System.Windows;
using HarmonyLib;
using VOCALOIDPatcher.Utils.Audio;
using Yamaha.VOCALOID;
using Yamaha.VOCALOID.VAE;

namespace VOCALOIDPatcher.Patch.Patches;

public sealed class SpectrumAudioCapturePatch : PatchBase
{
    public override string PatchName => "SpectrumAudioCapturePatch";
    public override Type TargetClass => typeof(AudioPlayer);
    public override string TargetMethodName => ".ctor";
    public override bool IsConstructor => true;
    public override Type[] ArgumentTypes => new[] { typeof(VEConnectMode), typeof(Window) };

    [HarmonyPrefix]
    private static void Prefix()
    {
        AsioPcmTap.Install();
        DirectSoundPcmTap.Install();
    }
}
