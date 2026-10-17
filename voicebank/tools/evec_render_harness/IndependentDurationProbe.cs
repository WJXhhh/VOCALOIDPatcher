using System.IO;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using VOCALOIDPatcher.ConsonantTiming;
using Yamaha.VOCALOID.DSE;
using Yamaha.VOCALOID.VDM;
using Yamaha.VOCALOID.VSM;

internal static partial class Program
{
    // Disposable process only: neither the Editor nor installed binaries are
    // patched. Selection scoring, real-time planning and render-owned histories
    // are tested separately; NOTE data and generated PCM buffers are not rewritten.
    private static unsafe int ProbeIndependentDuration(WIVSMSequenceManager manager, DSEManager dse,
        DatabaseManager database, string directory)
    {
        Directory.CreateDirectory(directory);
        LocalTimingNative.VerifyImages();
        using var counters = new TimingDseCounters();
        using var capture = new TimingCoefficientHook();
        using var eventTrace=new DurationEventTrace();
        using var budget = new DurationBudgetHook();
        using var candidate = new DurationCandidateHook();
        float? pitchWidth = float.TryParse(Environment.GetEnvironmentVariable("VOCALOID_DURATION_PITCH_WIDTH"),
            System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float width) ? width : null;
        Require(!pitchWidth.HasValue || pitchWidth.Value is >= 75 and <= 1200, "Unreviewed candidate pitch width.");
        var ids = budget.Ids;
        bool traceAcoustics = Environment.GetEnvironmentVariable("VOCALOID_DURATION_TRACE") == "1";
        using var acousticTrace=traceAcoustics ? new DurationAcousticTrace(ids) : null;
        using var mixTrace = traceAcoustics ? new DurationMixTrace() : null;
        using var fftTrace = traceAcoustics ? new DurationFftTrace() : null;
        using var effectScope = Environment.GetEnvironmentVariable("VOCALOID_DURATION_EFFECT_SCOPE") == "1" ? new DurationEffectScope() : null;
        using var layerScope = Environment.GetEnvironmentVariable("VOCALOID_DURATION_LAYER_SCOPE") == "1" ? new DurationLayerScope() : null;
        using var timingContract = Environment.GetEnvironmentVariable("VOCALOID_DURATION_TIMING_CONTRACT") == "1" ? new DurationTimingContract() : null;
        DurationFixture[] fixtures =
        [
            new("miku-ka", "BCNFCY43LB2LZCD4", "k a"),
            new("miku-kai", "BCNFCY43LB2LZCD4", "k a i"),
            new("rin-ka", "BKKP765AEHXWSKDB", "k a"),
            new("ling-multi", "BMBNDB8EM5222MK3", "ts\\_h i iE_r @N", 4),
            new("ling-la", "BMBNDB8EM5222MK3", "l a", 4),
            new("miku-ka-up", "BCNFCY43LB2LZCD4", "k a", Previous: true),
            new("miku-ka-down", "BCNFCY43LB2LZCD4", "k a", Previous: true),
            new("miku-kai-sweep", "BCNFCY43LB2LZCD4", "k a i"),
            new("ling-multi-sweep", "BMBNDB8EM5222MK3", "ts\\_h i iE_r @N", 4),
            new("chinese-multi", "BL8CEAM5N4XN3LFK", "s\\ i_n n", 4),
        ];
        string? selected = Environment.GetEnvironmentVariable("VOCALOID_DURATION_FIXTURE");
        if (!string.IsNullOrWhiteSpace(selected)) fixtures = fixtures.Where(f => f.Name == selected).ToArray();
        if (Environment.GetEnvironmentVariable("VOCALOID_DURATION_FIXTURE_ORDER") == "reverse") Array.Reverse(fixtures);
        var reports = new List<object>();
        foreach (var test in fixtures)
        {
            candidate.PitchWidth = null;
            using var fixture = CreateStandaloneFixture(manager, test.Previous ? 2 : 1, test.Previous ? 480 : 960, 0, 7680);
            var part = fixture.Part; var note = fixture.Notes[^1];
            part.SetVoiceBankID(test.Bank); part.MoveNote(new VSMRelTick(960), note);
            note.SetDuration(new VSMRelTick(960));
            note.SetPhonemes(test.Phones, true, test.Language);
            if (test.Previous)
            {
                fixture.Notes[0].SetPhonemes("a", true, test.Language);
                fixture.Notes[0].SetNoteNumber(test.Name.EndsWith("up", StringComparison.Ordinal) ? 72 : 48);
            }
            if (test.Name.EndsWith("sweep", StringComparison.Ordinal))
            {
                part.InsertController(VSMRelTick.Zero, VSMControllerType.PitchBendSens, 12);
                for (int point = 0; point <= 16; point++)
                    part.InsertController(new VSMRelTick(960 + point * 60), VSMControllerType.PitchBend, -6000 + point * 750);
            }
            fixture.Sequence.InterpolatedMasterVolume(); fixture.Sequence.Commit(false);
            string reference = Path.Combine(directory, test.Name + "-unhooked.wav");
            effectScope?.Reset();
            layerScope?.Reset();
            timingContract?.Capture();
            RenderTransitionWithScore(fixture.Sequence, part, reference);
            string coldPcm = TimingPcmHash(reference);
            File.Copy(reference, Path.Combine(directory, test.Name + "-cold.wav"), true);
            effectScope?.Reset();
            layerScope?.Reset();
            timingContract?.Capture();
            SampledTimingVsmBinding.InvalidateRenderedPart(part, fixture.Notes);
            RenderTransitionWithScore(fixture.Sequence, part, reference);
            effectScope?.Check();
            layerScope?.Check();
            timingContract?.Check();
            int[] baseline = note.GetPhonemePositions().ToArray();
            string baselinePcm = TimingPcmHash(reference), baselinePitch = DurationPitchHash(part);
            float[][] baselinePitchValues = DurationPitchValues(part);
            var baselineAll = fixture.Notes.Select(n => n.GetPhonemePositions().ToArray()).ToArray();
            var snapshots = fixture.Notes.Select(Snapshot).ToArray();
            object? planOnly = null;
            if (timingContract != null && Environment.GetEnvironmentVariable("VOCALOID_DURATION_PLAN_ONLY") == "1")
            {
                var oracle = timingContract.Plan.ToArray();
                using var backend = new LocalConsonantTimingBackend(manager, dse, database);
                using var prepared = backend.PrepareCoefficient(note, 1);
                var flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
                var windows = (LocalConsonantTimingBackend.CoefficientWindow[])prepared.GetType().GetField("_windows", flags)!.GetValue(prepared)!;
                var singer = (byte[])prepared.GetType().GetField("_singer", flags)!.GetValue(prepared)!;
                var planner = (NativeConsonantTimingEngine)typeof(LocalConsonantTimingBackend).GetField("_engine", flags)!.GetValue(backend)!;
                Require(windows.Length == 1 && windows[0].Complete, "Plan-only fixture requires complete prepared context.");
                timingContract.Capture();
                var before = counters.Read(); var timer = Stopwatch.StartNew();
                planner.Begin();
                try
                {
                    planner.Plan(0, singer);
                    foreach (var message in windows[0].Messages)
                        planner.PlanNote(message.Frame + windows[0].Origin, message.Bytes, message.Coefficient);
                }
                finally { planner.End(); }
                timingContract.Check();
                var queries = counters.Read().Since(before);
                bool equal = oracle.Length == timingContract.Plan.Count && oracle.Zip(timingContract.Plan).All(p =>
                    p.First.Key == p.Second.Key && p.First.Gap == p.Second.Gap && p.First.Halves.SequenceEqual(p.Second.Halves));
                planOnly = new { milliseconds = timer.Elapsed.TotalMilliseconds, queries, equal,
                    planner.InitializationMs, prepared.PreparationMs, prepared.EncodedNotes, contextNotes = windows[0].Messages.Length,
                    oracle, actual = timingContract.Plan.ToArray() };
                File.WriteAllText(Path.Combine(directory, test.Name + "-plan-only.json"), JsonSerializer.Serialize(planOnly, new JsonSerializerOptions { WriteIndented = true }));
                Require(equal && queries.Starts == 0 && queries.Steps == 0 && queries.Samples == 0, "Plan-only time contract differs from render oracle or synthesized audio.");
            }
            Console.WriteLine("independent.reference=" + JsonSerializer.Serialize(new { test.Name, baseline, hash = baselinePcm }));
            using var indexed = new DurationCostHook(budget.Ids);
            candidate.PitchWidth = pitchWidth;
            var rows = new List<object>();
            var repeats = new Dictionary<string, string>();
            string? baselineTransitions = null;
            int?[] budgets = [null, null, 0, 1, 4, 16, 64, 256, 1024, 64, 256, null];
            if (Environment.GetEnvironmentVariable("VOCALOID_DURATION_REPLAY") == "baseline-only")
                budgets = Enumerable.Repeat<int?>(null, 12).ToArray();
            for (int index = 0; index < budgets.Length; index++)
            {
                budget.Reset(budgets[index]);
                candidate.Reset();
                indexed.Reset(budgets[index].HasValue ? Math.Max(1, budgets[index]!.Value) / (44100d / 256) : null);
                capture.Reset(-1, false, null);
                eventTrace.Reset();
                acousticTrace?.Reset();
                mixTrace?.Reset();
                fftTrace?.Reset();
                effectScope?.Reset();
                layerScope?.Reset();
                timingContract?.Enforce();
                int consumed = 0;
                object? variationBefore = null, variationAfter = null;
                using var nativePcm=IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                long nativeSamples=0;
                capture.AfterOutput=(_,output)=>
                {
                    int count=Marshal.ReadInt32(output);
                    if(count is <0 or >8192) throw new InvalidOperationException("Native PCM trace exceeds frame bound.");
                    nint pointer=Marshal.ReadIntPtr(output,8);
                    if(count>0 && pointer!=0){nativePcm.AppendData(new ReadOnlySpan<byte>((void*)pointer,count*2));nativeSamples+=count;}
                };
                var actual = new Dictionary<int, DurationRow>();
                var framePositions=new List<DurationFramePosition>();
                object Variation(nint state)
                {
                    nint variation=state+0x1d570+0x40a8;
                    return new { enabled=Marshal.ReadByte(variation),lookup=Marshal.ReadByte(variation,1),
                        key=Marshal.ReadInt32(variation,4),cursor=Marshal.ReadInt32(variation,8) };
                }
                capture.BeforeStep = state => variationBefore ??= Variation(state);
                capture.AfterStep = state =>
                {
                    int cursor = Marshal.ReadInt32(state, 0x24158);
                    foreach (var row in ReadDurationRows(state, ids, consumed, cursor)) actual[row.Begin] = row;
                    framePositions.AddRange(ReadDurationFramePositions(state,ids,consumed,cursor));
                    consumed = cursor;
                    variationAfter = Variation(state);
                };
                SampledTimingVsmBinding.InvalidateRenderedPart(part, fixture.Notes);
                string path = Path.Combine(directory, $"{test.Name}-{index}-{budgets[index]?.ToString() ?? "native"}.wav");
                var timer = Stopwatch.StartNew();
                RenderTransitionWithScore(fixture.Sequence, part, path);
                effectScope?.Check();
                layerScope?.Check();
                fftTrace?.Check();
                timingContract?.Check();
                double renderMs = timer.Elapsed.TotalMilliseconds;
                capture.Check(); budget.Check(); indexed.Check(); eventTrace.Check();acousticTrace?.Check();mixTrace?.Check();capture.AfterStep = null; capture.BeforeStep = null;capture.AfterOutput=null;
                var boundaries = note.GetPhonemePositions().ToArray();
                Require(note.DurationTick.Value == 960 && note.RelPosTick.Value == 960, "Budget experiment modified the source note.");
                Require(fixture.Notes.Select(Snapshot).SequenceEqual(snapshots), "Selection experiment modified a source note property.");
                Require(ReadWaveMetrics(path).Peak > .00001, "Budget experiment produced silent output.");
                string pcm = TimingPcmHash(path), pitchHash = DurationPitchHash(part);
                bool pitchUnchanged = pitchHash == baselinePitch;
                var pitchComparison = CompareDurationPitch(baselinePitchValues, DurationPitchValues(part));
                bool allTimingUnchanged = fixture.Notes.Select((n, i) => n.GetPhonemePositions().SequenceEqual(baselineAll[i])).All(v => v);
                string key = budgets[index]?.ToString() ?? "native";
                bool repeat = repeats.TryGetValue(key, out string? earlier);
                bool repeatStable = !repeat || earlier == pcm;
                repeats[key] = pcm;
                if (!pitchWidth.HasValue && !budgets[index].HasValue)
                    Require(pcm == baselinePcm && pitchUnchanged && allTimingUnchanged, "Passive hooks changed the unmodified reference.");
                var sampleRows = actual.Values.OrderBy(r => r.Begin).ToArray();
                var transitions = new List<string>();
                for (int rowIndex = 0; rowIndex < sampleRows.Length; rowIndex++)
                {
                    var row = sampleRows[rowIndex];
                    if (row.To >= 0) transitions.Add($"{row.From}/{row.To}@{row.Begin + row.LeftLength}");
                    if (rowIndex == 0) continue;
                    var left = sampleRows[rowIndex - 1];
                    int from = left.To < 0 ? left.From : left.To;
                    if (left.End == row.Begin && from != row.From) transitions.Add($"{from}/{row.From}@{row.Begin}");
                }
                string transitionKey = string.Join('|', transitions.OrderBy(v => v, StringComparer.Ordinal));
                baselineTransitions ??= transitionKey;
                var item = new
                {
                    index,
                    budgetFrames = budgets[index],
                    boundaries,
                    timingUnchanged = boundaries.SequenceEqual(baseline),
                    allTimingUnchanged,
                    allBoundaries = fixture.Notes.Select(n => n.GetPhonemePositions().ToArray()).ToArray(),
                    pitchUnchanged,
                    pitchHash,
                    pitchComparison,
                    nativeTransitions = transitions,
                    nativeTransitionsUnchanged = transitionKey == baselineTransitions,
                    hash = pcm,
                    pcmMatchesUnhooked = pcm == baselinePcm,
                    repeat,
                    repeatStable,
                    variationBefore,
                    variationAfter,
                    nativePcmHash=Convert.ToHexString(nativePcm.GetHashAndReset()),nativeSamples,
                    framePositions,
                    events=eventTrace.Events.ToArray(),
                    acousticFrames=acousticTrace?.Rows.ToArray(),
                    mixFrames = mixTrace?.Rows.ToArray(),
                    fftFrames = fftTrace?.Rows.ToArray(),
                    inputHash=Convert.ToHexString(SHA256.HashData(capture.Inputs.SelectMany(block=>BitConverter.GetBytes(block.Frame).Concat(block.Bytes)).ToArray())),
                    renderMs,
                    metrics = ReadWaveMetrics(path),
                    selected = sampleRows,
                    calls = budget.Calls.ToArray(),
                    indexedCalls = indexed.Calls.ToArray(),
                    indexed.Allocations,
                    indexed.ReplacedCosts,
                    filterCalls = candidate.Calls,
                    maxCandidates = candidate.MaximumCandidates,
                    pitchWidth,
                    indexed.Weight,
                    contractChanged = timingContract?.Changed, contractPlan = timingContract?.Plan.ToArray(), layerCalls = layerScope?.Calls
                };
                rows.Add(item);
                Console.WriteLine("independent.row=" + JsonSerializer.Serialize(new
                {
                    test.Name,
                    index,
                    budget = budgets[index],
                    item.timingUnchanged,
                    calls = budget.Calls.Count,
                    indexedCalls = indexed.Calls.Count,
                    indexed.Allocations,
                    indexed.ReplacedCosts,
                    filterCalls = candidate.Calls,
                    maxCandidates = candidate.MaximumCandidates,
                    pitchWidth,
                    sources = sampleRows.Select(r => r.Material.Id).Distinct().ToArray(),
                    item.hash
                }));
                File.WriteAllText(Path.Combine(directory, test.Name + ".json"), JsonSerializer.Serialize(new { fixture = test, baseline, baselinePcm, coldPcm, effectScoped = effectScope != null, baselinePitch, planOnly, rows }, new JsonSerializerOptions { WriteIndented = true }));
                Require(repeatStable, "Repeated independent duration control changed PCM unexpectedly; full failing record saved.");
                if (timingContract != null)
                    Require(allTimingUnchanged && transitionKey == baselineTransitions,
                        "Real time contract changed a visible boundary or native phoneme transition; full failing record saved.");
            }
            reports.Add(new { fixture = test, baseline, baselinePcm, baselinePitch, rows });
            File.WriteAllText(Path.Combine(directory, "independent-duration.json"), JsonSerializer.Serialize(reports, new JsonSerializerOptions { WriteIndented = true }));
        }
        return 0;
    }

