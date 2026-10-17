using System.Diagnostics;
using System.IO;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using Yamaha.VOCALOID.VSM;

internal static partial class Program
{
    private static int ProbeMetadataTiming(WIVSMSequenceManager manager, string directory, bool directEvents)
    {
        Directory.CreateDirectory(directory);
        using var counters = new TimingDseCounters();
        using var probe = new MetadataTimingReplay(counters.VtableRva);
        probe.DirectEvents = directEvents;
        var results = new List<object>();
        var fixtures = new[]
        {
            new TimingFixture("three", ["a", "k a", "n a"], [480, 960, 1440], [480, 480, 480], 1),
            new TimingFixture("alone", ["k a"], [960], [480], 0),
            new TimingFixture("previous", ["a", "k a"], [480, 960], [480, 480], 1),
            new TimingFixture("following", ["k a", "n a"], [960, 1440], [480, 480], 0),
            new TimingFixture("five", ["h e", "r M", "a", "k a", "n a"], [0, 480, 960, 1440, 1920], [480, 480, 480, 480, 480], 3),
            new TimingFixture("five_crop_three", ["a", "k a", "n a"], [960, 1440, 1920], [480, 480, 480], 1),
            new TimingFixture("gap", ["a", "k a", "n a"], [480, 1080, 1800], [360, 480, 360], 1),
            new TimingFixture("short", ["a", "k a", "n a"], [480, 600, 720], [120, 120, 120], 1),
            new TimingFixture("evec", ["a", "k k k k#6 a", "n a"], [480, 960, 1440], [480, 480, 480], 1),
            new TimingFixture("shift_tempo", ["a", "k a", "n a"], [480, 960, 1440], [480, 480, 480], 1,
                PartStart: 3840, TempoChangeTick: 910),
            new TimingFixture("rin", ["a", "k k k a", "n a"], [480, 960, 1440], [480, 480, 480], 1,
                Bank: "BKKP765AEHXWSKDB"),
            // The oracle stays short. Only the canceled candidate gains a long
            // tail, to measure VSM preparation overhead separately from planning.
            new TimingFixture("tail_1000", ["a", "k a", "n a"], [480, 960, 1440], [480, 480, 480], 1,
                TailNotes: 1000, CandidateDuration: 130000),
            new TimingFixture("long_empty_tail", ["a", "k a", "n a"], [480, 960, 1440], [480, 480, 480], 1,
                CandidateDuration: 130000),
        };
        foreach (var fixture in fixtures)
        {
            byte[][]? baselineInputs = null;
            foreach (int velocity in new[] { 64, 32, 96 })
            {
                var sequence = manager.CreateSequence(new VSMSequenceData
                { SamplingRate = VSMSamplingRate._44100, MaxNumTracks = 4, MaxUndoCount = 0 })!;
                try
                {
                    sequence.InterpolatedMasterVolume();
                    sequence.InsertTimeSig(0, new VSMTimeSigEvent(4, 4));
                    sequence.InsertTempo(VSMRelTick.Zero, 12000);
                    if (fixture.TempoChangeTick >= 0)
                        sequence.InsertTempo(new VSMRelTick(fixture.PartStart + fixture.TempoChangeTick), 9000);
                    var track = (WIVSMMidiTrack)sequence.InsertTrackEx(0, VSMTrackType.Midi, "timing")!;
                    var part = track.InsertPart(new VSMAbsTick(fixture.PartStart), new VSMRelTick(2880), "timing")!;
                    if (!part.SetVoiceBankID(fixture.Bank)) throw new InvalidOperationException("Fixture bank unavailable.");
                    var expression = part.GetDefaultNoteExpression();
                    WIVSMNote? target = null;
                    for (int i = 0; i < fixture.Phonemes.Length; i++)
                    {
                        var note = part.InsertNote(new VSMRelTick(fixture.Positions[i]),
                            new VSMNoteEvent(fixture.Durations[i], i == fixture.Target ? 60 : 62,
                                i == fixture.Target ? velocity : 64), expression,
                            part.GetDefaultAiNoteExpression(), "fixture", fixture.Phonemes[i], true, 0)!;
                        if (i == fixture.Target) target = note;
                    }
                    if (target == null) throw new InvalidOperationException("Missing target.");
                    sequence.Commit(false);
                    probe.Capture();
                    var referenceWatch = Stopwatch.StartNew();
                    var referenceBefore = counters.Read();
                    RenderTransitionWithScore(sequence, part, Path.Combine(directory, $"{fixture.Name}-{velocity}.wav"));
                    double referenceWallMs = referenceWatch.Elapsed.TotalMilliseconds;
                    var referenceDse = counters.Read().Since(referenceBefore);
                    probe.Check();
                    int[] boundaries = target.GetPhonemePositions().ToArray();
                    var reference = probe.Records.ToArray();
                    int inputFrames = probe.Inputs.Count;
                    if (velocity == 64) baselineInputs = probe.Inputs.Select(bytes => bytes.ToArray()).ToArray();
                    byte[][] predictedInputs = RewriteTimingVelocity(baselineInputs!, fixture.Target, velocity);
                    bool inputsEqual = predictedInputs.Length == probe.Inputs.Count &&
                        predictedInputs.Zip(probe.Inputs).All(pair => pair.First.SequenceEqual(pair.Second));
                    if (!inputsEqual) throw new InvalidOperationException("Locally changed VEL input differs from fresh VSM input.");
                    probe.Inputs.Clear();
                    probe.Inputs.AddRange(predictedInputs);
                    probe.Replay();
                    if (fixture.CandidateDuration != 2880 &&
                        !part.SetDuration(new VSMRelTick(fixture.CandidateDuration)))
                        throw new InvalidOperationException("Cannot extend isolated benchmark.");
                    for (int i = 0; i < fixture.TailNotes; i++)
                        part.InsertNote(new VSMRelTick(4000 + i * 120), new VSMNoteEvent(120, 60, 64),
                            expression, part.GetDefaultAiNoteExpression(), "fixture", "a", true, 0);
                    // Invalidate the private reference so VSM enters a new engine session.
                    target.NoteVelocity = 0;
                    sequence.Commit(false);
                    // Keep the host Part at the baseline VEL. Only the independently
                    // changed event input may produce the candidate's new timing.
                    target.NoteVelocity = 64;
                    sequence.Commit(false);
                    int status = 0;
                    using var observer = new WIVSMRendererObserver();
                    observer.Canceled += (_, _) => status = -1;
                    observer.Completed += (_, _) => status = 1;
                    sequence.AddRendererObserver(observer);
                    var before = counters.Read();
                    var timer = Stopwatch.StartNew();
                    try
                    {
                        sequence.StartAsyncRendering();
                        while (status == 0 && timer.Elapsed < TimeSpan.FromSeconds(15))
                        {
                            for (int count = 0; count < 8 && sequence.InvokeRendererObserver(); count++) { }
                            if (status == 0) Thread.Sleep(5);
                        }
                    }
                    finally { sequence.StopAsyncRendering(); sequence.RemoveRendererObserver(observer); }
                    probe.Check();
                    var delta = counters.Read().Since(before);
                    string[] activeReference = reference.Where(IsActiveTimingRecord).ToArray();
                    string[] activeEarly = probe.EarlyRecords.Where(IsActiveTimingRecord).ToArray();
                    bool earlyEqual = activeReference.SequenceEqual(activeEarly);
                    bool equal = directEvents ? earlyEqual : reference.SequenceEqual(probe.Records);
                    Console.WriteLine($"metadata.fixture={fixture.Name};v={velocity};active={activeReference.Length}/{activeEarly.Length};equal={equal};steps={delta.Steps};samples={delta.Samples};computeMs={probe.ElapsedMs:F3};startMs={probe.StartMs:F3};wallMs={timer.Elapsed.TotalMilliseconds:F3}");
                    results.Add(new { fixture = fixture.Name, notes = fixture.Phonemes.Length, velocity, boundaries,
                        bank = fixture.Bank, fixture.PartStart, fixture.TempoChangeTick,
                        fixture.TailNotes, fixture.CandidateDuration, directEvents,
                        hostVelocity = 64, planningCalls = probe.PlanningCalls,
                        nativeFrameAfter = probe.NativeFrameAfter,
                        inputFrames, reference, metadata = probe.Records.ToArray(), inputsEqual,
                        equal, earlyEqual, earlyInputFrames = probe.EarlyInputFrames,
                        earlyMs = probe.EarlyMs, activeReference, activeEarly,
                        dse = delta, referenceDse, referenceWallMs, computeMs = probe.ElapsedMs,
                        startMs = probe.StartMs, wallMs = timer.Elapsed.TotalMilliseconds, status });
                    if (status != -1 || delta.Steps != 0 || delta.Samples != 0 || !equal || !earlyEqual)
                        throw new InvalidOperationException("Metadata-only comparison failed.");
                }
                finally { sequence.Close(); }
                File.WriteAllText(Path.Combine(directory, directEvents ? "direct-timing.json" : "metadata-timing.json"),
                    JsonSerializer.Serialize(results, new JsonSerializerOptions { WriteIndented = true }));
            }
        }
        return 0;
    }

