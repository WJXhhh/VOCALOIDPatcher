using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using VOCALOIDPatcher.Config;
using VOCALOIDPatcher.Mcp;
using VOCALOIDPatcher.Mcp.Core;
using VOCALOIDPatcher.Utils;
using Yamaha.VOCALOID;
using Yamaha.VOCALOID.G2PA;
using Yamaha.VOCALOID.MusicalEditor;
using Yamaha.VOCALOID.VSM;

namespace VOCALOIDPatcher.Patch.Patches;

internal sealed class RuntimeObservationState
{
    public long Started { get; init; }
    public long CommitCycleId { get; init; }
    public RenderCycle RenderCycle { get; init; }
    public Dictionary<string, object?> Before { get; init; } = new();
}

internal static class RuntimeObservationPatchSupport
{
    public static RuntimeObservationState Begin(
        string eventName,
        Dictionary<string, object?> data,
        long commitCycleId = 0,
        RenderCycle renderCycle = default)
    {
        RuntimeObservationLog.AddCycleData(data, commitCycleId, renderCycle);
        RuntimeObservationLog.Write(eventName, "enter", data);
        return new RuntimeObservationState
        {
            Started = RuntimeObservationLog.Timestamp(),
            CommitCycleId = commitCycleId,
            RenderCycle = renderCycle,
            Before = data,
        };
    }

    public static void End(
        string eventName,
        RuntimeObservationState? state,
        Dictionary<string, object?> data)
    {
        if (state != null)
        {
            data["elapsedTicks"] = RuntimeObservationLog.Timestamp() - state.Started;
            RuntimeObservationLog.AddCycleData(data, state.CommitCycleId, state.RenderCycle);
        }
        RuntimeObservationLog.Write(eventName, "exit", data);
    }

    public static void Exception(string eventName, RuntimeObservationState? state, Exception exception)
    {
        var data = new Dictionary<string, object?>
        {
            ["exception"] = exception.GetType().Name,
        };
        if (state != null)
        {
            data["elapsedTicks"] = RuntimeObservationLog.Timestamp() - state.Started;
            RuntimeObservationLog.AddCycleData(data, state.CommitCycleId, state.RenderCycle);
        }
        RuntimeObservationLog.Write(eventName, "exception", data);
    }

    public static Dictionary<string, object?> PartEventData(
        WIVSMMidiPart? part,
        VSMRendererProgress? progress = null)
    {
        var data = new Dictionary<string, object?>
        {
            ["part"] = RuntimeObservationLog.PartSnapshot(part),
        };
        IntPtr partHandle = part == null ? IntPtr.Zero : (IntPtr)part;
        RenderCycle renderCycle = RuntimeObservationLog.RenderCycleForPart(partHandle);
        if (renderCycle.Id == 0 || renderCycle.CommitCycleId == 0)
            renderCycle = RuntimeObservationLog.BeginRenderCycle(partHandle, part);
        RuntimeObservationLog.AddCycleData(data, renderCycle: renderCycle);
        if (progress.HasValue)
            data["progress"] = RuntimeObservationLog.ProgressSnapshot(progress.Value);
        return data;
    }

    public static WIVSMSequence? TransactionSequence(Transaction transaction)
    {
        try
        {
            return AccessTools.Property(typeof(Transaction), "VSMSequence")?.GetValue(transaction) as WIVSMSequence;
        }
        catch
        {
            return null;
        }
    }

    public static Dictionary<string, object?> BreathEventData(WIVSMBreathEffect effect)
    {
        WIVSMMidiPart? part = null;
        try { part = effect.Parent?.Parent as WIVSMMidiPart; }
        catch { }
        return new Dictionary<string, object?>
        {
            ["part"] = RuntimeObservationLog.PartSnapshot(part),
            ["effect"] = RuntimeObservationLog.BreathEffectSnapshot(effect),
        };
    }
}

internal static class RendererNativeObservation
{
    public static Dictionary<string, object?> Snapshot(IntPtr part, IntPtr buffers, IntPtr scores)
    {
        var data = new Dictionary<string, object?>
        {
            ["audioListId"] = RuntimeObservationLog.ObjectId("pcmList", buffers),
            ["scoreListId"] = RuntimeObservationLog.ObjectId("scoreList", scores),
        };

        try
        {
            using var bufferList = buffers == IntPtr.Zero ? null : new WIVSMAudioBufferList(buffers);
            using var scoreList = scores == IntPtr.Zero ? null : new VSMScoreList(scores);
            foreach ((string key, object? value) in RuntimeObservationLog.BlockSnapshot(bufferList, scoreList, part))
                data[key] = value;
        }
        catch (Exception exception)
        {
            data["snapshotError"] = exception.GetType().Name;
        }

        return data;
    }
}

public class ObserveRendererStartPatch : PatchBase
{
    public override string PatchName => nameof(ObserveRendererStartPatch);
    public override Type TargetClass => typeof(WIVSMRendererObserver);
    public override string TargetMethodName => "InvokeStartEvent";
    public override Type[] ArgumentTypes => new[] { typeof(IntPtr), typeof(IntPtr) };

    [HarmonyPrefix]
    private static void Prefix(IntPtr client, IntPtr pMidiPart, out RuntimeObservationState? __state)
    {
        __state = null;
        try
        {
            RenderCycle renderCycle = RuntimeObservationLog.BeginRenderCycle(pMidiPart, null);
            __state = RuntimeObservationPatchSupport.Begin("render.started.native", new Dictionary<string, object?>
            {
                ["clientId"] = RuntimeObservationLog.ObjectId("observerClient", client),
                ["partId"] = RuntimeObservationLog.ObjectId("part", pMidiPart),
            }, renderCycle: renderCycle);
        }
        catch
        {
        }
    }

