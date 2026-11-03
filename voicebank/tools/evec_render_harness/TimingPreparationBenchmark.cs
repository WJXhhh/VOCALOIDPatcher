using System.Diagnostics;
using System.IO;
using System.Text.Json;
using VOCALOIDPatcher.ConsonantTiming;
using Yamaha.VOCALOID.DSE;
using Yamaha.VOCALOID.VDM;
using Yamaha.VOCALOID.VSM;

internal static partial class Program
{
    // Owns metadata-only sources in an isolated process. Measure the entire
    // prepare/query/dispose cycle, including native metadata destruction.
    private static int BenchmarkTimingPreparation(WIVSMSequenceManager manager, DSEManager dse,
        DatabaseManager database, string directory)
    {
        Directory.CreateDirectory(directory);
        NativeTransitionGain.Initialize(Environment.GetEnvironmentVariable("VOCALOID_TIMING_PROBE_NATIVE")!);
        using var counters = new TimingDseCounters();
        var before = counters.Read();
        var results = new List<object>();
        ValidateControllerRemovalOwner(manager);
        ValidateMappingShellLifecycle(manager, dse, database);
        using (var backend = new LocalConsonantTimingBackend(manager, dse, database))
        {
            foreach (int count in new[] { 1, 3, 9 })
            foreach (string bank in new[] { "BCNFCY43LB2LZCD4", "BKKP765AEHXWSKDB" })
            {
                using var fixture = CreateStandaloneFixture(manager, count, 480, 0, 7680);
                fixture.Part.SetVoiceBankID(bank);
                for (int i = 0; i < count; i++)
                {
                    fixture.Part.MoveNote(new VSMRelTick(960 + i * 480), fixture.Notes[i]);
                    fixture.Notes[i].SetPhonemes("k a", true, 0);
                }
                fixture.Sequence.Commit(false);
                var target = fixture.Notes[count / 2];
                var samples = new List<object>();
                for (int iteration = 0; iteration < 4; iteration++)
                {
                    // Change context to detect stale scores, notes, banks and sizes.
                    target.SetNoteNumber(60 + iteration);
                    fixture.Sequence.Commit(false);
                    Console.WriteLine($"preparation.count={count};bank={bank};iteration={iteration}");
                    var timer = Stopwatch.StartNew();
                    var preparation = backend.PrepareCoefficient(target, iteration);
                    double prepareMs = timer.Elapsed.TotalMilliseconds;
                    var transition = preparation.QueryTransitions(new(target.Phonemes, []), iteration);
                    var a = preparation.Query(.5f, iteration).Boundaries;
                    var b = preparation.Query(.65f, iteration).Boundaries;
                    double queryMs = timer.Elapsed.TotalMilliseconds - prepareMs;
                    preparation.Dispose();
                    double totalMs = timer.Elapsed.TotalMilliseconds;
                    samples.Add(new { iteration, prepareMs, queryMs, disposeMs = totalMs - prepareMs - queryMs,
                        totalMs, a, b, segments = transition.Edges.Select(e => new { e.Original.Begin,
                            e.Original.Split, e.Original.End, e.Original.From, e.Original.To,
                            frames = e.Original.Frames.ToArray() }).ToArray() });
                    if (target.NoteNumber != 60 + iteration || fixture.Sequence.IsStaged)
                        throw new InvalidOperationException("Preparation changed the source.");
                }
                results.Add(new { count, bank, samples });
            }
            using var simultaneous = CreateStandaloneFixture(manager, 9, 480, 0, 7680);
            var held = new List<LocalConsonantTimingBackend.PreparedCoefficientTiming>();
            try
            {
                for (int i = 0; i < 4; i++) held.Add(backend.PrepareCoefficient(simultaneous.Notes[4], 100));
                var expected = held[0].Query(.5f, 100).Boundaries;
                if (held.Skip(1).Any(p => !p.Query(.5f, 100).Boundaries.SequenceEqual(expected)))
                    throw new InvalidOperationException("Simultaneous preparations share mutable mappings.");
                held[0].Dispose(); held[0].Dispose();
                using var replacement = backend.PrepareCoefficient(simultaneous.Notes[4], 100);
                if (!replacement.Query(.5f, 100).Boundaries.SequenceEqual(expected) ||
                    held.Skip(1).Any(p => !p.Query(.5f, 100).Boundaries.SequenceEqual(expected)))
                    throw new InvalidOperationException("Recycled mappings alias a live preparation.");
                backend.ClearCache();
                using var fresh = backend.PrepareCoefficient(simultaneous.Notes[4], 101);
                if (!fresh.Query(.5f, 101).Boundaries.SequenceEqual(expected))
                    throw new InvalidOperationException("Invalidated mappings cannot be reused.");
            }
            finally { foreach (var preparation in held) preparation.Dispose(); }
        }
        var delta = counters.Read().Since(before);
        if (delta.Steps != 0 || delta.Samples != 0)
            throw new InvalidOperationException("Metadata benchmark synthesized audio.");
        File.WriteAllText(Path.Combine(directory, "timing-preparation.json"),
            JsonSerializer.Serialize(new { results, overlappingPreparations = 4, doubleDispose = true,
                clearCacheReuse = true, controllerRemovalScope = true, mappingShellCycles = 96,
                reentrantDispose = true, dse = delta }, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine("timing.preparation_benchmark=passed;cases=24;pcm_steps=0");
        return 0;
    }

    private static void ValidateControllerRemovalOwner(WIVSMSequenceManager manager)
    {
        using var fixture = CreateStandaloneFixture(manager, 1, 480, 0, 7680);
        var other = fixture.Part.Parent!.InsertPart(new VSMAbsTick(9600), new VSMRelTick(1920), "scope")!;
        var controller = fixture.Part.InsertController(new VSMRelTick(900), VSMControllerType.Dynamics, 64)!;
        fixture.Sequence.Commit(false);
        var scope = new NativeInputInvalidation();
        using var observer = new WIVSMUpdateObserver();
        int removed = 0;
        observer.Notified += update =>
        {
            foreach (var value in update.Objects)
                if (value is WIVSMMidiController item && update.NotifyType == VSMNotifyType.DidRemove)
                {
                    // The notification can only safely expose the old handle.
                    scope.ControllerRemoved((nint)item, [(nint)fixture.Part, (nint)other]);
                    removed++;
                }
        };
        if (!fixture.Sequence.AddUpdateObserver(observer)) throw new InvalidOperationException("Cannot observe removal.");
        try
        {
            scope.BeforeControllerRemoval((nint)controller, (nint)fixture.Part);
            if (!fixture.Part.RemoveController(controller) || !fixture.Sequence.Commit(false))
                throw new InvalidOperationException("Controller removal failed.");
            scope.EndMutation();
            if (removed != 1 || !scope.DirtyParts.SetEquals([(nint)fixture.Part]))
                throw new InvalidOperationException("Native removal handle lost its Part owner.");
        }
        finally { fixture.Sequence.RemoveUpdateObserver(observer); }
    }
}
