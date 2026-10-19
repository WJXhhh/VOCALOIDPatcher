using System.IO;
using System.Collections.Immutable;
using System.Runtime.InteropServices;
using System.Text.Json;
using VOCALOIDPatcher.ConsonantTiming;
using Yamaha.VOCALOID.DSE;
using Yamaha.VOCALOID.VDM;
using Yamaha.VOCALOID.VSM;

internal static partial class Program
{
    // Research only: disposable sequences, no host editing, no installed files.
    private static int ProbeCompoundVowels(WIVSMSequenceManager manager, DSEManager dse, DatabaseManager database, string directory)
    {
        NativeTransitionGain.Initialize(Environment.GetEnvironmentVariable("VOCALOID_TIMING_PROBE_NATIVE")!);
        manager.CacheCapacity = 0;
        using var counters = new TimingDseCounters();
        using var backend = new LocalConsonantTimingBackend(manager, dse, database);
        string bank = Environment.GetEnvironmentVariable("VOCALOID_COMPOUND_BANK") ?? "BY98KLLZTDYH7YEE";
        var states = new List<object>(); ulong epoch = 71000;
        void Record(object value)
        {
            states.Add(value);
            File.WriteAllText(Path.Combine(directory, "compound-vowels.json"), JsonSerializer.Serialize(states, new JsonSerializerOptions { WriteIndented = true }));
        }
        object Row(TransitionSegment row) => new
        {
            row.From, row.To, row.Begin, row.Split, row.End, row.Stage, row.Reverse, row.MaterialMetadata,
            sourcePositions = row.To == null ? null : row.Frames.Select((f, i) => new { i, f.Frame, source = BitConverter.Int32BitsToSingle(f.SourceBits), f.Flag0, f.Flag1 })
                .Where((_, i) => i == 0 || i == row.Frames.Length - 1 || i == row.Frames.Length / 4 || i == row.Frames.Length / 2 || i == row.Frames.Length * 3 / 4).ToArray()
        };
        Record(new { kind = "metadata", bank, sdk = typeof(WIVSMSequence).Assembly.GetName().Version?.ToString(),
            profile = LocalTimingNative.Profile.Id, synthesisRate = 44100, hop = 256, cacheDisabled = true });
        foreach (string phones in new[] { "a", "aI", "uo", "iE_r", "s\\ iE_r", "a i" })
        foreach (int duration in new[] { 240, 960, 1920 })
        foreach (int velocity in new[] { 0, 64, 127 })
        {
            using var fixture = CreateStandaloneFixture(manager, 1, duration, 0, 7680);
            var note = fixture.Notes[0]; var part = fixture.Part; var source = fixture.Sequence;
            Require(part.SetVoiceBankID(bank), "Missing compound-vowel research bank.");
            part.MoveNote(new VSMRelTick(960), note); note.SetNoteNumber(60); note.NoteVelocity = velocity;
            note.SetPhonemes(phones, true, 4); source.InterpolatedMasterVolume(); source.Commit(false);
            using var prepared = backend.PrepareCoefficient(note, 1);
            var before = counters.Read();
            try
            {
                var layout = prepared.QueryTransitions(new(phones, []), 1);
                Require(counters.Read().Since(before) == new TimingDseSnapshot(0, 0, 0), "Compound query synthesized audio.");
                var plain = prepared.QueryTransitionControls(layout, new(layout.Fingerprint), 1);
                Record(new { kind = "plan", bank, phones, duration, velocity, original = layout.Notes[0].Original,
                    rows = plain.Segments.Select(Row).ToArray(), prepared.PreparationMs });
                if (duration != 960 || velocity != 64 || phones is not ("uo" or "iE_r" or "s\\ iE_r")) continue;
                string stem = phones == "s\\ iE_r" ? "xie" : phones;
                string? baseline = null;
                foreach (float phase in new[] { 0f, -.75f, .75f, 0f })
                {
                    var controls = new TransitionControlPlan(layout.Fingerprint);
                    foreach (var edge in layout.Edges.Where(e => TransitionPhaseMapping.CanEdit(e.Current))) controls = controls.WithPhase(edge.Identity, phase);
                    var execution = prepared.QueryTransitionControls(layout, controls, 1);
                    Require(execution.RenderAnchors.SequenceEqual(plain.RenderAnchors), "Compound phase moved protected geometry.");
                    Require(execution.Segments.Zip(plain.Segments).All(p => (p.First.Begin, p.First.Split, p.First.End) ==
                        (p.Second.Begin, p.Second.Split, p.Second.End)), "Compound phase changed work-item durations.");
                    prepared.PublishTransitions(part, ++epoch, execution, 1);
                    SampledTimingVsmBinding.InvalidateRenderedPart(part, [note]);
                    string path = Path.Combine(directory, stem + "-" + epoch + ".wav");
                    RenderTransitionWithScore(source, part, path);
                    var status = NativeSampledTiming.ReadStatus((nint)part); var gain = NativeTransitionGain.ReadStatus((nint)part);
                    Require(status.Failures == 0 && gain.Failures == 0 && part.HasValidRenderedWave, "Compound render route failed.");
                    string pcm = TimingPcmHash(path); baseline ??= pcm;
                    Require(phase == 0 ? pcm == baseline : pcm != baseline, "Compound phase/restoration PCM check failed.");
                    Record(new { kind = "render", phones, phase, pcm, unchangedGeometry = true,
                        rawPositions = NativeTimingPositions.Read(note, false), gain.Applied, gain.Failures, rows = execution.Segments.Select(Row).ToArray(),
                        file = Path.GetFileName(path) });
                }
                NativeSampledTiming.Remove((nint)part);
            }
            catch (NotSupportedException error)
            {
                Record(new { kind = "unsupported", bank, phones, duration, velocity, error = error.Message });
            }
        }
        using (var fixture = CreateStandaloneFixture(manager, 1, 960, 0, 7680))
        {
            var note = fixture.Notes[0]; var part = fixture.Part; var source = fixture.Sequence;
            part.SetVoiceBankID(bank); part.MoveNote(new VSMRelTick(960), note); note.SetNoteNumber(60);
            note.SetPhonemes("s\\ iE_r", true, 4); source.InterpolatedMasterVolume(); source.Commit(false);
            using var prepared = backend.PrepareCoefficient(note, 2);
            using var capture = new TimingCoefficientHook();
            var materials = new Dictionary<nint, int>();
            string? baselineHash = null; int[]? baselinePositions = null;
            foreach (string variant in new[] { "baseline", "phone-end-short", "phone-end-short-full-source", "phone-end-long",
                "onset-end-short", "onset-end-short-full-source", "onset-end-long",
                "release-begin-early", "onset-phase-early", "onset-phase-late", "release-phase-early", "release-phase-late",
                "sta-source-plus96", "sta-source-plus176", "restored" })
            {
                var plan = new SampledTimingPlan("s\\ iE_r", variant.StartsWith("phone-end-") ?
                    [new(TimingAdjustment.PhonemeBoundary, 2, (float)((variant.StartsWith("phone-end-short") ? -120 : 36) * 256d / 44100))] : []);
                var layout = prepared.QueryTransitions(plan, 2);
                var controls = new TransitionControlPlan(layout.Fingerprint);
                var onset = layout.Edges.Single(e => e.Identity.From == "s\\" && e.Identity.To == "iE_r");
                var release = layout.Edges.Single(e => e.Identity.From == "iE_r" && e.Identity.To == "Sil");
                if (variant.StartsWith("onset-end-"))
                    controls = controls.WithJunction(layout, new(onset.Identity, TransitionEndpoint.End), onset.Current.End + (variant.StartsWith("onset-end-short") ? -24 : 48));
                if (variant == "release-begin-early")
                    controls = controls.WithJunction(layout, new(release.Identity, TransitionEndpoint.Begin), release.Current.Begin - 96);
                if (variant.Contains("phase")) controls = controls.WithPhase(variant.StartsWith("onset") ? onset.Identity : release.Identity,
                    variant.EndsWith("early") ? -.9f : .9f);
                var execution = prepared.QueryTransitionControls(layout, controls, 2);
                if (variant.EndsWith("full-source"))
                {
                    int index = layout.Edges.IndexOf(onset);
                    var art = execution.Segments.Where(s => s.To != null).ElementAt(index);
                    var original = onset.Original;
                    var frames = art.Frames.Select(f =>
                    {
                        if (f.Frame < art.Split) return f;
                        int count = art.End - art.Split, first = original.Split - original.Begin;
                        double position = first + (f.Frame - (double)art.Split) / Math.Max(1, count - 1) * (original.Frames.Length - 1 - first);
                        int a = (int)Math.Floor(position), b = Math.Min(a + 1, original.Frames.Length - 1);
                        float value = (float)(BitConverter.Int32BitsToSingle(original.Frames[a].SourceBits) * (1 - position + a) +
                            BitConverter.Int32BitsToSingle(original.Frames[b].SourceBits) * (position - a));
                        return f with { SourceBits = BitConverter.SingleToInt32Bits(value) };
                    }).ToImmutableArray();
                    execution = execution with { SourceProgress = [new(onset.Identity, frames)],
                        Segments = execution.Segments.Select(s => ReferenceEquals(s, art) ? s with { Frames = frames } : s).ToImmutableArray() };
                }
                prepared.PublishTransitions(part, ++epoch, execution, 2);
                SampledTimingVsmBinding.InvalidateRenderedPart(part, [note]);
                capture.Reset(-1, false, null);
                int consumed = 0; var actual = new Dictionary<int, DurationRow>();
                int sourceShift = variant == "sta-source-plus96" ? 96 : variant == "sta-source-plus176" ? 176 : 0;
                var shifted = new HashSet<nint>();
                capture.BeforeSynthesis = state =>
                {
                    if (sourceShift == 0) return;
                    nint schedule = Marshal.ReadIntPtr(state, LocalTimingNative.Profile.StateSchedule);
                    int end = ReadCompoundScheduleEnd(schedule);
                    for (int frame = Math.Max(3087, Marshal.ReadInt32(state, LocalTimingNative.Profile.StateCurrentFrame)); frame < end; frame++)
                    {
                        nint entry = ReadCompoundScheduleRow(schedule, frame); nint row = Marshal.ReadIntPtr(entry);
                        if (row == 0 || Marshal.ReadInt32(row, 0x11c) < 0 || Marshal.ReadInt32(row, 0x10) != onset.Current.ToId ||
                            Marshal.ReadInt32(row, 0x14) != -1 || Marshal.ReadByte(row, 0x90) == 0 || !shifted.Add(row)) continue;
                        int start = Marshal.ReadInt32(row, 0x94), span = Marshal.ReadInt32(row, 4) - Marshal.ReadInt32(row);
                        nint material = Marshal.ReadIntPtr(row, 0x20);
                        Require(material != 0, "Owned compound STA has no material.");
                        Require(start >= 0 && start + sourceShift + span <= Marshal.ReadInt32(material, 0x1b0), "STA shift exceeds owned material.");
                        Marshal.WriteInt32(row, 0x94, start + sourceShift);
                    }
                };
                capture.AfterStep = state =>
                {
                    int cursor = Marshal.ReadInt32(state, LocalTimingNative.Profile.StateCurrentFrame);
                    foreach (var row in ReadDurationRows(state, materials, consumed, cursor)) actual[row.Begin] = row;
                    consumed = cursor;
                };
                string file = "xie-" + variant + ".wav";
                try { RenderTransitionWithScore(source, part, Path.Combine(directory, file)); capture.Check(); }
                finally { capture.AfterStep = null; capture.BeforeSynthesis = null; }
                var route = NativeSampledTiming.ReadStatus((nint)part); var gain = NativeTransitionGain.ReadStatus((nint)part);
                Require(route.Failures == 0 && gain.Failures == 0 && ReadWaveMetrics(Path.Combine(directory, file)).Peak > .00001, "Xie control failed.");
                string pcm = TimingPcmHash(Path.Combine(directory, file));
                int[] positions = NativeTimingPositions.Read(note, false);
                baselineHash ??= pcm; baselinePositions ??= positions;
                if (variant == "restored") Require(pcm == baselineHash, "Xie restoration changed PCM.");
                if (sourceShift != 0) Require(shifted.Count > 0 && pcm != baselineHash && positions.SequenceEqual(baselinePositions), "STA source experiment failed.");
                Record(new { kind = "xie-case", variant, file, pcm = TimingPcmHash(Path.Combine(directory, file)),
                    sourceShift, shiftedItems = shifted.Count,
                    anchors = execution.RenderAnchors, rawPositions = NativeTimingPositions.Read(note, false),
                    planned = execution.Segments.Select(Row).ToArray(), actual = actual.Values.OrderBy(r => r.Begin).ToArray() });
                Console.WriteLine("compound.xie=" + variant);
            }
            NativeSampledTiming.Remove((nint)part);
        }
        Record(new { kind = "complete" });
        return 0;
    }

    private static unsafe int ReadCompoundScheduleEnd(nint schedule)
    {
        int end = ((delegate* unmanaged<nint, int>)(LocalTimingNative.Dse + LocalTimingNative.Profile.DseScheduleEnd))(schedule);
        Require(end is >= 0 and <= 7183, "Compound schedule exceeds probe bound."); return end;
    }
    private static unsafe nint ReadCompoundScheduleRow(nint schedule, int frame)
        => ((delegate* unmanaged<nint, int, nint>)(LocalTimingNative.Dse + LocalTimingNative.Profile.DseScheduleRow))(schedule, frame);
}