    [HarmonyPostfix]
    private static void Postfix(IntPtr pMidiPart, RuntimeObservationState? __state)
    {
        try
        {
            if (Settings.McpEnabled)
            {
                (string projectId, long revision) = McpRevisionTracker.Current();
                McpEventHub.Publish("render_started", projectId, revision);
            }
            RuntimeObservationPatchSupport.End("render.started.native", __state, new Dictionary<string, object?>
            {
                ["partId"] = RuntimeObservationLog.ObjectId("part", pMidiPart),
            });
        }
        catch
        {
        }
    }

    [HarmonyFinalizer]
    private static Exception? Finalizer(Exception? __exception, RuntimeObservationState? __state)
    {
        if (__exception != null)
            RuntimeObservationPatchSupport.Exception("render.started.native", __state, __exception);
        return __exception;
    }
}

public class ObserveRendererBlockPatch : PatchBase
{
    public override string PatchName => nameof(ObserveRendererBlockPatch);
    public override Type TargetClass => typeof(WIVSMRendererObserver);
    public override string TargetMethodName => "InvokeBlockRenderingEvent";
    public override Type[] ArgumentTypes => new[]
    {
        typeof(IntPtr), typeof(IntPtr), typeof(VSMRendererProgress), typeof(IntPtr), typeof(IntPtr),
    };

    [HarmonyPrefix]
    private static void Prefix(
        IntPtr client,
        IntPtr pMidiPart,
        VSMRendererProgress progress,
        IntPtr pAudioBufferList,
        IntPtr scoreListHandle,
        out RuntimeObservationState? __state)
    {
        __state = null;
        try
        {
            RenderCycle renderCycle = RuntimeObservationLog.RenderCycleForPart(pMidiPart);
            Dictionary<string, object?> data = RendererNativeObservation.Snapshot(
                pMidiPart, pAudioBufferList, scoreListHandle);
            data["clientId"] = RuntimeObservationLog.ObjectId("observerClient", client);
            data["partId"] = RuntimeObservationLog.ObjectId("part", pMidiPart);
            data["progress"] = RuntimeObservationLog.ProgressSnapshot(progress);
            __state = RuntimeObservationPatchSupport.Begin("render.block.native", data, renderCycle: renderCycle);
        }
        catch
        {
        }
    }

    [HarmonyPostfix]
    private static void Postfix(IntPtr pMidiPart, RuntimeObservationState? __state)
    {
        try
        {
            RuntimeObservationPatchSupport.End("render.block.native", __state, new Dictionary<string, object?>
            {
                ["partId"] = RuntimeObservationLog.ObjectId("part", pMidiPart),
            });
        }
        catch
        {
        }
    }

    [HarmonyFinalizer]
    private static Exception? Finalizer(Exception? __exception, RuntimeObservationState? __state)
    {
        if (__exception != null)
            RuntimeObservationPatchSupport.Exception("render.block.native", __state, __exception);
        return __exception;
    }
}

public class ObserveRendererProgressPatch : PatchBase
{
    public override string PatchName => nameof(ObserveRendererProgressPatch);
    public override Type TargetClass => typeof(WIVSMRendererObserver);
    public override string TargetMethodName => "InvokeProgressEvent";
    public override Type[] ArgumentTypes => new[] { typeof(IntPtr), typeof(IntPtr), typeof(VSMRendererProgress) };

    [HarmonyPrefix]
    private static void Prefix(IntPtr pMidiPart, VSMRendererProgress progress)
    {
        try
        {
            var data = new Dictionary<string, object?>
            {
                ["partId"] = RuntimeObservationLog.ObjectId("part", pMidiPart),
                ["progress"] = RuntimeObservationLog.ProgressSnapshot(progress),
            };
            RuntimeObservationLog.AddCycleData(
                data, renderCycle: RuntimeObservationLog.RenderCycleForPart(pMidiPart));
            RuntimeObservationLog.Write("render.progress.native", "callback", data);
        }
        catch
        {
        }
    }
}

public class ObserveRendererCompletePatch : PatchBase
{
    public override string PatchName => nameof(ObserveRendererCompletePatch);
    public override Type TargetClass => typeof(WIVSMRendererObserver);
    public override string TargetMethodName => "InvokeCompleteEvent";
    public override Type[] ArgumentTypes => new[] { typeof(IntPtr), typeof(IntPtr), typeof(VSMRendererProgress) };

    [HarmonyPrefix]
    private static void Prefix(IntPtr pMidiPart, VSMRendererProgress progress, out RuntimeObservationState? __state)
    {
        __state = null;
        try
        {
            RenderCycle renderCycle = RuntimeObservationLog.RenderCycleForPart(pMidiPart);
            __state = RuntimeObservationPatchSupport.Begin("render.completed.native", new Dictionary<string, object?>
            {
                ["partId"] = RuntimeObservationLog.ObjectId("part", pMidiPart),
                ["progress"] = RuntimeObservationLog.ProgressSnapshot(progress),
            }, renderCycle: renderCycle);
        }
        catch
        {
        }
    }