    private static string DurationPitchHash(WIVSMMidiPart part)
    {
        using var score = part.HoldingScoreList ?? part.RenderingScoreList ?? throw new InvalidOperationException("No rendered score.");
        if (score.NumScores is < 1 or > 10000) throw new InvalidOperationException("Unbounded pitch score.");
        using var bytes = new MemoryStream(); using var writer = new BinaryWriter(bytes);
        writer.Write(score.NumScores);
        for (long i = 0; i < score.NumScores; i++)
        {
            var row = score.ScoreAtIndex(i);
            writer.Write(row.NotePit); writer.Write(row.ExpPit); writer.Write(row.DirectPit);
            writer.Write(row.PbPit); writer.Write(row.VibPit); writer.Write(row.S5Vibrato);
        }
        writer.Flush(); return Convert.ToHexString(SHA256.HashData(bytes.ToArray()));
    }

    private static float[][] DurationPitchValues(WIVSMMidiPart part)
    {
        using var scores = part.HoldingScoreList ?? part.RenderingScoreList ?? throw new InvalidOperationException("No pitch score.");
        if (scores.NumScores is < 1 or > 10000) throw new InvalidOperationException("Unbounded pitch score.");
        return Enumerable.Range(0, (int)scores.NumScores).Select(i =>
        {
            var row = scores.ScoreAtIndex(i);
            return new[] { row.NotePit, row.ExpPit, row.DirectPit, row.PbPit, row.VibPit, row.S5Vibrato };
        }).ToArray();
    }

