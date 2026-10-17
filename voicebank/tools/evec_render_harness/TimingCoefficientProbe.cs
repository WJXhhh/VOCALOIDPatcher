using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using VOCALOIDPatcher.ConsonantTiming;
using Yamaha.VOCALOID.DSE;
using Yamaha.VOCALOID.VDM;
using Yamaha.VOCALOID.VSM;

internal static partial class Program
{
    private sealed record TimingCoefficientFixture(string Name, string Phones, bool Legato = false,
        string Bank = "BCNFCY43LB2LZCD4", int Duration = 480, int Pitch = 60, int Gap = 0);

    // Disposable process only. Splits the target NOTE commit, then optionally
    // changes its decoded coefficient before the native planner consumes it.
    // No code patch, installed file, Editor process or bank data is changed.
    private static unsafe int ProbeTimingCoefficient(WIVSMSequenceManager manager, DSEManager dse,
        DatabaseManager database, string directory)
    {
        Directory.CreateDirectory(directory);
        LocalTimingNative.VerifyImages();
        using var counters = new TimingDseCounters();
        using var hook = new TimingCoefficientHook();
        using var backend = new LocalConsonantTimingBackend(manager, dse, database);
        using var native = new NativeConsonantTimingEngine((nint)dse);
        nint nativeState = (nint)typeof(NativeConsonantTimingEngine)
            .GetField("_state", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(native)!;
        var decode = (delegate* unmanaged<byte, float>)(LocalTimingNative.Dse + 0x1b3cd0);
        float[] coefficients = Enumerable.Range(0, 128).Select(v => decode((byte)v)).ToArray();
        var results = new List<object>();
        TimingCoefficientFixture[] fixtures = [
            new("miku_ka", "k a"), new("miku_ka_legato", "k a", true),
            new("miku_na", "n a"), new("miku_na_legato", "n a", true),
            new("rin_ka", "k a", Bank: "BKKP765AEHXWSKDB"), new("miku_multi", "k a i", true),
            // Independent challenges for a model prepared from one baseline:
            // different classes, temporal constraints, pitch and a real gap.
            new("miku_sa", "s a", true), new("miku_ja", "j a", true),
            new("miku_he", "h e"), new("miku_ma", "m a", true),
            new("miku_ka_short", "k a", true, Duration: 120),
            new("miku_ka_high", "k a", true, Pitch: 72),
            new("miku_ka_gap", "k a", true, Gap: 120),
            new("rin_na", "n a", true, Bank: "BKKP765AEHXWSKDB") ];
        foreach (var test in fixtures)
        {
            using var fixture = CreateStandaloneFixture(manager, test.Legato ? 2 : 1, test.Duration, 0, 2880);
            fixture.Part.SetVoiceBankID(test.Bank);
            var target = fixture.Notes[^1];
            if (test.Legato) fixture.Part.MoveNote(new VSMRelTick(960 - test.Duration - test.Gap), fixture.Notes[0]);
            fixture.Part.MoveNote(new VSMRelTick(960), target);
            target.SetNoteNumber(test.Pitch);
            target.SetPhonemes(test.Phones, true, 0);
            fixture.Sequence.InterpolatedMasterVolume();
            fixture.Sequence.Commit(false);
            hook.Reset(fixture.Notes.Length - 1, false, null);
            string referencePath = Path.Combine(directory, test.Name + "-reference.wav");
            // Match every render's invalidation history; first render may warm
            // native/part state and is not the repeatability oracle.
            RenderTransitionWithScore(fixture.Sequence, fixture.Part, Path.Combine(directory, test.Name + "-warmup.wav"));
            target.NoteVelocity = 63; fixture.Sequence.Commit(false);
            target.NoteVelocity = 64; fixture.Sequence.Commit(false);
            hook.Reset(fixture.Notes.Length - 1, false, null);
            RenderTransitionWithScore(fixture.Sequence, fixture.Part, referencePath);
            hook.Check();
            int[] original = target.GetPhonemePositions().ToArray();
            string originalHash = TimingPcmHash(referencePath);
            var script = hook.Inputs.ToArray();
            // Compute every ordinary VEL boundary without producing candidate PCM.
            var beforeQueries = counters.Read();
            var reachable = new HashSet<string>();
            for (int velocity = 0; velocity < 128; velocity++)
                reachable.Add(string.Join(',', backend.Query(target, velocity).Boundaries));
            var dense = new List<object>();
            var denseKeys = new HashSet<string>();
            var ordinaryPlans = new List<object>();
            var sourceIds = new Dictionary<nint, int>();
            object? lastPlan = null;
            var queryLatencies = new List<double>();
            float? newCoefficient = null;
            int[]? newBoundaries = null;
            double queryMs = 0;
            int[] Query(float coefficient, bool capturePlan = false)
            {
                var watch = Stopwatch.StartNew();
                native.Begin();
                try
                {
                    int ordinal = 0, msb = -1, lsb = -1;
                    foreach (var block in script)
                        TimingCoefficientHook.Feed(nativeState, block.Frame, block.Bytes,
                            fixture.Notes.Length - 1, coefficient, ref ordinal, ref msb, ref lsb, null);
                    if (capturePlan) lastPlan = ReadTimingPlan(nativeState, sourceIds);
                    using var score = new ConsonantTimingScoreAdapter(native.CreateBoundaryScore());
                    foreach (var note in fixture.Notes)
                    {
                        nint pointer = (nint)note;
                        Marshal.WriteIntPtr(pointer, 0xd0, Marshal.ReadIntPtr(pointer, 0xc8));
                    }
                    score.Map(fixture.Part, [0, 12000]);
                    return target.GetPhonemePositions().ToArray();
                }
                finally
                {
                    native.End();
                    double milliseconds = watch.Elapsed.TotalMilliseconds;
                    queryMs += milliseconds; queryLatencies.Add(milliseconds);
                }
            }
            // Establish that direct event preparation equals all 128 existing
            // supported VEL queries before testing values between those codes.
            int nativeMatches = 0;
            for (int velocity = 0; velocity < 128; velocity++)
            {
                int[] predicted = Query(coefficients[velocity], true);
                long[] expected = backend.Query(target, velocity).Boundaries;
                if (!predicted.Select(v => (long)v).SequenceEqual(expected))
                    throw new InvalidOperationException($"Decoded VEL replay differs from backend: {test.Name}, VEL={velocity}, coefficient={coefficients[velocity]}, predicted={string.Join(',', predicted)}, expected={string.Join(',', expected)}.");
                ordinaryPlans.Add(new { velocity, coefficient = coefficients[velocity], predicted, plan = lastPlan });
                nativeMatches++;
            }
            Query(coefficients[64], true);
            object? baselinePlan = lastPlan;
            var prepared = PreparedTimingOnset.FromPlan(baselinePlan!, test.Phones.Split(' ')[0]);
            int denseStart = queryLatencies.Count;
            int preparedMismatches = 0;
            for (int index = 0; index <= 512; index++)
            {
                float coefficient = coefficients[0] + (coefficients[^1] - coefficients[0]) * index / 512f;
                int[] predicted = Query(coefficient);
                denseKeys.Add(string.Join(',', predicted));
                bool extra = !reachable.Contains(string.Join(',', predicted));
                int rawFrame = prepared.Frame(coefficient);
                long preparedOnset = fixture.Sequence.GetTickFromTime(VSMAbsTick.Zero,
                    (rawFrame - 3087) * 256d / 44100 - 0.5).Value - 960;
                if (preparedOnset != predicted[0]) preparedMismatches++;
                dense.Add(new { coefficient, boundaries = predicted, extra, preparedOnset });
                if (extra && newCoefficient == null) { newCoefficient = coefficient; newBoundaries = predicted; }
            }
            object? continuousPlan = null;
            if (newCoefficient.HasValue) { Query(newCoefficient.Value, true); continuousPlan = lastPlan; }
            long checksum = 0;
            for (int i = 0; i < 1000; i++) checksum += prepared.Frame(i % 513 / 512f);
            const int modelIterations = 200000;
            var modelWatch = Stopwatch.StartNew();
            for (int i = 0; i < modelIterations; i++) checksum += prepared.Frame(i % 513 / 512f);
            double modelNanoseconds = modelWatch.Elapsed.TotalMilliseconds * 1000000 / modelIterations;
            var queryDse = counters.Read().Since(beforeQueries);
            if (queryDse.Starts != 0 || queryDse.Steps != 0 || queryDse.Samples != 0 || native.CurrentFrame != 0)
                throw new InvalidOperationException("Candidate planning entered synthesis.");
            var renders = new List<object>();
            foreach (var mode in new[] { "restore", "split_control", "decoded_control", "continuous", "restore_after" })
            {
                if (mode == "continuous" && newCoefficient == null) continue;
                bool split = !mode.StartsWith("restore", StringComparison.Ordinal);
                float? coefficient = mode == "continuous" ? newCoefficient : mode == "decoded_control" ? coefficients[64] : null;
                hook.Reset(fixture.Notes.Length - 1, split, coefficient);
                target.NoteVelocity = 63;
                fixture.Sequence.Commit(false);
                target.NoteVelocity = 64;
                fixture.Sequence.Commit(false);
                var before = counters.Read();
                var timer = Stopwatch.StartNew();
                string path = Path.Combine(directory, test.Name + "-" + mode + ".wav");
                RenderTransitionWithScore(fixture.Sequence, fixture.Part, path);
                hook.Check();
                int[] actual = target.GetPhonemePositions().ToArray();
                string hash = TimingPcmHash(path);
                bool control = mode != "continuous";
                bool expectedBoundary = actual.SequenceEqual(control ? original : newBoundaries!);
                if (!expectedBoundary || (control && hash != originalHash))
                    throw new InvalidOperationException($"Replay control or prediction failed: {test.Name}/{mode}, boundaries={string.Join(',', actual)}, expected={string.Join(',', control ? original : newBoundaries!)}, pcmSame={hash == originalHash}.");
                if (!control && hash == originalHash) throw new InvalidOperationException("Coefficient changed no PCM.");
                renders.Add(new { mode, coefficient, actual, expectedBoundary, pcmSha256 = hash,
                    pcmSame = hash == originalHash, milliseconds = timer.Elapsed.TotalMilliseconds,
                    injections = hook.Injections, decodedBefore = float.IsFinite(hook.DecodedBefore) ? (float?)hook.DecodedBefore : null,
                    dse = counters.Read().Since(before) });
            }
            results.Add(new { test.Name, test.Phones, test.Legato, test.Bank, test.Duration, test.Pitch, test.Gap, original, originalHash,
                ordinaryDistinct = reachable.Count, nativeMatches, newCoefficient, newBoundaries,
                denseDistinct = denseKeys.Count,
                selectedSourcesAcrossVelocities = sourceIds.Count,
                prepared, preparedMismatches, modelIterations, modelNanoseconds, checksum,
                latency = SummarizeTimingLatency(queryLatencies.Skip(denseStart).Take(513)),
                queryDse, queryMs, ordinaryPlans, baselinePlan, continuousPlan, dense, renders });
            Console.WriteLine($"coefficient.case={test.Name};ordinary={reachable.Count};extra={newCoefficient};boundary={string.Join(',', newBoundaries ?? [])};controls=passed");
        }
        File.WriteAllText(Path.Combine(directory, "timing-coefficient.json"), JsonSerializer.Serialize(new {
            dseSha256 = "7a4965bd377dbc2b08710e807ae1345b67d9fff6579ca0c01a769f744366f5b5",
            coefficients, results, scope = "Owned isolated sequences; unchanged VEL 64; private decoded coefficient before NOTE commit; full PCM controls. No Editor integration." },
            new JsonSerializerOptions { WriteIndented = true }));
        return 0;
    }

    // Deliberately incomplete research model: tests whether a single selected
    // material pair explains the entire onset curve. Failing constraints/path
    // changes are retained as evidence, never hidden or used for UI previews.
    private sealed record PreparedTimingOnset(int IncomingInner, int OutgoingInner, int OuterSum,
        float Cap, int AnchorFrame)
    {
        internal static PreparedTimingOnset FromPlan(object plan, string token)
        {
            var rows = JsonSerializer.SerializeToElement(plan).EnumerateArray().ToArray();
            var incoming = rows.First(row => row.GetProperty("to").GetString() == token);
            var outgoing = rows.First(row => row.GetProperty("from").GetString() == token &&
                !string.IsNullOrEmpty(row.GetProperty("to").GetString()));
            int[] Side(JsonElement row, bool right)
            {
                var alignment = row.GetProperty("alignment").EnumerateArray().Select(v => v.GetInt32()).ToArray();
                if (alignment.Length != 8) throw new InvalidOperationException("Prepared model needs a two-side ART stage.");
                if (row.GetProperty("reverse").GetInt32() != 0) right = !right;
                return alignment.Skip(right ? 4 : 0).Take(4).ToArray();
            }
            int[] a = Side(incoming, true), b = Side(outgoing, false);
            int outer = a[1] - a[0] + b[1] - b[0];
            var categories = incoming.GetProperty("toClasses").EnumerateArray().Select(v => v.GetString()).ToArray();
            float cap = categories.Contains("Semivowels") ? 25 : categories.Contains("Liquids") ? 18 :
                categories.Contains("Nasals") ? 30 : categories.Contains("VoicedFricatives") ? 2 * outer : 55;
            return new(a[3] - a[2], b[3] - b[2], outer, cap,
                outgoing.GetProperty("begin").GetInt32() + outgoing.GetProperty("leftLength").GetInt32());
        }

        internal int Frame(float coefficient)
        {
            float factor = coefficient < 0.5f
                ? (float)Math.Pow(Math.Max(Cap, 2 * OuterSum) / OuterSum, (0.5f - coefficient) * 2)
                : MathF.Pow(2, 1 - 2 * coefficient);
            int left = (int)(Math.Max(1, IncomingInner * factor) + 0.5f);
            int right = (int)(Math.Max(1, OutgoingInner * factor) + 0.5f);
            return AnchorFrame - left - right;
        }
    }

    private static unsafe object[] ReadTimingPlan(nint state, Dictionary<nint, int> sourceIds)
    {
        nint dse = LocalTimingNative.Dse;
        nint schedule = Marshal.ReadIntPtr(state, 0x1d568);
        int end = ((delegate* unmanaged<nint, int>)(dse + 0x18aba0))(schedule);
        if (end is < 0 or > 7183 || end > Marshal.ReadInt32(schedule, 8))
            throw new InvalidOperationException("Plan metadata exceeds research bound.");
        var seen = new HashSet<nint>();
        var result = new List<object>();
        for (int frame = 3087; frame < end; frame++)
        {
            nint entry = ((delegate* unmanaged<nint, int, nint>)(dse + 0x18b260))(schedule, frame);
            nint row = Marshal.ReadIntPtr(entry);
            if (row == 0 || Marshal.ReadInt32(row, 0x11c) < 0 || !seen.Add(row)) continue;
            if (seen.Count > 96) throw new InvalidOperationException("Too many material rows.");
            nint material = Marshal.ReadIntPtr(row, 0x20);
            int stage = Marshal.ReadInt32(row, 0x28), limit = Marshal.ReadInt32(row, 0x2c);
            int from = Marshal.ReadInt32(row, 0x10), to = Marshal.ReadInt32(row, 0x14);
            int[] alignment = [];
            int sourceId = -1;
            if (material != 0)
            {
                if (!sourceIds.TryGetValue(material, out sourceId)) sourceIds.Add(material, sourceId = sourceIds.Count);
                if (to >= 0)
                {
                    nint begin = Marshal.ReadIntPtr(material, 0x1d8), finish = Marshal.ReadIntPtr(material, 0x1e0);
                    if (stage is < 0 or >= 128 || finish < begin || finish - begin > 128 * 32 || finish - begin < (stage + 1) * 32)
                        throw new InvalidOperationException("Unexpected selected alignment range.");
                    alignment = Enumerable.Range(0, 8).Select(i => Marshal.ReadInt32(begin, stage * 32 + i * 4)).ToArray();
                }
            }
            nint unit = ((delegate* unmanaged<nint, int, int, nint>)(dse + 0x181ff0))(
                Marshal.ReadIntPtr(state), Marshal.ReadInt32(row, 0x11c), Marshal.ReadInt32(row, 0x120));
            nint dictionary = Marshal.ReadIntPtr(unit, 0x170);
            string Name(int id) => id < 0 ? "" : Marshal.PtrToStringAnsi(
                ((delegate* unmanaged<nint, byte, nint>)(dse + 0x125480))(dictionary, checked((byte)id))) ?? "";
            string[] Classes(int id)
            {
                if (id < 0) return [];
                nint name = ((delegate* unmanaged<nint, byte, nint>)(dse + 0x125480))(dictionary, checked((byte)id));
                string[] categories = ["Semivowels", "Liquids", "Nasals", "VoicedFricatives", "UnvoicedFricatives", "Silence"];
                var matches = new List<string>();
                foreach (string category in categories)
                {
                    nint text = Marshal.StringToHGlobalAnsi(category);
                    try
                    {
                        if (((delegate* unmanaged<nint, nint, nint, int>)(dse + 0x127d50))(
                            Marshal.ReadIntPtr(dictionary, 0x158), name, text) != 0) matches.Add(category);
                    }
                    finally { Marshal.FreeHGlobal(text); }
                }
                return matches.ToArray();
            }
            result.Add(new {
                begin = Marshal.ReadInt32(row), end = Marshal.ReadInt32(row, 4), leftLength = Marshal.ReadInt32(row, 8),
                from = Name(from), to = Name(to), fromClasses = Classes(from), toClasses = Classes(to),
                leftFlag = Marshal.ReadByte(row, 24), rightFlag = Marshal.ReadByte(row, 25),
                sourceId, stage, limit, reverse = Marshal.ReadByte(row, 0x12c), alignment,
                bank = Marshal.ReadInt32(row, 0x11c), unit = Marshal.ReadInt32(row, 0x120), stationaryLength = Marshal.ReadInt32(row, 0x118) });
        }
        return result.ToArray();
    }

    private static string TimingPcmHash(string path)
    {
        using var reader = new BinaryReader(File.OpenRead(path));
        if (reader.ReadUInt32() != 0x46464952) throw new InvalidOperationException("Not RIFF.");
        reader.ReadUInt32();
        if (reader.ReadUInt32() != 0x45564157) throw new InvalidOperationException("Not WAVE.");
        while (reader.BaseStream.Position + 8 <= reader.BaseStream.Length)
        {
            uint tag = reader.ReadUInt32(), size = reader.ReadUInt32();
            if (size > reader.BaseStream.Length - reader.BaseStream.Position) throw new InvalidOperationException("Truncated WAV.");
            if (tag == 0x61746164) return Convert.ToHexString(SHA256.HashData(reader.ReadBytes(checked((int)size))));
            reader.BaseStream.Position += size + (size & 1);
        }
        throw new InvalidOperationException("Missing PCM.");
    }

    private sealed unsafe class TimingCoefficientHook : IDisposable
    {
        private static TimingCoefficientHook? _active;
        private readonly nint _table, _step;
        private int _target, _ordinal, _msb, _lsb;
        private bool _split;
        private float? _coefficient;
        private IReadOnlyDictionary<int, float>? _coefficients;
        private Exception? _error;
        internal List<(int Frame, byte[] Bytes)> Inputs { get; } = new();
        internal Action<nint>? AfterStep { get; set; }
        internal Action<nint>? BeforeSynthesis { get; set; }
        internal Action<nint>? BeforeStep { get; set; }
        internal Action<nint, nint>? AfterOutput { get; set; }
        internal int Injections { get; private set; }
        internal float DecodedBefore { get; private set; }

        internal TimingCoefficientHook()
        {
            _table = LocalTimingNative.Dse + 0x5066e8;
            _step = Marshal.ReadIntPtr(_table, 15 * 8);
            if (_active != null) throw new InvalidOperationException("Probe already active.");
            _active = this;
            WriteSlot((nint)(delegate* unmanaged<nint, nint, nint, nint, int>)&Step);
        }

        internal void Reset(int target, bool split, float? coefficient)
        {
            _target = target; _split = split; _coefficient = coefficient;
            _ordinal = 0; _msb = _lsb = -1; _error = null;
            _coefficients = null;
            Injections = 0; DecodedBefore = float.NaN; Inputs.Clear();
        }

        internal void ResetAll(IReadOnlyDictionary<int, float> coefficients)
        {
            Reset(-1, true, null);
            _coefficients = new Dictionary<int, float>(coefficients);
        }

        internal void Check() { if (_error != null) throw new InvalidOperationException("Native probe callback failed.", _error); }

        // Feeds the prefix and suffix in native order. Only NOTE commit is split;
        // all event sample offsets and the engine frame remain unchanged.
        internal static void Feed(nint state, int frame, byte[] bytes, int target, float? coefficient,
            ref int ordinal, ref int msb, ref int lsb, TimingCoefficientHook? owner)
        {
            int start = 0;
            for (int offset = 0; offset < bytes.Length; offset += 8)
            {
                if (bytes[offset + 4] != 0xb0) continue;
                int controller = bytes[offset + 5], value = bytes[offset + 6];
                if (controller == 99) msb = value;
                else if (controller == 98) lsb = value;
                else if (controller == 6 && msb == 0x50 && lsb == 0x7f)
                {
                    int current = ordinal++;
                    float? selected = coefficient;
                    if (owner?._coefficients != null)
                    {
                        if (!owner._coefficients.TryGetValue(current, out float selectedValue)) continue;
                        selected = selectedValue;
                    }
                    else if (current != target) continue;
                    Plan(state, frame, bytes.AsSpan(start, offset - start));
                    int slot = Marshal.ReadInt32(state + 0x1d570, 0x7a8);
                    if (slot is < 0 or > 1) throw new InvalidOperationException("Invalid NOTE parser slot.");
                    float* decoded = (float*)(state + 0x1d570 + 0x58 + slot * 0x3a8 + 0x250);
                    if (owner != null)
                    {
                        owner.DecodedBefore = *decoded; owner.Injections++;
                    }
                    if (selected.HasValue) NativeTimingNoteInput.SetCoefficient(state, selected.Value);
                    start = offset;
                }
            }
            Plan(state, frame, bytes.AsSpan(start));
        }

        private static void Plan(nint state, int frame, ReadOnlySpan<byte> bytes)
        {
            if (bytes.Length == 0) return;
            fixed (byte* data = bytes)
            {
                long* input = stackalloc long[2]; input[0] = bytes.Length / 8; input[1] = (long)data;
                ((delegate* unmanaged<nint, int, nint, void>)(LocalTimingNative.Dse + 0x1b2ab0))(
                    state + 0x1d570, frame, (nint)input);
            }
        }

        [UnmanagedCallersOnly]
        private static int Step(nint engine, nint input, nint output, nint metadata)
        {
            var probe = _active!;
            try
            {
                int count = Marshal.ReadInt32(input);
                if (count is < 0 or > 4096) throw new InvalidOperationException("Input bound exceeded.");
                int slot = Marshal.ReadInt32(engine, 16);
                if (slot is < 0 or >= 32) throw new InvalidOperationException("Invalid DSE slot.");
                nint state = Marshal.ReadIntPtr(LocalTimingNative.Dse + 0xaa69d0, slot * 8);
                int frame = Marshal.ReadInt32(state, 0x24158);
                probe.BeforeStep?.Invoke(state);
                byte[] bytes = new byte[count * 8];
                if (count != 0) Marshal.Copy(Marshal.ReadIntPtr(input, 8), bytes, 0, bytes.Length);
                if (count != 0)
                {
                    if (probe.Inputs.Count >= 4096) throw new InvalidOperationException("Input block bound exceeded.");
                    probe.Inputs.Add((frame, bytes));
                }
                if (!probe._split || count == 0)
                    return probe.CallStep(engine, state, input, output, metadata);
                nint mutex = Marshal.ReadIntPtr(state, 0x2aef0);
                uint wait = WaitForSingleObject(mutex, 1000);
                if (wait != 0) throw new InvalidOperationException("Probe lock unavailable.");
                try { Feed(state, frame, bytes, probe._target, probe._coefficient,
                    ref probe._ordinal, ref probe._msb, ref probe._lsb, probe); }
                finally { ReleaseMutex(mutex); }
                long* empty = stackalloc long[2]; empty[0] = empty[1] = 0;
                return probe.CallStep(engine, state, (nint)empty, output, metadata);
            }
            catch (Exception error) { probe._error = error; return -1; }
        }

        private int CallStep(nint engine, nint state, nint input, nint output, nint metadata)
        {
            BeforeSynthesis?.Invoke(state);
            int result = ((delegate* unmanaged<nint, nint, nint, nint, int>)_step)(engine, input, output, metadata);
            AfterOutput?.Invoke(state, output);
            AfterStep?.Invoke(state);
            return result;
        }

        private void WriteSlot(nint value)
        {
            nint address = _table + 15 * 8;
            if (!VirtualProtect(address, 8, 0x40, out uint protection)) throw new InvalidOperationException("Cannot instrument probe vtable.");
            Marshal.WriteIntPtr(address, value);
            if (!VirtualProtect(address, 8, protection, out _)) throw new InvalidOperationException("Cannot restore page protection.");
        }

        public void Dispose() { WriteSlot(_step); _active = null; }
        [DllImport("kernel32")] private static extern uint WaitForSingleObject(nint handle, uint milliseconds);
        [DllImport("kernel32")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool ReleaseMutex(nint handle);
        [DllImport("kernel32")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool VirtualProtect(nint address, nuint size, uint protection, out uint old);
    }
}