    private sealed record TimingFixture(string Name, string[] Phonemes, int[] Positions, int[] Durations, int Target,
        string Bank = "BCNFCY43LB2LZCD4", int PartStart = 0, int TempoChangeTick = -1,
        int TailNotes = 0, int CandidateDuration = 2880, int InitialTempo = 12000,
        int ChangedTempo = 9000, int ExpressionSeed = 0, bool ControllerVariant = false);

    private static bool IsActiveTimingRecord(string value) => int.Parse(value.Split(',')[6]) >= 0;

    // Controlled fixture experiment: edit the target's NRPN 0x50:0x03 value in
    // baseline input. No candidate-velocity render is used to prepare this input.
    // Fresh VSM input is captured only as the independent comparison oracle.
    private static byte[][] RewriteTimingVelocity(byte[][] source, int targetOrdinal, int velocity)
    {
        var result = source.Select(bytes => bytes.ToArray()).ToArray();
        int msb = -1, lsb = -1, ordinal = 0, changed = 0;
        foreach (byte[] bytes in result)
        for (int offset = 0; offset < bytes.Length; offset += 8)
        {
            if (bytes[offset + 4] != 0xb0) continue;
            int controller = bytes[offset + 5], value = bytes[offset + 6];
            if (controller == 99) msb = value;
            else if (controller == 98) lsb = value;
            else if (controller == 6 && msb == 0x50 && lsb == 3)
            {
                if (ordinal++ != targetOrdinal) continue;
                bytes[offset + 6] = checked((byte)velocity);
                changed++;
            }
        }
        if (changed != 1) throw new InvalidOperationException("Target VEL event was not unique.");
        return result;
    }

