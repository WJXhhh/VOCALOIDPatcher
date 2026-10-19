#if PATCHER_ISOLATED_VALIDATION
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;
using System.Windows;
using HarmonyLib;
using VOCALOIDPatcher.BreathVolume;
using VOCALOIDPatcher.Config;
using VOCALOIDPatcher.DurationSelection;
using VOCALOIDPatcher.Patch;
using VOCALOIDPatcher.Patch.Patches;
using VOCALOIDPatcher.PhonemeVolume;
using Yamaha.VOCALOID.DSE;
using Yamaha.VOCALOID.VDM;
using Yamaha.VOCALOID.VSM;

namespace VOCALOIDPatcher.ConsonantTiming;

// Uses production file/history/route patches in a disposable SDK process.
// Only private-backend availability is injected; no installed binaries change.
[CLSCompliant(false)]
public static class CompatibilityHostValidation
{
    private static int _backendAttempts, _hashes;
    private static bool _missingModules;
    private static bool _failSave;
    public static void Run(WIVSMSequenceManager manager, DSEManager dse, DatabaseManager database, string directory)
    {
        Exception? fault = null;
        var thread = new Thread(() => { try { Execute(manager, dse, database, directory); } catch (Exception error) { fault = error; } });
        thread.SetApartmentState(ApartmentState.STA); thread.Start(); thread.Join();
        if (fault != null) throw new InvalidOperationException("Compatibility host validation failed.", fault);
    }