    private static object CompareDurationPitch(float[][] original, float[][] actual)
    {
        int maskDifferences = 0; float maximumCents = 0;
        for (int i = 0; i < Math.Min(original.Length, actual.Length); i++)
            for (int column = 0; column < 6; column++)
            {
                float a = original[i][column], b = actual[i][column];
                if (a == b) continue;
                if (a == VSMScore.UnusedPitchData || b == VSMScore.UnusedPitchData) maskDifferences++;
                else maximumCents = Math.Max(maximumCents, Math.Abs(a - b));
            }
        return new { sameFrames = original.Length == actual.Length, maskDifferences, maximumCents };
    }

    private sealed unsafe class DurationBudgetHook : IDisposable
    {
        internal sealed record Call(int NativeBudget, int EffectiveBudget, float Pitch, float Dynamics,
            byte Trim, ulong SourceLength, int[] Selected, int[][] CandidateIds);
        private static DurationBudgetHook? _active;
        private readonly nint _entry;
        private nint _trampoline;
        private readonly byte[] _original = [0x48, 0x8b, 0xc4, 0x48, 0x89, 0x58, 0x08,
            0x48, 0x89, 0x70, 0x18, 0x48, 0x89, 0x78, 0x20];
        private int? _budget;
        private Exception? _error;
        internal Dictionary<nint, int> Ids { get; } = new();
        internal List<Call> Calls { get; } = new();

