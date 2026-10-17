#if TIMING_HOST_PROBE
using System.Collections;
using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Threading;
using HarmonyLib;
using VOCALOIDPatcher.ConsonantTiming;
using VOCALOIDPatcher.Patch;
using VOCALOIDPatcher.Patch.Patches;
using VOCALOIDPatcher.Utils;
using Yamaha.VOCALOID.DSE;
using Yamaha.VOCALOID.VDM;
using Yamaha.VOCALOID.VSM;

internal static partial class Program
{
    private static bool _timingForceUnavailable;
    private static int ProbeTimingHostRegression(WIVSMSequenceManager manager,DSEManager dse,DatabaseManager database,string directory)
    {
        Exception? failure=null;
        var thread=new Thread(() => {
            try { RunTimingHostRegression(manager,dse,database,directory); }
            catch(Exception e) { failure=e; }
        });
        thread.SetApartmentState(ApartmentState.STA); thread.Start(); thread.Join();
        if(failure!=null) throw new InvalidOperationException("Host regression failed.",failure);
        return 0;
    }

    private static void RunTimingHostRegression(WIVSMSequenceManager manager,DSEManager dse,DatabaseManager database,string directory)
    {
        Directory.CreateDirectory(directory);
        var application=new Application();
        var harmony=new Harmony("v6patch.isolated-timing-host-regression");
        foreach(PatchBase patch in new PatchBase[] { new SampledTimingPhonemePositionsPatch(),new SampledTimingBeginPhonemePatch(),
            new SampledTimingEndPhonemePatch(),new SampledTimingRenderStartPatch(),new SampledTimingRenderCompletionPatch() })
        {
            var target=AccessTools.Method(patch.TargetClass,patch.TargetMethodName,patch.ArgumentTypes);
            Require(target!=null,"Host patch target missing.");
            harmony.Patch(target,new HarmonyMethod(AccessTools.Method(patch.GetType(),"Prefix")));
        }
        harmony.Patch(AccessTools.Method(typeof(WIVSMSequence),nameof(WIVSMSequence.Commit),[typeof(bool)]),
            new HarmonyMethod(typeof(Program),nameof(TimingHostCommitBefore)),new HarmonyMethod(typeof(Program),nameof(TimingHostCommitAfter)));
        harmony.Patch(AccessTools.Method(typeof(LocalConsonantTimingBackend),"PrepareCoefficient"),
            new HarmonyMethod(typeof(Program),nameof(TimingHostForceUnavailable)));
        foreach(string method in new[] { nameof(WIVSMSequence.Undo),nameof(WIVSMSequence.Redo) })
            harmony.Patch(AccessTools.Method(typeof(WIVSMSequence),method,Type.EmptyTypes),
                new HarmonyMethod(typeof(Program),nameof(TimingHostHistoryBefore)));
        bool vowelRegression = Environment.GetEnvironmentVariable("VOCALOID_HOST_VOWEL") == "1";
        string? savedProject = Environment.GetEnvironmentVariable("VOCALOID_HOST_PROJECT");
        using var fixture = savedProject != null ? OpenSavedTimingFixture(manager, savedProject, directory) :
            CreateStandaloneFixture(manager,1,480,0,vowelRegression ? 7680 : 2880);
        var source=fixture.Sequence; var part=fixture.Part; var note=fixture.Notes[0];
        if (savedProject == null)
        {
            part.SetVoiceBankID("BL8CEAM5N4XN3LFK"); note.SetPhonemes(vowelRegression ? "a" : "l a",true,4);
            if (vowelRegression) part.MoveNote(new VSMRelTick(960), note);
        }
        source.InterpolatedMasterVolume(); source.Commit(false);
        var backend=new LocalConsonantTimingBackend(manager,dse,database);
        var entryType=typeof(SampledTimingHost).GetNestedType("Entry",BindingFlags.NonPublic)!;
        var entry=Activator.CreateInstance(entryType,BindingFlags.Instance|BindingFlags.NonPublic|BindingFlags.Public,null,[source,backend],null)!;
        var entries=(IDictionary)typeof(SampledTimingHost).GetField("Entries",BindingFlags.NonPublic|BindingFlags.Static)!.GetValue(null)!;
        entries.Add((nint)source,entry);
        var session=SampledTimingHost.Find(source)!;
        var changed=typeof(SampledTimingHost).GetMethod("Changed",BindingFlags.NonPublic|BindingFlags.Static)!;
        session.Changed+=()=>changed.Invoke(null,[entry]);
        if (savedProject != null && Environment.GetEnvironmentVariable("VOCALOID_HOST_LOAD_CONTROLS") == "1")
        {
            entryType.GetField("Loading",BindingFlags.Instance|BindingFlags.NonPublic)!.SetValue(entry,true);
            try { session.Document.Load(SampledTimingProjectArchive.Read(savedProject)); }
            finally { entryType.GetField("Loading",BindingFlags.Instance|BindingFlags.NonPublic)!.SetValue(entry,false); }
        }
        var results=new List<object>();
        var pcmByPlan=new Dictionary<string,string>();
        using var counters = new TimingDseCounters();
        using var scheduleProbe = new MetadataTimingReplay(counters.VtableRva);
        bool completed=false;
        using var observer=new WIVSMRendererObserver();
        observer.Completed+=(_,e)=> { if((nint)e.MidiPart==(nint)part) completed=true; };
        source.AddRendererObserver(observer);
        SampledTimingPlan Plan(int offset) => new(note.Phonemes,[new(TimingAdjustment.PhonemeBoundary,0,(float)(offset*256d/44100))]);
        string Render(string name, bool startRendering = true)
        {
            completed=false;
                scheduleProbe.Capture();
                if (startRendering) source.StartAsyncRendering();
                var watch=System.Diagnostics.Stopwatch.StartNew();
                while(!completed && watch.Elapsed.TotalSeconds<15)
                {
                    while(source.InvokeRendererObserver()) {}
                    PumpTimingDispatcher(); Thread.Sleep(1);
                }
                Require(completed,$"Host render {name} did not complete; enabled={source.CanAsyncRendering()}, finished={source.IsFinishedRendering}, state={JsonSerializer.Serialize(SampledTimingHost.Diagnostics(source))}.");
                Require(source.CanAsyncRendering() && source.IsFinishedRendering,
                    $"Completed render {name} left the native service stopped or busy: {JsonSerializer.Serialize(SampledTimingHost.Diagnostics(source))}.");
                string path=Path.Combine(directory,"host-"+name+".wav");
                File.Copy(part.WaveFilePath,path,true);
                scheduleProbe.Check();
                var state=new {name,hash=TimingPcmHash(path),positions=note.GetPhonemePositions(),
                    nativePositions=NativeTimingPositions.Read(note,false),sampleBoundaries=scheduleProbe.SampleBoundaries.Order().ToArray(),
                    nativeScopes=NativeSampledTiming.ReadStatus((nint)part).Scopes,nativeCacheKeys=NativeSampledTiming.ReadStatus((nint)part).CacheKeys,
                    host=SampledTimingHost.Diagnostics(source)};
                results.Add(state); Console.WriteLine("timing.host="+JsonSerializer.Serialize(state));
                if(vowelRegression && session.Document.Snapshot().Count!=0)
                {
                    string key=session.Document.Fingerprint;
                    if(pcmByPlan.TryGetValue(key,out string? previous)) Require(state.hash==previous,"Repeated controls returned different PCM.");
                    pcmByPlan[key]=state.hash;
                }
                return state.hash;
        }
        try
        {
            Render("baseline");
            if (vowelRegression)
            {
                var pairs = new[] { (1, 1), (1, 0), (1, 1), (0, 1), (-1, 1), (1, -1), (0, 0), (0, 16), (0, 35), (1, 1) };
                int cycles = int.TryParse(Environment.GetEnvironmentVariable("VOCALOID_HOST_CYCLES"), out int requestedCycles) ? requestedCycles : 1;
                foreach (var pair in Enumerable.Range(0, cycles).SelectMany(_ => pairs))
                {
                    var plan = new SampledTimingPlan(note.Phonemes, new[] { pair.Item1, pair.Item2 }
                        .Select((value, index) => new TimingAdjustment(TimingAdjustment.PhonemeBoundary, index, (float)(value * 256d / 44100))));
                    bool unchanged = session.Document.Get(SampledTimingVsmBinding.Address(note))?.Fingerprint == plan.Fingerprint;
                    SampledTimingHost.SetPhonemePlan(note, plan);
                    if (unchanged)
                    {
                        Require(source.CanAsyncRendering() && source.IsFinishedRendering, "An unchanged timing plan stopped the service.");
                        continue;
                    }
                    Render($"vowel-{pair.Item1}-{pair.Item2}-{results.Count}", false);
                    using (var prepared = session.Prepare(note))
                    {
                        var layout = prepared.QueryPhonemes(plan, session.Revision);
                        var status=NativeSampledTiming.ReadStatus((nint)part);
                        if(status.Scopes!=0)
                            Require(layout.Anchors.Skip(1).Take(layout.Anchors.Length - 2).All(p => scheduleProbe.SampleBoundaries.Contains(p.To)),
                                "Rendered sample boundaries differ from the query.");
                        else Require(status.CacheKeys!=0,"Cached controls bypassed the timing key.");
                    }
                    Require(!JsonSerializer.Serialize(SampledTimingHost.Diagnostics(source)).Contains("\"suspended\":true"), "Vowel timing render was suspended.");
                    Require(NativeSampledTiming.ReadStatus((nint)part).Failures == 0, "Vowel timing render failed.");
                }
                File.WriteAllText(Path.Combine(directory,"timing-host-regression.json"),JsonSerializer.Serialize(results,new JsonSerializerOptions{WriteIndented=true}));
                return;
            }
            Require(source.CanAsyncRendering(),"Native service was not enabled by StartAsyncRendering.");
            source.StopAsyncRendering();
            Require(!source.CanAsyncRendering(),"CanAsyncRendering no longer reports the service enabled state.");
            source.StartAsyncRendering();
            // The Editor starts the asynchronous service once. Native Commit
            // schedules subsequent renders without another managed Start call.
            note.NoteVelocity=65; source.Commit(false);
            Render("native-commit-service",false);
            note.NoteVelocity=64; source.Commit(false);
            Render("native-commit-restored",false);
            int[] defaultPositions=note.GetPhonemePositions().ToArray();
            SampledTimingHost.SetPhonemePlan(note,Plan(-2));
            string first=Render("control-a",false);
            Require(!defaultPositions.SequenceEqual(note.GetPhonemePositions()),"Custom timing did not change the rendered boundaries.");
            using(var prepared=session.Prepare(note)) _=prepared.QueryPhonemes(Plan(-3),session.Revision);
            results.Add(new {caseName="projected-source-can-prepare",passed=true});
            SampledTimingHost.SetPhonemePlan(note,Plan(-3));
            string second=Render("control-b",false); Require(first!=second,"Repeated timing edit did not affect audio.");
            note.NoteVelocity=0; source.Commit(false);
            string slow=Render("vel-0",false); Require(slow!=second,"VEL was blocked after timing edit.");
            note.NoteVelocity=100; source.Commit(false);
            Require(Render("vel-100",false)!=slow,"Second VEL edit did not render.");
            note.NoteVelocity=30; source.Commit(true);
            string historyValue=Render("native-history-edit",false);
            Require(source.CanUndo(),"Native edit history was lost.");
            source.Undo(); Render("native-history-undo",false);
            Require(note.NoteVelocity==100 && source.CanRedo(),"Native undo did not restore VEL.");
            source.Redo();
            Require(Render("native-history-redo",false)==historyValue && note.NoteVelocity==30,"Native redo changed custom timing audio.");
            for(int i=0;i<12;i++) { note.NoteVelocity=40+i; source.Commit(false); }
            Render("rapid-native-commits",false);
            _timingForceUnavailable=true;
            note.NoteVelocity=64; source.Commit(false);
            PumpTimingDispatcher();
            source.StartAsyncRendering();
            Require(!source.CanAsyncRendering(), "Unsupported controls silently used an uncontrolled native render.");
            results.Add(new { caseName="unsupported-context-preserved", renderRejected=true, storedPlans=session.Document.Snapshot().Count });
            Require(JsonSerializer.Serialize(SampledTimingHost.Diagnostics(source)).Contains("\"suspended\":true"),"Failed planner was not suspended.");
            _timingForceUnavailable=false;
            note.NoteVelocity=100; source.Commit(false); Render("restored-context",false);
            Require(part.MoveNote(new VSMRelTick(1200),note),"Move failed."); source.Commit(false);
            Render("moved-native",false);
            Require(SampledTimingVsmBinding.ResolveActivePlans(source,session.Document).Count==0 && session.Document.Snapshot().Count==1,
                "Moved plan was rebound to a different identity or discarded.");
            using(var prepared=session.Prepare(note)) _=prepared.QueryPhonemes(new(note.Phonemes,[]),session.Revision);
            SampledTimingHost.SetPhonemePlan(note,Plan(-2)); Render("moved-new-control",false);
            Require(session.Document.Snapshot().Keys.Single().Position==1200,"New timing edit did not replace detached locator.");
            Require(part.RemoveNote(note),"Delete failed."); source.Commit(false);
            PumpTimingDispatcher();
            var emptyWatch=System.Diagnostics.Stopwatch.StartNew();
            while(!source.IsFinishedRendering && emptyWatch.Elapsed.TotalSeconds<5)
            { while(source.InvokeRendererObserver()) {} PumpTimingDispatcher(); Thread.Sleep(1); }
            Require(source.IsFinishedRendering && part.NumNotes==0,"Deleting the last controlled note left rendering stuck.");
            Require(NativeSampledTiming.ReadStatus((nint)part).Epoch==0,"Deleted note retained its native route.");
            results.Add(new {caseName="delete-last-note",renderFinished=source.IsFinishedRendering,notes=part.NumNotes});
            string detachedPath=Path.Combine(directory,"detached-data.vpr");
            using(var archive=System.IO.Compression.ZipFile.Open(detachedPath,System.IO.Compression.ZipArchiveMode.Create)) {}
            SampledTimingHost.Save(source,detachedPath,false);
        }
        finally
        {
            source.RemoveRendererObserver(observer);
            nint pointer = (nint)source;
            source.StopAsyncRendering();
            try { source.Close(); }
            finally { SampledTimingHost.Close(pointer); }
            harmony.UnpatchAll(harmony.Id);
            application.Shutdown();
        }
        File.WriteAllText(Path.Combine(directory,"timing-host-regression.json"),JsonSerializer.Serialize(results,new JsonSerializerOptions{WriteIndented=true}));
    }

    private static void PumpTimingDispatcher()
    {
        var frame=new DispatcherFrame();
        Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle,new Action(()=>frame.Continue=false));
        Dispatcher.PushFrame(frame);
    }
    private static void TimingHostCommitBefore(WIVSMSequence __instance,out bool __state)
    {
        __state=__instance.IsStaged;
        if(__state && !NativeRenderSession.IsInternalSequence(__instance)) SampledTimingHost.BeforeSourceChange(__instance);
    }
    private static void TimingHostForceUnavailable()
    {
        if(_timingForceUnavailable) throw new NotSupportedException("Injected unavailable timing context.");
    }
    private static void TimingHostHistoryBefore(WIVSMSequence __instance) => SampledTimingHost.SourceChanged(__instance);
    private static void TimingHostCommitAfter(WIVSMSequence __instance,bool __state,bool __result)
    {
        if(__state && __result && !NativeRenderSession.IsInternalSequence(__instance)) SampledTimingHost.SourceChanged(__instance);
    }
}
#endif