    [HarmonyPostfix]
    private static void Postfix(IntPtr pMidiPart, RuntimeObservationState? __state)
    {
        try
        {
            if (Settings.McpEnabled)
            {
                (string projectId, long revision) = McpRevisionTracker.Current();
                McpEventHub.Publish("render_idle", projectId, revision);
            }

            var data = new Dictionary<string, object?>
            {
                ["partId"] = RuntimeObservationLog.ObjectId("part", pMidiPart),
            };

            // 渲染完成后的音符/score/波形快照：携带每个音符的 velocity、
            // ConsonantOffset、音素位置，以及全量 score 音素时长与波形峰值，
            // 用于观察 VEL → 渲染辅音时长的关系与饱和点。
            WIVSMMidiPart? part = RuntimeObservationLog.PartFromHandle(pMidiPart);
            if (part != null)
            {
                try
                {
                    data["postRender"] = RuntimeObservationLog.PostRenderSnapshot(part);
                }
                finally
                {
                    (part as IDisposable)?.Dispose();
                }
            }

            RuntimeObservationPatchSupport.End("render.completed.native", __state, data);
            RuntimeObservationLog.EndRenderCycle(pMidiPart);
        }
        catch
        {
        }
    }

    [HarmonyFinalizer]
    private static Exception? Finalizer(Exception? __exception, RuntimeObservationState? __state)
    {
        if (__exception != null)
            RuntimeObservationPatchSupport.Exception("render.completed.native", __state, __exception);
        return __exception;
    }
}

public class ObserveRendererCancelPatch : PatchBase
{
    public override string PatchName => nameof(ObserveRendererCancelPatch);
    public override Type TargetClass => typeof(WIVSMRendererObserver);
    public override string TargetMethodName => "InvokeCancelEvent";
    public override Type[] ArgumentTypes => new[] { typeof(IntPtr), typeof(IntPtr), typeof(VSMRenderCancelReason) };

    [HarmonyPrefix]
    private static void Prefix(IntPtr pMidiPart, VSMRenderCancelReason reason)
    {
        try
        {
            var data = new Dictionary<string, object?>
            {
                ["partId"] = RuntimeObservationLog.ObjectId("part", pMidiPart),
                ["reason"] = reason.ToString(),
                ["reasonValue"] = Convert.ToInt32(reason),
            };
            RuntimeObservationLog.AddCycleData(
                data, renderCycle: RuntimeObservationLog.RenderCycleForPart(pMidiPart));
            RuntimeObservationLog.Write("render.canceled.native", "callback", data);
            RuntimeObservationLog.EndRenderCycle(pMidiPart);
        }
        catch
        {
        }
    }
}

public class ObserveMusicalEditorRendererStartPatch : PatchBase
{
    public override string PatchName => nameof(ObserveMusicalEditorRendererStartPatch);
    public override Type TargetClass => typeof(MusicalEditorViewModel);
    public override string TargetMethodName => "OnRendererStarted";
    public override Type[] ArgumentTypes => new[] { typeof(object), typeof(RendererObserverStartEventArgs) };

    [HarmonyPrefix]
    private static void Prefix(object __instance, RendererObserverStartEventArgs e, out RuntimeObservationState? __state)
    {
        __state = null;
        try
        {
            Dictionary<string, object?> data = RuntimeObservationPatchSupport.PartEventData(e?.MidiPart);
            data["audioSources"] = RuntimeObservationLog.DictionaryCount(__instance, "audioSourceDictionary");
            __state = RuntimeObservationPatchSupport.Begin("render.started.editor", data);
        }
        catch
        {
        }
    }

    [HarmonyPostfix]
    private static void Postfix(object __instance, RendererObserverStartEventArgs e, RuntimeObservationState? __state)
    {
        try
        {
            Dictionary<string, object?> data = RuntimeObservationPatchSupport.PartEventData(e?.MidiPart);
            data["audioSources"] = RuntimeObservationLog.DictionaryCount(__instance, "audioSourceDictionary");
            RuntimeObservationPatchSupport.End("render.started.editor", __state, data);
        }
        catch
        {
        }
    }
}

public class ObserveMusicalEditorRendererBlockPatch : PatchBase
{
    public override string PatchName => nameof(ObserveMusicalEditorRendererBlockPatch);
    public override Type TargetClass => typeof(MusicalEditorViewModel);
    public override string TargetMethodName => "OnRendererBlockRendered";
    public override Type[] ArgumentTypes => new[] { typeof(object), typeof(RendererObserverBlockRenderingEventArgs) };

    [HarmonyPrefix]
    private static void Prefix(object __instance, RendererObserverBlockRenderingEventArgs e, out RuntimeObservationState? __state)
    {
        __state = null;
        try
        {
            Dictionary<string, object?> data = RuntimeObservationPatchSupport.PartEventData(e?.MidiPart, e?.Progress);
            data["audioSources"] = RuntimeObservationLog.DictionaryCount(__instance, "audioSourceDictionary");
            data["block"] = RuntimeObservationLog.BlockSnapshot(
                e?.AudioBufferList, e?.ScoreList,
                e?.MidiPart is { } part ? (IntPtr)part : IntPtr.Zero);
            __state = RuntimeObservationPatchSupport.Begin("render.block.editor", data);
        }
        catch
        {
        }
    }

    [HarmonyPostfix]
    private static void Postfix(object __instance, RendererObserverBlockRenderingEventArgs e, RuntimeObservationState? __state)
    {
        try
        {
            Dictionary<string, object?> data = RuntimeObservationPatchSupport.PartEventData(e?.MidiPart, e?.Progress);
            data["audioSources"] = RuntimeObservationLog.DictionaryCount(__instance, "audioSourceDictionary");
            RuntimeObservationPatchSupport.End("render.block.editor", __state, data);
        }
        catch
        {
        }
    }
}

