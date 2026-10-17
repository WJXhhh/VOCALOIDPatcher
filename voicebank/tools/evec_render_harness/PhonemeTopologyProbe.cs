using System.IO;
using System.Text.Json;
using VOCALOIDPatcher.ConsonantTiming;
using Yamaha.VOCALOID.DSE;
using Yamaha.VOCALOID.VDM;
using Yamaha.VOCALOID.VSM;

internal static partial class Program
{
    private static int ProbePhonemeTopology(WIVSMSequenceManager manager, DSEManager dse, DatabaseManager database, string directory, bool volumeOnly = false)
    {
        Directory.CreateDirectory(directory);
        string nativePath = Environment.GetEnvironmentVariable("VOCALOID_TIMING_PROBE_NATIVE")!;
        NativePhonemeTiming.Initialize(nativePath);
        NativeSampledTiming.Initialize(nativePath);
        string bank = Environment.GetEnvironmentVariable("VOCALOID_PHONEME_BANK") ?? "BMBNDB8EM5222MK3";
        string phones = Environment.GetEnvironmentVariable("VOCALOID_PHONEME_FIXTURE") ?? "ts\\_h i iE_r @N";
        int language = int.TryParse(Environment.GetEnvironmentVariable("VOCALOID_PHONEME_LANGUAGE"), out int lang) ? lang : 4;
        using var fixture = CreateStandaloneFixture(manager,1,960,0,7680);
        var source=fixture.Sequence; var part=fixture.Part; var note=fixture.Notes[0];
        part.SetVoiceBankID(bank); part.MoveNote(new VSMRelTick(960),note);
        if (int.TryParse(Environment.GetEnvironmentVariable("VOCALOID_PHONEME_VELOCITY"), out int velocity)) note.NoteVelocity = velocity;
        if (int.TryParse(Environment.GetEnvironmentVariable("VOCALOID_PHONEME_PITCH"), out int pitch)) note.SetNoteNumber(pitch);
        if (int.TryParse(Environment.GetEnvironmentVariable("VOCALOID_PHONEME_DURATION"), out int duration)) note.SetDuration(new VSMRelTick(duration));
        note.SetPhonemes(phones,true,language); source.InterpolatedMasterVolume(); source.Commit(false);
        RenderTransitionWithScore(source,part,Path.Combine(directory,"baseline.wav"));
        string baselineHash=TimingPcmHash(Path.Combine(directory,"baseline.wav"));
        Console.WriteLine("topology.baseline="+JsonSerializer.Serialize(new{bank,phones,positions=note.GetPhonemePositions()}));
        if (volumeOnly) return ProbePhonemeVolume(source, part, note, directory);
        using var backend = new LocalConsonantTimingBackend(manager,dse,database);
        using var counters = new TimingDseCounters();
        using var schedule = new MetadataTimingReplay(counters.VtableRva);
        bool render = Environment.GetEnvironmentVariable("VOCALOID_TOPOLOGY_RENDER") == "1";
        ulong epoch=0;
        bool failed=false;
        var results = new List<object>();
        using var prepared = backend.PrepareCoefficient(note,1);
        int axes=phones.Split(' ',StringSplitOptions.RemoveEmptyEntries).Length+1;
        int[] offsets = (Environment.GetEnvironmentVariable("VOCALOID_PHONEME_OFFSETS") ?? "-2,2").Split(',').Select(int.Parse).ToArray();
        Require(offsets.Length is > 0 and <= 32, "Topology offset count exceeds probe bound.");
        foreach (var test in new[]{(axis:-1,offset:0)}.Concat(Enumerable.Range(0,axes).SelectMany(axis=>offsets.Select(offset=>(axis,offset)))))
        {
            var before=counters.Read();
            var plan = new SampledTimingPlan(phones,test.axis<0 ? [] : [new(TimingAdjustment.PhonemeBoundary,test.axis,(float)(test.offset*256d/44100))]);
            try
            {
                var layout=prepared.QueryPhonemes(plan,1);
                var queryCounters=counters.Read().Since(before);
                Require(queryCounters.Starts==0 && queryCounters.Steps==0 && queryCounters.Samples==0,"Topology query synthesized audio.");
                object? rendering=null;
                if(render && test.axis>=0)
                {
                    SampledTimingVsmBinding.InvalidateRenderedPart(part,[note]);
                    NativeSampledTiming.Publish(part,++epoch,new Dictionary<nint,float>(),layout.Anchors);
                    try
                    {
                        schedule.Capture();
                        string path=Path.Combine(directory,$"axis-{test.axis}-{test.offset}.wav");
                        RenderTransitionWithScore(source,part,path); schedule.Check();
                        var status=NativeSampledTiming.ReadStatus((nint)part);
                        int[] actual=NativeTimingPositions.Read(note,false);
                        bool rawMatches=actual.Select(v=>(long)v).SequenceEqual(layout.Result.Boundaries);
                        int firstVowel=Array.FindIndex(phones.Split(' ',StringSplitOptions.RemoveEmptyEntries),note.IsVowel);
                        bool needsProjection=actual.Length==0 && test.axis==firstVowel && test.offset>0;
                        bool sampleMatches=layout.Anchors.Skip(1).Take(layout.Anchors.Length-2).All(p=>schedule.SampleBoundaries.Contains(p.To));
                        string hash=TimingPcmHash(path);
                        rendering=new{actual,rawMatches,needsProjection,sampleMatches,status.Failures,status.Scopes,hash,audioChanged=hash!=baselineHash};
                        Console.WriteLine("topology.render="+JsonSerializer.Serialize(rendering));
                        Require(status.Failures==0 && status.Scopes>0 && sampleMatches,"Topology rendering differs from native query.");
                        Require(rawMatches || needsProjection,"Native VSM boundaries differ from topology query.");
                        Require(hash!=baselineHash && ReadWaveMetrics(path).Peak>.00001,"Boundary edit did not change non-silent audio.");
                    }
                    finally {NativeSampledTiming.Remove((nint)part);}
                }
                var result=new {test.axis,test.offset,success=true,layout.Result.Boundaries,layout.Anchors,counters=queryCounters,rendering};
                results.Add(result); Console.WriteLine("topology.query="+JsonSerializer.Serialize(result));
            }
            catch(NotSupportedException e)
            {
                failed=true;
                var result=new {test.axis,test.offset,success=false,error=e.Message,counters=counters.Read().Since(before)};
                results.Add(result); Console.WriteLine("topology.query="+JsonSerializer.Serialize(result));
            }
        }
        File.WriteAllText(Path.Combine(directory,"topology.json"),JsonSerializer.Serialize(results,new JsonSerializerOptions{WriteIndented=true}));
        return failed ? 1 : 0;
    }
}
