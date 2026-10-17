using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using VOCALOIDPatcher.BreathVolume;
using VOCALOIDPatcher.ConsonantTiming;
using Yamaha.VOCALOID.DSE;
using Yamaha.VOCALOID.VDM;
using Yamaha.VOCALOID.VSM;

internal static partial class Program
{
    private static int ProbeSharedBoundary(WIVSMSequenceManager manager, DSEManager dse, DatabaseManager database, string directory)
    {
        Directory.CreateDirectory(directory);
        string native = Environment.GetEnvironmentVariable("VOCALOID_TIMING_PROBE_NATIVE")!;
        NativePhonemeTiming.Initialize(native); NativeSampledTiming.Initialize(native);
        using var fixture = CreateStandaloneFixture(manager, 2, 540, 0, 7680);
        fixture.Part.SetVoiceBankID("BL8CEAM5N4XN3LFK");
        string[] phones = ["k uo", "t 7"];
        for (int n = 0; n < 2; n++)
        {
            fixture.Part.MoveNote(new VSMRelTick(n == 0 ? 960 : 1500), fixture.Notes[n]);
            fixture.Notes[n].SetDuration(new VSMRelTick(n == 0 ? 540 : 360));
            fixture.Notes[n].SetNoteNumber(n == 0 ? 58 : 56);
            fixture.Notes[n].SetPhonemes(phones[n], true, 4);
        }
        fixture.Sequence.InterpolatedMasterVolume(); fixture.Sequence.Commit(false);
        var addresses = fixture.Notes.Select(SampledTimingVsmBinding.Address).ToArray();
        float Seconds(int frames) => (float)(frames * 256d / 44100);
        var original = new Dictionary<TimingNoteAddress, SampledTimingPlan>
        {
            [addresses[0]] = new(phones[0], [new(TimingAdjustment.PhonemeBoundary, 0, Seconds(-8)), new(TimingAdjustment.PhonemeBoundary, 2, Seconds(-8))]),
            [addresses[1]] = new(phones[1], [new(TimingAdjustment.PhonemeBoundary, 0, Seconds(-8)), new(TimingAdjustment.PhonemeBoundary, 1, Seconds(1)), new(TimingAdjustment.PhonemeBoundary, 2, Seconds(-1))])
        };
        var document = new SampledTimingDocument(); document.Load(original);
        using var backend = new LocalConsonantTimingBackend(manager, dse, database);
        using var capture = new TimingCoefficientHook();
        using var counters = new TimingDseCounters();
        var history = new List<ICustomParameterHistoryEdit>();
        var hashes = new Dictionary<string, string>();
        var reports = new List<object>(); ulong epoch = 0;
        SampledTimingPlan Plan(int n) => document.Get(addresses[n]) ?? new(phones[n], []);
        LocalConsonantTimingBackend.PreparedCoefficientTiming Prepare(int n) => backend.PrepareCoefficient(fixture.Notes[n], document.Revision,
            note => document.Get(SampledTimingVsmBinding.Address(note))?.PhonemeCoefficient(note.Phonemes),
            note => document.Get(SampledTimingVsmBinding.Address(note)));

        void Render(string stage)
        {
            var anchors = new SortedDictionary<int, int>();
            var predicted = new long[2][];
            for (int n = 0; n < 2; n++)
            {
                using var prepared = Prepare(n);
                var layout = prepared.QueryPhonemes(Plan(n), document.Revision);
                predicted[n] = layout.Result.Boundaries;
                foreach (var point in layout.Anchors)
                {
                    if (anchors.TryGetValue(point.From, out int old) && old != point.To)
                    {
                        if (point.From == point.To) continue;
                        Require(old == point.From, "Shared edit produced conflicting Part anchors.");
                    }
                    anchors[point.From] = point.To;
                }
            }
            long sharedTick = addresses[0].Position + predicted[0][2];
            Require(sharedTick == addresses[1].Position + predicted[1][0], "Shared phoneme edges separated.");
            var consumedEdges = new HashSet<int>(); int consumed = 0;
            var materials = new Dictionary<nint, int>();
            capture.Reset(-1, false, null);
            capture.AfterStep = state =>
            {
                int cursor = Marshal.ReadInt32(state, 0x24158);
                foreach (var row in ReadDurationRows(state, materials, consumed, cursor))
                { consumedEdges.Add(row.Begin); consumedEdges.Add(row.End); consumedEdges.Add(row.Begin + row.LeftLength); }
                consumed = cursor;
            };
            var coefficients = fixture.Notes.Where(n => document.Get(SampledTimingVsmBinding.Address(n))?.PhonemeCoefficient(n.Phonemes) != null)
                .ToDictionary(n => (nint)n, n => document.Get(SampledTimingVsmBinding.Address(n))!.PhonemeCoefficient(n.Phonemes)!.Value);
            NativeSampledTiming.Publish(fixture.Part, ++epoch, coefficients, anchors.Select(p => new NativePhonemeTiming.Point(p.Key, p.Value)).ToArray());
            SampledTimingVsmBinding.InvalidateRenderedPart(fixture.Part, fixture.Notes);
            string wave = Path.Combine(directory, stage + ".wav");
            try { RenderTransitionWithScore(fixture.Sequence, fixture.Part, wave, blockRendering: true); capture.Check(); }
            finally { capture.AfterStep = null; }
            int sharedFrame = checked((int)Math.Round((fixture.Sequence.GetTimeFromTick(new VSMAbsTick(sharedTick)) + .5) * 44100 / 256) + 3087);
            Require(consumedEdges.Contains(sharedFrame), "Rendered source segments missed the shared boundary.");
            var status = NativeSampledTiming.ReadStatus((nint)fixture.Part);
            Require(status.Failures == 0 && status.Scopes > 0 && ReadWaveMetrics(wave).Peak > .00001, "Shared boundary render failed.");
            hashes[stage] = TimingPcmHash(wave);
            reports.Add(new { stage, predicted, sharedTick, sharedFrame, consumedBoundaryMatch = true, status.Failures, hash = hashes[stage] });
            File.WriteAllText(Path.Combine(directory, "shared-boundary.json"), JsonSerializer.Serialize(reports, new JsonSerializerOptions { WriteIndented = true }));
            Console.WriteLine($"shared.stage={stage}; tick={sharedTick}; frame={sharedFrame}; failures={status.Failures}");
        }

        void Edit(int n, int index, int? frames)
        {
            using var prepared = Prepare(n);
            var candidate = PhonemeBoundaryEdit.WithBoundary(Plan(n), index, frames.HasValue ? Seconds(frames.Value) : null);
            var before = counters.Read();
            var layout = prepared.QueryPhonemes(candidate, document.Revision, editedBoundary: index);
            Require(counters.Read().Since(before) == new TimingDseSnapshot(0, 0, 0), "Shared boundary query synthesized audio.");
            Require(layout.Plans?.Count == 2 && layout.RelatedBoundaries?.Count == 2, "Shared alias was not captured.");
            document.EditMany(layout.Plans!, edit => history.Add(edit));
        }

        using (var prepared = Prepare(0))
        {
            var candidate = PhonemeBoundaryEdit.WithBoundary(Plan(0), 2, Seconds(-4));
            bool rejected = false;
            try { prepared.QueryPhonemes(candidate, document.Revision); }
            catch (NotSupportedException e) when (e.Message.Contains("Conflicting controls")) { rejected = true; }
            Require(rejected, "Fixture did not reproduce the original shared-control conflict.");
            var preview = prepared.QueryPhonemes(candidate, document.Revision, editedBoundary: 2);
            Require(preview.Plans?.Count == 2, "Preview did not resolve both aliases.");
            rejected = false;
            try { prepared.QueryPhonemes(candidate, document.Revision); }
            catch (NotSupportedException e) when (e.Message.Contains("Conflicting controls")) { rejected = true; }
            Require(rejected, "Edit preview cache bypassed strict render validation.");
        }
        // Keep the first bank render visible in the report. Compare restoration
        // against the warm reference, as in the ordinary-Part concurrency probe.
        Render("cold"); Render("baseline");
        Edit(0, 2, -4); Render("left-edit");
        Edit(1, 0, -12); Render("right-edit");
        Edit(0, 2, null); Render("reset");
        Require(history.Count == 3, "Shared edits did not create one undo record each.");
        var reset = history[^1]; reset.ApplyBefore(); reset.AfterApply(); Render("undo-reset");
        reset.ApplyAfter(); reset.AfterApply(); Render("redo-reset");
        string archive = Path.Combine(directory, "synthetic.vpr");
        using (var stream = File.Create(archive))
        using (var zip = new System.IO.Compression.ZipArchive(stream, System.IO.Compression.ZipArchiveMode.Create)) { }
        SampledTimingProjectArchive.Write(archive, document.Snapshot());
        var saved = document.Fingerprint; document.Load(SampledTimingProjectArchive.Read(archive));
        Require(document.Fingerprint == saved, "Shared edit save/load changed controls.");
        Render("reloaded");
        document.Load(original); Render("restored");
        Require(hashes["baseline"] == hashes["restored"] && hashes["right-edit"] == hashes["undo-reset"] &&
            hashes["reset"] == hashes["redo-reset"] && hashes["reset"] == hashes["reloaded"], "Shared control restoration changed PCM.");
        document.Load(original.ToDictionary(p => p.Key, p => new SampledTimingPlan(p.Key.Topology,
            p.Value.Adjustments.Append(new(TimingAdjustment.NoteCoefficient, -1, p.Key == addresses[0] ? .18359375f : .736328125f)))));
        Edit(1, 0, -4); Render("coefficients-shared");
        Edit(1, 0, null); Render("coefficients-reset");
        var neighborOwned = new Dictionary<TimingNoteAddress, SampledTimingPlan>(original)
        { [addresses[0]] = PhonemeBoundaryEdit.WithBoundary(original[addresses[0]], 2, null) };
        document.Load(neighborOwned);
        Edit(0, 2, null); Render("neighbor-owned-reset");
        NativeSampledTiming.Remove((nint)fixture.Part);
        return 0;
    }
}
