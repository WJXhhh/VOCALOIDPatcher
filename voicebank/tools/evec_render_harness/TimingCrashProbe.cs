using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using VOCALOIDPatcher.ConsonantTiming;
using VOCALOIDPatcher.DurationSelection;
using Yamaha.VOCALOID.DSE;
using Yamaha.VOCALOID.VDM;
using Yamaha.VOCALOID.VSM;

internal static partial class Program
{
    private static int ProbeTimingCrash(WIVSMSequenceManager manager, DSEManager dse, DatabaseManager database, string directory)
    {
        Directory.CreateDirectory(directory);
        string library = Environment.GetEnvironmentVariable("VOCALOID_TIMING_PROBE_NATIVE")!;
        if (Environment.GetEnvironmentVariable("VOCALOID_CRASH_DURATION_HOOKS") != "0") NativeDurationSelection.Initialize(library);
        NativePhonemeTiming.Initialize(library); NativeSampledTiming.Initialize(library);
        using var backend = new LocalConsonantTimingBackend(manager, dse, database);
        using var capture = new TimingCoefficientHook();
        var results = new List<object>(); ulong epoch = 9000;
        int[] offsets = (Environment.GetEnvironmentVariable("VOCALOID_CRASH_OFFSETS") ?? "-80,-40,-12,0,12,40,80")
            .Split(',').Select(int.Parse).ToArray();
        string[] scenarios = (Environment.GetEnvironmentVariable("VOCALOID_CRASH_SCENARIOS") ?? "vowel,connected,multi").Split(',');
        string bank = Environment.GetEnvironmentVariable("VOCALOID_CRASH_BANK") ?? "BL8CEAM5N4XN3LFK";
        bool splitInput = Environment.GetEnvironmentVariable("VOCALOID_CRASH_SPLIT") == "1";
        bool injectPadding = Environment.GetEnvironmentVariable("VOCALOID_CRASH_PADDING") == "1";
        Require(!injectPadding || splitInput, "Padding replay requires split input observation.");
        foreach (string scenario in scenarios)
        {
            using var fixture = CreateStandaloneFixture(manager, scenario == "vowel" ? 1 : 3, 300, 0, 1872);
            fixture.Part.SetVoiceBankID(bank);
            string[] phones = scenario == "multi" ? ["k uo", "x 7", "t u"] : ["a", "a", "a"];
            for (int n = 0; n < fixture.Notes.Length; n++)
            {
                fixture.Part.MoveNote(new VSMRelTick(600 + n * 300), fixture.Notes[n]);
                fixture.Notes[n].SetPhonemes(phones[n], true, bank == "BL8CEAM5N4XN3LFK" ? 4 : 0);
                fixture.Notes[n].SetNoteNumber(n == 2 ? 66 : 67);
            }
            fixture.Sequence.InterpolatedMasterVolume(); fixture.Sequence.Commit(false);
            var target = fixture.Notes[^1];
            int terminal = target.Phonemes.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length;
            using var prepared = backend.PrepareCoefficient(target, 1);
            foreach (int head in offsets)
            foreach (int tail in offsets)
            {
                var edits = new List<TimingAdjustment>();
                if (head != 0) edits.Add(new(TimingAdjustment.PhonemeBoundary, 0, (float)(head * 256d / 44100)));
                if (tail != 0) edits.Add(new(TimingAdjustment.PhonemeBoundary, terminal, (float)(tail * 256d / 44100)));
                var plan = new SampledTimingPlan(target.Phonemes, edits);
                LocalConsonantTimingBackend.PhonemeLayout layout;
                try { layout = prepared.QueryPhonemes(plan, 1); }
                catch (NotSupportedException e) { results.Add(new { scenario, head, tail, rejected = e.Message }); continue; }
                var item = new { scenario, head, tail, layout.Anchors, layout.Result.Boundaries };
                File.WriteAllText(Path.Combine(directory, "last-case.json"), JsonSerializer.Serialize(item));
                NativeSampledTiming.Publish(fixture.Part, ++epoch, new Dictionary<nint, float>(), layout.Anchors);
                SampledTimingVsmBinding.InvalidateRenderedPart(fixture.Part, fixture.Notes);
                capture.Reset(-1, splitInput, null);
                bool paddingInjected = false;
                var paddingFrames = new List<int>();
                var sampleBoundaries = new HashSet<int>(); int consumed = 0;
                var actualRows = new Dictionary<int, DurationRow>();
                DurationRow[] pendingRows = [];
                var materials = new Dictionary<nint, int>();
                capture.AfterStep = state =>
                {
                    int cursor = Marshal.ReadInt32(state, 0x24158);
                    foreach (var row in ReadDurationRows(state, materials, consumed, cursor))
                    {
                        actualRows[row.Begin] = row;
                        sampleBoundaries.Add(row.Begin); sampleBoundaries.Add(row.End); sampleBoundaries.Add(row.Begin + row.LeftLength);
                    }
                    consumed = cursor;
                };
                capture.BeforeSynthesis = state =>
                {
                    pendingRows = ReadDurationRows(state, materials);
                    if (!paddingInjected && injectPadding)
                    {
                        nint schedule = Marshal.ReadIntPtr(state, 0x1d568);
                        int end = TimingScheduleEnd(schedule);
                        if (end >= layout.Anchors[^2].To)
                        {
                            paddingInjected = true;
                            for (int padding = 0; padding < 8; padding++)
                            {
                                int beforeEnd = TimingScheduleEnd(schedule);
                                FeedEmptyTimingClock(state, beforeEnd);
                                paddingFrames.Add(TimingScheduleEnd(schedule) - beforeEnd);
                                if (FindUnmappedTimingJunctions(state).Length != 0) break;
                            }
                        }
                    }
                    var invalid = FindUnmappedTimingJunctions(state);
                    if (invalid.Length != 0)
                    {
                        File.WriteAllText(Path.Combine(directory, "invalid-junctions.json"), JsonSerializer.Serialize(new { item, invalid }, new JsonSerializerOptions { WriteIndented = true }));
                        throw new InvalidOperationException("Reproduced unmapped native timing junction.");
                    }
                };
                string? error = null;
                try { RenderTransitionWithScore(fixture.Sequence, fixture.Part, Path.Combine(directory, "case.wav"), blockRendering: true); capture.Check(); }
                catch (Exception e)
                {
                    // VSM reports a canceled render when the native observer
                    // rejects an invalid schedule. Preserve that observer's
                    // exact failure instead of only recording cancellation.
                    try { capture.Check(); error = e.ToString(); }
                    catch (Exception probeError) { error = probeError.ToString(); }
                }
                capture.BeforeSynthesis = null;
                capture.AfterStep = null;
                var status = NativeSampledTiming.ReadStatus((nint)fixture.Part);
                // A short SDK fixture may finish rendering before a delayed
                // release is consumed. Compare the final allocated schedule,
                // and report consumed coverage separately rather than calling
                // an unconsumed (but correctly allocated) boundary a mismatch.
                var plannedBoundaries = pendingRows.SelectMany(r => new[] { r.Begin, r.End, r.Begin + r.LeftLength }).ToHashSet();
                var phonemeAnchors = layout.Anchors.Skip(1).Take(layout.Anchors.Length - 2).ToArray();
                bool boundariesMatch = phonemeAnchors.All(p => plannedBoundaries.Contains(p.To));
                bool consumedBoundariesMatch = phonemeAnchors.All(p => sampleBoundaries.Contains(p.To));
                bool paddingMatches = !injectPadding || (paddingInjected && paddingFrames.Count == 8 && paddingFrames.All(n => n == 1));
                string? hash = error == null ? TimingPcmHash(Path.Combine(directory, "case.wav")) : null;
                results.Add(new { item, error, status.Failures, status.Scopes, boundariesMatch, consumedBoundariesMatch, paddingInjected, paddingFrames, paddingMatches, hash,
                    consumed, pendingRows, actual = actualRows.Values.OrderBy(r => r.Begin).ToArray() });
                File.WriteAllText(Path.Combine(directory, "crash-matrix.json"), JsonSerializer.Serialize(results, new JsonSerializerOptions { WriteIndented = true }));
                Console.WriteLine($"crash-case={scenario}/{head}/{tail}; error={error != null}; failures={status.Failures}; boundaries={boundariesMatch}; padding={paddingFrames.Count}");
                NativeSampledTiming.Remove((nint)fixture.Part);
                if (error != null || status.Failures != 0 || !boundariesMatch || !paddingMatches) return 1;
            }
        }
        File.WriteAllText(Path.Combine(directory, "crash-matrix.json"), JsonSerializer.Serialize(results, new JsonSerializerOptions { WriteIndented = true }));
        return 0;
    }

