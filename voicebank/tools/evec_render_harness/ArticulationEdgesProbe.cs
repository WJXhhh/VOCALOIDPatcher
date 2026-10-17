using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using VOCALOIDPatcher.ConsonantTiming;
using Yamaha.VOCALOID.DSE;
using Yamaha.VOCALOID.VDM;
using Yamaha.VOCALOID.VSM;

internal static partial class Program
{
    // Disposable engine experiment. Never touches the editor's sequence or installs DLLs.
    private static int ProbeArticulationEdges(WIVSMSequenceManager manager, DSEManager dse,
        DatabaseManager database, string directory)
    {
        Directory.CreateDirectory(directory);
        string library = Environment.GetEnvironmentVariable("VOCALOID_TIMING_PROBE_NATIVE")!;
        NativePhonemeTiming.Initialize(library);
        NativeSampledTiming.Initialize(library);
        using var counters = new TimingDseCounters();
        using var backend = new LocalConsonantTimingBackend(manager, dse, database);
        using var native = new NativeConsonantTimingEngine((nint)dse);
        nint nativeState = (nint)typeof(NativeConsonantTimingEngine).GetField("_state", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(native)!;
        using var capture = new TimingCoefficientHook();
        var report = new List<object>();
        ulong epoch = 8000;
        foreach (var test in new[] { ("chinese-a", "BL8CEAM5N4XN3LFK", 4), ("miku-a", "BCNFCY43LB2LZCD4", 0) })
        {
            using var fixture = CreateStandaloneFixture(manager, 1, 960, 0, 4800);
            fixture.Part.SetVoiceBankID(test.Item2);
            var note = fixture.Notes[0];
            fixture.Part.MoveNote(new VSMRelTick(960), note);
            note.SetNoteNumber(48);
            note.SetPhonemes("a", true, test.Item3);
            fixture.Sequence.InterpolatedMasterVolume(); fixture.Sequence.Commit(false);
            var materials = new Dictionary<nint, int>();
            string? baselineHash = null, baselineSources = null;
            DurationRow[]? baselineRows = null;
            NativePhonemeTiming.Point[]? baselineAnchors = null;
            int[]? baselineBoundaries = null;
            DurationFramePosition[]? baselineFrames = null;
            using var prepared = backend.PrepareCoefficient(note, 1);
            foreach (var variant in new[] { ("baseline", 0, 0, false), ("head-early", -24, 0, false),
                ("tail-late", 0, 24, false), ("both-out", -24, 24, false), ("head-late", 12, 0, false),
                ("tail-early", 0, -12, false), ("restored", 0, 0, false),
                ("inner-head-long", 24, 0, true), ("inner-tail-long", 0, -24, true),
                ("inner-both-long", 24, -24, true), ("inner-restored", 0, 0, true) })
            {
                var edits = new List<TimingAdjustment>();
                if (!variant.Item4 && variant.Item2 != 0) edits.Add(new(TimingAdjustment.PhonemeBoundary, 0, (float)(variant.Item2 * 256d / 44100)));
                if (!variant.Item4 && variant.Item3 != 0) edits.Add(new(TimingAdjustment.PhonemeBoundary, 1, (float)(variant.Item3 * 256d / 44100)));
                var plan = new SampledTimingPlan("a", edits);
                var before = counters.Read();
                var layout = prepared.QueryPhonemes(plan, 1);
                var queryCounters = counters.Read().Since(before);
                Require(queryCounters == new TimingDseSnapshot(0, 0, 0), "Edge query synthesized audio.");
                var anchors = layout.Anchors;
                if (variant.Item4)
                {
                    var art = baselineRows!.Where(r => r.To != -1).OrderBy(r => r.Begin).ToArray();
                    Require(art.Length == 2, "Expected isolated onset/release pair.");
                    var inner = baselineAnchors!.ToDictionary(p => p.From, p => p.To);
                    inner[art[0].Begin] = art[0].Begin;
                    inner[art[0].End] = art[0].End + variant.Item2;
                    inner[art[1].Begin] = art[1].Begin + variant.Item3;
                    inner[art[1].End] = art[1].End;
                    anchors = inner.OrderBy(p => p.Key).Select(p => new NativePhonemeTiming.Point(p.Key, p.Value)).ToArray();
                }
                NativeSampledTiming.Publish(fixture.Part, ++epoch, new Dictionary<nint, float>(), anchors);
                SampledTimingVsmBinding.InvalidateRenderedPart(fixture.Part, fixture.Notes);
                capture.Reset(-1, false, null);
                int consumed = 0;
                var actual = new Dictionary<int, DurationRow>();
                var frames = new SortedDictionary<int, DurationFramePosition>();
                capture.AfterStep = state =>
                {
                    int cursor = Marshal.ReadInt32(state, 0x24158);
                    foreach (var row in ReadDurationRows(state, materials, consumed, cursor)) actual[row.Begin] = row;
                    foreach (var frame in ReadDurationFramePositions(state, materials, consumed, cursor)) frames[frame.Frame] = frame;
                    consumed = cursor;
                };
                string wave = Path.Combine(directory, test.Item1 + "-" + variant.Item1 + ".wav");
                var timer = Stopwatch.StartNew();
                RenderTransitionWithScore(fixture.Sequence, fixture.Part, wave);
                double renderMs = timer.Elapsed.TotalMilliseconds;
                capture.AfterStep = null; capture.Check();
                var route = NativeSampledTiming.ReadStatus((nint)fixture.Part);
                Require(route.Failures == 0 && route.Scopes > 0 && actual.Count > 0, "Edge render route failed.");
                Require(ReadWaveMetrics(wave).Peak > .00001, "Edge render is silent.");
                string hash = TimingPcmHash(wave);
                string sources = string.Join(';', actual.Values.Where(r => r.To != -1).OrderBy(r => r.Begin)
                    .Select(r => $"{r.From}>{r.To}/{r.Material.Id}/{r.Stage}/{r.Reverse}"));
                baselineHash ??= hash; baselineSources ??= sources;
                if (variant.Item1 is "restored" or "inner-restored") Require(hash == baselineHash, "Restored edges differ from baseline PCM.");
                if (variant.Item1 is not "baseline" and not "restored" and not "inner-restored") Require(hash != baselineHash, "Edge change did not change PCM.");
                baselineRows ??= actual.Values.OrderBy(r => r.Begin).ToArray();
                baselineAnchors ??= layout.Anchors;
                baselineBoundaries ??= note.GetPhonemePositions().ToArray();
                baselineFrames ??= frames.Values.ToArray();
                if (variant.Item4) Require(note.GetPhonemePositions().SequenceEqual(baselineBoundaries), "Inner allocation changed outer phoneme boundaries.");
                Require(note.RelPosTick.Value == 960 && note.DurationTick.Value == 960, "Edge experiment modified the musical note.");
                var baselineArt = baselineRows.Where(r => r.To != -1).OrderBy(r => r.Begin).ToArray();
                bool SameFrameMap(DurationRow span) => baselineFrames.Where(f => f.Frame >= span.Begin && f.Frame < span.End)
                    .All(f => frames.TryGetValue(f.Frame, out var point) && point == f);
                bool onsetMappingUnchanged = SameFrameMap(baselineArt[0]);
                bool releaseMappingUnchanged = SameFrameMap(baselineArt[^1]);
                if (variant.Item1 == "inner-head-long") Require(releaseMappingUnchanged, "Head-only control changed release mapping.");
                if (variant.Item1 == "inner-tail-long") Require(onsetMappingUnchanged, "Tail-only control changed onset mapping.");
                var planningMs = new List<double>();
                var planningBefore = counters.Read();
                for (int repeat = 0; repeat < (variant.Item4 ? 20 : 1); repeat++)
                {
                    var watch = Stopwatch.StartNew();
                    native.Begin();
                    bool scope = false;
                    try
                    {
                        NativePhonemeTiming.Begin(anchors); scope = true;
                        foreach (var block in capture.Inputs) native.Plan(block.Frame, block.Bytes);
                        var plannedArt = ReadDurationRows(nativeState, materials).Where(r => r.To != -1).OrderBy(r => r.Begin).ToArray();
                        var actualArt = actual.Values.Where(r => r.To != -1).OrderBy(r => r.Begin).ToArray();
                        Require(plannedArt.SequenceEqual(actualArt, new EdgeArtComparer()), "Pure planning differs from actual ART allocation.");
                        var plannedFrames = ReadDurationFramePositions(nativeState, materials, actualArt[0].Begin, actualArt[^1].End)
                            .ToDictionary(f => f.Frame);
                        Require(actualArt.All(r => frames.Values.Where(f => f.Frame >= r.Begin && f.Frame < r.End)
                            .All(f => plannedFrames.TryGetValue(f.Frame, out var point) && point == f)),
                            "Pure planning differs from consumed ART source-frame mapping.");
                    }
                    finally
                    {
                        try { if (scope) NativePhonemeTiming.End(); }
                        finally { native.End(); }
                    }
                    planningMs.Add(watch.Elapsed.TotalMilliseconds);
                }
                var planningCounters = counters.Read().Since(planningBefore);
                Require(planningCounters == new TimingDseSnapshot(0, 0, 0), "Internal allocation planning synthesized audio.");
                var spans = actual.Values.OrderBy(r => r.Begin).Select(row =>
                {
                    var points = frames.Values.Where(f => f.Frame >= row.Begin && f.Frame < row.End && f.Source == row.Material.Id).ToArray();
                    float Position(DurationFramePosition p) => BitConverter.Int32BitsToSingle(p.SourcePositionBits);
                    return new { row, sampledFrames = points.Length, firstSource = points.Length == 0 ? 0 : Position(points[0]),
                        lastSource = points.Length == 0 ? 0 : Position(points[^1]),
                        uniquePositions = points.Select(p => p.SourcePositionBits).Distinct().Count(),
                        stationarySteps = points.Zip(points.Skip(1)).Count(p => p.First.SourcePositionBits == p.Second.SourcePositionBits) };
                }).ToArray();
                report.Add(new { fixture = test.Item1, variant = variant.Item1, headFrames = variant.Item2, tailFrames = variant.Item3,
                    notePosition = note.RelPosTick.Value, noteDuration = note.DurationTick.Value, hash, sameArtSources = sources == baselineSources,
                    sources, innerAllocation = variant.Item4, onsetMappingUnchanged, releaseMappingUnchanged,
                    anchors, boundaries = note.GetPhonemePositions(), predicted = layout.Result.Boundaries,
                    queryMs = layout.Result.Milliseconds, queryCounters, planningMs, planningCounters,
                    renderMs, spans, frames = frames.Values.ToArray() });
                File.WriteAllText(Path.Combine(directory, "articulation-edges.json"), JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
                Console.WriteLine($"edges={test.Item1}/{variant.Item1}; sameArt={sources == baselineSources}; rows={actual.Count}; frames={frames.Count}; queryMs={layout.Result.Milliseconds:F3}");
            }
            NativeSampledTiming.Remove((nint)fixture.Part);
        }
        return 0;
    }

    private sealed class EdgeArtComparer : IEqualityComparer<DurationRow>
    {
        public bool Equals(DurationRow? a, DurationRow? b) => a != null && b != null &&
            (a.Begin, a.End, a.From, a.To, a.Material.Id, a.LeftLength, a.Reverse) ==
            (b.Begin, b.End, b.From, b.To, b.Material.Id, b.LeftLength, b.Reverse);
        public int GetHashCode(DurationRow row) => HashCode.Combine(row.Begin, row.End, row.Material.Id);
    }
}
