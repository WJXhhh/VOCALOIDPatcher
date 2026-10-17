using System.IO;
using System.Text.Json;
using VOCALOIDPatcher.ConsonantTiming;
using Yamaha.VOCALOID.DSE;
using Yamaha.VOCALOID.VDM;
using Yamaha.VOCALOID.VSM;

internal static partial class Program
{
    private static StandaloneFixture OpenSavedTimingFixture(WIVSMSequenceManager manager, string path, string directory)
    {
        string projectDirectory = Path.Combine(directory, "source-project");
        Directory.CreateDirectory(projectDirectory);
        using var zip = System.IO.Compression.ZipFile.OpenRead(path);
        var entry = zip.GetEntry("Project/sequence.json") ?? throw new InvalidDataException("Missing sequence JSON.");
        Require(entry.Length <= 4 * 1024 * 1024, "Timing fixture exceeds its size bound.");
        string jsonPath = Path.Combine(projectDirectory, "sequence.json");
        using (var input = entry.Open())
        using (var output = File.Create(jsonPath)) input.CopyTo(output);
        var source = manager.OpenSequence(jsonPath, EditorDirectory,
            new VSMSequenceData { SamplingRate = VSMSamplingRate._44100, MaxNumTracks = 4, MaxUndoCount = 100 })
            ?? throw new InvalidDataException("Cannot open timing fixture.");
        try
        {
            var part = (WIVSMMidiPart)((WIVSMMidiTrack)source.GetTrack(0UL)!).GetPart(0UL)!;
            Require(part.NumNotes == 1, "Expected a one-note timing fixture.");
            return new StandaloneFixture(source, part, part.NotesEnumerator.ToArray());
        }
        catch { source.Close(); throw; }
    }

