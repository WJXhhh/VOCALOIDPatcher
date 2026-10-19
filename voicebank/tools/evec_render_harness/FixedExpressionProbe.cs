using System.Collections.Immutable;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using VOCALOIDPatcher.ConsonantTiming;
using Yamaha.VOCALOID.DSE;
using Yamaha.VOCALOID.VDM;
using Yamaha.VOCALOID.VSM;

internal static partial class Program
{
    // Research in a disposable process. Every comparison fixes the complete
    // ART/STA allocation, including Split and existing phoneme-timing edits.
    private static int ProbeFixedExpression(WIVSMSequenceManager manager, DSEManager dse,
        DatabaseManager database, string directory)
    {
        Directory.CreateDirectory(directory);
        string native = Environment.GetEnvironmentVariable("VOCALOID_TIMING_PROBE_NATIVE")!;
        NativePhonemeTiming.Initialize(native); NativeSampledTiming.Initialize(native);
        bool production = Environment.GetEnvironmentVariable("VOCALOID_FIXED_EXPRESSION_BACKEND") == "1";
        if (production) NativeTransitionGain.Initialize(native);
        using var counters = new TimingDseCounters();
        using var backend = new LocalConsonantTimingBackend(manager, dse, database);
        using var capture = new TimingCoefficientHook();
        using var gain = production ? null : new TransitionGainHook();
        using var fixture = CreateStandaloneFixture(manager, 1, 480, 0, 3840);
        fixture.Part.SetVoiceBankID("BL8CEAM5N4XN3LFK");
        var note = fixture.Notes[0]; fixture.Part.MoveNote(new VSMRelTick(960), note);
        note.SetNoteNumber(60); note.NoteVelocity = 64; note.SetPhonemes("a", true, 4);
        fixture.Sequence.InterpolatedMasterVolume(); fixture.Sequence.Commit(false);
        var ids = new Dictionary<nint, int>(); var reports = new List<object>();
        ulong epoch = 72000;
        foreach (bool edited in new[] { false, true })
        {
            using var prepared = backend.PrepareCoefficient(note, 1);
            var fixedTiming = new SampledTimingPlan("a", edited ? [
                new(TimingAdjustment.PhonemeBoundary, 0, (float)(-8 * 256d / 44100)),
                new(TimingAdjustment.PhonemeBoundary, 1, (float)(4 * 256d / 44100))] : []);
            var before = counters.Read(); var clock = Stopwatch.StartNew();
            var layout = prepared.QueryTransitions(fixedTiming, 1);
            var allocation = prepared.QueryTransitionControls(layout, new(layout.Fingerprint), 1);
            var referenceLayout = prepared.QueryTransitions(new("a", [
                new(TimingAdjustment.PhonemeBoundary, 0, (float)(-24 * 256d / 44100))]), 1);
            var reference = prepared.QueryTransitionControls(referenceLayout, new(referenceLayout.Fingerprint), 1);
            Require(counters.Read().Since(before) == new TimingDseSnapshot(0, 0, 0), "Fixed-expression planning synthesized audio.");
            double planningMs = clock.Elapsed.TotalMilliseconds;
            string prefix = edited ? "edited" : "original";
            int[]? fixedPositions = null; DurationRow[]? fixedRows = null;
            var traces = new Dictionary<string, TransitionGainHook.Frame[]>();
            var snapshots = new Dictionary<string, TransitionSegment[]>();
            Dictionary<int, TransitionFrame>? fixedMap = null;
            var expected = allocation.Segments.Where(s => s.To != null).ToArray();

            string Render(string suffix, TransitionExecutionPlan plan, int targetIndex = 0,
                ImmutableArray<TransitionFrame>? progress = null, Func<int, string, float, float>? transform = null,
                bool fixedRange = true)
            {
                string name = prefix + "-" + suffix;
                NativeSampledTiming.Publish(fixture.Part, ++epoch, new Dictionary<nint, float>(),
                    plan.RenderAnchors.Select(a => new NativePhonemeTiming.Point(a.From, a.To)).ToArray(),
                    production ? NativeTransitionGain.Gains(plan) : null, production ? NativeTransitionGain.Progress(plan) : null);
                SampledTimingVsmBinding.InvalidateRenderedPart(fixture.Part, fixture.Notes);
                capture.Reset(-1, false, null);
                var actual = new Dictionary<int, TransitionSegment>(); var frames = new Dictionary<int, TransitionFrame>();
                var materials = new Dictionary<int, DurationRow>(); var rewritten = new HashSet<int>(); int consumed = 0;
                var target = plan.Segments.Where(s => s.To != null).ElementAt(targetIndex);
                gain?.Reset(new(target.Begin, target.End, target.FromId, target.ToId, target.Stage, target.Reverse ? 1 : 0,
                    0, 0, new(-1, 0, 0, 0, 0, [])), 0, 0, 1);
                if (gain != null) gain.GainTransform = transform;
                capture.BeforeSynthesis = state =>
                {
                    TransitionGainHook.State = state;
                    if (progress is { } p) RewriteOnsetProgress(state, target, p, rewritten, false);
                };
                capture.AfterOutput = (_, _) => TransitionGainHook.State = 0;
                capture.AfterStep = state =>
                {
                    int cursor = Marshal.ReadInt32(state, LocalTimingNative.Profile.StateCurrentFrame);
                    foreach (var row in NativeConsonantTimingEngine.ReadTransitionSchedule(LocalTimingNative.Dse, state, 0, consumed, cursor))
                    { actual[row.Begin] = row; foreach (var f in row.Frames) frames[f.Frame] = f; }
                    foreach (var row in ReadDurationRows(state, ids, consumed, cursor)) materials[row.Begin] = row;
                    consumed = cursor;
                };
                string path = Path.Combine(directory, name + ".wav"); clock.Restart();
                try { RenderTransitionWithScore(fixture.Sequence, fixture.Part, path); capture.Check(); gain?.Check(); }
                finally { capture.BeforeSynthesis = null; capture.AfterStep = null; capture.AfterOutput = null; if (gain != null) gain.GainTransform = null; TransitionGainHook.State = 0; }
                var positions = note.GetPhonemePositions().ToArray();
                var rows = actual.Values.OrderBy(s => s.Begin).ToArray();
                var materialRows = materials.Values.OrderBy(r => r.Begin).ToArray();
                var status = NativeSampledTiming.ReadStatus((nint)fixture.Part);
                Require(status.Scopes > 0 && status.Failures == 0, "Fixed-expression route was not consumed.");
                Require(note.RelPosTick.Value == 960 && note.DurationTick.Value == 480 && note.NoteNumber == 60 && note.NoteVelocity == 64,
                    "Fixed-expression test altered its musical note.");
                if (fixedRange)
                {
                    var nativeMaps = plan.SourceProgress.SelectMany(p => p.Frames).ToDictionary(f => f.Frame);
                    Require(plan.RenderAnchors.SequenceEqual(allocation.RenderAnchors), "Phase backend moved a render anchor.");
                    fixedPositions ??= positions; fixedRows ??= materialRows;
                    Require(positions.SequenceEqual(fixedPositions), "Fixed-expression moved a native phoneme boundary.");
                    Require(rows.Length == allocation.Segments.Length, "Fixed-expression changed schedule topology.");
                    for (int i = 0; i < rows.Length; i++)
                    {
                        var a = rows[i]; var b = allocation.Segments[i];
                        Require((a.Begin,a.Split,a.End,a.From,a.To,a.Stage,a.Reverse,a.MaterialMetadata) ==
                            (b.Begin,b.Split,b.End,b.From,b.To,b.Stage,b.Reverse,b.MaterialMetadata), "Fixed-expression moved an ART/STA boundary or changed source identity.");
                        foreach (var f in b.Frames)
                        {
                            var want = progress?.FirstOrDefault(p => p.Frame == f.Frame) ?? nativeMaps.GetValueOrDefault(f.Frame) ?? fixedMap?.GetValueOrDefault(f.Frame);
                            Require(frames.TryGetValue(f.Frame,out var got) && (want == null || got == want),
                                $"Fixed-expression source map differs at {name}/{f.Frame}: expected {want}, actual {got}.");
                        }
                    }
                    Require(JsonSerializer.Serialize(materialRows) == JsonSerializer.Serialize(fixedRows), "Fixed-expression changed actual material objects or allocation.");
                    fixedMap ??= frames.ToDictionary();
                }
                if (progress.HasValue) Require(rewritten.Count == progress.Value.Length, "Fixed-expression did not reach every requested frame.");
                traces[suffix] = gain?.Trace.ToArray() ?? [];
                snapshots[suffix] = rows.Select(s => s with { Frames = frames.Values.Where(f => f.Frame>=s.Begin && f.Frame<s.End).OrderBy(f=>f.Frame).ToImmutableArray() }).ToArray();
                string hash = TimingPcmHash(path);
                reports.Add(new { name, production, fixedRange, targetIndex, hash, planningMs, renderMs = clock.Elapsed.TotalMilliseconds,
                    phases = plan.Controls.Phases, legacyOnsets = plan.Controls.Onsets,
                    positions, anchors = plan.RenderAnchors, rows = snapshots[suffix],
                    materials = materialRows, gainTrace = traces[suffix], rewrittenFrames = rewritten.Count, status.Scopes, status.Failures });
                File.WriteAllText(Path.Combine(directory,"fixed-expression.json"),JsonSerializer.Serialize(reports,new JsonSerializerOptions{WriteIndented=true}));
                Console.WriteLine($"fixed.expression={name}; hash={hash[..12]}; fixed={fixedRange}; frames={rewritten.Count}");
                return hash;
            }

            try
            {
                Render("warmup", allocation);
                string baseline = Render("baseline", allocation);
                expected = snapshots["baseline"].Where(s=>s.To!=null).ToArray();
                Render("reference-outside", reference, fixedRange:false);
                if (production)
                {
                    before = counters.Read(); var dragWatch = Stopwatch.StartNew(); var points = new List<double>();
                    for (int i = -25; i <= 25; i++)
                    {
                        var candidate = prepared.QueryTransitionControls(layout,
                            new TransitionControlPlan(layout.Fingerprint).WithPhase(layout.Edges[0].Identity, i / 25f), 1);
                        Require(candidate.NativePlans == 0 && candidate.RenderAnchors.SequenceEqual(allocation.RenderAnchors), "Rapid phase preview replanned or moved its range.");
                        points.Add(candidate.Milliseconds);
                    }
                    Require(counters.Read().Since(before) == new TimingDseSnapshot(0,0,0), "Rapid phase preview synthesized audio.");
                    File.WriteAllText(Path.Combine(directory,prefix+"-phase-preview.json"),JsonSerializer.Serialize(new { count=points.Count,
                        totalMilliseconds=dragWatch.Elapsed.TotalMilliseconds, maximumMilliseconds=points.Max(), medianMilliseconds=points.Order().ElementAt(points.Count/2),
                        noNativePlanning=true, noAudio=true }));
                    foreach (var (suffix,index,amount) in new[] { ("onset-slow",0,.5f),("onset-fast",0,-.5f),
                        ("release-slow",expected.Length-1,.5f),("release-fast",expected.Length-1,-.5f) })
                    {
                        before = counters.Read();
                        var candidate = prepared.QueryTransitionControls(layout,
                            new TransitionControlPlan(layout.Fingerprint).WithPhase(layout.Edges[index].Identity, amount),1);
                        Require(counters.Read().Since(before) == new TimingDseSnapshot(0,0,0), "Phase backend synthesized audio during planning.");
                        Console.WriteLine($"phase.plan={suffix}; ms={candidate.Milliseconds:F3}; native={candidate.NativePlans}");
                        Render(suffix,candidate,index);
                    }
                    Require(Render("restore",allocation)==baseline,"Phase backend did not restore exact PCM.");
                    continue;
                }
                var referenceOnset = snapshots["reference-outside"].First(s=>s.To!=null);
                var source = expected[0];
                // Uniformly fitting the two reference halves is a control:
                // increasing a duration and then shrinking it may cancel out.
                var uniform = FitFixedProgress(source,referenceOnset,1);
                Render("reference-fit-source",allocation,progress:uniform);
                foreach (var (name,power) in new[] { ("onset-slow",2d),("onset-fast",.5) })
                    Render(name,allocation,progress:FitFixedProgress(source,source,power));
                int releaseIndex = expected.Length-1;
                Render("release-slow",allocation,releaseIndex,FitFixedProgress(expected[releaseIndex],expected[releaseIndex],2));
                Render("release-fast",allocation,releaseIndex,FitFixedProgress(expected[releaseIndex],expected[releaseIndex],.5));
                var referenceGain = traces["reference-outside"].Where(f=>f.Selected).GroupBy(f=>f.Lane)
                    .ToDictionary(g=>g.Key,g=>g.OrderBy(f=>f.Time).ToArray());
                float Transport(int frame,string lane,float nativeGain)
                {
                    double at = FixedReferenceTime(source,referenceOnset,frame,1);
                    var values = referenceGain[lane];
                    int i = Array.FindLastIndex(values,f=>f.Time<=at);
                    if (i < 0) return values[0].OriginalGain;
                    if (i == values.Length-1) return values[^1].OriginalGain;
                    var a = values[i]; var b = values[i+1];
                    return (float)(a.OriginalGain+(b.OriginalGain-a.OriginalGain)*(at-a.Time)/(b.Time-a.Time));
                }
                Render("reference-fit-source-gain",allocation,progress:uniform,transform:Transport);
                Require(Render("restore",allocation)==baseline,"Fixed-expression did not restore exact PCM.");
            }
            finally { NativeSampledTiming.Remove((nint)fixture.Part); }
        }
        return 0;
    }