public class ObserveMusicalEditorRendererCompletePatch : PatchBase
{
    public override string PatchName => nameof(ObserveMusicalEditorRendererCompletePatch);
    public override Type TargetClass => typeof(MusicalEditorViewModel);
    public override string TargetMethodName => "OnRendererCompleted";
    public override Type[] ArgumentTypes => new[] { typeof(object), typeof(RendererObserverCompleteEventArgs) };

    [HarmonyPrefix]
    private static void Prefix(object __instance, RendererObserverCompleteEventArgs e, out RuntimeObservationState? __state)
    {
        __state = null;
        try
        {
            Dictionary<string, object?> data = RuntimeObservationPatchSupport.PartEventData(e?.MidiPart, e?.Progress);
            data["audioSources"] = RuntimeObservationLog.DictionaryCount(__instance, "audioSourceDictionary");
            __state = RuntimeObservationPatchSupport.Begin("render.completed.editor", data);
        }
        catch
        {
        }
    }

    [HarmonyPostfix]
    private static void Postfix(object __instance, RendererObserverCompleteEventArgs e, RuntimeObservationState? __state)
    {
        try
        {
            Dictionary<string, object?> data = RuntimeObservationPatchSupport.PartEventData(e?.MidiPart, e?.Progress);
            data["audioSources"] = RuntimeObservationLog.DictionaryCount(__instance, "audioSourceDictionary");
            RuntimeObservationPatchSupport.End("render.completed.editor", __state, data);
        }
        catch
        {
        }
    }
}

public class ObserveAudioPlayerRendererBlockPatch : PatchBase
{
    public override string PatchName => nameof(ObserveAudioPlayerRendererBlockPatch);
    public override Type TargetClass => typeof(AudioPlayer);
    public override string TargetMethodName => "OnRendererBlockRendered";
    public override Type[] ArgumentTypes => new[] { typeof(object), typeof(RendererObserverBlockRenderingEventArgs) };

    [HarmonyPrefix]
    private static void Prefix(RendererObserverBlockRenderingEventArgs e, out RuntimeObservationState? __state)
    {
        __state = null;
        try
        {
            Dictionary<string, object?> data = RuntimeObservationPatchSupport.PartEventData(e?.MidiPart, e?.Progress);
            data["block"] = RuntimeObservationLog.BlockSnapshot(
                e?.AudioBufferList, e?.ScoreList,
                e?.MidiPart is { } part ? (IntPtr)part : IntPtr.Zero);
            __state = RuntimeObservationPatchSupport.Begin("render.block.audioPlayer", data);
        }
        catch
        {
        }
    }

    [HarmonyPostfix]
    private static void Postfix(RendererObserverBlockRenderingEventArgs e, RuntimeObservationState? __state)
    {
        try
        {
            RuntimeObservationPatchSupport.End(
                "render.block.audioPlayer",
                __state,
                RuntimeObservationPatchSupport.PartEventData(e?.MidiPart, e?.Progress));
        }
        catch
        {
        }
    }
}

public class ObserveAudioPlayerRendererCompletePatch : PatchBase
{
    public override string PatchName => nameof(ObserveAudioPlayerRendererCompletePatch);
    public override Type TargetClass => typeof(AudioPlayer);
    public override string TargetMethodName => "OnRendererCompleted";
    public override Type[] ArgumentTypes => new[] { typeof(object), typeof(RendererObserverCompleteEventArgs) };

    [HarmonyPrefix]
    private static void Prefix(RendererObserverCompleteEventArgs e, out RuntimeObservationState? __state)
    {
        __state = null;
        try
        {
            __state = RuntimeObservationPatchSupport.Begin(
                "render.completed.audioPlayer",
                RuntimeObservationPatchSupport.PartEventData(e?.MidiPart, e?.Progress));
        }
        catch
        {
        }
    }

    [HarmonyPostfix]
    private static void Postfix(RendererObserverCompleteEventArgs e, RuntimeObservationState? __state)
    {
        try
        {
            RuntimeObservationPatchSupport.End(
                "render.completed.audioPlayer",
                __state,
                RuntimeObservationPatchSupport.PartEventData(e?.MidiPart, e?.Progress));
        }
        catch
        {
        }
    }
}

public class ObserveAudioBufferReleasePatch : PatchBase
{
    public override string PatchName => nameof(ObserveAudioBufferReleasePatch);
    public override Type TargetClass => typeof(AudioPlayer);
    public override string TargetMethodName => "NTNeedReleaseAudioBuffer";
    public override Type[] ArgumentTypes => new[] { typeof(IntPtr), typeof(IntPtr) };

    [HarmonyPrefix]
    private static void Prefix(IntPtr ptr, IntPtr audioBufferHandle)
    {
        try
        {
            RenderCycle renderCycle = RuntimeObservationLog.RenderCycleForAudioBuffer(audioBufferHandle);
            var data = new Dictionary<string, object?>
            {
                ["clientId"] = RuntimeObservationLog.ObjectId("audioClient", ptr),
                ["audioBufferId"] = RuntimeObservationLog.ObjectId("pcm", audioBufferHandle),
            };
            RuntimeObservationLog.AddCycleData(data, renderCycle: renderCycle);
            RuntimeObservationLog.Write("pcm.releaseRequested", "callback", data);
        }
        catch
        {
        }
    }
}

