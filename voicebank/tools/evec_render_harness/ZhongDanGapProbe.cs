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
    // Reads a saved project copy. All experiments and WAVs belong to a private SDK sequence.
    private static int ProbeZhongDanGap(WIVSMSequenceManager manager, DSEManager dse,
        DatabaseManager database, string directory)
    {
        string project = Environment.GetEnvironmentVariable("VOCALOID_GENG_PROJECT")!;
        NativeTransitionGain.Initialize(Environment.GetEnvironmentVariable("VOCALOID_TIMING_PROBE_NATIVE")!);
        using (var zip = System.IO.Compression.ZipFile.OpenRead(project))
        {
            var entry = zip.GetEntry("Project/sequence.json") ?? throw new InvalidDataException("Missing sequence.");
            Require(entry.Length < 10 * 1024 * 1024, "Project exceeds probe bound.");
            using var input = entry.Open(); using var output = File.Create(Path.Combine(directory, "sequence.json")); input.CopyTo(output);
        }
        var source = manager.OpenSequence(Path.Combine(directory, "sequence.json"), EditorDirectory,
            new VSMSequenceData { SamplingRate = VSMSamplingRate._44100, MaxNumTracks = 4, MaxUndoCount = 0 })!;
        try
        {
            var track = (WIVSMMidiTrack)source.GetTrack(0UL)!;
            var part = (WIVSMMidiPart)track.GetPart(1UL)!;
            var notes = part.NotesEnumerator.ToArray();
            Require(notes.Length == 18 && notes[16].Phonemes == "ts` UN" && notes[17].Phonemes == "t a_n", "Unexpected captured target.");
            var archive = SampledTimingProjectArchive.Read(project);
            var plans = notes.ToDictionary(n => (nint)n, n => archive.GetValueOrDefault(SampledTimingVsmBinding.Address(n)) ?? new(n.Phonemes, []));
            var stored = TransitionProjectArchive.Read(project).Data!.KeepNotes(n => n.Address.Track == 0 && n.Address.Part == 1);
            // Keep note pointers and tempo/controllers; isolate rendering to the target Part.
            for (ulong t = 0; t < source.NumTrack; t++)
                if (source.GetTrack(t) is WIVSMMidiTrack midi)
                    foreach (var other in midi.PartsEnumerator.OfType<WIVSMMidiPart>().Where(p => (nint)p != (nint)part).ToArray())
                        Require(midi.RemovePart(other), "Cannot isolate target Part.");
            stored = stored with { Notes = stored.Notes.Select(n => n with { Address = n.Address with { Part = 0 } }).ToImmutableArray() };
            source.InterpolatedMasterVolume(); source.Commit(false);
            var reports = new List<object>(); ulong epoch = 170000;
            using var counters = new TimingDseCounters();
            using var capture = new TimingCoefficientHook();
            using var backend = new LocalConsonantTimingBackend(manager, dse, database);
            string? baselineHash = null;
            var context = notes.Select(n => new { position = n.RelPosTick.Value, duration = n.DurationTick.Value,
                pitch = n.NoteNumber, velocity = n.NoteVelocity, phones = n.Phonemes, controls = plans[(nint)n].Adjustments }).ToArray();
            var originalDan = plans[(nint)notes[17]];
            var originalZhong = plans[(nint)notes[16]];
            int originalVelocity = notes[17].NoteVelocity;

            void Test(string name, Action? change = null, Func<TransitionLayout, TransitionControlPlan, TransitionControlPlan>? edit = null, bool neutralTransitions = false)
            {
                NativeSampledTiming.Remove((nint)part);
                plans[(nint)notes[17]] = originalDan; plans[(nint)notes[16]] = originalZhong;
                notes[17].NoteVelocity = originalVelocity;
                change?.Invoke(); source.Commit(false);
                using var prepared = backend.PrepareCoefficient(notes[notes.Length / 2], 1,
                    coefficientForNote: n => plans[(nint)n].PhonemeCoefficient(n.Phonemes), planForNote: n => plans[(nint)n]);
                var before = counters.Read();
                var layout = prepared.QueryTransitions(plans[(nint)notes[notes.Length / 2]], 1);
                var controls = neutralTransitions ? new TransitionControlPlan(layout.Fingerprint) : TransitionProjectBinding.ResolveAvailable(stored, layout);
                controls = edit?.Invoke(layout, controls) ?? controls;
                var execution = prepared.QueryTransitionControls(layout, controls, 1);
                var queryCounters = counters.Read().Since(before);
                Require(queryCounters == new TimingDseSnapshot(0, 0, 0), "Gap planning synthesized audio.");
                prepared.ValidateTransitionCommit(part, execution, 1); prepared.PublishTransitions(part, ++epoch, execution, 1);
                SampledTimingVsmBinding.InvalidateRenderedPart(part, notes);
                var observed = new Dictionary<int, TransitionFrame>(); int consumed = 0;
                capture.Reset(-1, false, null);
                capture.AfterStep = state =>
                {
                    int cursor = Marshal.ReadInt32(state, LocalTimingNative.Profile.StateCurrentFrame);
                    foreach (var row in NativeConsonantTimingEngine.ReadTransitionSchedule(LocalTimingNative.Dse, state, 0, consumed, cursor))
                        foreach (var frame in row.Frames) observed[frame.Frame] = frame;
                    consumed = cursor;
                };
                string wave = Path.Combine(directory, name + ".wav");
                try { RenderTransitionWithScore(source, part, wave); capture.Check(); }
                finally { capture.AfterStep = null; }
                var status = NativeSampledTiming.ReadStatus((nint)part); var gain = NativeTransitionGain.ReadStatus((nint)part);
                bool map = execution.Segments.Where(r => r.To != null).SelectMany(r => r.Frames).Where(f => f.Frame < consumed)
                    .All(f => observed.TryGetValue(f.Frame, out var actual) && actual == f);
                Require(map && status.Failures == 0 && gain.Failures == 0, "Gap render certification failed.");
                var targets = layout.Notes.Where(n => n.Address.Note >= 15).ToArray();
                int center = targets.Single(n => n.Address.Note == 17).Original[0];
                var rows = execution.Segments.Where(r => r.End >= center - 40 && r.Begin <= center + 50).ToArray();
                var pcm = ReadAceWave(wave);
                var energy = new List<object>();
                for (int frame = center - 35; frame < center + 55; frame++)
                {
                    int begin = Math.Clamp((frame - 3087) * 256 * pcm.Channels, 0, pcm.Samples.Length);
                    int end = Math.Clamp(begin + 256 * pcm.Channels, begin, pcm.Samples.Length);
                    double sum = 0, peak = 0;
                    for (int i = begin; i < end; i++) { double v = pcm.Samples[i] / 32768d; sum += v * v; peak = Math.Max(peak, Math.Abs(v)); }
                    energy.Add(new { frame, rmsDb = 10 * Math.Log10(Math.Max(1e-12, sum / Math.Max(1, end - begin))), peak });
                }
                string hash = TimingPcmHash(wave);
                if (name == "stored") baselineHash = hash;
                if (name == "restored") Require(hash == baselineHash, "Gap experiment did not restore stored PCM.");
                var report = new { name, hash, map, status.Failures, gainFailures = gain.Failures, queryCounters,
                    velocity = notes[17].NoteVelocity, targets, layout.Anchors, limits = layout.BoundaryLimits?.ToArray(),
                    nativeBoundaries = notes.Skip(16).Select(n => n.GetPhonemePositions().ToArray()).ToArray(),
                    controls = new { controls.Junctions, controls.Gains, controls.Phases, controls.Coverages },
                    edges = layout.Edges.Where(e => e.Identity.Left?.Note.Note >= 15 || e.Identity.Right?.Note.Note >= 15)
                        .Select(e => new { e.Identity, original = new { e.Original.Begin, e.Original.Split, e.Original.End, e.Original.MaterialMetadata },
                            current = new { e.Current.Begin, e.Current.Split, e.Current.End }, e.Current.MaterialCoverage }),
                    junctions = layout.Junctions.Where(j => j.CurrentFrame >= center - 40 && j.CurrentFrame <= center + 60),
                    rows = rows.Select(r => new { r.Begin, r.Split, r.End, r.From, r.To, r.Stage, r.Reverse, r.MaterialMetadata,
                        r.MaterialCoverage, frames = r.Frames.Select(f => new { f.Frame, source = BitConverter.Int32BitsToSingle(f.SourceBits), f.Flag0, f.Flag1 }) }),
                    energy };
                reports.Add(report);
                File.WriteAllText(Path.Combine(directory, "gap-renders.json"), JsonSerializer.Serialize(new { context, reports }, GapJson));
                Console.WriteLine($"gap.render={name}; map={map}; failures={status.Failures}/{gain.Failures}");
            }
            void Attempt(string name, Action? change = null, Func<TransitionLayout, TransitionControlPlan, TransitionControlPlan>? edit = null)
            {
                try { Test(name, change, edit); }
                catch (NotSupportedException e)
                { reports.Add(new { name, rejected = e.Message }); Console.WriteLine($"gap.rejected={name}; {e.Message}"); }
            }
            Test("warm"); Test("stored"); Test("neutral-transitions", neutralTransitions: true);
            if (Environment.GetEnvironmentVariable("VOCALOID_GAP_MATRIX") == "1")
            {
                foreach (int velocity in new[] { 0, 64, 127 }) Attempt("vel" + velocity, () => notes[17].NoteVelocity = velocity);
                foreach (int axis in new[] { 0, 1 }) foreach (int delta in new[] { -4, -2, 2, 4 })
                    Attempt($"dan-boundary{axis}-{delta}", () => plans[(nint)notes[17]] = new(notes[17].Phonemes,
                        originalDan.Adjustments.Where(a => a.Kind != TimingAdjustment.PhonemeBoundary || a.Index != axis)
                            .Append(new(TimingAdjustment.PhonemeBoundary, axis, (float)(delta * 256d / 44100)))));
                foreach (string from in new[] { "UN", "t" }) foreach (float amount in new[] { -1f, 1f })
                    Attempt($"phase-{from}-{amount}", edit: (layout, plan) =>
                    {
                        var edge = layout.Edges.Single(e => e.Identity.From == from && e.Identity.Right?.Note.Note == 17);
                        return TransitionExpressionControl.Progress(plan, edge.Current, edge.Identity, amount);
                    });
                foreach (string from in new[] { "UN", "t" }) foreach (var endpoint in new[] { TransitionEndpoint.Begin, TransitionEndpoint.End })
                    Attempt($"edge-{from}-{endpoint}", edit: (layout, plan) =>
                    {
                        var edge = layout.Edges.Single(e => e.Identity.From == from && e.Identity.Right?.Note.Note == 17);
                        var key = new TransitionEndpointRef(edge.Identity, endpoint); var joint = layout.Junction(key);
                        int goal = endpoint == TransitionEndpoint.Begin ? joint.MaximumCandidate : joint.MinimumCandidate;
                        return plan.WithJunction(layout, key, goal);
                    });
            }
            Test("restored"); NativeSampledTiming.Remove((nint)part);
            return 0;
        }
        finally { source.Close(); }
    }
    private static readonly JsonSerializerOptions GapJson = new() { WriteIndented = true, IncludeFields = true };
}
