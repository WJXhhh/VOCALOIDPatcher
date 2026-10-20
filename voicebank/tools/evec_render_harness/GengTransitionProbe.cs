using System.IO;
using System.Text.Json;
using VOCALOIDPatcher.ConsonantTiming;
using Yamaha.VOCALOID.DSE;
using Yamaha.VOCALOID.VDM;
using Yamaha.VOCALOID.VSM;

internal static partial class Program
{
    private static StandaloneFixture OpenGengFixture(WIVSMSequenceManager manager, string directory)
    {
        using var zip = System.IO.Compression.ZipFile.OpenRead(Environment.GetEnvironmentVariable("VOCALOID_GENG_PROJECT")!);
        var entry = zip.GetEntry("Project/sequence.json") ?? throw new InvalidDataException("Missing source sequence.");
        Require(entry.Length < 10 * 1024 * 1024, "Source sequence exceeds the probe bound.");
        string path = Path.Combine(directory, "sequence.json");
        using (var input = entry.Open())
        using (var output = File.Create(path)) input.CopyTo(output);
        var source = manager.OpenSequence(path, EditorDirectory,
            new VSMSequenceData { SamplingRate = VSMSamplingRate._44100, MaxNumTracks = 4, MaxUndoCount = 100 })!;
        try
        {
            var track = (WIVSMMidiTrack)source.GetTrack(0UL)!;
            var part = (WIVSMMidiPart)track.GetPart(0UL)!;
            foreach (var other in track.PartsEnumerator.OfType<WIVSMMidiPart>().Where(p => (nint)p != (nint)part).ToArray())
                Require(track.RemovePart(other), "Cannot isolate captured Part.");
            return new(source, part, part.NotesEnumerator.ToArray());
        }
        catch { source.Close(); throw; }
    }