        internal DurationBudgetHook()
        {
            LocalTimingNative.VerifyImages();
            _entry = LocalTimingNative.Dse + 0x1b8f40;
            if (_active != null || !new ReadOnlySpan<byte>((void*)_entry, _original.Length).SequenceEqual(_original))
                throw new NotSupportedException("Duration selector entry is not the reviewed, unhooked version.");
            // Ghidra verified: four whole MOV instructions, 15 bytes total;
            // no RIP-relative operand or relative control flow is relocated.
            _trampoline = VirtualAlloc(0, 64, 0x3000, 0x40);
            if (_trampoline == 0) throw new InvalidOperationException("Cannot allocate research trampoline.");
            _original.CopyTo(new Span<byte>((void*)_trampoline, _original.Length));
            WriteJump(_trampoline + _original.Length, _entry + _original.Length, 14);
            FlushInstructionCache(GetCurrentProcess(), _trampoline, 64);
            _active = this;
            try { WriteEntry((nint)(delegate* unmanaged<nint, nint, int, float, float, byte, ulong>)&Select); }
            catch { _active = null; VirtualFree(_trampoline, 0, 0x8000); _trampoline = 0; throw; }
        }

        internal void Reset(int? frames) { _budget = frames; _error = null; Calls.Clear(); }
        internal void Check() { if (_error != null) throw new InvalidOperationException("Independent selection callback failed.", _error); }
        private int Id(nint value)
        {
            if (value == 0) return -1;
            if (!Ids.TryGetValue(value, out int id)) Ids.Add(value, id = Ids.Count);
            return id;
        }

