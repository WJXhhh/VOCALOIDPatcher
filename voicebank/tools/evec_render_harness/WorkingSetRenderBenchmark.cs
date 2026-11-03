using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using Yamaha.VOCALOID.VSM;

internal static partial class Program
{
    private sealed record WorkingSetSnapshot(long WorkingSet, long PrivateBytes, uint PageFaults,
        ulong ReadBytes, ulong WriteBytes, double CpuMs, ulong AvailablePhysicalBytes);

    private sealed record WorkingSetRender(double EditMs, double RenderMs, double TotalMs, double StartMs,
        double StopMs, TimingDseSnapshot Dse, WorkingSetSnapshot Before, WorkingSetSnapshot After,
        string Sha256, long WaveBytes);

    private sealed record WorkingSetTrial(string Bank, int Notes, string EditScope, string Arm, int WaitMs, int Pair, int Position,
        bool Trim, DateTimeOffset StartedAt, double IdleMs, double TrimMs, WorkingSetSnapshot BeforeTrim,
        WorkingSetSnapshot AfterTrim, WorkingSetRender Render);

    // Only the isolated benchmark process is trimmed. No Editor, user project,
    // persistent preference, system cache, or other process is modified.
    private static int BenchmarkWorkingSetRendering(WIVSMSequenceManager manager, string directory, string[] args)
    {
        int Option(string name, int fallback) => int.Parse(args.FirstOrDefault(a => a.StartsWith(name + "=", StringComparison.Ordinal))
            ?[(name.Length + 1)..] ?? fallback.ToString(System.Globalization.CultureInfo.InvariantCulture));
        int pairs = Option("--ab-pairs", 30), count = Option("--ab-notes", 12);
        bool triplets = args.Contains("--ab-triplets", StringComparer.OrdinalIgnoreCase);
        bool[] scopes = args.Contains("--ab-all-only", StringComparer.OrdinalIgnoreCase) ? [false] : [false, true];
        if (pairs is < 1 or > 100 || count is < 1 or > 32) throw new ArgumentOutOfRangeException(nameof(args));
        string banks = args.FirstOrDefault(a => a.StartsWith("--ab-banks=", StringComparison.Ordinal))?[11..]
            ?? "BD79E492NWWK3DDF,BL8CEAM5N4XN3LFK";
        int[] waits = (args.FirstOrDefault(a => a.StartsWith("--ab-waits=", StringComparison.Ordinal))?[11..] ?? "0,1250")
            .Split(',').Select(int.Parse).ToArray();
        if (waits.Any(w => w is not (0 or 1250))) throw new ArgumentException("Waits must be 0 or 1250 ms.");
        manager.CacheCapacity = 1024L * 1024 * 1024;
        Directory.CreateDirectory(directory);
        string report = Path.Combine(directory, "working-set-render-ab.jsonl");
        if (File.Exists(report)) throw new IOException("Use a fresh output directory.");
        var metadata = new
        {
            kind = "metadata", version = triplets ? 3 : 2, startedAt = DateTimeOffset.Now, processId = Environment.ProcessId,
            sdk = typeof(WIVSMSequence).Assembly.GetName().Version?.ToString(), runtime = RuntimeInformation.FrameworkDescription,
            os = RuntimeInformation.OSDescription, logicalProcessors = Environment.ProcessorCount,
            editor = EditorDirectory, pairs, count, waits, banks, cacheCapacity = manager.CacheCapacity,
            triplets, editScopes = scopes.Select(s => s ? "single" : "all").ToArray(),
            ordering = triplets ? "All six retain/trim/sham permutations, balanced and shuffled with seed 20261023." : "AB/BA alternating pairs.",
            nativeBinaries = new[] { "VOCALOID6.dll", "DSE.dll", "VSM.dll" }.Select(name => new
            {
                name, version = FileVersionInfo.GetVersionInfo(Path.Combine(EditorDirectory, name)).FileVersion,
                sha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(Path.Combine(EditorDirectory, name))))
            }).ToArray(),
            scope = "Isolated VSM async render, full-Part and single-note edits; no Editor UI or Patcher DSP. Each measurement is preceded by an untrimmed primer.",
            intervention = "0 ms: render before the 1 s trim deadline, no trim in either arm. 1250 ms: B trims at 1000 ms, both request after at least 1250 ms."
        };
        File.WriteAllText(Path.Combine(directory, "metadata.json"), JsonSerializer.Serialize(metadata, new JsonSerializerOptions { WriteIndented = true }));
        using var counters = new TimingDseCounters();
        using var process = Process.GetCurrentProcess();
        var random = new Random(20261023);
        foreach (string bank in banks.Split(',', StringSplitOptions.RemoveEmptyEntries))
        {
            using var fixture = CreateStandaloneFixture(manager, count, 480, 0, 1440 + count * 480L);
            if (!fixture.Part.SetVoiceBankID(bank)) throw new InvalidOperationException("Cannot select benchmark voicebank.");
            int language = bank == "BCNFCY43LB2LZCD4" ? 0 : 4;
            foreach (var note in fixture.Notes)
                if (!note.SetPhonemes("a", true, language)) throw new InvalidOperationException("Cannot set benchmark phoneme.");
            if (!fixture.Sequence.InterpolatedMasterVolume() || !fixture.Sequence.Commit(false))
                throw new InvalidOperationException("Cannot initialize benchmark input.");
            fixture.Sequence.BlockRenderingEnabled = false;
            foreach (bool single in scopes)
            foreach (int wait in waits)
            {
                // Warm both input variants and the benchmark/JIT/metrics path before each case.
                for (int warm = 0; warm < 4; warm++) _ = WorkingSetRenderOnce(fixture, warm % 2, single, counters, process);
                string? expected = null;
                string[][] permutations =
                [
                    ["retain", "trim", "sham"], ["retain", "sham", "trim"],
                    ["trim", "retain", "sham"], ["trim", "sham", "retain"],
                    ["sham", "retain", "trim"], ["sham", "trim", "retain"]
                ];
                int[] schedule = Enumerable.Range(0, pairs).Select(p => p % permutations.Length).ToArray();
                for (int i = schedule.Length - 1; i > 0; i--)
                {
                    int j = random.Next(i + 1);
                    (schedule[i], schedule[j]) = (schedule[j], schedule[i]);
                }
                for (int pair = 0; pair < pairs; pair++)
                {
                    // Triplets include a label-only sham at the SAME wait,
                    // preventing a fixed engine/OS cycle from aligning with an arm.
                    string[] order = triplets ? permutations[schedule[pair]] :
                        pair % 2 == 0 ? ["retain", "trim"] : ["trim", "retain"];
                    for (int position = 0; position < order.Length; position++)
                    {
                        string arm = order[position];
                        bool trim = arm == "trim";
                        _ = WorkingSetRenderOnce(fixture, 0, single, counters, process);
                        var trialStartedAt = DateTimeOffset.Now;
                        var idle = Stopwatch.StartNew();
                        if (wait != 0) Thread.Sleep(1000);
                        var before = ReadWorkingSetSnapshot(process);
                        long trimStarted = Stopwatch.GetTimestamp();
                        if (trim && wait != 0 && !WorkingSetNative.EmptyWorkingSet(process.Handle))
                            throw new Win32Exception(Marshal.GetLastWin32Error());
                        double trimMs = trim && wait != 0 ? Stopwatch.GetElapsedTime(trimStarted).TotalMilliseconds : 0;
                        var after = ReadWorkingSetSnapshot(process);
                        if (wait != 0 && idle.Elapsed.TotalMilliseconds < wait)
                            Thread.Sleep((int)Math.Ceiling(wait - idle.Elapsed.TotalMilliseconds));
                        double idleMs = idle.Elapsed.TotalMilliseconds;
                        var result = WorkingSetRenderOnce(fixture, 1, single, counters, process);
                        expected ??= result.Sha256;
                        if (result.Sha256 != expected) throw new InvalidOperationException("A/B output changed for identical input.");
                        var row = new WorkingSetTrial(bank, count, single ? "single" : "all", arm, wait, pair, position, trim,
                            trialStartedAt, idleMs, trimMs, before, after, result);
                        File.AppendAllText(report, JsonSerializer.Serialize(row) + Environment.NewLine);
                    }
                    Console.WriteLine($"working_set.bank={bank};edit={(single ? "single" : "all")};wait={wait};pair={pair + 1}/{pairs};hash={expected}");
                }
            }
        }
        File.WriteAllText(Path.Combine(directory, "complete.json"), JsonSerializer.Serialize(new { completedAt = DateTimeOffset.Now }));
        return 0;
    }

    private static WorkingSetRender WorkingSetRenderOnce(StandaloneFixture fixture, int variant, bool single,
        TimingDseCounters counters, Process process)
    {
        var sequence = fixture.Sequence;
        nint partId = (nint)fixture.Part;
        int completed = 0, started = 0;
        double startMs = -1;
        long renderStarted = 0;
        using var observer = new WIVSMRendererObserver();
        observer.Started += (_, e) =>
        {
            if ((nint)e.MidiPart != partId) return;
            started++;
            startMs = Stopwatch.GetElapsedTime(renderStarted).TotalMilliseconds;
        };
        observer.Completed += (_, e) => { if ((nint)e.MidiPart == partId) completed++; };
        observer.Canceled += (_, e) => { if ((nint)e.MidiPart == partId) completed = -100; };
        if (!sequence.AddRendererObserver(observer)) throw new InvalidOperationException("Cannot attach benchmark observer.");
        try
        {
            var before = ReadWorkingSetSnapshot(process);
            var dseBefore = counters.Read();
            long totalStarted = Stopwatch.GetTimestamp();
            for (int i = 0; i < (single ? 1 : fixture.Notes.Length); i++)
                if (!fixture.Notes[i].SetNoteNumber(60 + i % 5 + variant * 2))
                    throw new InvalidOperationException("Cannot update benchmark pitch.");
            if (sequence.IsStaged && !sequence.Commit(false)) throw new InvalidOperationException("Cannot commit benchmark input.");
            // Measure the native pitch-edit path itself. Do not add temporary
            // velocity touches/extra commits that could broaden invalidation.
            // Fresh synthesis is verified independently using the DSE counters.
            double editMs = Stopwatch.GetElapsedTime(totalStarted).TotalMilliseconds;
            renderStarted = Stopwatch.GetTimestamp();
            sequence.StartAsyncRendering();
            while ((completed == 0 || !sequence.IsFinishedRendering) && Stopwatch.GetElapsedTime(renderStarted).TotalSeconds < 60)
            {
                for (int pump = 0; pump < 32 && sequence.InvokeRendererObserver(); pump++) { }
                if (completed < 0) break;
                if (completed == 0 || !sequence.IsFinishedRendering) Thread.Sleep(1);
            }
            double renderMs = Stopwatch.GetElapsedTime(renderStarted).TotalMilliseconds;
            double totalMs = Stopwatch.GetElapsedTime(totalStarted).TotalMilliseconds;
            var after = ReadWorkingSetSnapshot(process);
            var dse = counters.Read().Since(dseBefore);
            if (completed != 1 || started != 1 || !sequence.IsFinishedRendering || !fixture.Part.HasValidRenderedWave || dse.Steps == 0 || dse.Samples == 0)
                throw new InvalidOperationException($"Render did not produce fresh PCM: started={started};completed={completed};steps={dse.Steps};samples={dse.Samples}.");
            long stopStarted = Stopwatch.GetTimestamp();
            sequence.StopAsyncRendering();
            double stopMs = Stopwatch.GetElapsedTime(stopStarted).TotalMilliseconds;
            string wave = fixture.Part.WaveFilePath;
            var metrics = ReadWaveMetrics(wave);
            if (metrics.Peak <= 0.000001) throw new InvalidOperationException("Benchmark output is silent.");
            return new(editMs, renderMs, totalMs, startMs, stopMs, dse, before, after,
                Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(wave))), new FileInfo(wave).Length);
        }
        finally
        {
            sequence.StopAsyncRendering();
            sequence.RemoveRendererObserver(observer);
        }
    }

    private static WorkingSetSnapshot ReadWorkingSetSnapshot(Process process)
    {
        var memory = new WorkingSetNative.MemoryCounters { Size = (uint)Marshal.SizeOf<WorkingSetNative.MemoryCounters>() };
        var available = new WorkingSetNative.MemoryStatus { Size = (uint)Marshal.SizeOf<WorkingSetNative.MemoryStatus>() };
        if (!WorkingSetNative.GetProcessMemoryInfo(process.Handle, ref memory, memory.Size) ||
            !WorkingSetNative.GetProcessIoCounters(process.Handle, out var io) || !WorkingSetNative.GlobalMemoryStatusEx(ref available))
            throw new Win32Exception(Marshal.GetLastWin32Error());
        process.Refresh();
        return new((long)memory.WorkingSet, (long)memory.PrivateUsage, memory.PageFaultCount,
            io.ReadBytes, io.WriteBytes, process.TotalProcessorTime.TotalMilliseconds, available.AvailablePhysical);
    }

    private static class WorkingSetNative
    {
        [StructLayout(LayoutKind.Sequential)]
        internal struct MemoryCounters
        {
            internal uint Size, PageFaultCount;
            internal nuint PeakWorkingSet, WorkingSet, QuotaPeakPagedPool, QuotaPagedPool,
                QuotaPeakNonPagedPool, QuotaNonPagedPool, PagefileUsage, PeakPagefileUsage, PrivateUsage;
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct IoCounters
        {
            internal ulong ReadOperations, WriteOperations, OtherOperations, ReadBytes, WriteBytes, OtherBytes;
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct MemoryStatus
        {
            internal uint Size, Load;
            internal ulong TotalPhysical, AvailablePhysical, TotalPagefile, AvailablePagefile,
                TotalVirtual, AvailableVirtual, AvailableExtendedVirtual;
        }

        [DllImport("psapi.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool EmptyWorkingSet(nint process);

        [DllImport("psapi.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool GetProcessMemoryInfo(nint process, ref MemoryCounters counters, uint size);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool GetProcessIoCounters(nint process, out IoCounters counters);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool GlobalMemoryStatusEx(ref MemoryStatus status);
    }
}
