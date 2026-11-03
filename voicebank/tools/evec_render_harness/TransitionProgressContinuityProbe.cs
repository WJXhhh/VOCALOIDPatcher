using System.IO;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Text.Json;
using VOCALOIDPatcher.ConsonantTiming;
using Yamaha.VOCALOID.DSE;
using Yamaha.VOCALOID.VDM;
using Yamaha.VOCALOID.VSM;

internal static partial class Program
{
    // A saved-project copy is loaded into a private SDK sequence. No Editor
    // instance, source file or source note event is changed by this probe.
    private static int ProbeTransitionProgressContinuity(WIVSMSequenceManager manager, DSEManager dse,
        DatabaseManager database, string directory)
    {
        string project = Environment.GetEnvironmentVariable("VOCALOID_GENG_PROJECT")!;
        NativeTransitionGain.Initialize(Environment.GetEnvironmentVariable("VOCALOID_TIMING_PROBE_NATIVE")!);
        using (var zip = ZipFile.OpenRead(project))
        using (var input = zip.GetEntry("Project/sequence.json")!.Open())
        using (var output = File.Create(Path.Combine(directory, "sequence.json"))) input.CopyTo(output);
        var source = manager.OpenSequence(Path.Combine(directory, "sequence.json"), EditorDirectory,
            new VSMSequenceData { SamplingRate = VSMSamplingRate._44100, MaxNumTracks = 4, MaxUndoCount = 0 })!;
        try
        {
            var part = (WIVSMMidiPart)((WIVSMMidiTrack)source.GetTrack(0UL)!).GetPart(0UL)!;
            var notes = part.NotesEnumerator.ToArray();
            Require(notes.Length == 18 && notes[2].Phonemes == "t_h iE_n", "Unexpected continuity fixture.");
            var saved = SampledTimingProjectArchive.Read(project);
            var stored = TransitionProjectArchive.Read(project).Data!;
            for (ulong t = 0; t < source.NumTrack; t++)
                if (source.GetTrack(t) is WIVSMMidiTrack track)
                    foreach (var other in track.PartsEnumerator.OfType<WIVSMMidiPart>().Where(p => (nint)p != (nint)part).ToArray())
                        Require(track.RemovePart(other), "Cannot isolate continuity Part.");
            source.InterpolatedMasterVolume(); source.Commit(false);
            using var backend = new LocalConsonantTimingBackend(manager, dse, database);
            using var prepared = backend.PrepareCoefficient(notes[2], 1,
                coefficientForNote: n => saved.GetValueOrDefault(SampledTimingVsmBinding.Address(n))?.PhonemeCoefficient(n.Phonemes),
                planForNote: n => saved.GetValueOrDefault(SampledTimingVsmBinding.Address(n)));
            var layout = prepared.QueryTransitions(saved.GetValueOrDefault(SampledTimingVsmBinding.Address(notes[2])) ?? new(notes[2].Phonemes, []), 1);
            var edge = layout.Edges.Single(e => e.Identity.From == "t_h" && e.Identity.To == "iE_n" && e.Identity.Right?.Note.Note == 2);
            var controls = TransitionExpressionControl.Reset(TransitionProjectBinding.ResolveAvailable(stored, layout), edge.Identity)
                .WithJunction(layout, new(edge.Identity, TransitionEndpoint.Begin), 3380)
                .WithJunction(layout, new(edge.Identity, TransitionEndpoint.End), 3400);
            var basis = prepared.QueryTransitionControls(layout, controls, 1);
            var row = basis.Segments.Where(s => s.To != null).ElementAt(layout.Edges.IndexOf(edge));
            Require(row.Split == 3385, "Saved phoneme context differs from the live capture.");
            var context = notes.Select(n => (n.RelPosTick.Value, n.DurationTick.Value, n.NoteNumber, n.Phonemes)).ToArray();
            var records = new List<object>(); ulong epoch = 190000;
            using var capture = new TimingCoefficientHook();
            string? baselineHash = null;
            foreach (var variant in new[] { ("warm", 0f, false), ("baseline", 0f, false),
                ("minus", -.001f, false), ("plus", .001f, false),
                ("legacy-minus", -.001f, true), ("legacy-plus", .001f, true), ("full-zero", 0f, true), ("restored", 0f, false) })
            {
                var plan = variant.Item3 ? TransitionExpressionControl.Source(controls, row, edge.Identity, true) : controls;
                plan = TransitionExpressionControl.Progress(plan, row, edge.Identity, variant.Item2);
                var execution = prepared.QueryTransitionControls(layout, plan, 1);
                var applied = execution.Segments.Where(s => s.To != null).ElementAt(layout.Edges.IndexOf(edge));
                prepared.ValidateTransitionCommit(part, execution, 1);
                prepared.PublishTransitions(part, ++epoch, execution, 1);
                SampledTimingVsmBinding.InvalidateRenderedPart(part, notes);
                string wave = Path.Combine(directory, variant.Item1 + ".wav");
                var observed = new Dictionary<int, TransitionFrame>(); int consumed = 0;
                capture.Reset(-1, false, null);
                capture.AfterStep = state =>
                {
                    int cursor = Marshal.ReadInt32(state, LocalTimingNative.Profile.StateCurrentFrame);
                    foreach (var segment in NativeConsonantTimingEngine.ReadTransitionSchedule(LocalTimingNative.Dse, state, 0, consumed, cursor))
                        foreach (var frame in segment.Frames) observed[frame.Frame] = frame;
                    consumed = cursor;
                };
                try { RenderTransitionWithScore(source, part, wave); capture.Check(); }
                finally { capture.AfterStep = null; }
                var timing = NativeSampledTiming.ReadStatus((nint)part); var gain = NativeTransitionGain.ReadStatus((nint)part);
                Require(timing.Failures == 0 && gain.Failures == 0, "Continuity render failed.");
                bool map = applied.Frames.All(f => observed.TryGetValue(f.Frame, out var actual) && actual == f);
                Require(map, "DSE did not consume the planned progress map.");
                Require(context.SequenceEqual(notes.Select(n => (n.RelPosTick.Value, n.DurationTick.Value, n.NoteNumber, n.Phonemes))), "Source note changed.");
                float Value(TransitionFrame f) => BitConverter.Int32BitsToSingle(f.SourceBits);
                double maximumSourceChange = applied.Frames.Zip(row.Frames).Max(p => Math.Abs(Value(p.First) - Value(p.Second)));
                string hash = TimingPcmHash(wave);
                if (variant.Item1 == "baseline") baselineHash = hash;
                if (variant.Item1 == "restored") Require(hash == baselineHash, "Zero progress did not restore PCM exactly.");
                if (!variant.Item3) Require(maximumSourceChange < .1, "Tiny progress switched the source range.");
                records.Add(new { name = variant.Item1, phase = variant.Item2, fullSource = variant.Item3, maximumSourceChange,
                    hash, map, timing.Applied, timing.Failures, gainFailures = gain.Failures,
                    geometry = new { applied.Begin, applied.Split, applied.End }, row.MaterialCoverage,
                    sources = applied.Frames.Select(Value).ToArray() });
                File.WriteAllText(Path.Combine(directory, "progress-continuity.json"), JsonSerializer.Serialize(records, new JsonSerializerOptions { WriteIndented = true }));
                Console.WriteLine($"continuity={variant.Item1}; sourceDelta={maximumSourceChange:0.######}; failures={timing.Failures}/{gain.Failures}");
            }
            NativeSampledTiming.Remove((nint)part);
            return 0;
        }
        finally { source.Close(); }
    }
}