        [UnmanagedCallersOnly]
        private static ulong Select(nint state, nint work, int duration, float pitch, float dynamics, byte trim)
        {
            var owner = _active!;
            var original = (delegate* unmanaged<nint, nint, int, float, float, byte, ulong>)owner._trampoline;
            // Never unwind managed exceptions through native DSE frames.
            int effective = owner._budget ?? duration;
            ulong result = original(state, work, effective, pitch, dynamics, trim);
            try
            {
                int count = Marshal.ReadInt32(work, 12);
                if (count is < 0 or > 32 || owner.Calls.Count >= 512) throw new InvalidOperationException("Selector trace exceeds bound.");
                var chosen = new int[count]; var candidates = new int[count][];
                for (int i = 0; i < count; i++)
                {
                    chosen[i] = owner.Id(Marshal.ReadIntPtr(work, 0x810 + i * 0x68));
                    nint unit = Marshal.ReadIntPtr(work, 0x410 + i * 8);
                    if (unit == 0) { candidates[i] = []; continue; }
                    int size = Marshal.ReadInt32(unit, 0x150);
                    if (size is < 0 or > 128) throw new InvalidOperationException("Candidate count exceeds reviewed table size.");
                    nint array = Marshal.ReadIntPtr(unit, 0x148);
                    candidates[i] = Enumerable.Range(0, size).Select(j => owner.Id(Marshal.ReadIntPtr(array, j * 8))).ToArray();
                }
                owner.Calls.Add(new(duration, effective, pitch, dynamics, trim, result, chosen, candidates));
            }
            catch (Exception error) { owner._error ??= error; }
            return result;
        }