public class ObserveTransactionEndPatch : PatchBase
{
    public override string PatchName => nameof(ObserveTransactionEndPatch);
    public override Type TargetClass => typeof(Transaction);
    public override string TargetMethodName => "EndProc";

    [HarmonyPrefix]
    private static void Prefix(Transaction __instance, out RuntimeObservationState? __state)
    {
        __state = null;
        try
        {
            WIVSMSequence? sequence = RuntimeObservationPatchSupport.TransactionSequence(__instance);
            bool staged = sequence != null && sequence.IsStaged;
            long commitCycle = RuntimeObservationLog.CurrentCommitCycle();
            if (commitCycle == 0 && staged)
                commitCycle = RuntimeObservationLog.EnsureCommitCycle();
            __state = RuntimeObservationPatchSupport.Begin("transaction.end", new Dictionary<string, object?>
            {
                ["result"] = __instance.Result,
                ["hasSequence"] = sequence != null,
                ["sequenceId"] = RuntimeObservationLog.ObjectId(
                    "sequence", sequence == null ? IntPtr.Zero : (IntPtr)sequence),
                ["staged"] = staged,
            }, commitCycle);
        }
        catch
        {
        }
    }

    [HarmonyPostfix]
    private static void Postfix(Transaction __instance, RuntimeObservationState? __state)
    {
        try
        {
            WIVSMSequence? sequence = RuntimeObservationPatchSupport.TransactionSequence(__instance);
            RuntimeObservationPatchSupport.End("transaction.end", __state, new Dictionary<string, object?>
            {
                ["result"] = __instance.Result,
                ["hasSequence"] = sequence != null,
                ["sequenceId"] = RuntimeObservationLog.ObjectId(
                    "sequence", sequence == null ? IntPtr.Zero : (IntPtr)sequence),
                ["staged"] = sequence != null && sequence.IsStaged,
            });
            RuntimeObservationLog.ClearCommitCycle();
        }
        catch
        {
        }
    }

    [HarmonyFinalizer]
    private static Exception? Finalizer(Exception? __exception, RuntimeObservationState? __state)
    {
        if (__exception != null)
            RuntimeObservationPatchSupport.Exception("transaction.end", __state, __exception);
        RuntimeObservationLog.ClearCommitCycle();
        return __exception;
    }
}

public class ObserveSequenceCommitPatch : PatchBase
{
    public override string PatchName => nameof(ObserveSequenceCommitPatch);
    public override Type TargetClass => typeof(WIVSMSequence);
    public override string TargetMethodName => "Commit";
    public override Type[] ArgumentTypes => new[] { typeof(bool) };

    [HarmonyPrefix]
    private static void Prefix(WIVSMSequence __instance, bool updateHistory, out RuntimeObservationState? __state)
    {
        __state = null;
        try
        {
            long commitCycle = RuntimeObservationLog.EnsureCommitCycle();
            __state = RuntimeObservationPatchSupport.Begin("transaction.commit", new Dictionary<string, object?>
            {
                ["sequenceId"] = RuntimeObservationLog.ObjectId("sequence", (IntPtr)__instance),
                ["updateHistory"] = updateHistory,
                ["staged"] = __instance.IsStaged,
            }, commitCycle);
        }
        catch
        {
        }
    }

    [HarmonyPostfix]
    private static void Postfix(WIVSMSequence __instance, bool __result, RuntimeObservationState? __state)
    {
        try
        {
            RuntimeObservationLog.CompleteCommitCycle(__instance, __result);
            RuntimeObservationPatchSupport.End("transaction.commit", __state, new Dictionary<string, object?>
            {
                ["sequenceId"] = RuntimeObservationLog.ObjectId("sequence", (IntPtr)__instance),
                ["nativeResult"] = __result,
                ["staged"] = __instance.IsStaged,
            });
        }
        catch
        {
        }
    }

    [HarmonyFinalizer]
    private static Exception? Finalizer(Exception? __exception, RuntimeObservationState? __state)
    {
        if (__exception != null)
            RuntimeObservationPatchSupport.Exception("transaction.commit", __state, __exception);
        return __exception;
    }
}

public class ObserveSequenceRollbackPatch : PatchBase
{
    public override string PatchName => nameof(ObserveSequenceRollbackPatch);
    public override Type TargetClass => typeof(WIVSMSequence);
    public override string TargetMethodName => "Rollback";

    [HarmonyPrefix]
    private static void Prefix(WIVSMSequence __instance, out RuntimeObservationState? __state)
    {
        __state = null;
        try
        {
            long commitCycle = RuntimeObservationLog.EnsureCommitCycle();
            __state = RuntimeObservationPatchSupport.Begin("transaction.rollback", new Dictionary<string, object?>
            {
                ["sequenceId"] = RuntimeObservationLog.ObjectId("sequence", (IntPtr)__instance),
                ["staged"] = __instance.IsStaged,
            }, commitCycle);
        }
        catch
        {
        }
    }

    [HarmonyPostfix]
    private static void Postfix(WIVSMSequence __instance, bool __result, RuntimeObservationState? __state)
    {
        try
        {
            RuntimeObservationPatchSupport.End("transaction.rollback", __state, new Dictionary<string, object?>
            {
                ["sequenceId"] = RuntimeObservationLog.ObjectId("sequence", (IntPtr)__instance),
                ["nativeResult"] = __result,
                ["staged"] = __instance.IsStaged,
            });
        }
        catch
        {
        }
    }