    private static unsafe int TimingScheduleEnd(nint schedule) =>
        ((delegate* unmanaged<nint, int>)(LocalTimingNative.Dse + 0x18aba0))(schedule);

    private static unsafe void FeedEmptyTimingClock(nint state, int frame)
    {
        long* empty = stackalloc long[2]; empty[0] = empty[1] = 0;
        ((delegate* unmanaged<nint, int, nint, void>)(LocalTimingNative.Dse + 0x1b2ab0))(state + 0x1d570, frame, (nint)empty);
    }

    private static unsafe object[] FindUnmappedTimingJunctions(nint state)
    {
        nint schedule = Marshal.ReadIntPtr(state, 0x1d568);
        int capacity = Marshal.ReadInt32(schedule, 0x28), count = Marshal.ReadInt32(schedule, 0x34);
        if (capacity is < 1 or > 4096 || count < 0 || count > capacity) throw new InvalidDataException("Unbounded native segment table.");
        nint rows = Marshal.ReadIntPtr(schedule, 0x50);
        int endIndex = Marshal.ReadInt32(schedule, 0x60), current = Marshal.ReadInt32(state, 0x24158);
        var invalid = new List<object>();
        for (int n = 0; n < count; n++)
        {
            int index = (endIndex - count + n + capacity) % capacity;
            nint row = rows + index * 0x140;
            int begin = Marshal.ReadInt32(row), end = Marshal.ReadInt32(row, 4);
            if (begin < current || begin < 3087 || Marshal.ReadByte(row, 0x98) == 0 || Marshal.ReadByte(row, 0xe4) != 0) continue;
            nint at = ((delegate* unmanaged<nint, int, nint>)(LocalTimingNative.Dse + 0x18b260))(schedule, begin);
            if (Marshal.ReadIntPtr(at) == 0)
                invalid.Add(new { current, index, begin, end, from = Marshal.ReadInt32(row, 16), to = Marshal.ReadInt32(row, 20), hasSource = Marshal.ReadIntPtr(row, 0x20) != 0 });
        }
        return invalid.ToArray();
    }
}