    private static int ProbePhonemeExtreme(WIVSMSequenceManager manager, DSEManager dse, DatabaseManager database, string directory)
    {
        Directory.CreateDirectory(directory);
        string nativePath = Environment.GetEnvironmentVariable("VOCALOID_TIMING_PROBE_NATIVE")!;
        NativePhonemeTiming.Initialize(nativePath);
        NativeSampledTiming.Initialize(nativePath);
        bool vowel = Environment.GetEnvironmentVariable("VOCALOID_EXTREME_VOWEL") == "1";
        int axis = int.TryParse(Environment.GetEnvironmentVariable("VOCALOID_EXTREME_AXIS"), out int axisValue) ? axisValue : 0;
        int direction = Environment.GetEnvironmentVariable("VOCALOID_EXTREME_DIRECTION") == "right" ? 1 : -1;
        int otherOffset = int.TryParse(Environment.GetEnvironmentVariable("VOCALOID_EXTREME_OTHER"), out int otherValue) ? otherValue : 0;
        using var fixture = CreateStandaloneFixture(manager, 1, vowel ? 480 : 540, 0, 7680);
        var source = fixture.Sequence; var part = fixture.Part; var note = fixture.Notes[0];
        part.SetVoiceBankID("BL8CEAM5N4XN3LFK");
        part.MoveNote(new VSMRelTick(960), note);
        note.SetNoteNumber(vowel ? 60 : 61); note.NoteVelocity = vowel ? (byte)64 : (byte)38;
        note.SetPhonemes(vowel ? "a" : "s\\ i_n n", true, 4);
        source.InterpolatedMasterVolume(); source.Commit(false);
        RenderTransitionWithScore(source, part, Path.Combine(directory, "baseline.wav"));
        string baselineHash = TimingPcmHash(Path.Combine(directory, "baseline.wav"));
        Console.WriteLine("extreme.baseline=" + JsonSerializer.Serialize(note.GetPhonemePositions()));
        using var backend = new LocalConsonantTimingBackend(manager, dse, database);
        using var counters = new TimingDseCounters();
        using var scheduleProbe = new MetadataTimingReplay(counters.VtableRva);
        var results = new List<object>();
        ulong epoch = 0;
        int firstMagnitude = int.TryParse(Environment.GetEnvironmentVariable("VOCALOID_EXTREME_FIRST"), out int firstValue) ? firstValue : 2;
        int lastMagnitude = int.TryParse(Environment.GetEnvironmentVariable("VOCALOID_EXTREME_LAST"), out int lastValue) ? lastValue : 120;
        for (int magnitude = firstMagnitude; magnitude <= lastMagnitude; magnitude += 2)
        {
            int offset = direction * magnitude;
            using var prepared = backend.PrepareCoefficient(note, 1);
            var edits = new List<TimingAdjustment> { new(TimingAdjustment.PhonemeBoundary, axis, (float)(offset * 256d / 44100)) };
            if (otherOffset != 0) edits.Add(new(TimingAdjustment.PhonemeBoundary, 1 - axis, (float)(otherOffset * 256d / 44100)));
            var plan = new SampledTimingPlan(note.Phonemes, edits);
            LocalConsonantTimingBackend.PhonemeLayout layout;
            var before = counters.Read();
            try { layout = prepared.QueryPhonemes(plan, 1); }
            catch (NotSupportedException e)
            {
                Console.WriteLine("extreme.limit=" + JsonSerializer.Serialize(new { axis, offset, e.Message }));
                break;
            }
            var queryCounters = counters.Read().Since(before);
            Require(queryCounters.Starts == 0 && queryCounters.Steps == 0 && queryCounters.Samples == 0,
                "Extreme timing query synthesized audio.");
            SampledTimingVsmBinding.InvalidateRenderedPart(part, [note]);
            NativeSampledTiming.Publish(part, ++epoch, new Dictionary<nint, float>(), layout.Anchors);
            scheduleProbe.Capture();
            string? error = null;
            try { RenderTransitionWithScore(source, part, Path.Combine(directory, $"offset{offset}.wav")); }
            catch (Exception e) { error = e.Message; }
            var status = NativeSampledTiming.ReadStatus((nint)part);
            var actual = NativeTimingPositions.Read(note, false);
            scheduleProbe.Check();
            string path = Path.Combine(directory, $"offset{offset}.wav");
            bool audioChanged = File.Exists(path) && TimingPcmHash(path) != baselineHash && ReadWaveMetrics(path).Peak > .00001;
            bool rawMatches = actual.Select(v => (long)v).SequenceEqual(layout.Result.Boundaries);
            int[] expectedSampleFrames = layout.Anchors.Skip(1).Take(layout.Anchors.Length - 2).Select(p => p.To).ToArray();
            bool sampleBoundariesMatch = expectedSampleFrames.All(scheduleProbe.SampleBoundaries.Contains);
            bool needsProjection = vowel && !rawMatches && sampleBoundariesMatch;
            var state = new { axis, offset, otherOffset, predicted = layout.Result.Boundaries, actual, needsProjection,
                expectedSampleFrames, actualSampleFrames = scheduleProbe.SampleBoundaries.Order().ToArray(), sampleBoundariesMatch,
                anchors = layout.Anchors, status.Epoch, status.Scopes, status.Applied, status.Failures, error,
                audioChanged, queryCounters, layout.Result.Milliseconds };
            Console.WriteLine("extreme.render=" + JsonSerializer.Serialize(state));
            results.Add(state);
            File.WriteAllText(Path.Combine(directory, "extreme.json"), JsonSerializer.Serialize(results, new JsonSerializerOptions { WriteIndented = true }));
            if (status.Failures != 0 || status.Scopes == 0 || error != null || !audioChanged ||
                !sampleBoundariesMatch || !(needsProjection || rawMatches))
            {
                File.WriteAllLines(Path.Combine(directory,"failed-schedule.csv"),scheduleProbe.Records);
                File.WriteAllLines(Path.Combine(directory,"failed-queued-schedule.csv"),scheduleProbe.EarlyRecords);
                Console.WriteLine("extreme.cursor="+NativeSampledTiming.PhonemeDiagnostic((nint)part));
                NativeSampledTiming.Remove((nint)part);
                return 1;
            }
            NativeSampledTiming.Remove((nint)part);
        }
        return 0;
    }
}