    [HarmonyFinalizer]
    private static Exception? Finalizer(Exception? __exception, RuntimeObservationState? __state)
    {
        if (__exception != null)
            RuntimeObservationPatchSupport.Exception("transaction.rollback", __state, __exception);
        return __exception;
    }
}

public class ObserveCandidatePhonemesPatch : PatchBase
{
    public override string PatchName => nameof(ObserveCandidatePhonemesPatch);
    public override Type TargetClass => typeof(G2PAMultiLingualManager);
    public override string TargetMethodName => "CandidatePhonemes";
    public override Type[] ArgumentTypes => new[]
    {
        typeof(WIVSMNote), typeof(string), typeof(int), typeof(bool), typeof(bool),
    };

    [HarmonyPrefix]
    private static void Prefix(
        WIVSMNote note,
        string lyrics,
        int langID,
        bool useExtensionDictionary,
        bool isAi,
        out RuntimeObservationState? __state)
    {
        __state = null;
        try
        {
            __state = RuntimeObservationPatchSupport.Begin("g2pa.candidate", new Dictionary<string, object?>
            {
                ["inputLength"] = lyrics?.Length ?? 0,
                ["inputId"] = RuntimeObservationLog.HashText(lyrics),
                ["matrixCase"] = RuntimeObservationLog.ExperimentInputClass(lyrics),
                ["langID"] = langID,
                ["useExtensionDictionary"] = useExtensionDictionary,
                ["isAi"] = isAi,
                ["noteWindow"] = RuntimeObservationLog.NoteWindowSnapshot(note),
            });
        }
        catch
        {
        }
    }

    [HarmonyPostfix]
    private static void Postfix(List<Syllables>? __result, RuntimeObservationState? __state)
    {
        try
        {
            RuntimeObservationPatchSupport.End("g2pa.candidate", __state, new Dictionary<string, object?>
            {
                ["candidateCount"] = __result?.Count ?? 0,
                ["recognized"] = __result is { Count: > 0 },
            });
        }
        catch
        {
        }
    }

    [HarmonyFinalizer]
    private static Exception? Finalizer(Exception? __exception, RuntimeObservationState? __state)
    {
        if (__exception != null)
            RuntimeObservationPatchSupport.Exception("g2pa.candidate", __state, __exception);
        return __exception;
    }
}

public class ObserveSetSyllablesPatch : PatchBase
{
    public override string PatchName => nameof(ObserveSetSyllablesPatch);
    public override Type TargetClass => typeof(G2PAMultiLingualManager);
    public override string TargetMethodName => "SetSyllables";
    public override Type[] ArgumentTypes => new[]
    {
        typeof(WIVSMNote), typeof(SyllablesData), typeof(int), typeof(int), typeof(bool),
    };

    [HarmonyPrefix]
    private static void Prefix(
        WIVSMNote note,
        int syllablesSize,
        int langID,
        bool isAi,
        out RuntimeObservationState? __state)
    {
        __state = null;
        try
        {
            long commitCycle = RuntimeObservationLog.EnsureCommitCycle();
            __state = RuntimeObservationPatchSupport.Begin("g2pa.setSyllables", new Dictionary<string, object?>
            {
                ["syllablesSize"] = syllablesSize,
                ["langID"] = langID,
                ["isAi"] = isAi,
                ["noteWindow"] = RuntimeObservationLog.NoteWindowSnapshot(note),
            }, commitCycle);
        }
        catch
        {
        }
    }

    [HarmonyPostfix]
    private static void Postfix(
        WIVSMNote note,
        (bool IsSuccess, WIVSMNote? NextNote) __result,
        RuntimeObservationState? __state)
    {
        try
        {
            RuntimeObservationPatchSupport.End("g2pa.setSyllables", __state, new Dictionary<string, object?>
            {
                ["nativeResult"] = __result.IsSuccess,
                ["nextNoteHandle"] = RuntimeObservationLog.Handle((IntPtr)__result.NextNote),
                ["noteWindow"] = RuntimeObservationLog.NoteWindowSnapshot(note),
            });
        }
        catch
        {
        }
    }

    [HarmonyFinalizer]
    private static Exception? Finalizer(Exception? __exception, RuntimeObservationState? __state)
    {
        if (__exception != null)
            RuntimeObservationPatchSupport.Exception("g2pa.setSyllables", __state, __exception);
        return __exception;
    }
}

public class ObserveResetPhonemesPatch : PatchBase
{
    public override string PatchName => nameof(ObserveResetPhonemesPatch);
    public override Type TargetClass => typeof(G2PAMultiLingualManager);
    public override string TargetMethodName => "ResetPhonemes";
    public override Type[] ArgumentTypes => new[] { typeof(WIVSMNote), typeof(WIVSMNote) };

    [HarmonyPrefix]
    private static void Prefix(WIVSMNote beginNote, WIVSMNote? endNote, out RuntimeObservationState? __state)
    {
        __state = null;
        try
        {
            long commitCycle = RuntimeObservationLog.EnsureCommitCycle();
            __state = RuntimeObservationPatchSupport.Begin("g2pa.reset", new Dictionary<string, object?>
            {
                ["beginHandle"] = RuntimeObservationLog.Handle((IntPtr)beginNote),
                ["endHandle"] = RuntimeObservationLog.Handle((IntPtr)endNote),
                ["noteWindow"] = RuntimeObservationLog.NoteWindowSnapshot(beginNote),
            }, commitCycle);
        }
        catch
        {
        }
    }