    private static void Execute(WIVSMSequenceManager manager, DSEManager dse, DatabaseManager database, string directory)
    {
        Directory.CreateDirectory(directory);
        var application = new Application();
        Patcher.ConfigManager = new ConfigManager(Path.Combine(directory, "isolated-config.json"));
        foreach (var pair in new (string, object)[] { ("SequenceManager", manager), ("DSEManager", dse), ("DatabaseManager", database) })
            typeof(Yamaha.VOCALOID.App).GetProperty(pair.Item1)!.SetValue(null, pair.Item2);
        var harmony = new Harmony("VOCALOIDPatcher.isolated-compatibility");
        foreach (PatchBase patch in new PatchBase[] {
            new SampledTimingRenderStartPatch(), new SampledTimingDirtyPatch(), new SampledTimingProjectSavePatch(),
            new SampledTimingProjectLoadPatch(), new SampledTimingDuplicateSequencePatch(), new ConsonantTimingCommitPatch(),
            new ConsonantTimingUndoPatch(), new ConsonantTimingRedoPatch(), new ConsonantTimingClosePatch(),
            new BreathVolumeCommitHistoryPatch(), new BreathVolumeUndoPatch(), new BreathVolumeRedoPatch(),
            new BreathVolumeDirtyPatch(), new BreathVolumeProjectSavePatch(), new BreathVolumeProjectLoadPatch(), new BreathVolumeRemoveNotePatch(), new BreathVolumeDuplicateSequencePatch()
        })
        {
            var target = AccessTools.Method(patch.TargetClass, patch.TargetMethodName, patch.ArgumentTypes);
            Check(target != null, "Missing file/history patch: " + patch.PatchName);
            HarmonyMethod? Hook(string name) => AccessTools.Method(patch.GetType(), name) is { } method ? new HarmonyMethod(method) : null;
            harmony.Patch(target, Hook("Prefix"), Hook("Postfix"), finalizer: Hook("Finalizer"));
        }
        harmony.Patch(AccessTools.Constructor(typeof(LocalConsonantTimingBackend), [typeof(WIVSMSequenceManager), typeof(DSEManager), typeof(DatabaseManager)]),
            new HarmonyMethod(typeof(CompatibilityHostValidation), nameof(BackendConstruction)));
        bool realUnknown = Environment.GetEnvironmentVariable("VOCALOID_COMPAT_REAL_UNKNOWN") == "1";
        if (!realUnknown)
        {
            harmony.Patch(AccessTools.Method(typeof(LocalTimingNative), "Hash"), new HarmonyMethod(typeof(CompatibilityHostValidation), nameof(UnknownHash)));
            harmony.Patch(AccessTools.Method(typeof(LocalTimingNative), "RequireModule"), new HarmonyMethod(typeof(CompatibilityHostValidation), nameof(MissingModules)));
        }
        harmony.Patch(AccessTools.Method(typeof(DurationSelectionArchive), "Write"), new HarmonyMethod(typeof(CompatibilityHostValidation), nameof(FailSave)));
        var shell = RuntimeHelpers.GetUninitializedObject(typeof(Yamaha.VOCALOID.Sequence)); GC.SuppressFinalize(shell);
        var save = AccessTools.Method(typeof(Yamaha.VOCALOID.Sequence), "SaveSequence");
        var load = AccessTools.Method(typeof(Yamaha.VOCALOID.Sequence), "LoadProjectSequenceFile");
        bool Save(WIVSMSequence sequence, string name) => (bool)save.Invoke(shell, [sequence, directory, name, ".vpr"])!;
        WIVSMSequence Open(string name)
        {
            object?[] input = [Path.Combine(directory, name + ".vpr"), null];
            var result = ((Yamaha.VOCALOID.Sequence.LoadResult, bool))load.Invoke(shell, input)!;
            Check(result.Item1 == Yamaha.VOCALOID.Sequence.LoadResult.Success && input[1] is WIVSMSequence, "Project did not open: " + name);
            return (WIVSMSequence)input[1]!;
        }
        string Hash(string name) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(Path.Combine(directory, name + ".vpr"))));
        var states = new List<object>();
        var source = manager.CreateSequence(new VSMSequenceData { SamplingRate = VSMSamplingRate._44100, MaxNumTracks = 4, MaxUndoCount = 100 })!;
        try
        {
            source.InsertTimeSig(0, new VSMTimeSigEvent(4, 4)); source.InsertTempo(VSMRelTick.Zero, 12000);
            var track = (WIVSMMidiTrack)source.InsertTrackEx(0, VSMTrackType.Midi, "compatibility")!;
            var part = track.InsertPart(VSMAbsTick.Zero, new VSMRelTick(3840), "compatibility")!;
            part.SetVoiceBankID("BL8CEAM5N4XN3LFK");
            var note = part.InsertNote(new VSMRelTick(960), new VSMNoteEvent(480, 60, 64), part.GetDefaultNoteExpression(),
                part.GetDefaultAiNoteExpression(), "validation", "k a", true, 4)!;
            source.InterpolatedMasterVolume(); source.Commit(false); source.StopAsyncRendering();
            Check(Save(source, "plain"), "Native seed save failed.");
            SampledTimingHost.BeforeRendering(source);
            source.StartAsyncRendering(); Check(source.CanAsyncRendering(), "Plain native project was blocked."); source.StopAsyncRendering();
            Check(_backendAttempts == 0, "Plain project requested the private backend.");
            var address = SampledTimingVsmBinding.Address(note);
            var timing = new SampledTimingPlan(address.Topology, [new(TimingAdjustment.PhonemeBoundary, 1, .01f)]);
            var unbound = address with { Track = 3 };
            foreach (string mode in (realUnknown ? new[] { "timing", "duration", "volume", "transitions", "combined", "unbound" } : ["missing-modules", "timing", "duration", "volume", "transitions", "combined", "unbound"]))
            {
                _missingModules = mode == "missing-modules";
                string seed = Path.Combine(directory, mode + ".vpr"); File.Copy(Path.Combine(directory, "plain.vpr"), seed);
                var plans = new Dictionary<TimingNoteAddress, SampledTimingPlan>();
                if (mode is "missing-modules" or "timing" or "combined" or "unbound") plans[address] = timing;
                if (mode == "unbound") plans[unbound] = new(unbound.Topology, [new("future.transition.gain", 0, .3f)]);
                SampledTimingProjectArchive.Write(seed, plans);
                var durations = mode is "duration" or "combined" or "unbound" ? new[] {
                    new DurationSelectionArchive.Entry(mode == "unbound" ? unbound : address, .5f,
                        DurationCandidatePolicy.ExpandedExperimental, DurationSelectionPlan.AlgorithmVersion) } : [];
                DurationSelectionArchive.Write(seed, durations);
                var volumes = mode is "volume" or "combined" or "unbound" ? new[] {
                    new PhonemeVolumeArchive.Entry(mode == "unbound" ? unbound : address, [-3, 2]) } : [];
                PhonemeVolumeArchive.Write(seed, volumes);
                var transitionId = Guid.NewGuid();
                var transitionEdge = new TransitionProjectEdge(new(transitionId, 0, "k"), new(transitionId, 1, "a"), "k", "a", 0, false, 0);
                var transitions = mode is "transitions" or "combined" ? TransitionArchiveData.From(new(1,
                    [new(transitionId, address)], [], [new(transitionEdge, -6)])) : TransitionArchiveData.Empty;
                TransitionProjectArchive.Write(seed, transitions);
                int before = _backendAttempts;
                var opened = Open(mode);
                try
                {
                    Check(_backendAttempts == before && SampledTimingHost.Find(opened)?.HasBackend != true, "Loading constructed a backend.");
                    var copy = manager.DuplicateSequence(opened, new VSMSequenceData { SamplingRate = VSMSamplingRate._44100, MaxNumTracks = 4, MaxUndoCount = 100 })!;
                    try
                    {
                        Check(_backendAttempts == before && SampledTimingHost.Find(copy)?.HasBackend != true, "Copy constructed a backend.");
                        Check(Save(copy, mode + "-copy"), "Copy could not save without a backend.");
                        string copyPath = Path.Combine(directory, mode + "-copy.vpr");
                        Check(SampledTimingProjectArchive.Read(copyPath).Count == plans.Count, "Copy lost timing data.");
                        Check(DurationSelectionArchive.Read(copyPath).Length == durations.Length, "Copy lost duration data.");
                        Check(PhonemeVolumeArchive.Read(copyPath).Length == volumes.Length, "Copy lost phoneme volume data.");
                        Check(TransitionProjectArchive.Read(copyPath).Fingerprint == transitions.Fingerprint, "Copy lost transition data.");
                    }
                    finally { copy.Close(); }
                    string saved = mode + "-saved";
                    Check(Save(opened, saved), "Unavailable backend prevented save.");
                    string path = Path.Combine(directory, saved + ".vpr");
                    Check(SampledTimingProjectArchive.Read(path).Count == plans.Count, "Timing controls were lost.");
                    foreach (var pair in plans) Check(SampledTimingProjectArchive.Read(path)[pair.Key].Fingerprint == pair.Value.Fingerprint, "Timing control changed.");
                    Check(JsonSerializer.Serialize(DurationSelectionArchive.Read(path)) == JsonSerializer.Serialize(durations), "Duration controls changed.");
                    Check(JsonSerializer.Serialize(PhonemeVolumeArchive.Read(path)) == JsonSerializer.Serialize(volumes), "Volume controls changed.");
                    Check(TransitionProjectArchive.Read(path).Fingerprint == transitions.Fingerprint, "Transition controls changed.");
                    var reopened = Open(saved);
                    try { Check(Save(reopened, mode + "-twice"), "Second save failed."); }
                    finally { reopened.Close(); }
                    if (mode != "volume")
                    {
                        opened.StartAsyncRendering();
                        Check(!opened.CanAsyncRendering(), "Unsupported controls silently rendered without controls.");
                    }
                    if (mode == "timing")
                    {
                        var session = SampledTimingHost.Find(opened)!;
                        var liveNote = ((WIVSMMidiPart)((WIVSMMidiTrack)opened.GetTrack(0UL)!).GetPart(0UL)!).GetNote(0UL)!;
                        int attempts = _backendAttempts;
                        bool rejected = false;
                        try { using var prepared = session.Prepare(liveNote); }
                        catch (NotSupportedException) { rejected = true; }
                        Check(rejected && !session.HasBackend && _backendAttempts == attempts + 1, "Explicit query did not isolate failed construction.");
                        session.Document.Edit(address, new(address.Topology, [new(TimingAdjustment.NoteCoefficient, -1, .375f)]),
                            edit => CustomParameterHistoryCoordinator.Push(opened, edit));
                        Check(SampledTimingHost.IsDirty(opened), "Data edit was not dirty.");
                        opened.Undo(); Check(session.Document.Get(address)!.Fingerprint == timing.Fingerprint, "Shared Undo failed without backend.");
                        opened.Redo(); Check(session.Document.Get(address)!.Fingerprint != timing.Fingerprint, "Shared Redo failed without backend.");
                        string old = Hash(saved); _failSave = true;
                        try { Check(!Save(opened, saved), "Failed extension write reported success."); }
                        finally { _failSave = false; }
                        Check(Hash(saved) == old && SampledTimingHost.IsDirty(opened), "Failed save lost old file or dirty state.");
                        Check(Save(opened, saved) && !SampledTimingHost.IsDirty(opened), "Recovery save failed.");
                        Check(_backendAttempts == attempts + 1, "History/save retried private backend.");
                        liveNote.SetNoteNumber(62); opened.Commit(true);
                        Check(session.Document.Snapshot().Keys.Single().Pitch == 62, "Native edit lost parameter relocation.");
                        Check(Save(opened, "timing-relocated"), "Native edit prevented data save.");
                        opened.Undo(); Check(session.Document.Snapshot().Keys.Single().Pitch == 60, "Native Undo lost parameter identity.");
                        opened.Redo(); Check(session.Document.Snapshot().Keys.Single().Pitch == 62, "Native Redo lost parameter identity.");
                        Check(_backendAttempts == attempts + 1, "Native history retried private backend.");
                    }
                    states.Add(new { mode, loadSaveLoad = true, backendCreated = SampledTimingHost.Find(opened)?.HasBackend == true,
                        timing = plans.Count, duration = durations.Length, volume = volumes.Length, transitions = transitions.HasData, host = SampledTimingHost.Diagnostics(opened) });
                }
                finally { opened.Close(); }
            }
            Check(_hashes == (realUnknown ? 0 : 2), "Unexpected component identity injection or repeated hashing.");
            File.WriteAllText(Path.Combine(directory, "compatibility-host.json"), JsonSerializer.Serialize(new { componentIdentityInjected = !realUnknown, qualificationHashes = _hashes, states }, new JsonSerializerOptions { WriteIndented = true }));
            Console.WriteLine("compatibility.host=plain native render; data/copy round-trips; shared/native history; save rollback; unsupported execution rejection passed");
        }
        finally { source.Close(); harmony.UnpatchAll(harmony.Id); application.Shutdown(); }
    }

    private static void BackendConstruction() => _backendAttempts++;
    private static bool UnknownHash(ref string __result) { _hashes++; __result = new string('0', 64); return false; }
    private static void MissingModules() { if (_missingModules) throw new NotSupportedException("Injected timing module not loaded."); }
    private static void FailSave() { if (_failSave) throw new IOException("Injected extension save failure."); }
    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}
#endif
