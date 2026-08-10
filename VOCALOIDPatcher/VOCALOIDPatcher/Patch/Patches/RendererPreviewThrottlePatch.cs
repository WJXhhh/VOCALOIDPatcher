using System;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using HarmonyLib;
using VOCALOIDPatcher.Config;
using Yamaha.VOCALOID.MusicalEditor;
using Yamaha.VOCALOID.VSM;

namespace VOCALOIDPatcher.Patch.Patches;

public class RendererPreviewThrottlePatch : PatchBase
{
    public override string PatchName        => "RendererPreviewThrottlePatch";
    public override Type   TargetClass      => typeof(PianorollView);
    public override string TargetMethodName => "OnRendererBlockRendered";

    public override Type[] ArgumentTypes => new[] { typeof(object), typeof(RendererObserverBlockRenderingEventArgs) };

    private const int FramesPerSecond = 30;
    private static readonly long IntervalTicks = Math.Max(Stopwatch.Frequency / FramesPerSecond, 1L);

    private sealed class State
    {
        public long LastUpdate;
    }

    private static readonly ConditionalWeakTable<PianorollView, State> States = new();

    [HarmonyPrefix]
    private static bool Prefix(PianorollView __instance, RendererObserverBlockRenderingEventArgs e)
    {
        if (!Settings.ThrottleRendererPreview)
        {
            WaveformSnapshot.RendererBlockRendered(__instance, e);
            return true;
        }

        var state = States.GetOrCreateValue(__instance);
        long now = Stopwatch.GetTimestamp();
        if (now - state.LastUpdate < IntervalTicks)
            return false;

        state.LastUpdate = now;
        WaveformSnapshot.RendererBlockRendered(__instance, e);
        return true;
    }
}