    [HarmonyPostfix]
    private static void Postfix(WIVSMNote beginNote, bool __result, RuntimeObservationState? __state)
    {
        try
        {
            RuntimeObservationPatchSupport.End("g2pa.reset", __state, new Dictionary<string, object?>
            {
                ["nativeResult"] = __result,
                ["noteWindow"] = RuntimeObservationLog.NoteWindowSnapshot(beginNote),
            });
        }
        catch
        {
        }
    }

    [HarmonyFinalizer]
    private static Exception? Finalizer(Exception? __exception, RuntimeObservationState? __state)
    {
        if (__exception != null)
            RuntimeObservationPatchSupport.Exception("g2pa.reset", __state, __exception);
        return __exception;
    }
}

public class ObserveSetLyricsAndResetPatch : PatchBase
{
    public override string PatchName => nameof(ObserveSetLyricsAndResetPatch);
    public override Type TargetClass => typeof(WIVSMNoteExtension);
    public override string TargetMethodName => "SetLyricsAndResetPhonemes";
    public override Type[] ArgumentTypes => new[] { typeof(WIVSMNote), typeof(string) };

    [HarmonyPrefix]
    private static void Prefix(WIVSMNote note, string lyrics, out RuntimeObservationState? __state)
    {
        __state = null;
        try
        {
            long commitCycle = RuntimeObservationLog.EnsureCommitCycle();
            __state = RuntimeObservationPatchSupport.Begin("g2pa.setLyricsAndReset", new Dictionary<string, object?>
            {
                ["inputLength"] = lyrics?.Length ?? 0,
                ["inputId"] = RuntimeObservationLog.HashText(lyrics),
                ["matrixCase"] = RuntimeObservationLog.ExperimentInputClass(lyrics),
                ["noteWindow"] = RuntimeObservationLog.NoteWindowSnapshot(note),
            }, commitCycle);
        }
        catch
        {
        }
    }

    [HarmonyPostfix]
    private static void Postfix(WIVSMNote note, bool __result, RuntimeObservationState? __state)
    {
        try
        {
            RuntimeObservationPatchSupport.End("g2pa.setLyricsAndReset", __state, new Dictionary<string, object?>
            {
                ["result"] = __result,
                ["noteWindow"] = RuntimeObservationLog.NoteWindowSnapshot(note),
            });
        }
        catch
        {
        }
    }

    [HarmonyFinalizer]
    private static Exception? Finalizer(Exception? __exception, RuntimeObservationState? __state)
    {
        if (__exception != null)
            RuntimeObservationPatchSupport.Exception("g2pa.setLyricsAndReset", __state, __exception);
        return __exception;
    }
}

public class ObserveSetLyricsAndResetWithLanguagePatch : PatchBase
{
    public override string PatchName => nameof(ObserveSetLyricsAndResetWithLanguagePatch);
    public override Type TargetClass => typeof(WIVSMNoteExtension);
    public override string TargetMethodName => "SetLyricsAndResetPhonemes";
    public override Type[] ArgumentTypes => new[] { typeof(WIVSMNote), typeof(string), typeof(int) };

    [HarmonyPrefix]
    private static void Prefix(WIVSMNote note, string lyrics, int langID, out RuntimeObservationState? __state)
    {
        __state = null;
        try
        {
            long commitCycle = RuntimeObservationLog.EnsureCommitCycle();
            __state = RuntimeObservationPatchSupport.Begin("g2pa.setLyricsAndReset", new Dictionary<string, object?>
            {
                ["inputLength"] = lyrics?.Length ?? 0,
                ["inputId"] = RuntimeObservationLog.HashText(lyrics),
                ["matrixCase"] = RuntimeObservationLog.ExperimentInputClass(lyrics),
                ["langID"] = langID,
                ["noteWindow"] = RuntimeObservationLog.NoteWindowSnapshot(note),
            }, commitCycle);
        }
        catch
        {
        }
    }

    [HarmonyPostfix]
    private static void Postfix(WIVSMNote note, bool __result, RuntimeObservationState? __state)
    {
        try
        {
            RuntimeObservationPatchSupport.End("g2pa.setLyricsAndReset", __state, new Dictionary<string, object?>
            {
                ["result"] = __result,
                ["noteWindow"] = RuntimeObservationLog.NoteWindowSnapshot(note),
            });
        }
        catch
        {
        }
    }

    [HarmonyFinalizer]
    private static Exception? Finalizer(Exception? __exception, RuntimeObservationState? __state)
    {
        if (__exception != null)
            RuntimeObservationPatchSupport.Exception("g2pa.setLyricsAndReset", __state, __exception);
        return __exception;
    }
}

public class ObserveBreathBypassPatch : PatchBase
{
    public override string PatchName => nameof(ObserveBreathBypassPatch);
    public override Type TargetClass => typeof(WIVSMEffect);
    public override string TargetMethodName => "SetBypass";
    public override Type[] ArgumentTypes => new[] { typeof(bool) };

    [HarmonyPrefix]
    private static void Prefix(WIVSMEffect __instance, bool bypass, out RuntimeObservationState? __state)
    {
        __state = null;
        try
        {
            if (__instance is not WIVSMBreathEffect effect)
                return;
            long commitCycle = RuntimeObservationLog.EnsureCommitCycle();
            Dictionary<string, object?> data = RuntimeObservationPatchSupport.BreathEventData(effect);
            data["requestedBypass"] = bypass;
            __state = RuntimeObservationPatchSupport.Begin("breath.setBypass", data, commitCycle);
        }
        catch
        {
        }
    }

