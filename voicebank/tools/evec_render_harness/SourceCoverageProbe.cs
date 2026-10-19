using System.Collections.Immutable;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using VOCALOIDPatcher.ConsonantTiming;
using Yamaha.VOCALOID.DSE;
using Yamaha.VOCALOID.VDM;
using Yamaha.VOCALOID.VSM;

internal static partial class Program
{
    private static int ProbeSourceCoverage(WIVSMSequenceManager manager, DSEManager dse, DatabaseManager database, string directory)
    {
        NativeTransitionGain.Initialize(Environment.GetEnvironmentVariable("VOCALOID_TIMING_PROBE_NATIVE")!);
        // Retain the normal native cache. All changes use production snapshots.
        using var counters = new TimingDseCounters();
        using var backend = new LocalConsonantTimingBackend(manager, dse, database);
        using var fixture = CreateStandaloneFixture(manager, 1, 960, 0, 7680);
        var source = fixture.Sequence; var part = fixture.Part; var note = fixture.Notes[0];
        Require(part.SetVoiceBankID("BY98KLLZTDYH7YEE"), "Missing source-coverage validation bank.");
        part.MoveNote(new VSMRelTick(960), note); note.SetNoteNumber(60); note.SetPhonemes("s\\ iE_r", true, 4);
        source.InterpolatedMasterVolume(); source.Commit(false);
        using var prepared = backend.PrepareCoefficient(note, 1);
        using var capture = new TimingCoefficientHook();
        var states = new List<object>(); var materials = new Dictionary<nint, int>(); ulong epoch = 82000;
        void Record(object value)
        {
            states.Add(value); File.WriteAllText(Path.Combine(directory, "source-coverage.json"),
                JsonSerializer.Serialize(states, new JsonSerializerOptions { WriteIndented = true }));
        }
        bool RepeatEqual(string first, string second)
        {
            byte[] Samples(string path)
            {
                using var input = File.OpenRead(path); using var reader = new BinaryReader(input); input.Position = 12;
                while (input.Position + 8 <= input.Length)
                {
                    string key = System.Text.Encoding.ASCII.GetString(reader.ReadBytes(4)); uint length = reader.ReadUInt32();
                    if (length > input.Length - input.Position) throw new InvalidDataException("Invalid validation WAV.");
                    if (key == "data") return reader.ReadBytes(checked((int)length));
                    input.Position += length + (length & 1);
                }
                throw new InvalidDataException("Missing validation PCM.");
            }
            var a = Samples(first); var b = Samples(second);
            if (a.Length != b.Length || a.Length == 0 || a.Length % 2 != 0) return false;
            int peak = 0, changed = 0; double squared = 0;
            for (int i = 0; i < a.Length; i += 2)
            {
                int delta = Math.Abs(BitConverter.ToInt16(a, i) - BitConverter.ToInt16(b, i));
                peak = Math.Max(peak, delta); squared += delta * (double)delta; if (delta != 0) changed++;
            }
            double rms = Math.Sqrt(squared / (a.Length / 2));
            Record(new { kind = "native-repeat-comparison", peak, rms, changed });
            return peak <= 2 && rms <= .1;
        }
        Record(new { kind = "metadata", sdk = typeof(WIVSMSequence).Assembly.GetName().Version?.ToString(), LocalTimingNative.Profile.Id, manager.CacheCapacity });
        foreach (int phoneEnd in new[] { 0, -120 })
        foreach (int transitionEnd in new[] { 0, phoneEnd == 0 ? -24 : -6 })
        {
            var timing = new SampledTimingPlan("s\\ iE_r", phoneEnd == 0 ? [] :
                [new(TimingAdjustment.PhonemeBoundary, 2, (float)(phoneEnd * 256d / 44100))]);
            var layout = prepared.QueryTransitions(timing, 1);
            var edge = layout.Edges.Single(e => e.Identity.From == "s\\" && e.Identity.To == "iE_r");
            var release = layout.Edges.Single(e => e.Identity.From == "iE_r" && e.Identity.To == "Sil");
            var basis = new TransitionControlPlan(layout.Fingerprint).WithJunction(layout, new(edge.Identity, TransitionEndpoint.End), edge.Current.End + transitionEnd);
            string? referencePcm = null; int[]? referencePositions = null; DurationRow[]? referenceRows = null;
            var plain = prepared.QueryTransitionControls(layout, basis, 1);
            foreach (string mode in new[] { "native", "coverage", "early", "late", "gain", "release", "both-edges", "restored" })
            {
                var controls = basis;
                if (mode is not ("native" or "restored")) controls = controls.WithCoverage(edge.Identity, TransitionSourceSides.Right);
                if (mode is "early" or "late") controls = controls.WithPhase(edge.Identity, mode == "early" ? -.75f : .75f);
                if (mode == "gain") controls = new(layout.Fingerprint, controls.Junctions, [new(edge.Identity, -6)], controls.Onsets, controls.Phases, controls.Coverages);
                if (mode is "release" or "both-edges")
                {
                    if (mode == "release") controls = basis;
                    controls = controls.WithCoverage(release.Identity, TransitionSourceSides.Left).WithPhase(release.Identity, .75f);
                }
                var before = counters.Read(); var watch = System.Diagnostics.Stopwatch.StartNew();
                var execution = prepared.QueryTransitionControls(layout, controls, 1);
                var cached = prepared.QueryTransitionControls(layout, controls, 1);
                Require(cached.CacheHit && cached.NativePlans == 0 && counters.Read().Since(before) == new TimingDseSnapshot(0, 0, 0), "Coverage planning requested synthesis or missed cache.");
                Require(execution.RenderAnchors.SequenceEqual(plain.RenderAnchors), "Coverage moved timing anchors.");
                Require(execution.Segments.Zip(plain.Segments).All(p => (p.First.Begin, p.First.Split, p.First.End, p.First.MaterialMetadata) ==
                    (p.Second.Begin, p.Second.Split, p.Second.End, p.Second.MaterialMetadata)), "Coverage moved work-item allocation.");
                var row = execution.Segments.Where(s => s.To != null).ElementAt(layout.Edges.IndexOf(edge));
                foreach (var coverage in controls.Coverages)
                {
                    var coveredEdge = layout.Edges.Single(e => e.Identity == coverage.Edge);
                    var coveredRow = execution.Segments.Where(s => s.To != null).ElementAt(layout.Edges.IndexOf(coveredEdge));
                    var original = coveredEdge.Original;
                    foreach (var side in new[] { TransitionSourceSides.Left, TransitionSourceSides.Right }.Where(s => coverage.Sides.HasFlag(s)))
                    {
                        int first = side == TransitionSourceSides.Left ? 0 : coveredRow.Split - coveredRow.Begin;
                        int last = side == TransitionSourceSides.Left ? coveredRow.Split - coveredRow.Begin - 1 : coveredRow.Frames.Length - 1;
                        var span = side == TransitionSourceSides.Left ? original.MaterialCoverage!.Left : original.MaterialCoverage!.Right;
                        if (first > last && span == null) continue;
                        Require(span != null && BitConverter.Int32BitsToSingle(coveredRow.Frames[first].SourceBits) == span.First &&
                            BitConverter.Int32BitsToSingle(coveredRow.Frames[last].SourceBits) == span.Last, "Coverage lost material stage endpoints.");
                    }
                }
                double planMs = watch.Elapsed.TotalMilliseconds;
                prepared.ValidateTransitionCommit(part, execution, 1);
                prepared.PublishTransitions(part, ++epoch, execution, 1);
                SampledTimingVsmBinding.InvalidateRenderedPart(part, [note]);
                var actual = new Dictionary<int, DurationRow>(); int consumed = 0;
                capture.Reset(-1, false, null);
                capture.AfterStep = state =>
                {
                    int cursor = Marshal.ReadInt32(state, LocalTimingNative.Profile.StateCurrentFrame);
                    foreach (var item in ReadDurationRows(state, materials, consumed, cursor)) actual[item.Begin] = item;
                    consumed = cursor;
                };
                string file = $"phone{phoneEnd}-art{transitionEnd}-{mode}.wav";
                try { RenderTransitionWithScore(source, part, Path.Combine(directory, file)); capture.Check(); }
                finally { capture.AfterStep = null; }
                var route = NativeSampledTiming.ReadStatus((nint)part); var gain = NativeTransitionGain.ReadStatus((nint)part);
                Require(route.Failures == 0 && gain.Failures == 0 && part.HasValidRenderedWave, "Production source route failed.");
                string pcm = TimingPcmHash(Path.Combine(directory, file)); int[] positions = NativeTimingPositions.Read(note, false);
                var rows = actual.Values.OrderBy(r => r.Begin).ToArray();
                referencePcm ??= pcm; referencePositions ??= positions; referenceRows ??= rows;
                Require(positions.SequenceEqual(referencePositions), "Coverage changed actual phoneme boundaries.");
                Require(JsonSerializer.Serialize(rows) == JsonSerializer.Serialize(referenceRows), "Coverage changed actual ART/STA allocation, material or sustain source start.");
                if (mode == "restored") Require(RepeatEqual(Path.Combine(directory, $"phone{phoneEnd}-art{transitionEnd}-native.wav"),
                    Path.Combine(directory, file)), "Coverage reset exceeded the independently measured native repeat envelope.");
                if (mode == "coverage" && (phoneEnd != 0 || transitionEnd != 0)) Require(pcm != referencePcm, "Complete source did not change shortened articulation.");
                Require(rows.Any(r => r.To == -1 && r.Direct != 0), "No direct native sustain continuation was exercised.");
                Record(new { kind = "case", phoneEnd, transitionEnd, mode, pcm, positions, planMs, execution.NativePlans,
                    cacheHit = cached.CacheHit, gain.Applied, gain.Failures, sourceFirst = BitConverter.Int32BitsToSingle(row.Frames[row.Split - row.Begin].SourceBits),
                    sourceLast = BitConverter.Int32BitsToSingle(row.Frames[^1].SourceBits), actual = rows, file });
                Console.WriteLine("coverage=" + file);
            }
        }
        NativeSampledTiming.Remove((nint)part);
        using (var shortFixture = CreateStandaloneFixture(manager, 1, 240, 0, 7680))
        {
            var shortPart = shortFixture.Part; var shortNote = shortFixture.Notes[0]; var shortSource = shortFixture.Sequence;
            shortPart.SetVoiceBankID("BY98KLLZTDYH7YEE"); shortPart.MoveNote(new VSMRelTick(960), shortNote);
            shortNote.SetNoteNumber(60); shortNote.SetPhonemes("s\\ iE_r", true, 4); shortSource.InterpolatedMasterVolume(); shortSource.Commit(false);
            using var shortPrepared = backend.PrepareCoefficient(shortNote, 2);
            var shortLayout = shortPrepared.QueryTransitions(new("s\\ iE_r", []), 2);
            var shortEdge = shortLayout.Edges.Single(e => e.Identity.From == "s\\" && e.Identity.To == "iE_r");
            Require(BitConverter.Int32BitsToSingle(shortEdge.Original.Frames[^1].SourceBits) < shortEdge.Original.MaterialCoverage!.Right!.Last,
                "Short fixture did not exercise an already cropped original reference.");
            int[]? originalPositions = null; string? originalPcm = null; DurationRow[]? originalRows = null;
            foreach (string mode in new[] { "native", "coverage", "early", "restored" })
            {
                var controls = new TransitionControlPlan(shortLayout.Fingerprint);
                if (mode is "coverage" or "early") controls = controls.WithCoverage(shortEdge.Identity, TransitionSourceSides.Right);
                if (mode == "early") controls = controls.WithPhase(shortEdge.Identity, -.75f);
                var execution = shortPrepared.QueryTransitionControls(shortLayout, controls, 2);
                shortPrepared.ValidateTransitionCommit(shortPart, execution, 2);
                shortPrepared.PublishTransitions(shortPart, ++epoch, execution, 2);
                SampledTimingVsmBinding.InvalidateRenderedPart(shortPart, [shortNote]);
                var actual = new Dictionary<int, DurationRow>(); int consumed = 0;
                capture.Reset(-1, false, null);
                capture.AfterStep = state =>
                {
                    int cursor = Marshal.ReadInt32(state, LocalTimingNative.Profile.StateCurrentFrame);
                    foreach (var item in ReadDurationRows(state, materials, consumed, cursor)) actual[item.Begin] = item;
                    consumed = cursor;
                };
                string file = "short-note-" + mode + ".wav";
                try { RenderTransitionWithScore(shortSource, shortPart, Path.Combine(directory, file)); capture.Check(); }
                finally { capture.AfterStep = null; }
                var positions = NativeTimingPositions.Read(shortNote, false); originalPositions ??= positions;
                string pcm = TimingPcmHash(Path.Combine(directory, file)); originalPcm ??= pcm;
                var rows = actual.Values.OrderBy(r => r.Begin).ToArray(); originalRows ??= rows;
                Require(positions.SequenceEqual(originalPositions) && JsonSerializer.Serialize(rows) == JsonSerializer.Serialize(originalRows),
                    "Short-note coverage changed timing, material or sustain continuation.");
                Require(NativeSampledTiming.ReadStatus((nint)shortPart).Failures == 0 && NativeTransitionGain.ReadStatus((nint)shortPart).Failures == 0,
                    "Short-note coverage execution failed.");
                var art = execution.Segments.Where(s => s.To != null).ElementAt(shortLayout.Edges.IndexOf(shortEdge));
                if (!controls.Coverages.IsEmpty) Require(pcm != originalPcm && BitConverter.Int32BitsToSingle(art.Frames[^1].SourceBits) ==
                    shortEdge.Original.MaterialCoverage!.Right!.Last, "Short note still used the cropped reference.");
                if (mode == "restored") Require(RepeatEqual(Path.Combine(directory, "short-note-native.wav"), Path.Combine(directory, file)), "Short-note reset changed audio.");
                Record(new { kind = "short-note", mode, pcm, positions, sourceLast = BitConverter.Int32BitsToSingle(art.Frames[^1].SourceBits), actual = rows, file });
                Console.WriteLine("coverage=" + file);
            }
            NativeSampledTiming.Remove((nint)shortPart);
        }
        Record(new { kind = "acceptance", renders = 36, fixedTiming = true, sustainAllocationAndSourceStartUnchanged = true, restoredWithinNativeRepeatEnvelope = true });
        return 0;
    }
}
