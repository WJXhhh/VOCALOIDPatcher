using System.Runtime.InteropServices;

internal readonly record struct TimingDseSnapshot(ulong Starts, ulong Steps, ulong Samples)
{
    internal TimingDseSnapshot Since(TimingDseSnapshot before) =>
        // Sample diagnostics reset at Start; Starts and Steps are cumulative.
        new(Starts - before.Starts, Steps - before.Steps,
            Starts == before.Starts ? Samples - before.Samples : Samples);
}

// Isolated probes only. ABI 14 offsets are pinned to DseCaptureStatus in the
// repository's native/playback-clock code. No Editor or patcher assembly is loaded.
internal sealed unsafe class TimingDseCounters : IDisposable
{
    private readonly nint _buffer = Marshal.AllocHGlobal(4096);
    private readonly delegate* unmanaged[Cdecl]<nint, int> _status;
    internal ulong VtableRva { get; }

    internal TimingDseCounters()
    {
        string path = Environment.GetEnvironmentVariable("VOCALOID_TIMING_PROBE_NATIVE")
            ?? throw new InvalidOperationException("Set the isolated counter DLL path.");
        // Keep the library mapped until process exit: installed vtable hooks refer to it.
        nint library = NativeLibrary.Load(path);
        var abi = (delegate* unmanaged[Cdecl]<uint>)NativeLibrary.GetExport(library, "v6_clock_abi_version");
        if (abi() != 15) throw new InvalidOperationException("Unsupported counter ABI.");
        var install = (delegate* unmanaged[Cdecl]<int>)NativeLibrary.GetExport(library, "v6_dse_install");
        if (install() < 0) throw new InvalidOperationException("DSE counter installation failed.");
        _status = (delegate* unmanaged[Cdecl]<nint, int>)NativeLibrary.GetExport(library, "v6_dse_status");
        Read();
        VtableRva = unchecked((ulong)Marshal.ReadInt64(_buffer, 8));
        Console.WriteLine($"local.dse_vtable_rva=0x{VtableRva:X}");
    }

    internal TimingDseSnapshot Read()
    {
        if (_status(_buffer) < 0) throw new InvalidOperationException("Cannot read DSE counters.");
        return new(unchecked((ulong)Marshal.ReadInt64(_buffer, 40)),
            unchecked((ulong)Marshal.ReadInt64(_buffer, 56)),
            unchecked((ulong)Marshal.ReadInt64(_buffer, 152)));
    }

    public void Dispose() => Marshal.FreeHGlobal(_buffer);
}