    [HarmonyPostfix]
    private static void Postfix(WIVSMEffect __instance, bool __result, RuntimeObservationState? __state)
    {
        if (__state == null || __instance is not WIVSMBreathEffect effect)
            return;
        try
        {
            Dictionary<string, object?> data = RuntimeObservationPatchSupport.BreathEventData(effect);
            data["nativeResult"] = __result;
            RuntimeObservationPatchSupport.End("breath.setBypass", __state, data);
        }
        catch
        {
        }
    }

    [HarmonyFinalizer]
    private static Exception? Finalizer(Exception? __exception, RuntimeObservationState? __state)
    {
        if (__exception != null && __state != null)
            RuntimeObservationPatchSupport.Exception("breath.setBypass", __state, __exception);
        return __exception;
    }
}

public class ObserveBreathModePatch : PatchBase
{
    public override string PatchName => nameof(ObserveBreathModePatch);
    public override Type TargetClass => typeof(WIVSMBreathEffect);
    public override string TargetMethodName => "SetBreathMode";
    public override Type[] ArgumentTypes => new[] { typeof(VSMBreathMode) };

    [HarmonyPrefix]
    private static void Prefix(WIVSMBreathEffect __instance, VSMBreathMode mode, out RuntimeObservationState? __state)
    {
        __state = null;
        try
        {
            long commitCycle = RuntimeObservationLog.EnsureCommitCycle();
            Dictionary<string, object?> data = RuntimeObservationPatchSupport.BreathEventData(__instance);
            data["requestedMode"] = mode.ToString();
            __state = RuntimeObservationPatchSupport.Begin("breath.setMode", data, commitCycle);
        }
        catch
        {
        }
    }

    [HarmonyPostfix]
    private static void Postfix(WIVSMBreathEffect __instance, bool __result, RuntimeObservationState? __state)
    {
        if (__state == null)
            return;
        Dictionary<string, object?> data = RuntimeObservationPatchSupport.BreathEventData(__instance);
        data["nativeResult"] = __result;
        RuntimeObservationPatchSupport.End("breath.setMode", __state, data);
    }

    [HarmonyFinalizer]
    private static Exception? Finalizer(Exception? __exception, RuntimeObservationState? __state)
    {
        if (__exception != null && __state != null)
            RuntimeObservationPatchSupport.Exception("breath.setMode", __state, __exception);
        return __exception;
    }
}

public class ObserveBreathTypePatch : PatchBase
{
    public override string PatchName => nameof(ObserveBreathTypePatch);
    public override Type TargetClass => typeof(WIVSMBreathEffect);
    public override string TargetMethodName => "SetBreathType";
    public override Type[] ArgumentTypes => new[] { typeof(VSMBreathType) };

    [HarmonyPrefix]
    private static void Prefix(WIVSMBreathEffect __instance, VSMBreathType type, out RuntimeObservationState? __state)
    {
        __state = null;
        try
        {
            long commitCycle = RuntimeObservationLog.EnsureCommitCycle();
            Dictionary<string, object?> data = RuntimeObservationPatchSupport.BreathEventData(__instance);
            data["requestedType"] = type.ToString();
            __state = RuntimeObservationPatchSupport.Begin("breath.setType", data, commitCycle);
        }
        catch
        {
        }
    }

    [HarmonyPostfix]
    private static void Postfix(WIVSMBreathEffect __instance, bool __result, RuntimeObservationState? __state)
    {
        if (__state == null)
            return;
        Dictionary<string, object?> data = RuntimeObservationPatchSupport.BreathEventData(__instance);
        data["nativeResult"] = __result;
        RuntimeObservationPatchSupport.End("breath.setType", __state, data);
    }

    [HarmonyFinalizer]
    private static Exception? Finalizer(Exception? __exception, RuntimeObservationState? __state)
    {
        if (__exception != null && __state != null)
            RuntimeObservationPatchSupport.Exception("breath.setType", __state, __exception);
        return __exception;
    }
}

public class ObserveBreathExhalationPatch : PatchBase
{
    public override string PatchName => nameof(ObserveBreathExhalationPatch);
    public override Type TargetClass => typeof(WIVSMBreathEffect);
    public override string TargetMethodName => "SetExhalation";
    public override Type[] ArgumentTypes => new[] { typeof(int) };

    [HarmonyPrefix]
    private static void Prefix(WIVSMBreathEffect __instance, int exhalation, out RuntimeObservationState? __state)
    {
        __state = null;
        try
        {
            long commitCycle = RuntimeObservationLog.EnsureCommitCycle();
            Dictionary<string, object?> data = RuntimeObservationPatchSupport.BreathEventData(__instance);
            data["requestedExhalation"] = exhalation;
            __state = RuntimeObservationPatchSupport.Begin("breath.setExhalation", data, commitCycle);
        }
        catch
        {
        }
    }

    [HarmonyPostfix]
    private static void Postfix(WIVSMBreathEffect __instance, bool __result, RuntimeObservationState? __state)
    {
        if (__state == null)
            return;
        Dictionary<string, object?> data = RuntimeObservationPatchSupport.BreathEventData(__instance);
        data["nativeResult"] = __result;
        RuntimeObservationPatchSupport.End("breath.setExhalation", __state, data);
    }

    [HarmonyFinalizer]
    private static Exception? Finalizer(Exception? __exception, RuntimeObservationState? __state)
    {
        if (__exception != null && __state != null)
            RuntimeObservationPatchSupport.Exception("breath.setExhalation", __state, __exception);
        return __exception;
    }
}