    private sealed unsafe class MetadataTimingReplay : IDisposable
    {
        private static MetadataTimingReplay? _active;
        private readonly nint _dse;
        private readonly nint _vtable;
        private readonly nint _start;
        private readonly nint _step;
        private readonly delegate* unmanaged<nint, nint, nint, int> _metadataStep;
        private readonly delegate* unmanaged<nint, int, nint> _frameAt;
        private readonly delegate* unmanaged<nint, int> _plannedEnd;
        private readonly delegate* unmanaged<nint, int, nint, void> _parseEvents;
        private string? _error;
        private bool _replay;
        private int _lastQueuedEnd = -1;
        internal List<byte[]> Inputs { get; } = new();
        internal List<string> Records { get; } = new();
        internal HashSet<int> SampleBoundaries { get; } = new();
        internal double ElapsedMs { get; private set; }
        internal double StartMs { get; private set; }
        internal List<string> EarlyRecords { get; } = new();
        internal int EarlyInputFrames { get; private set; }
        internal double EarlyMs { get; private set; }
        internal bool DirectEvents { get; set; }
        internal int PlanningCalls { get; private set; }
        internal int NativeFrameAfter { get; private set; }

        internal MetadataTimingReplay(ulong vtableRva)
        {
            string hash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(Path.Combine(EditorDirectory, "DSE.dll"))));
            var profile = VOCALOIDPatcher.ConsonantTiming.LocalTimingNative.Profile;
            if (hash != profile.DseSha256 || vtableRva != (ulong)profile.DseEngineVtable)
                throw new InvalidOperationException("Unrecognized engine image.");
            _dse = ResolveEditorLibrary("dse");
            _vtable = _dse + (nint)vtableRva;
            _start = Marshal.ReadIntPtr(_vtable, 10 * 8);
            _step = Marshal.ReadIntPtr(_vtable, 15 * 8);
            _metadataStep = (delegate* unmanaged<nint, nint, nint, int>)Marshal.ReadIntPtr(_vtable, 14 * 8);
            if ((nint)_metadataStep != _dse + 0x1d1630) throw new InvalidOperationException("Metadata slot mismatch.");
            _frameAt = (delegate* unmanaged<nint, int, nint>)(_dse + 0x18b260);
            _plannedEnd = (delegate* unmanaged<nint, int>)(_dse + 0x18aba0);
            _parseEvents = (delegate* unmanaged<nint, int, nint, void>)(_dse + 0x1b2ab0);
            _active = this;
            WriteSlot(10, (nint)(delegate* unmanaged<nint, int>)&Start);
            WriteSlot(15, (nint)(delegate* unmanaged<nint, nint, nint, nint, int>)&Step);
        }

        internal void Capture() { _replay = false; _lastQueuedEnd = -1; Inputs.Clear(); Records.Clear(); EarlyRecords.Clear(); SampleBoundaries.Clear(); _error = null; }
        internal void Replay()
        {
            _replay = true; Records.Clear(); EarlyRecords.Clear(); PlanningCalls = 0; _error = null;
        }
        internal void Check() { if (_error != null) throw new InvalidOperationException(_error); }

        private nint State(nint engine)
        {
            int slot = Marshal.ReadInt32(engine, 0x10);
            if (slot is < 0 or > 31) throw new InvalidOperationException("Invalid engine slot.");
            nint state = Marshal.ReadIntPtr(_dse + 0xaa69d0, slot * 8);
            if (state == 0) throw new InvalidOperationException("Missing engine state.");
            return state;
        }

        private void ReadRecords(nint state, int first, int last, List<string>? destination = null)
        {
            if (last < first || last - first > (destination == null ? 128 : 10000))
                throw new InvalidOperationException("Unexpected frame advance.");
            var records = destination ?? Records;
            nint schedule = Marshal.ReadIntPtr(state, 0x1d568);
            for (int frame = first; frame < last; frame++)
            {
                nint row = Marshal.ReadIntPtr(_frameAt(schedule, frame));
                if (row == 0) continue;
                int from = Marshal.ReadInt32(row, 16), to = Marshal.ReadInt32(row, 20);
                if (Marshal.ReadInt32(row, 0x11c) >= 0 && from is >= 0 and <= 255 && to is >= -1 and <= 255)
                {
                    // Independent sample junctions are real phoneme boundaries
                    // too; they need not be the split inside a diphone sample.
                    SampleBoundaries.Add(Marshal.ReadInt32(row));
                    SampleBoundaries.Add(Marshal.ReadInt32(row,4));
                    SampleBoundaries.Add(checked(Marshal.ReadInt32(row) + Marshal.ReadInt32(row, to == -1 ? 0x118 : 8)));
                }
                int[] offsets = [0, 4, 8, 16, 20, 0x118, 0x11c, 0x120];
                string value = string.Join(',', offsets.Select(offset => Marshal.ReadInt32(row, offset)));
                if (records.Count == 0 || records[^1] != value) records.Add(value);
                if (records.Count > 10000) throw new InvalidOperationException("Schedule exceeds probe bound.");
            }
        }

        [UnmanagedCallersOnly]
        private static int Start(nint engine)
        {
            var probe = _active!;
            try
            {
                var startWatch = Stopwatch.StartNew();
                int result = ((delegate* unmanaged<nint, int>)probe._start)(engine);
                probe._lastQueuedEnd = -1;
                probe.StartMs = startWatch.Elapsed.TotalMilliseconds;
                if (result != 0 || !probe._replay) return result;
                nint state = probe.State(engine);
                var watch = Stopwatch.StartNew();
                byte* metadata = stackalloc byte[128];
                long* input = stackalloc long[2];
                int lastEvent = probe.Inputs.FindLastIndex(bytes => bytes.Length != 0);
                for (int index = 0; index < probe.Inputs.Count; index++)
                {
                    byte[] bytes = probe.Inputs[index];
                    if (probe.DirectEvents && bytes.Length == 0) continue;
                    fixed (byte* payload = bytes)
                    {
                        input[0] = bytes.Length / 8;
                        input[1] = (long)payload;
                        int first = Marshal.ReadInt32(state, 0x24158);
                        if (probe.DirectEvents)
                        {
                            probe._parseEvents(state + 0x1d570, index, (nint)input);
                            probe.PlanningCalls++;
                        }
                        else
                        {
                            if (probe._metadataStep(engine, (nint)input, (nint)metadata) != 0)
                                throw new InvalidOperationException("Metadata step rejected input.");
                            probe.ReadRecords(state, first, Marshal.ReadInt32(state, 0x24158));
                        }
                        if (index == lastEvent)
                        {
                            nint schedule = Marshal.ReadIntPtr(state, 0x1d568);
                            int end = probe._plannedEnd(schedule);
                            int capacity = Marshal.ReadInt32(schedule, 8);
                            if (end < 0 || end > 10000 || capacity <= 0)
                                throw new InvalidOperationException("Future schedule exceeds fixture bound.");
                            probe.ReadRecords(state, Math.Max(0, end - capacity), end, probe.EarlyRecords);
                            probe.EarlyInputFrames = index + 1;
                            probe.EarlyMs = watch.Elapsed.TotalMilliseconds;
                        }
                    }
                }
                probe.ElapsedMs = watch.Elapsed.TotalMilliseconds;
                probe.NativeFrameAfter = Marshal.ReadInt32(state, 0x24158);
                return -1; // Expected cancellation: never enter PCM Step or WAV publication.
            }
            catch (Exception exception) { probe._error = exception.GetType().Name + ": " + exception.Message; return -1; }
        }

        [UnmanagedCallersOnly]
        private static int Step(nint engine, nint input, nint output, nint metadata)
        {
            var probe = _active!;
            try
            {
                if (probe._replay) throw new InvalidOperationException("PCM step entered during metadata replay.");
                int count = Marshal.ReadInt32(input);
                if (count is < 0 or > 4096 || probe.Inputs.Count >= 10000)
                    throw new InvalidOperationException("Unexpected MIDI input size.");
                byte[] bytes = new byte[count * 8];
                if (bytes.Length != 0) Marshal.Copy(Marshal.ReadIntPtr(input, 8), bytes, 0, bytes.Length);
                probe.Inputs.Add(bytes);
                nint state = probe.State(engine);
                int first = Marshal.ReadInt32(state, 0x24158);
                int result = ((delegate* unmanaged<nint, nint, nint, nint, int>)probe._step)(engine, input, output, metadata);
                int next = Marshal.ReadInt32(state, 0x24158);
                probe.ReadRecords(state, first, next);
                // Step can enqueue the final release transition ahead of the
                // frame cursor and finish without advancing through it again.
                // Inspect a bounded tail of this real synthesis engine's queue,
                // separate from the independently prepared query engine.
                nint schedule = Marshal.ReadIntPtr(state,0x1d568);
                int end = probe._plannedEnd(schedule);
                if(end>next && end!=probe._lastQueuedEnd)
                {
                    probe.ReadRecords(state,Math.Max(next,end-128),end,probe.EarlyRecords);
                    probe._lastQueuedEnd=end;
                }
                return result;
            }
            catch (Exception exception) { probe._error = exception.GetType().Name + ": " + exception.Message; return -1; }
        }

        private void WriteSlot(int slot, nint value)
        {
            nint address = _vtable + slot * 8;
            if (!VirtualProtect(address, 8, 0x40, out uint protection)) throw new InvalidOperationException("Cannot instrument private vtable.");
            Marshal.WriteIntPtr(address, value);
            VirtualProtect(address, 8, protection, out _);
        }

        public void Dispose() { WriteSlot(10, _start); WriteSlot(15, _step); _active = null; }

        [DllImport("kernel32", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool VirtualProtect(nint address, nuint size, uint protection, out uint old);
    }
}
