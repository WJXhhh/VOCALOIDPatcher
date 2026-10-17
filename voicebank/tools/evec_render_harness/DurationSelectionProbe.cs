using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using VOCALOIDPatcher.ConsonantTiming;
using Yamaha.VOCALOID.DSE;
using Yamaha.VOCALOID.VSM;

internal static partial class Program
{
    private sealed record DurationFixture(string Name, string Bank, string Phones, int Language = 0,
        bool Previous = false, int Tempo = 12000);

    // A source ID denotes a material object kept alive by this process's loaded
    // voicebank. It is not an address, a universal bank ID or a PCM hash. A/B/A
    // repeats below check its stability within the fixture before comparison.
    private sealed record DurationMaterial(int Id, int Frames, float Pitch, float Dynamics,
        float RecordedTempo, int[] Alignment);
    private sealed record DurationRow(int Begin, int End, int From, int To, int Stage, int Reverse,
        int Bank, int Unit, DurationMaterial Material, int LeftLength = 0, int SourceStart = 0, byte Direct = 0);

    private static unsafe int ProbeDurationSelection(WIVSMSequenceManager manager, DSEManager dse, string directory)
    {
        LocalTimingNative.VerifyImages();
        Directory.CreateDirectory(directory);
        using var counters = new TimingDseCounters();
        using var capture = new TimingCoefficientHook();
        using var native = new NativeConsonantTimingEngine((nint)dse);
        nint state = (nint)typeof(NativeConsonantTimingEngine).GetField("_state", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(native)!;
        DurationFixture[] fixtures =
        [
            new("miku-ka", "BCNFCY43LB2LZCD4", "k a"),
            new("miku-na", "BCNFCY43LB2LZCD4", "n a"),
            new("miku-a", "BCNFCY43LB2LZCD4", "a"),
            new("miku-kai", "BCNFCY43LB2LZCD4", "k a i"),
            new("miku-ka-connected", "BCNFCY43LB2LZCD4", "k a", Previous: true),
            new("rin-ka", "BKKP765AEHXWSKDB", "k a"),
            new("ling-multi", "BMBNDB8EM5222MK3", "ts\\_h i iE_r @N", 4),
            new("ling-la", "BMBNDB8EM5222MK3", "l a", 4),
            new("miku-ka-fast", "BCNFCY43LB2LZCD4", "k a", Tempo: 24000),
        ];
        string? selected = Environment.GetEnvironmentVariable("VOCALOID_DURATION_FIXTURE");
        if (!string.IsNullOrWhiteSpace(selected)) fixtures = fixtures.Where(f => f.Name == selected).ToArray();
        Require(fixtures.Length > 0, "No duration fixture selected.");
        int[] lengths = [24, 48, 72, 96, 120, 180, 240, 360, 480, 960, 1440, 1920, 480, 24];
        var all = new List<object>();
        foreach (var test in fixtures)
        {
            using var fixture = CreateStandaloneFixture(manager, test.Previous ? 2 : 1, 480, 0, 7680);
            fixture.Part.SetVoiceBankID(test.Bank);
            var target = fixture.Notes[^1];
            fixture.Part.MoveNote(new VSMRelTick(960), target);
            target.SetPhonemes(test.Phones, true, test.Language);
            if (test.Previous) fixture.Notes[0].SetPhonemes("a", true, test.Language);
            if (test.Tempo != 12000) fixture.Sequence.InsertTempo(VSMRelTick.Zero, test.Tempo);
            fixture.Sequence.InterpolatedMasterVolume(); fixture.Sequence.Commit(false);
            var ids = new Dictionary<nint, int>();
            var signatures = new Dictionary<int, string>();
            var rows = new List<object>();
            for (int index = 0; index < lengths.Length; index++)
            {
                int ticks = lengths[index];
                Require(target.SetDuration(new VSMRelTick(ticks)), "Cannot set fixture duration.");
                fixture.Sequence.Commit(false);
                SampledTimingVsmBinding.InvalidateRenderedPart(fixture.Part, fixture.Notes);
                int consumedUntil = 0;
                var live = new Dictionary<int, DurationRow>();
                capture.Reset(-1, false, null);
                capture.AfterStep = liveState =>
                {
                    // Lookahead may still contain a provisional release which
                    // the following NOTE replaces. Only rows actually consumed
                    // by synthesis are evidence of the final sample selection.
                    int cursor = Marshal.ReadInt32(liveState, 0x24158);
                    foreach (var row in ReadDurationRows(liveState, ids, consumedUntil, cursor)) live[row.Begin] = row;
                    consumedUntil = cursor;
                };
                string wave = Path.Combine(directory, $"{test.Name}-{index}-{ticks}.wav");
                var watch = Stopwatch.StartNew();
                RenderTransitionWithScore(fixture.Sequence, fixture.Part, wave);
                double renderMs = watch.Elapsed.TotalMilliseconds;
                capture.Check(); capture.AfterStep = null;
                Require(capture.Inputs.Count > 0, "Duration render reused cached output.");
                Require(ReadWaveMetrics(wave).Peak > .00001, "Duration render is silent.");
                var before = counters.Read();
                watch.Restart();
                DurationRow[] plan;
                native.Begin();
                try
                {
                    foreach (var block in capture.Inputs) native.Plan(block.Frame, block.Bytes);
                    plan = ReadDurationRows(state, ids);
                }
                finally { native.End(); }
                double planningMs = watch.Elapsed.TotalMilliseconds;
                var delta = counters.Read().Since(before);
                Require(delta.Starts == 0 && delta.Steps == 0 && delta.Samples == 0, "Duration planning synthesized audio.");
                string Identity(DurationRow r) => $"{r.From}/{r.To}:{r.Material.Id}:{r.Stage}:{r.Reverse}:{r.Bank}/{r.Unit}";
                // Stationary material is repeated for sustain; its repeat count
                // is not another source choice. Compare consecutive source runs,
                // and retain uncollapsed counts separately for the raw audit.
                string Signature(IEnumerable<DurationRow> data)
                {
                    string? last = null;
                    var runs = new List<string>();
                    foreach (string value in data.OrderBy(r => r.Begin).Select(Identity))
                    {
                        if (value != last) runs.Add(value);
                        last = value;
                    }
                    return string.Join('|', runs);
                }
                string signature = Signature(plan);
                string actual = Signature(live.Values);
                bool matches = signature == actual;
                Require(plan.Length > 0 && matches, $"Duration plan differs from actual selected materials: {test.Name}/{ticks}: {signature} != {actual}");
                bool repeat = signatures.TryGetValue(ticks, out string? earlier);
                Require(!repeat || earlier == signature, "A/B/A material identity was unstable.");
                signatures[ticks] = signature;
                var item = new { index, ticks, milliseconds = fixture.Sequence.GetTimeFromTick(target.AbsPosTick,
                    new VSMAbsTick(target.AbsPosTick.Value + ticks)) * 1000, signature, actual, matches, repeat,
                    plan, actualPlan = live.Values.OrderBy(r => r.Begin).ToArray(),
                    rawRowCountsMatch = plan.Length == live.Count,
                    boundaries = target.GetPhonemePositions(), renderMs, planningMs, counters = delta };
                rows.Add(item);
                Console.WriteLine("duration.row=" + JsonSerializer.Serialize(new { test.Name, ticks, signature, matches, repeat, planningMs }));
                File.WriteAllText(Path.Combine(directory, test.Name + ".json"), JsonSerializer.Serialize(new { fixture = test, rows }, new JsonSerializerOptions { WriteIndented = true }));
            }
            all.Add(new { fixture = test, variants = signatures.Values.Distinct().Count(), rows });
            File.WriteAllText(Path.Combine(directory, "duration-selection.json"), JsonSerializer.Serialize(new { dseSha256 = "7A4965BD377DBC2B08710E807AE1345B67D9FFF6579CA0C01A769F744366F5B5", fixtures = all }, new JsonSerializerOptions { WriteIndented = true }));
        }
        Console.WriteLine("duration.fixtures=" + all.Count);
        return 0;
    }

    private static unsafe DurationRow[] ReadDurationRows(nint state, Dictionary<nint, int> ids,
        int first = 3087, int last = 7183)
    {
        nint schedule = Marshal.ReadIntPtr(state, 0x1d568);
        int end = ((delegate* unmanaged<nint, int>)(LocalTimingNative.Dse + 0x18aba0))(schedule);
        if (end is < 0 or > 7183 || end > Marshal.ReadInt32(schedule, 8))
            throw new InvalidOperationException("Duration plan exceeds bounded context.");
        var seen = new HashSet<nint>();
        var result = new List<DurationRow>();
        for (int frame = Math.Max(3087, first); frame < Math.Min(end, last); frame++)
        {
            nint entry = ((delegate* unmanaged<nint, int, nint>)(LocalTimingNative.Dse + 0x18b260))(schedule, frame);
            nint row = Marshal.ReadIntPtr(entry);
            if (row == 0 || Marshal.ReadInt32(row, 0x11c) < 0 || !seen.Add(row)) continue;
            if (seen.Count > 96) throw new InvalidOperationException("Too many duration sample rows.");
            nint material = Marshal.ReadIntPtr(row, 0x20);
            if (material == 0) throw new InvalidOperationException("Missing selected material.");
            if (!ids.TryGetValue(material, out int id)) ids.Add(material, id = ids.Count);
            nint align = Marshal.ReadIntPtr(material, 0x1d8), finish = Marshal.ReadIntPtr(material, 0x1e0);
            long bytes = finish - align;
            if (bytes is < 0 or > 4096 || bytes % 4 != 0 || (bytes > 0 && align == 0))
                throw new InvalidOperationException("Unexpected duration material alignment.");
            int[] alignment = Enumerable.Range(0, checked((int)bytes / 4)).Select(i => Marshal.ReadInt32(align, i * 4)).ToArray();
            float ReadFloat(int offset) => BitConverter.Int32BitsToSingle(Marshal.ReadInt32(material, offset));
            var source = new DurationMaterial(id, Marshal.ReadInt32(material, 0x1b0), ReadFloat(0x160),
                ReadFloat(0x16c), ReadFloat(0x170), alignment);
            result.Add(new(Marshal.ReadInt32(row), Marshal.ReadInt32(row, 4), Marshal.ReadInt32(row, 0x10),
                Marshal.ReadInt32(row, 0x14), Marshal.ReadInt32(row, 0x28), Marshal.ReadByte(row, 0x12c),
                Marshal.ReadInt32(row, 0x11c), Marshal.ReadInt32(row, 0x120), source,
                Marshal.ReadInt32(row, Marshal.ReadInt32(row, 0x14) == -1 ? 0x118 : 8),
                Marshal.ReadInt32(row, 0x94), Marshal.ReadByte(row, 0x90)));
        }
        return result.ToArray();
    }

}
