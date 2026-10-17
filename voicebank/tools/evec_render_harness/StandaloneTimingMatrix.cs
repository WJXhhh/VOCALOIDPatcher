using VOCALOIDPatcher.ConsonantTiming;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using Yamaha.VOCALOID.DSE;
using Yamaha.VOCALOID.VDM;
using Yamaha.VOCALOID.VSM;

internal static partial class Program
{
    private static int ProbeStandaloneTiming(WIVSMSequenceManager manager, DSEManager dse,
        DatabaseManager database, string directory, string? caseFilter)
    {
        Directory.CreateDirectory(directory);
        using var counters = new TimingDseCounters();
        var cold = Stopwatch.StartNew();
        using var backend = new LocalConsonantTimingBackend(manager, dse, database);
        double initializationMs = cold.Elapsed.TotalMilliseconds;
        var fixtures = new List<TimingFixture>
        {
            new TimingFixture("alone", ["k a"], [960], [480], 0),
            new TimingFixture("three", ["a", "k a", "n a"], [480, 960, 1440], [480, 480, 480], 1),
            new TimingFixture("previous", ["a", "k a"], [480, 960], [480, 480], 1),
            new TimingFixture("following", ["k a", "n a"], [960, 1440], [480, 480], 0),
            new TimingFixture("five", ["h e", "r M", "a", "k a", "n a"], [0, 480, 960, 1440, 1920], [480, 480, 480, 480, 480], 3),
            new TimingFixture("gap", ["a", "k a", "n a"], [480, 1080, 1800], [360, 480, 360], 1),
            new TimingFixture("short", ["a", "k a", "n a"], [480, 600, 720], [120, 120, 120], 1),
            new TimingFixture("evec", ["a", "k k k k#6 a", "n a"], [480, 960, 1440], [480, 480, 480], 1),
            new TimingFixture("shift_tempo", ["a", "k a", "n a"], [480, 960, 1440], [480, 480, 480], 1,
                PartStart: 3840, TempoChangeTick: 910),
            new TimingFixture("rin", ["a", "k k k a", "n a"], [480, 960, 1440], [480, 480, 480], 1,
                Bank: "BKKP765AEHXWSKDB"),
            new TimingFixture("repeat", ["a", "k a", "a", "k a", "a", "k a", "a"],
                [480, 960, 1440, 1920, 2400, 2880, 3360], [480, 480, 480, 480, 480, 480, 480], 3),
            new TimingFixture("chain100", Enumerable.Range(0, 100).Select(i => i % 3 == 0 ? "k a" : i % 3 == 1 ? "a" : "n a").ToArray(),
                Enumerable.Range(0, 100).Select(i => 480 + i * 120).ToArray(), Enumerable.Repeat(120, 100).ToArray(), 50),
            new TimingFixture("release", ["a", "k k#2 a a#2 *#1", "n a"], [480, 960, 1440], [480, 480, 480], 1),
            new TimingFixture("controllers", ["a", "k a", "n a"], [480, 960, 1440], [480, 480, 480], 1,
                ControllerVariant: true),
        };
        var random = new Random(20261016);
        string[] phones = ["a", "k a", "n a", "h e", "r M", "s a", "dZ M", "a i"];
        for (int variant = 0; variant < 24; variant++)
        {
            int count = random.Next(3, 10), tick = random.Next(0, 480);
            int[] positions = new int[count], durations = new int[count];
            for (int i = 0; i < count; i++)
            {
                positions[i] = tick;
                durations[i] = random.Next(60, 361);
                tick += durations[i] + (random.Next(4) == 0 ? random.Next(1, 180) : 0);
            }
            fixtures.Add(new TimingFixture($"fuzz_{variant}",
                Enumerable.Range(0, count).Select(_ => phones[random.Next(phones.Length)]).ToArray(),
                positions, durations, random.Next(count), Bank: variant % 3 == 0 ? "BKKP765AEHXWSKDB" : "BCNFCY43LB2LZCD4",
                PartStart: variant % 2 == 0 ? 17003 : 0,
                TempoChangeTick: variant % 2 == 0 ? positions[count / 2] + 13 : -1,
                InitialTempo: new[] { 2000, 7311, 12000, 30000 }[variant % 4],
                ChangedTempo: new[] { 2000, 30000, 9437 }[variant % 3], ExpressionSeed: variant + 1));
        }
        var results = new List<object>();
        foreach (var fixture in fixtures.Where(fixture => caseFilter == null || fixture.Name == caseFilter))
        {
            backend.ClearCache();
            var sequence = manager.CreateSequence(new VSMSequenceData
            { SamplingRate = VSMSamplingRate._44100, MaxNumTracks = 4, MaxUndoCount = 0 })!;
            try
            {
                sequence.InterpolatedMasterVolume();
                sequence.InsertTimeSig(0, new VSMTimeSigEvent(4, 4));
                sequence.InsertTempo(VSMRelTick.Zero, fixture.InitialTempo);
                if (fixture.TempoChangeTick >= 0)
                    sequence.InsertTempo(new VSMRelTick(fixture.PartStart + fixture.TempoChangeTick), fixture.ChangedTempo);
                var track = (WIVSMMidiTrack)sequence.InsertTrackEx(0, VSMTrackType.Midi, "oracle")!;
                var part = track.InsertPart(new VSMAbsTick(fixture.PartStart),
                    new VSMRelTick(Math.Max(2880, fixture.Positions.Max() + 960)), "oracle")!;
                part.SetVoiceBankID(fixture.Bank);
                WIVSMNote? target = null;
                var expressionRandom = new Random(fixture.ExpressionSeed);
                for (int i = 0; i < fixture.Phonemes.Length; i++)
                {
                    var note = part.InsertNote(new VSMRelTick(fixture.Positions[i]),
                        new VSMNoteEvent(fixture.Durations[i], fixture.ExpressionSeed == 0 ? i == fixture.Target ? 60 : 62 : expressionRandom.Next(48, 73),
                            fixture.ExpressionSeed == 0 || i == fixture.Target ? 64 : expressionRandom.Next(0, 128)),
                        fixture.ExpressionSeed == 0 ? part.GetDefaultNoteExpression() : new VSMNoteExpression(
                            expressionRandom.Next(0, 101), expressionRandom.Next(0, 101), expressionRandom.Next(0, 101),
                            expressionRandom.Next(0, 101), expressionRandom.Next(0, 128), i % 2 == 0, i % 3 == 0),
                        part.GetDefaultAiNoteExpression(), "fixture", fixture.Phonemes[i], true, 0)!;
                    if (i == fixture.Target) target = note;
                }
                if (fixture.ControllerVariant)
                {
                    foreach (var kind in new[] { VSMControllerType.PitchBend, VSMControllerType.PitchBendSens,
                                 VSMControllerType.Portamento, VSMControllerType.Breathiness, VSMControllerType.Brightness })
                    {
                        int value = kind == VSMControllerType.PitchBend ? 4096 : kind == VSMControllerType.PitchBendSens ? 12 : 100;
                        part.InsertController(new VSMRelTick(0), kind, value);
                        part.InsertController(new VSMRelTick(1100), kind, value / 2);
                    }
                }
                sequence.Commit(false);
                foreach (int velocity in fixture.ExpressionSeed == 0 ? new[] { 64, 32, 96, 0, 127 } : new[] { 0, 64, 127 })
                {
                    var before = counters.Read();
                    int originalVelocity = target!.NoteVelocity;
                    var oldPositions = target.GetPhonemePositions().ToArray();
                    bool staged = sequence.IsStaged, scoreValid = part.HasValidRenderedScore;
                    LocalConsonantTimingResult prediction;
                    try { prediction = backend.Query(target, velocity); }
                    catch when (caseFilter != null)
                    {
                        Console.WriteLine($"standalone.failure_fixture={JsonSerializer.Serialize(fixture)}");
                        target.NoteVelocity = velocity; sequence.Commit(false);
                        RenderTransitionWithScore(sequence, part, Path.Combine(directory, "failure-oracle.wav"));
                        Console.WriteLine($"standalone.failure_oracle={string.Join(',', target.GetPhonemePositions())}");
                        throw;
                    }
                    var cached = backend.Query(target, velocity);
                    if (!cached.CacheHit || cached.PlannerRuns != 0 || !prediction.Boundaries.SequenceEqual(cached.Boundaries))
                        throw new InvalidOperationException("Cross-query cache failed.");
                    var delta = counters.Read().Since(before);
                    bool sourceUnchanged = target.NoteVelocity == originalVelocity &&
                        target.GetPhonemePositions().SequenceEqual(oldPositions) &&
                        sequence.IsStaged == staged && part.HasValidRenderedScore == scoreValid;
                    if (!sourceUnchanged || delta.Steps != 0 || delta.Samples != 0)
                        throw new InvalidOperationException("Standalone query changed source or synthesized audio.");
                    // Ground truth comes after the first independent query.
                    target.NoteVelocity = velocity;
                    sequence.Commit(false);
                    var referenceBefore = counters.Read();
                    RenderTransitionWithScore(sequence, part, Path.Combine(directory, "last-oracle.wav"));
                    var referenceDse = counters.Read().Since(referenceBefore);
                    if (referenceDse.Steps == 0 || referenceDse.Samples == 0)
                        throw new InvalidOperationException("Reference render did not exercise the PCM counter.");
                    var actual = target.GetPhonemePositions().Select(value => (long)value).ToArray();
                    bool equal = prediction.Boundaries.SequenceEqual(actual);
                    Console.WriteLine($"standalone.fixture={fixture.Name};v={velocity};predicted={string.Join(',', prediction.Boundaries)};actual={string.Join(',', actual)};equal={equal};context={prediction.ContextNotes};runs={prediction.PlannerRuns};ms={prediction.Milliseconds:F3}");
                    results.Add(new { fixture = fixture.Name, velocity, prediction, cached, actual, equal, sourceUnchanged,
                        dse = delta, referenceDse });
                    File.WriteAllText(Path.Combine(directory, "standalone-timing.json"), JsonSerializer.Serialize(
                        new { initializationMs, results }, new JsonSerializerOptions { WriteIndented = true }));
                    if (!equal) throw new InvalidOperationException("Standalone boundary comparison failed.");
                }
            }
            finally { sequence.Close(); }
        }
        return 0;
    }
}