    private static double FixedReferenceTime(TransitionSegment target,TransitionSegment reference,int frame,double power)
    {
        bool left = frame < target.Split;
        int a = left ? target.Begin : target.Split, b = left ? target.Split-1 : target.End-1;
        int ra = left ? reference.Begin : reference.Split, rb = left ? reference.Split-1 : reference.End-1;
        double u = b<=a ? 0 : (frame-(double)a)/(b-a);
        return ra+Math.Pow(Math.Clamp(u,0,1),power)*Math.Max(0,rb-ra);
    }
    private static ImmutableArray<TransitionFrame> FitFixedProgress(TransitionSegment target,TransitionSegment reference,double power)
        => target.Frames.Select(f=>
        {
            double at=FixedReferenceTime(target,reference,f.Frame,power)-reference.Begin;
            int a=Math.Clamp((int)Math.Floor(at),0,reference.Frames.Length-1),b=Math.Min(a+1,reference.Frames.Length-1);
            float source=(float)(BitConverter.Int32BitsToSingle(reference.Frames[a].SourceBits)*(1-(at-Math.Floor(at)))+
                BitConverter.Int32BitsToSingle(reference.Frames[b].SourceBits)*(at-Math.Floor(at)));
            return f with { SourceBits=BitConverter.SingleToInt32Bits(source) };
        }).ToImmutableArray();
}
