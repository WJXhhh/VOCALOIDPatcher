using System.Diagnostics;
using System.IO;
using System.Text.Json;
using VOCALOIDPatcher.ConsonantTiming;
using Yamaha.VOCALOID.DSE;
using Yamaha.VOCALOID.VDM;
using Yamaha.VOCALOID.VSM;

internal static partial class Program
{
    private static void ProbeTransitionIsolation(WIVSMSequenceManager manager, DSEManager dse,
        DatabaseManager database, string directory)
    {
        Directory.CreateDirectory(directory);
        using var backend = new LocalConsonantTimingBackend(manager, dse, database);
        var fixtures = Enumerable.Range(0, 4).Select(_ => CreateStandaloneFixture(manager, 1, 960, 0, 4800)).ToArray();
        var preparations = new LocalConsonantTimingBackend.PreparedCoefficientTiming?[3];
        var layouts = new TransitionLayout[3];
        var plans = new TransitionExecutionPlan[3];
        var expected = new string[4]; var ordinary = new string[4];
        var observers = new List<WIVSMRendererObserver>();
        var report = new List<object>(); ulong epoch = 90000;
        try
        {
            for (int i = 0; i < 4; i++)
            {
                var f = fixtures[i];
                f.Part.SetVoiceBankID("BL8CEAM5N4XN3LFK");
                f.Part.MoveNote(new VSMRelTick(960), f.Notes[0]);
                f.Notes[0].SetPhonemes("l a", true, 4);
                f.Sequence.InterpolatedMasterVolume(); f.Sequence.Commit(false);
                string path = Path.Combine(directory, "ordinary-" + i + ".wav");
                RenderTransitionWithScore(f.Sequence, f.Part, path);
                SampledTimingVsmBinding.InvalidateRenderedPart(f.Part, f.Notes);
                RenderTransitionWithScore(f.Sequence, f.Part, path);
                ordinary[i] = TimingPcmHash(path);
                if (i < 3)
                {
                    var p = preparations[i] = backend.PrepareCoefficient(f.Notes[0], 1);
                    var source = new SampledTimingPlan("l a", i == 2
                        ? [new(TimingAdjustment.NoteCoefficient, -1, .375f), new(TimingAdjustment.PhonemeBoundary, 2, (float)(2 * 256d / 44100))] : []);
                    var layout = layouts[i] = p.QueryTransitions(source, 1);
                    var edge = layout.Edges[1];
                    var endpoint = new TransitionEndpointRef(edge.Identity, TransitionEndpoint.End);
                    var joint = layout.Junction(endpoint);
                    int frame = Math.Min(joint.MaximumCandidate, joint.CurrentFrame + 2 + i);
                    plans[i] = p.QueryTransitionControls(layout, new(layout.Fingerprint,
                        [new(endpoint, frame)], [new(edge.Identity, new[] { -12f, 6f, -6f }[i])]), 1);
                    p.PublishTransitions(f.Part, ++epoch, plans[i], 1);
                    path = Path.Combine(directory, "controlled-" + i + ".wav");
                    RenderTransitionWithScore(f.Sequence, f.Part, path);
                    expected[i] = TimingPcmHash(path);
                    Require(expected[i] != ordinary[i], "Concurrent fixture has no audible control.");
                }
                else expected[i] = ordinary[i];
            }
            // Identical content in a different source cannot borrow a prepared route.
            bool Rejected(Action action)
            {
                try { action(); return false; }
                catch (InvalidOperationException) { return true; }
            }
            var first = preparations[0]!;
            Require(Rejected(() => first.PublishTransitions(fixtures[1].Part, ++epoch, plans[0], 1)), "Foreign Part accepted.");
            Require(Rejected(() => first.QueryTransitionControls(layouts[1], new(layouts[1].Fingerprint), 1)), "Foreign scope accepted.");
            Require(Rejected(() => first.QueryTransitions(new("l a", []), 2)), "Stale revision accepted.");
            ulong priorEpoch = NativeSampledTiming.ReadStatus((nint)fixtures[0].Part).Epoch;
            fixtures[0].Notes[0].NoteVelocity = 65;
            Require(Rejected(() => first.PublishTransitions(fixtures[0].Part, ++epoch, plans[0], 1)), "Staged input accepted.");
            fixtures[0].Sequence.Commit(false);
            Require(Rejected(() => first.PublishTransitions(fixtures[0].Part, ++epoch, plans[0], 1)), "Changed VEL accepted stale input.");
            Require(NativeSampledTiming.ReadStatus((nint)fixtures[0].Part).Epoch == priorEpoch, "Rejected publication changed native route.");
            fixtures[0].Notes[0].NoteVelocity = 64; fixtures[0].Sequence.Commit(false);
            report.Add(new { validation = "foreign-source/scope/stale-revision/staged/changed-VEL", passed = true });

            int[] completed = new int[4];
            for (int pass = 0; pass < 3; pass++)
            {
                Array.Clear(completed);
                for (int i = 0; i < 4; i++)
                {
                    int index = i; var f = fixtures[i];
                    if (i < 3) preparations[i]!.PublishTransitions(f.Part, ++epoch, plans[i], 1);
                    SampledTimingVsmBinding.InvalidateRenderedPart(f.Part, f.Notes);
                    var observer = new WIVSMRendererObserver();
                    observer.Completed += (_, e) => { if ((nint)e.MidiPart == (nint)f.Part) completed[index] = 1; };
                    observer.Canceled += (_, e) => { if ((nint)e.MidiPart == (nint)f.Part) completed[index] = -1; };
                    Require(f.Sequence.AddRendererObserver(observer), "Cannot attach transition concurrent observer.");
                    observers.Add(observer); f.Sequence.BlockRenderingEnabled = false;
                }
                int nativePlans = 0, busyRetries = 0;
                using var started = new ManualResetEventSlim();
                // Private no-audio plans and cache queries run beside real
                // renderer scopes; a busy native mutex is retried explicitly.
                var queries = Task.Run(() =>
                {
                    started.Wait();
                    for (int n = 0; n < 128; n++)
                    {
                        var layout = layouts[n % 3];
                        var result = preparations[n % 3]!.QueryTransitionControls(layout,
                            new(layout.Fingerprint, gains: [new(layout.Edges[1].Identity, -3)]), 1);
                        Require(result.Controls.Gains.Single().Decibels == -3, "Concurrent query crossed Parts.");
                    }
                    for (int n = 0; n < 12; n++)
                    {
                        int index = n % 3; var layout = layouts[index];
                        var endpoint = new TransitionEndpointRef(layout.Edges[1].Identity, TransitionEndpoint.End);
                        var joint = layout.Junction(endpoint);
                        int frame = Math.Min(joint.MaximumCandidate, joint.CurrentFrame + 8 + n + pass * 12);
                        var watch = Stopwatch.StartNew();
                        while (true)
                        {
                            try
                            {
                                nativePlans += preparations[index]!.QueryTransitionControls(layout,
                                    new(layout.Fingerprint, [new(endpoint, frame)], [new(endpoint.Edge, -3)]), 1).NativePlans;
                                break;
                            }
                            catch (TimeoutException) when (watch.ElapsedMilliseconds < 5000) { busyRetries++; }
                        }
                    }
                });
                foreach (var f in fixtures) f.Sequence.StartAsyncRendering();
                started.Set();
                var watch = Stopwatch.StartNew();
                while (completed.Any(v => v == 0) && watch.ElapsedMilliseconds < 30000)
                {
                    foreach (var f in fixtures) for (int n = 0; n < 8 && f.Sequence.InvokeRendererObserver(); n++) { }
                    Thread.Sleep(1);
                }
                queries.GetAwaiter().GetResult();
                Require(nativePlans > 0, "Concurrent query test reused only cached planning.");
                report.Add(new { pass, validation = "parallel-private-plans", nativePlans, busyRetries });
                for (int i = 0; i < 4; i++)
                {
                    var f = fixtures[i]; f.Sequence.StopAsyncRendering(); f.Sequence.RemoveRendererObserver(observers[i]);
                    string hash = completed[i] == 1 ? TimingPcmHash(f.Part.WaveFilePath) : "unavailable";
                    var gain = NativeTransitionGain.ReadStatus((nint)f.Part);
                    report.Add(new { pass, part = i, completed = completed[i], hash, expected = expected[i],
                        gain.Failures, gain.Applied, peakScopes = NativeSampledTiming.PeakScopes });
                    Require(completed[i] == 1 && hash == expected[i] && gain.Failures == 0,
                        "Concurrent transition route leaked, canceled or changed PCM.");
                }
                foreach (var observer in observers) observer.Dispose(); observers.Clear();
                Require(NativeSampledTiming.PeakScopes > 1, "Transition test did not overlap native scopes.");
                Console.WriteLine($"transition.concurrent pass={pass}; exact=4; peak={NativeSampledTiming.PeakScopes}");
            }
            // Detach while source is open, then close it. Controlled PCM must
            // not enter the ordinary native cache after the route was removed.
            string saved = Path.Combine(directory, "early-detach"); Directory.CreateDirectory(saved);
            Require(fixtures[0].Sequence.Save(saved, false), "Cannot save detach fixture.");
            first.Dispose(); preparations[0] = null;
            report.Add(new { validation = "early-detach-state", validWave = fixtures[0].Part.HasValidRenderedWave });
            fixtures[0].Sequence.Close();
            var loaded = manager.OpenSequence(Path.Combine(saved, "Project", "sequence.json"), EditorDirectory,
                new VSMSequenceData { SamplingRate = VSMSamplingRate._44100, MaxNumTracks = 4, MaxUndoCount = 0 })!;
            try
            {
                loaded.InterpolatedMasterVolume(); loaded.Commit(false);
                var part = (WIVSMMidiPart)((WIVSMMidiTrack)loaded.GetTrack(0UL)!).GetPart(0UL)!;
                string path = Path.Combine(directory, "early-detach-reopen.wav");
                RenderTransitionWithScore(loaded, part, path);
                Require(TimingPcmHash(path) == ordinary[0], "Early detach poisoned ordinary native cache.");
                report.Add(new { validation = "early-detach-cache", passed = true, hash = TimingPcmHash(path) });
            }
            finally { loaded.Close(); }
            // Deleting all source notes cannot strand an owned native route.
            Require(fixtures[1].Part.RemoveNote(fixtures[1].Notes[0]), "Cannot empty disposal fixture.");
            fixtures[1].Sequence.Commit(false);
            preparations[1]!.Dispose(); preparations[1] = null;
            Require(NativeSampledTiming.ReadStatus((nint)fixtures[1].Part).Epoch == 0, "Empty source retained transition controls.");
            report.Add(new { validation = "empty-source-dispose", passed = true });
            File.WriteAllText(Path.Combine(directory, "isolation.json"), JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
        }
        finally
        {
            foreach (var f in fixtures) { f.Sequence.StopAsyncRendering(); f.Dispose(); }
            foreach (var p in preparations) p?.Dispose();
            foreach (var observer in observers) observer.Dispose();
        }
    }
}
