using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using VOCALOIDPatcher.ConsonantTiming;
using Yamaha.VOCALOID.DSE;
using Yamaha.VOCALOID.VDM;
using Yamaha.VOCALOID.VSM;

internal static partial class Program
{
    // Run only in a disposable process: native deadlocks/access violations must
    // never be investigated inside a user document. The watchdog exits only
    // this harness process and never controls Editor or another process.
    private static int ProbeStandaloneTimingConcurrency(WIVSMSequenceManager manager, DSEManager dse,
        DatabaseManager database, string directory, string? caseFilter)
    {
        if (caseFilter != null && !new[] { "memory", "lifecycle", "two_queries", "mutex", "render", "same_part", "other_bank", "paced", "close" }.Contains(caseFilter))
            throw new ArgumentException("Unknown concurrency case.");
        Directory.CreateDirectory(directory);
        string stage = "initialization";
        var results = new List<object>();
        void Save(bool complete = false, string? error = null) => File.WriteAllText(
            Path.Combine(directory, "standalone-concurrency.json"), JsonSerializer.Serialize(
                new { stage, complete, error, results }, new JsonSerializerOptions { WriteIndented = true }));
        using var watchdog = new Timer(_ =>
        {
            Console.Error.WriteLine($"standalone.watchdog={stage}");
            Environment.Exit(78);
        }, null, 20000, Timeout.Infinite);
        void Enter(string next)
        {
            stage = next;
            watchdog.Change(20000, Timeout.Infinite);
            Console.WriteLine($"standalone.concurrency_stage={next}");
            Save();
        }
        using var counters = new TimingDseCounters();
        try
        {
            if (caseFilter == null || caseFilter == "memory")
            {
                Enter("memory");
                using var fixture = CreateStandaloneFixture(manager, 3, 480, 0, 4000);
                using var backend = new LocalConsonantTimingBackend(manager, dse, database);
                for (int i = 0; i < 100; i++) { backend.ClearCache(); backend.Query(fixture.Notes[1], i % 128); }
                var before = counters.Read();
                var snapshots = new List<object>();
                var process = Process.GetCurrentProcess();
                int completed = 0;
                foreach (int count in new[] { 0, 1000, 2000, 4000, 8000 })
                {
                    for (; completed < count; completed++)
                    {
                        backend.ClearCache();
                        backend.Query(fixture.Notes[1], completed % 128);
                    }
                    GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect(); process.Refresh();
                    snapshots.Add(new { queries = count, privateBytes = process.PrivateMemorySize64,
                        managedBytes = GC.GetTotalMemory(false), handles = process.HandleCount,
                        threads = process.Threads.Count });
                }
                var delta = counters.Read().Since(before);
                if (delta.Starts != 0 || delta.Steps != 0 || delta.Samples != 0)
                    throw new InvalidOperationException("Memory stress entered native start or audio synthesis.");
                results.Add(new { name = "memory", snapshots, dse = delta });
                Save();
            }
            if (caseFilter == null || caseFilter == "lifecycle")
            {
                Enter("lifecycle");
                using var fixture = CreateStandaloneFixture(manager, 9, 120, 17003, 6000);
                var backend = new LocalConsonantTimingBackend(manager, dse, database);
                var target = fixture.Notes[4];
                var expected = backend.Query(target, 64).Boundaries;
                backend.ClearCache();
                object gate = typeof(LocalConsonantTimingBackend).GetField("_gate", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(backend)!;
                using var held = new ManualResetEventSlim();
                using var release = new ManualResetEventSlim();
                using var cancellation = new CancellationTokenSource();
                nint mutex = Marshal.ReadIntPtr(LocalTimingNative.Dse + 0xa9cfa0);
                var owner = Task.Run(() =>
                {
                    if (TimingWaitMutex(mutex, 2000) != 0) throw new InvalidOperationException("Cannot hold cancellation test mutex.");
                    try { held.Set(); release.Wait(); }
                    finally { TimingReleaseMutex(mutex); }
                });
                if (!held.Wait(2000)) throw new TimeoutException("Cancellation test holder did not start.");
                bool canceled = false;
                var running = Task.Run(() =>
                {
                    try { backend.Query(target, 64, cancellation.Token); }
                    catch (OperationCanceledException) { canceled = true; }
                });
                bool GateBusy()
                {
                    if (!Monitor.TryEnter(gate)) return true;
                    Monitor.Exit(gate);
                    return false;
                }
                try
                {
                    if (!SpinWait.SpinUntil(GateBusy, 1000)) throw new TimeoutException("Query never entered its gate.");
                    var busyWatch = Stopwatch.StartNew();
                    bool rejected = false;
                    try { backend.Query(target, 64); }
                    catch (InvalidOperationException) { rejected = true; }
                    double busyMs = busyWatch.Elapsed.TotalMilliseconds;
                    var closing = Task.Run(backend.Dispose);
                    var cancelWatch = Stopwatch.StartNew();
                    cancellation.Cancel();
                    running.GetAwaiter().GetResult();
                    double cancellationMs = cancelWatch.Elapsed.TotalMilliseconds;
                    release.Set();
                    owner.GetAwaiter().GetResult();
                    if (!closing.Wait(3000)) throw new TimeoutException("Backend close did not complete.");
                    bool disposed = false;
                    try { backend.Query(target, 64); }
                    catch (ObjectDisposedException) { disposed = true; }
                    if (!canceled || !rejected || !disposed || busyMs > 100 || cancellationMs > 200)
                        throw new InvalidOperationException("Cancellation, busy or close contract failed.");
                    using var replacement = new LocalConsonantTimingBackend(manager, dse, database);
                    if (!replacement.Query(target, 64).Boundaries.SequenceEqual(expected))
                        throw new InvalidOperationException("Replacement session differs after cancellation.");
                    results.Add(new { name = "lifecycle", canceled, rejected, disposed, busyMs, cancellationMs,
                        replacementEqual = true });
                    Save();
                }
                finally
                {
                    cancellation.Cancel();
                    release.Set();
                    owner.GetAwaiter().GetResult();
                    running.GetAwaiter().GetResult();
                    backend.Dispose();
                }

                Enter("source_switch");
                using var switching = new LocalConsonantTimingBackend(manager, dse, database);
                for (int iteration = 0; iteration < 30; iteration++)
                {
                    using var source = CreateStandaloneFixture(manager, 3, 480, iteration * 173, 4000);
                    source.Part.SetVoiceBankID(iteration % 2 == 0 ? "BCNFCY43LB2LZCD4" : "BKKP765AEHXWSKDB");
                    var result = switching.Query(source.Notes[1], 64);
                    if (result.Boundaries.Length < 2) throw new InvalidOperationException("Source switch lost its boundaries.");
                    switching.ClearCache();
                    // Owner closes source only after the query has returned.
                }
                results.Add(new { name = "source_switch", closedSources = 30, bankAlternations = 29 });
                Save();
            }
            if (caseFilter == null || caseFilter == "two_queries")
            {
                Enter("two_queries");
                using var sourceA = CreateStandaloneFixture(manager, 5, 480, 0, 6000);
                using var sourceB = CreateStandaloneFixture(manager, 5, 480, 17003, 6000);
                sourceB.Part.SetVoiceBankID("BKKP765AEHXWSKDB");
                using var backendA = new LocalConsonantTimingBackend(manager, dse, database);
                using var backendB = new LocalConsonantTimingBackend(manager, dse, database);
                using var barrier = new Barrier(2);
                var expectedA = Enumerable.Range(0, 16).ToDictionary(v => v * 8, v => backendA.Query(sourceA.Notes[1], v * 8).Boundaries);
                var expectedB = Enumerable.Range(0, 16).ToDictionary(v => v * 8, v => backendB.Query(sourceB.Notes[1], v * 8).Boundaries);
                var before = counters.Read();
                int Run(LocalConsonantTimingBackend backend, WIVSMNote target, Dictionary<int, long[]> expected)
                {
                    if (!barrier.SignalAndWait(3000)) throw new TimeoutException("Query workers did not rendezvous.");
                    int successes = 0;
                    for (int i = 0; i < 60; i++)
                    {
                        backend.ClearCache();
                        int velocity = (i % 16) * 8;
                        try
                        {
                            var result = backend.Query(target, velocity);
                            if (!result.Boundaries.SequenceEqual(expected[velocity]))
                                throw new InvalidOperationException("Concurrent private engines contaminated each other.");
                            successes++;
                        }
                        catch (TimeoutException) { }
                    }
                    return successes;
                }
                var a = Task.Run(() => Run(backendA, sourceA.Notes[1], expectedA));
                var b = Task.Run(() => Run(backendB, sourceB.Notes[1], expectedB));
                Task.WaitAll(a, b);
                var delta = counters.Read().Since(before);
                if (a.Result < 20 || b.Result < 20 || delta.Starts != 0 || delta.Steps != 0 || delta.Samples != 0)
                    throw new InvalidOperationException("Concurrent query isolation or session reuse failed.");
                results.Add(new { name = "two_queries", attemptsPerWorker = 60, successesA = a.Result,
                    successesB = b.Result, dse = delta });
                Save();
            }
            if (caseFilter == null || caseFilter == "mutex")
            {
                Enter("mutex");
                using var fixture = CreateStandaloneFixture(manager, 3, 480, 0, 4000);
                using var backend = new LocalConsonantTimingBackend(manager, dse, database);
                using var engine = new NativeConsonantTimingEngine((nint)dse);
                nint state = (nint)typeof(NativeConsonantTimingEngine).GetField("_state", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(engine)!;
                nint startMutex = Marshal.ReadIntPtr(LocalTimingNative.Dse + 0xa9cfa0);
                nint planMutex = Marshal.ReadIntPtr(state, 0x2aef0);
                using var held = new ManualResetEventSlim();
                using var release = new ManualResetEventSlim();
                var owner = Task.Run(() =>
                {
                    uint wait = TimingWaitMutex(startMutex, 2000);
                    if (wait != 0) throw new InvalidOperationException("Cannot hold test mutex.");
                    try { held.Set(); release.Wait(); }
                    finally { TimingReleaseMutex(startMutex); }
                });
                if (!held.Wait(2000)) throw new TimeoutException("Mutex holder did not start.");
                string outcome = "success";
                var watch = Stopwatch.StartNew();
                try { backend.Query(fixture.Notes[1], 64); }
                catch (Exception exception) { outcome = exception.GetType().Name; }
                finally { release.Set(); owner.GetAwaiter().GetResult(); }
                double waitMs = watch.Elapsed.TotalMilliseconds;
                results.Add(new { name = "mutex", sameMutex = startMutex == planMutex, outcome, waitMs });
                Save();
                if (waitMs > 200 || outcome != nameof(TimeoutException))
                    throw new InvalidOperationException("Busy native start did not fail promptly.");
                backend.Query(fixture.Notes[1], 64);
            }
            if (caseFilter == null || caseFilter is "render" or "close" or "same_part" or "other_bank" or "paced")
            {
                string[]? reference = null;
                string? pcm = null;
                // Two independent references establish PCM determinism before
                // a concurrent run can be judged against it.
                for (int repeat = 0; repeat < 2; repeat++)
                {
                    Enter($"reference_{repeat}");
                    using var fixture = CreateStandaloneFixture(manager, 48, 120, 0, 7000);
                    fixture.Sequence.InterpolatedMasterVolume();
                    var before = counters.Read();
                    var referenceTimer = Stopwatch.StartNew();
                    string path = Path.Combine(directory, $"reference-{repeat}.wav");
                    RenderTransitionWithScore(fixture.Sequence, fixture.Part, path);
                    string[] actual = TimingBoundaryDigest(fixture);
                    string actualPcm = TimingPcmDigest(path);
                    if (reference != null && (!reference.SequenceEqual(actual) || pcm != actualPcm))
                        throw new InvalidOperationException("Serial reference is not deterministic.");
                    reference = actual;
                    pcm = actualPcm;
                    results.Add(new { name = stage, pcm, boundaries = actual,
                        wallMs = referenceTimer.Elapsed.TotalMilliseconds, dse = counters.Read().Since(before) });
                    Save();
                }
                foreach (string mode in new[] { "render", "same_part", "other_bank", "paced", "close" }.Where(mode => caseFilter == null || mode == caseFilter))
                {
                    Enter(mode);
                    using var render = CreateStandaloneFixture(manager, 48, 120, 0, 7000);
                    render.Sequence.InterpolatedMasterVolume();
                    using var querySource = CreateStandaloneFixture(manager, 5, 480, 17003, 5000);
                    if (mode == "other_bank") querySource.Part.SetVoiceBankID("BKKP765AEHXWSKDB");
                    var queryTarget = mode is "same_part" or "paced" ? render.Notes[25] : querySource.Notes[2];
                    var backend = new LocalConsonantTimingBackend(manager, dse, database);
                    var expected = Enumerable.Range(0, 128).Where(v => v % 8 == 0)
                        .ToDictionary(v => v, v => backend.Query(queryTarget, v).Boundaries);
                    backend.ClearCache();
                    using var observer = new WIVSMRendererObserver();
                    int status = 0;
                    observer.Completed += (_, args) => { if ((nint)args.MidiPart == (nint)render.Part) status = 1; };
                    observer.Canceled += (_, args) => { if ((nint)args.MidiPart == (nint)render.Part) status = -1; };
                    if (!render.Sequence.AddRendererObserver(observer)) throw new InvalidOperationException("Cannot observe render.");
                    var queryResults = new List<object>();
                    var closeResults = new List<object>();
                    int overlapping = 0, closedWhileRendering = 0;
                    var beforeRender = counters.Read();
                    var elapsed = Stopwatch.StartNew();
                    double nextQueryMs = 0;
                    try
                    {
                        render.Sequence.BlockRenderingEnabled = false;
                        render.Sequence.StartAsyncRendering();
                        while (status == 0)
                        {
                            for (int i = 0; i < 8 && render.Sequence.InvokeRendererObserver(); i++) { }
                            if (status != 0) break;
                            if (elapsed.Elapsed > TimeSpan.FromSeconds(15)) throw new TimeoutException("Concurrent render timed out.");
                            if (mode == "paced" && elapsed.Elapsed.TotalMilliseconds < nextQueryMs)
                            {
                                Thread.Sleep(1);
                                continue;
                            }
                            nextQueryMs = elapsed.Elapsed.TotalMilliseconds + 1000d / 60;
                            int velocity = (queryResults.Count % 16) * 8;
                            backend.ClearCache();
                            var before = counters.Read();
                            var queryWatch = Stopwatch.StartNew();
                            LocalConsonantTimingResult prediction;
                            try { prediction = backend.Query(queryTarget, velocity); }
                            catch (TimeoutException)
                            {
                                queryResults.Add(new { velocity, milliseconds = queryWatch.Elapsed.TotalMilliseconds,
                                    busy = true, renderPcmStepsDuringQuery = counters.Read().Since(before).Steps });
                                Thread.Sleep(1);
                                continue;
                            }
                            double milliseconds = queryWatch.Elapsed.TotalMilliseconds;
                            var delta = counters.Read().Since(before);
                            if (!prediction.Boundaries.SequenceEqual(expected[velocity]))
                                throw new InvalidOperationException("Concurrent query changed its boundaries.");
                            if (delta.Steps > 0) overlapping++;
                            queryResults.Add(new { velocity, milliseconds, busy = false, prediction.ContextNotes,
                                prediction.PlannerRuns, renderPcmStepsDuringQuery = delta.Steps });
                            if (mode == "close" && closedWhileRendering < 3)
                            {
                                var closeBefore = counters.Read();
                                var closeWatch = Stopwatch.StartNew();
                                backend.Dispose();
                                backend = new LocalConsonantTimingBackend(manager, dse, database);
                                closeResults.Add(new { milliseconds = closeWatch.Elapsed.TotalMilliseconds,
                                    renderPcmStepsDuringClose = counters.Read().Since(closeBefore).Steps });
                                closedWhileRendering++;
                            }
                        }
                        if (status != 1 || overlapping == 0)
                            throw new InvalidOperationException("No successfully completed concurrent render.");
                        string path = Path.Combine(directory, $"{mode}.wav");
                        File.Copy(render.Part.WaveFilePath, path, true);
                        string[] actual = TimingBoundaryDigest(render);
                        string actualPcm = TimingPcmDigest(path);
                        if (!reference!.SequenceEqual(actual) || pcm != actualPcm)
                            throw new InvalidOperationException("Concurrent render differs from serial reference.");
                        results.Add(new { name = mode, pcm = actualPcm, boundaries = actual,
                            overlapping, closedWhileRendering, queries = queryResults, closes = closeResults,
                            maxRequestRateHz = mode == "paced" ? 60 : (int?)null,
                            wallMs = elapsed.Elapsed.TotalMilliseconds, dse = counters.Read().Since(beforeRender) });
                        Save();
                    }
                    finally
                    {
                        render.Sequence.StopAsyncRendering();
                        render.Sequence.RemoveRendererObserver(observer);
                        backend.Dispose();
                    }
                }
            }
            Enter("completed");
            Save(true);
            return 0;
        }
        catch (Exception exception)
        {
            Save(false, exception.GetType().Name + ": " + exception.Message);
            throw;
        }
    }

    [DllImport("kernel32.dll", EntryPoint = "WaitForSingleObject")]
    private static extern uint TimingWaitMutex(nint handle, uint milliseconds);
    [DllImport("kernel32.dll", EntryPoint = "ReleaseMutex")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool TimingReleaseMutex(nint handle);

    private static string[] TimingBoundaryDigest(StandaloneFixture fixture) => fixture.Notes
        .Select(note => string.Join(',', note.GetPhonemePositions())).ToArray();

    private static string TimingPcmDigest(string path)
    {
        using var stream = File.OpenRead(path);
        if (stream.Length > 32 * 1024 * 1024) throw new InvalidOperationException("Reference WAV exceeds bound.");
        using var reader = new BinaryReader(stream, Encoding.ASCII, false);
        if (new string(reader.ReadChars(4)) != "RIFF") throw new InvalidOperationException("Missing RIFF header.");
        reader.ReadUInt32();
        if (new string(reader.ReadChars(4)) != "WAVE") throw new InvalidOperationException("Missing WAVE header.");
        while (stream.Position + 8 <= stream.Length)
        {
            string id = new(reader.ReadChars(4));
            uint size = reader.ReadUInt32();
            if (size > stream.Length - stream.Position) throw new InvalidOperationException("Invalid WAV chunk.");
            if (id == "data") return Convert.ToHexString(SHA256.HashData(reader.ReadBytes(checked((int)size))));
            stream.Position += size + (size & 1);
        }
        throw new InvalidOperationException("Missing PCM chunk.");
    }
}
