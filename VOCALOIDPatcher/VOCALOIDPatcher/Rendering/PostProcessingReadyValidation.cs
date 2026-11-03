#if PATCHER_ISOLATED_VALIDATION
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Threading;
using HarmonyLib;
using VOCALOIDPatcher.AceParameters;
using VOCALOIDPatcher.Config;
using VOCALOIDPatcher.ConsonantTiming;
using VOCALOIDPatcher.ConsonantTiming.Smoothing;
using VOCALOIDPatcher.PhonemeVolume;
using Yamaha.VOCALOID.DSE;
using Yamaha.VOCALOID.VDM;
using Yamaha.VOCALOID.VSM;

namespace VOCALOIDPatcher.Rendering;

internal static class PostProcessingReadyValidation
{
    private static readonly Type[] Hosts = [typeof(PhonemeVolumeHost), typeof(TransitionSmoothingHost), typeof(AceAcousticHost)];
    private static readonly Dictionary<int, string> Inputs = new();
    private static TransitionExecutionPlan? _plan;
    private static readonly int[] Processes = new int[3];
    private static readonly double[] Latency = new double[3];
    private static readonly object?[] Requests = new object?[3];
    private static readonly List<long> AceCaptures = new();
    private static long _armed;
    private static int Index(MethodBase method) => Array.IndexOf(Hosts, method.DeclaringType!);
    private static bool Input(WIVSMMidiPart part, MethodBase __originalMethod, ref string __result)
    {
        if (!Inputs.TryGetValue(Index(__originalMethod), out var input)) return true;
        __result = input; return false;
    }
    private static bool Plan(ref TransitionExecutionPlan? __result) { __result = _plan; return false; }
    private static bool SkipPlacement() => false;
    private static void CaptureAce() => AceCaptures.Add(Stopwatch.GetTimestamp());
    private static void CompReady() => Interlocked.Exchange(ref _armed, Stopwatch.GetTimestamp());
    private static void Count(MethodBase __originalMethod, object __0)
    {
        int i = Index(__originalMethod); Requests[i] = __0;
        Latency[i] = Stopwatch.GetElapsedTime(Volatile.Read(ref _armed)).TotalMilliseconds;
        Interlocked.Increment(ref Processes[i]);
    }
    private static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
    private static object? State(int stage, WIVSMMidiPart part)
    {
        object states = AccessTools.Field(Hosts[stage], "Parts").GetValue(null)!;
        object?[] args = [(nint)part, null];
        return (bool)states.GetType().GetMethod("TryGetValue")!.Invoke(states, args)! ? args[1] : null;
    }
    private static object? Cache(int stage, WIVSMMidiPart part) => State(stage, part) is { } state
        ? AccessTools.Field(state.GetType(), "Cache").GetValue(state) : null;
    private static string Cached(int stage, WIVSMMidiPart part, string property) => Cache(stage, part) is { } cache
        ? (string)cache.GetType().GetProperty(property)!.GetValue(cache)! : "";
    private static string Hash(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));
    private static void Pump(WIVSMSequence source)
    {
        for (int i = 0; i < 64 && source.InvokeRendererObserver(); i++) { }
        var frame = new DispatcherFrame();
        Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(() => frame.Continue = false));
        Dispatcher.PushFrame(frame); Thread.Sleep(1);
    }
    private static void Wait(WIVSMSequence source, Func<bool> predicate)
    {
        var timer = Stopwatch.StartNew();
        while (timer.ElapsedMilliseconds < 30000) { Pump(source); if (predicate()) return; }
        throw new TimeoutException("Ready-input probe did not complete.");
    }
    private static void Settle(WIVSMSequence source, int ms)
    { var timer = Stopwatch.StartNew(); while (timer.ElapsedMilliseconds < ms) Pump(source); }
    private static string Substitute(int stage, WIVSMMidiPart part, string input) => stage switch
    {
        0 => PhonemeVolumeHost.Substitute(part, input),
        1 => TransitionSmoothingHost.Substitute(part, input),
        _ => AceAcousticHost.Substitute(part, input)
    };
    private static void Edit(int stage, WIVSMMidiPart part)
    {
        if (stage == 0) PhonemeVolumeHost.RequestPart(part);
        else if (stage == 1) TransitionSmoothingHost.RequestPart(part);
        else
        {
            var queue = AccessTools.Method(Hosts[2], "Queue");
            queue.Invoke(null, queue.GetParameters().Length == 1 ? [part] : [part, false]);
        }
    }
    private static void Ready(int stage, WIVSMMidiPart part, string input)
    {
        var method = AccessTools.Method(Hosts[stage], "RebindReady");
        if (method == null) Edit(stage, part);
        else method.Invoke(null, [State(stage, part)!, input]);
    }
    private static Action PendingUpstream(int stage, WIVSMMidiPart part)
    {
        object state = stage == 0 ? AccessTools.Method(typeof(BreathVolume.BreathVolumeService), "GetPartState").Invoke(null, [part])!
            : State(stage - 1, part)!;
        var field = AccessTools.Field(state.GetType(), stage == 0 ? "PendingRebuildGeneration" : "PendingGeneration");
        object old = field.GetValue(state)!;
        field.SetValue(state, stage == 0 ? (object)123 : AccessTools.Field(state.GetType(), "Generation").GetValue(state)!);
        return () => field.SetValue(state, old);
    }
    private static void Fresh(int stage, WIVSMMidiPart part, string directory)
    {
        object request = Requests[stage]!;
        object Read(string name) => request.GetType().GetProperty(name)!.GetValue(request)!;
        string path = Path.Combine(directory, "fresh-" + stage + "-" + Guid.NewGuid().ToString("N") + ".wav");
        if (stage == 0) PhonemeGainWaveProcessor.Create((string)Read("Input"), path, (PhonemeGainRegion[])Read("Regions"));
        else if (stage == 1) WaveJunctionProcessor.ProcessWaveFile((string)Read("Input"), path,
            (List<WaveJunctionProcessor.JunctionInfo>)Read("Junctions"), (TransitionSmootherConfig)Read("Config"));
        else AceAcousticWave.Create((string)Read("Input"), path, (AceAcousticControls)Read("Controls"), compatiblePcm16: true);
        Check(Hash(path) == Hash(Cached(stage, part, "Output")), "Ready input output differs from fresh DSP.");
        File.Delete(path);
    }
    internal static void Execute(WIVSMSequenceManager manager, DSEManager dse, DatabaseManager database, string directory)
    {
        bool optimized = Environment.GetEnvironmentVariable("VOCALOID_POST_READY_EXPECT") != "0";
        var harmony = new Harmony("VOCALOIDPatcher.post-processing-ready-probe");
        harmony.Patch(AccessTools.Method(typeof(SampledTimingHost), "TransitionView"), new HarmonyMethod(typeof(PostProcessingReadyValidation), nameof(Plan)));
        foreach (var host in Hosts)
        {
            harmony.Patch(AccessTools.Method(host, "ReadInput"), new HarmonyMethod(typeof(PostProcessingReadyValidation), nameof(Input)));
            harmony.Patch(AccessTools.Method(host, "Process"), new HarmonyMethod(typeof(PostProcessingReadyValidation), nameof(Count)));
            harmony.Patch(AccessTools.Method(host, "RefreshPlacement"), new HarmonyMethod(typeof(PostProcessingReadyValidation), nameof(SkipPlacement)));
        }
        harmony.Patch(AccessTools.Method(typeof(AceAcousticHost), "Capture"), new HarmonyMethod(typeof(PostProcessingReadyValidation), nameof(CaptureAce)));
        if (AccessTools.Method(typeof(AceAcousticHost), "UpstreamReady") is { } ready)
            harmony.Patch(ready, new HarmonyMethod(typeof(PostProcessingReadyValidation), nameof(CompReady)));
        var source = manager.CreateSequence(new VSMSequenceData { SamplingRate = VSMSamplingRate._44100, MaxNumTracks = 4, MaxUndoCount = 100 })!;
        var records = new List<object>();
        try
        {
            var track = (WIVSMMidiTrack)source.InsertTrackEx(0, VSMTrackType.Midi, "ready-fixture")!;
            var part = track.InsertPart(VSMAbsTick.Zero, new VSMRelTick(3360), "ready-fixture")!;
            Check(part.SetVoiceBankID("BL8CEAM5N4XN3LFK"), "Missing legal bank.");
            for (int i = 0; i < 3; i++) Check(part.InsertNote(new VSMRelTick(480 + 720 * i), new VSMNoteEvent(600, 60 + i, 64),
                part.GetDefaultNoteExpression(), part.GetDefaultAiNoteExpression(), "fixture", "l a", true, 4) != null, "No fixture note.");
            var carrier = AceParameterDefinitions.All.Single(d => d.Kind == AceParameterKind.Energy).CarrierType;
            Check(part.InsertController(VSMRelTick.Zero, carrier, 75) != null, "No energy carrier.");
            source.InsertTempo(VSMRelTick.Zero, 14600); source.InterpolatedMasterVolume(); Check(source.Commit(false), "Fixture commit failed.");
            source.BlockRenderingEnabled = false; source.StartAsyncRendering();
            Wait(source, () => source.IsFinishedRendering && part.HasValidRenderedScore && part.HasValidRenderedWave);
            source.StopAsyncRendering(); SampledTimingHost.Attach(source, new LocalConsonantTimingBackend(manager, dse, database));
            using (var prepared = SampledTimingHost.PrepareTransitions(part)) _plan = prepared.Query().Execution;
            string original = WaveProcessingReadScope.ReadBefore(WaveProcessingStage.PhonemeVolume, () => part.WaveFilePath);
            File.Copy(original, Inputs[0] = Path.Combine(directory, "frozen-native.wav"));
            PhonemeVolumeHost.Set(part.GetNote(0)!, 1, -6); Wait(source, () => Cache(0, part) != null);
            File.Copy(Cached(0, part, "Output"), Inputs[1] = Path.Combine(directory, "frozen-volume.wav"));
            TransitionSmoothingHost.RequestPart(part); Wait(source, () => Cache(1, part) != null);
            File.Copy(Cached(1, part, "Output"), Inputs[2] = Path.Combine(directory, "frozen-smooth.wav"));
            AceAcousticHost.RenderCompleted(part); Wait(source, () => Cache(2, part) != null);
            for (int stage = 0; stage < 3; stage++)
            {
                string fixedInput = Inputs[stage]; string hash = Hash(Cached(stage, part, "Output"));
                var samples = new List<double>();
                for (int i = 0; i < 30; i++)
                {
                    string input = Path.Combine(directory, $"ready-{stage}-{i}.wav"); File.Copy(fixedInput, input); Inputs[stage] = input;
                    int before = Processes[stage]; Interlocked.Exchange(ref _armed, Stopwatch.GetTimestamp());
                    Substitute(stage, part, input);
                    Wait(source, () => Cached(stage, part, "Input") == input);
                    Check(Processes[stage] - before == 1 && Hash(Cached(stage, part, "Output")) == hash, "Ready rebind repeated work or changed PCM.");
                    samples.Add(Latency[stage]);
                }
                Fresh(stage, part, directory); var ordered = samples.Order().ToArray();
                records.Add(new { name = "ready-path-rebind", stage = Hosts[stage].Name, iterations = samples.Count,
                    medianMs = (ordered[14] + ordered[15]) / 2, p95Ms = ordered[28], maxMs = ordered[^1], hash, samples });

                string edited = Path.Combine(directory, "edited-" + stage + ".wav"); File.Copy(fixedInput, edited); Inputs[stage] = edited;
                int count = Processes[stage]; Interlocked.Exchange(ref _armed, Stopwatch.GetTimestamp()); Edit(stage, part);
                Settle(source, 50); Check(Processes[stage] == count, "Edit debounce ran before quiet period.");
                Ready(stage, part, edited); Wait(source, () => Cached(stage, part, "Input") == edited);
                Check(Latency[stage] >= (stage == 2 ? 110 : 90), "Ready input bypassed pending edit stability.");
                records.Add(new { name = "ready-preserves-pending-edit-deadline", stage = Hosts[stage].Name, startMs = Latency[stage] });

                if (optimized)
                {
                    Settle(source, 150);
                    string pending = Path.Combine(directory, "pending-" + stage + ".wav"); File.Copy(fixedInput, pending); Inputs[stage] = pending;
                    Action complete = PendingUpstream(stage, part); count = Processes[stage];
                    try
                    {
                        Ready(stage, part, pending); Settle(source, 50);
                        Check(Processes[stage] == count, "Upstream work in progress was treated as settled audio.");
                    }
                    finally { complete(); }
                    Interlocked.Exchange(ref _armed, Stopwatch.GetTimestamp()); Ready(stage, part, pending);
                    Wait(source, () => Cached(stage, part, "Input") == pending);
                    records.Add(new { name = "pending-upstream-retains-wait-then-promotes", stage = Hosts[stage].Name, promotedStartMs = Latency[stage] });

                    string a = Path.Combine(directory, "stale-a-" + stage + ".wav"), b = Path.Combine(directory, "stale-b-" + stage + ".wav");
                    File.Copy(fixedInput, a); File.Copy(fixedInput, b); Inputs[stage] = a; count = Processes[stage];
                    var getter = Task.Run(() => Substitute(stage, part, a)); getter.GetAwaiter().GetResult(); Inputs[stage] = b;
                    Settle(source, 160); Check(Processes[stage] == count, "Stale queued readiness was not rejected.");
                    Substitute(stage, part, b); Wait(source, () => Cached(stage, part, "Input") == b);
                    records.Add(new { name = "stale-ready-path-rejected", stage = Hosts[stage].Name, passed = true });
                }
            }
            if (optimized)
            {
                for (int stage = 0; stage < 3; stage++)
                {
                    Action clear = PendingUpstream(stage, part);
                    bool enabled = stage == 0 ? Settings.IndividualBreathVolume : stage == 1 ? Settings.IndividualPhonemeVolume : Settings.TransitionSmoothingEnabled;
                    void Enable(bool value)
                    {
                        if (stage == 0) Settings.IndividualBreathVolume = value;
                        else if (stage == 1) Settings.IndividualPhonemeVolume = value;
                        else Settings.TransitionSmoothingEnabled = value;
                    }
                    try
                    {
                        Enable(false);
                        var type = stage == 0 ? typeof(BreathVolume.BreathVolumeService) : Hosts[stage - 1];
                        Check(!(bool)AccessTools.Method(type, "HasPendingWave").Invoke(null, [part])!, "Disabled upstream retained wait.");
                    }
                    finally { Enable(enabled); clear(); }
                }
                records.Add(new { name = "disabled-upstream-does-not-delay", checks = 3, passed = true });
                string valid = Inputs[2]; Inputs[2] = Path.Combine(directory, "missing-ace-input.wav");
                int captures = AceCaptures.Count, processCount = Processes[2]; Ready(2, part, Inputs[2]); Settle(source, 420);
                var attempts = AceCaptures.Skip(captures).ToArray();
                Check(attempts.Length == 3 && Processes[2] == processCount && attempts.Zip(attempts.Skip(1),
                    (a, b) => Stopwatch.GetElapsedTime(a, b).TotalMilliseconds).All(ms => ms >= 100), "ACE ready I/O failure lost bounded retry delay.");
                records.Add(new { name = "ace-io-retry-keeps-backoff", attempts = attempts.Length, passed = true });
                Inputs[2] = valid; AceAcousticHost.RenderCompleted(part); Wait(source, () => Cache(2, part) != null);

                Inputs.Remove(2);
                VoiceComp.VoiceCompHost.Set(part.GetNote(1)!, new(Isolation: new(true)));
                Wait(source, () => VoiceComp.VoiceCompHost.View(part) is { Dirty: false, Running: false, Wave: not null } comp && Cached(2, part, "Input") == comp.Wave);
                Check(VoiceComp.CompWave.Peaks(Cached(2, part, "Output")).Any(p => p > .00001f), "Comp/ACE result is silent.");
                Fresh(2, part, directory);
                records.Add(new { name = "actual-comp-publishes-to-ace-ready", startMs = Latency[2], passed = true });
                Inputs[2] = valid; VoiceComp.VoiceCompHost.Set(part.GetNote(1)!, new());
                Wait(source, () => VoiceComp.VoiceCompHost.View(part) == null && Cached(2, part, "Input") == valid);

                var rebuilds = new List<(bool Current, bool Pending)>(); object rebuildLock = new();
                void Finished(WIVSMMidiPart target, int generation, bool current)
                {
                    if ((nint)target != (nint)part) return;
                    lock (rebuildLock) rebuilds.Add((current, BreathVolume.BreathVolumeService.HasPendingWave(part)));
                }
                BreathVolume.BreathVolumeService.RebuildCompleted += Finished;
                try
                {
                    BreathVolume.BreathVolumeService.RequestRebuild(source, part); Settle(source, 50);
                    BreathVolume.BreathVolumeService.RequestRebuild(source, part);
                    Wait(source, () => { lock (rebuildLock) return rebuilds.Count == 2; });
                    lock (rebuildLock) Check(!rebuilds[0].Current && rebuilds[0].Pending && rebuilds[1].Current && !rebuilds[1].Pending,
                        "Old BVL task cleared the latest rebuild's pending marker.");
                    records.Add(new { name = "bvl-pending-marker-keeps-latest-generation", passed = true });
                }
                finally { BreathVolume.BreathVolumeService.RebuildCompleted -= Finished; }
                Settle(source, 180);

                int[] before = Processes.ToArray();
                for (int stage = 0; stage < 3; stage++)
                {
                    string input = Inputs[stage] + ".next.wav"; File.Copy(Inputs[stage], input); Inputs[stage] = input;
                    int captured = stage; Task.Run(() => Substitute(captured, part, input)).GetAwaiter().GetResult();
                }
                PhonemeVolumeHost.Close(source); TransitionSmoothingHost.Close(source); AceAcousticHost.Close(source); Settle(source, 180);
                Check(before.SequenceEqual(Processes), "Closed Part ran queued readiness work.");
                records.Add(new { name = "closed-part-rejects-queued-ready", passed = true });
            }
            File.WriteAllText(Path.Combine(directory, "post-processing-ready.json"), JsonSerializer.Serialize(new { optimized, records }, new JsonSerializerOptions { WriteIndented = true }));
        }
        finally
        {
            PhonemeVolumeHost.Close(source); TransitionSmoothingHost.Close(source); AceAcousticHost.Close(source);
            VoiceComp.VoiceCompHost.Close(source);
            source.StopAsyncRendering(); SampledTimingHost.Close(source); source.Close(); harmony.UnpatchAll(harmony.Id);
        }
    }
}
#endif