    private static int ProbeGengTransition(WIVSMSequenceManager manager, DSEManager dse,
        DatabaseManager database, string directory)
    {
        Directory.CreateDirectory(directory);
        NativeTransitionGain.Initialize(Environment.GetEnvironmentVariable("VOCALOID_TIMING_PROBE_NATIVE")!);
        using var input = JsonDocument.Parse(File.ReadAllText(Environment.GetEnvironmentVariable("VOCALOID_GENG_FIXTURE")!));
        var root = input.RootElement;
        var notes = root.GetProperty("notes").EnumerateArray().ToArray();
        bool full = Environment.GetEnvironmentVariable("VOCALOID_GENG_FULL") == "1";
        using var fixture = full ? OpenGengFixture(manager, directory) : CreateStandaloneFixture(manager, notes.Length, 480,
            root.GetProperty("partStart").GetInt64(), root.GetProperty("partDuration").GetInt64());
        var source = fixture.Sequence;
        var part = fixture.Part;
        Require(part.SetVoiceBankID(root.GetProperty("bank").GetString()!), "Missing captured bank.");
        source.InsertTempo(VSMRelTick.Zero, root.GetProperty("bpm").GetInt32() * 100);
        var plans = new Dictionary<nint, SampledTimingPlan>();
        for (int i = 0; i < notes.Length; i++)
        {
            var n = notes[i]; var note = fixture.Notes[i];
            if (!full)
            {
                part.MoveNote(new VSMRelTick(n.GetProperty("position").GetInt64()), note);
                note.SetDuration(new VSMRelTick(n.GetProperty("duration").GetInt64()));
                note.SetNoteNumber(n.GetProperty("pitch").GetInt32());
                note.NoteVelocity = n.GetProperty("velocity").GetInt32();
            }
            string phones = n.GetProperty("phonemes").GetString()!;
            if (!full) note.SetPhonemes(phones, true, 4);
            plans[(nint)note] = new(phones, n.GetProperty("controls").EnumerateArray().Select(a =>
                new TimingAdjustment(a.GetProperty("kind").GetString()!, a.GetProperty("index").GetInt32(), a.GetProperty("value").GetSingle())));
        }
        source.InterpolatedMasterVolume(); source.Commit(false);
        using var backend = new LocalConsonantTimingBackend(manager, dse, database);
        using var prepared = backend.PrepareCoefficient(fixture.Notes[notes.Length / 2], 1, planForNote: n => plans[(nint)n]);
        using var counters = new TimingDseCounters();
        var before = counters.Read();
        var layout = prepared.QueryTransitions(plans[(nint)fixture.Notes[notes.Length / 2]], 1);
        var stored = TransitionProjectArchive.Read(Environment.GetEnvironmentVariable("VOCALOID_GENG_PROJECT")!).Data!;
        var basis = TransitionProjectBinding.ResolveAvailable(stored, layout);
        var edge = layout.Edges.Single(e => e.Identity.From == "@N" && e.Identity.To == "k" && e.Identity.Left?.Note.Note == 6);
        var reports = new List<object>();
        ulong epoch = 92000; bool failed = false;
        Console.WriteLine("geng.edge=" + JsonSerializer.Serialize(new { edge, baselineCounters = counters.Read().Since(before) }));
        bool cancel = Environment.GetEnvironmentVariable("VOCALOID_GENG_CANCEL") == "1";
        bool recovery = Environment.GetEnvironmentVariable("VOCALOID_GENG_FAILURE") == "1";
        var modes = recovery ? new[] { "inject-failure", "stored", "restored" } : cancel ? Enumerable.Range(0, 40).Select(i => "cancel-" + i).ToArray() :
            new[] { "stored", "coverage", "early", "late", "restored" };
        foreach (string mode in modes)
        {
            var controls = recovery ? basis : cancel ? basis.WithJunction(layout, new(edge.Identity, TransitionEndpoint.Begin),
                edge.Current.Begin + (reports.Count % 2 == 0 ? 3 : 0)) : mode is "stored" or "restored" ? basis :
                TransitionExpressionControl.Progress(basis, edge.Current, edge.Identity, mode == "early" ? -.75f : mode == "late" ? .75f : 0);
            before = counters.Read();
            var execution = prepared.QueryTransitionControls(layout, controls, 1);
            var queryCounters = counters.Read().Since(before);
            Require(queryCounters == new TimingDseSnapshot(0, 0, 0), "Geng candidate synthesized audio.");
            var progress = NativeTransitionGain.Progress(execution);
            if (mode == "inject-failure") progress[0].End++;
            if (full)
                NativeSampledTiming.Publish(part, ++epoch, new Dictionary<nint, float>(),
                    execution.RenderAnchors.Select(p => new NativePhonemeTiming.Point(p.From, p.To)).ToArray(),
                    NativeTransitionGain.Gains(execution), progress);
            else prepared.PublishTransitions(part, ++epoch, execution, 1);
            SampledTimingVsmBinding.InvalidateRenderedPart(part, fixture.Notes);
            if (Environment.GetEnvironmentVariable("VOCALOID_GENG_HOST_ORDER") == "1")
            {
                long beforeStop = System.Runtime.InteropServices.Marshal.ReadInt64((nint)source, 0x248);
                source.StopAsyncRendering();
                Console.WriteLine("geng.stop=" + JsonSerializer.Serialize(new { beforeStop,
                    afterStop = System.Runtime.InteropServices.Marshal.ReadInt64((nint)source, 0x248) }));
            }
            if (cancel)
            {
                source.BlockRenderingEnabled = true;
                source.StartAsyncRendering();
                Thread.Sleep(reports.Count % 8 * 2);
                source.StopAsyncRendering();
                // Editor idle delivery may lag a just-stopped render. Keep
                // obsolete observer events separate from the next render.
                while (source.InvokeRendererObserver()) { }
                if (full)
                    NativeSampledTiming.Publish(part, ++epoch, new Dictionary<nint, float>(),
                        execution.RenderAnchors.Select(p => new NativePhonemeTiming.Point(p.From, p.To)).ToArray(),
                        NativeTransitionGain.Gains(execution), NativeTransitionGain.Progress(execution));
                else prepared.PublishTransitions(part, ++epoch, execution, 1);
                SampledTimingVsmBinding.InvalidateRenderedPart(part, fixture.Notes);
            }
            string? error = null;
            try { RenderTransitionWithScore(source, part, Path.Combine(directory, "candidate.wav"), blockRendering: full); }
            catch (InvalidOperationException exception) { error = exception.Message; }
            var native = NativeTransitionGain.ReadStatus((nint)part);
            var timing = NativeSampledTiming.ReadStatus((nint)part);
            var report = new { mode, error, native.Calls, native.Applied, native.Failures, native.Primary, native.Auxiliary,
                timingFailures = timing.Failures, cursor = NativeSampledTiming.PhonemeDiagnostic((nint)part),
                queryCounters, pcm = error == null ? TimingPcmHash(Path.Combine(directory, "candidate.wav")) : null,
                boundaries = fixture.Notes.Select(n => NativeTimingPositions.Read(n, false)).ToArray() };
            reports.Add(report);
            Console.WriteLine("geng.render=" + JsonSerializer.Serialize(report));
            File.WriteAllText(Path.Combine(directory, "geng-transition.json"), JsonSerializer.Serialize(reports,
                new JsonSerializerOptions { WriteIndented = true }));
            failed |= error != null || native.Failures != 0 || timing.Failures != 0;
        }
        NativeSampledTiming.Remove((nint)part);
        return failed ? 1 : 0;
    }
}