        private static void WriteJump(nint address, nint target, int length)
        {
            var bytes = new Span<byte>((void*)address, length); bytes.Fill(0x90);
            bytes[0] = 0xff; bytes[1] = 0x25; bytes.Slice(2, 4).Clear();
            BitConverter.TryWriteBytes(bytes.Slice(6, 8), (long)target);
        }
        private void WriteEntry(nint target)
        {
            if (!VirtualProtect(_entry, 15, 0x40, out uint protection)) throw new InvalidOperationException("Cannot protect research detour.");
            WriteJump(_entry, target, 15);
            if (!VirtualProtect(_entry, 15, protection, out _)) throw new InvalidOperationException("Cannot restore selector protection.");
            FlushInstructionCache(GetCurrentProcess(), _entry, 15);
        }
        public void Dispose()
        {
            if (_trampoline == 0) return;
            if (!VirtualProtect(_entry, 15, 0x40, out uint protection)) throw new InvalidOperationException("Cannot restore research selector.");
            Marshal.Copy(_original, 0, _entry, _original.Length);
            if (!VirtualProtect(_entry, 15, protection, out _)) throw new InvalidOperationException("Cannot restore original protection.");
            FlushInstructionCache(GetCurrentProcess(), _entry, 15);
            _active = null; VirtualFree(_trampoline, 0, 0x8000); _trampoline = 0;
        }
        [DllImport("kernel32")] private static extern nint VirtualAlloc(nint address, nuint size, uint allocation, uint protection);
        [DllImport("kernel32")] private static extern bool VirtualFree(nint address, nuint size, uint kind);
        [DllImport("kernel32")] private static extern bool VirtualProtect(nint address, nuint size, uint protection, out uint previous);
        [DllImport("kernel32")] private static extern bool FlushInstructionCache(nint process, nint address, nuint size);
        [DllImport("kernel32")] private static extern nint GetCurrentProcess();
    }
}
