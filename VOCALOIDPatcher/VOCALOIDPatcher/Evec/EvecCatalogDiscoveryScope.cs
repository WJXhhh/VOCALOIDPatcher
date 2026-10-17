using System;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Threading;

namespace VOCALOIDPatcher.Evec;

/// <summary>
/// Allows the legacy VDM catalog reader to retain registered V3/V4 entries
/// without key descriptors. Only active during synchronous database creation;
/// does not alter DSE initialization or render results, or the DLL on disk.
/// </summary>
internal sealed class EvecCatalogDiscoveryScope : IDisposable
{
    private const string ExpectedHash = "73F1E128932521A1BA7DDB4736B59BC2D437E134753A3F3AFA2DF7751D430DB2";
    // 6.13.3 ships identical VDM code/data; only debug metadata differs.
    private const string UpdatedHash = "A673A7EBBAB0D12D2275FC459ECE630CC55DA55CBCA95A4E62E6D79F8D9D3722";
    private static readonly byte[] Original = [0x0F, 0x85, 0x79, 0x01, 0x00, 0x00];
    private static readonly byte[] Replacement = [0xE9, 0x7A, 0x01, 0x00, 0x00, 0x90];
    private static readonly object Sync = new();
    private static IntPtr _activeAddress;
    private static int _depth;
    private bool _disposed;

    private EvecCatalogDiscoveryScope() { }

    internal static EvecCatalogDiscoveryScope Enter(IntPtr module, string path)
    {
        Monitor.Enter(Sync);
        try
        {
            if (module == IntPtr.Zero)
                throw new ArgumentException("VDM module is unavailable.", nameof(module));
            IntPtr address = IntPtr.Add(module, 0xDC3D0);
            if (_depth > 0)
            {
                if (address != _activeAddress)
                    throw new InvalidOperationException("VDM catalog module changed during discovery.");
                _depth++;
                return new EvecCatalogDiscoveryScope();
            }

            using var stream = File.OpenRead(path);
            string hash = Convert.ToHexString(SHA256.HashData(stream));
            if (hash != ExpectedHash && hash != UpdatedHash)
                throw new InvalidOperationException("Unsupported VDM catalog binary.");
            byte[] actual = new byte[Original.Length];
            Marshal.Copy(address, actual, 0, actual.Length);
            if (!actual.AsSpan().SequenceEqual(Original))
                throw new InvalidOperationException("VDM catalog instruction mismatch.");

            try
            {
                Write(address, Replacement);
            }
            catch
            {
                // A protection/cache-flush failure after the copy must not
                // leave a patch active when no scope was returned to the caller.
                Write(address, Original);
                throw;
            }
            _activeAddress = address;
            _depth = 1;
            return new EvecCatalogDiscoveryScope();
        }
        catch
        {
            Monitor.Exit(Sync);
            throw;
        }
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        try
        {
            if (--_depth == 0)
            {
                Write(_activeAddress, Original);
                _activeAddress = IntPtr.Zero;
            }
        }
        finally
        {
            _disposed = true;
            Monitor.Exit(Sync);
        }
    }

    private static void Write(IntPtr address, byte[] bytes)
    {
        if (!VirtualProtect(address, (UIntPtr)bytes.Length, 0x40, out uint previous))
            throw new Win32Exception(Marshal.GetLastWin32Error());
        try
        {
            Marshal.Copy(bytes, 0, address, bytes.Length);
            if (!FlushInstructionCache(GetCurrentProcess(), address, (UIntPtr)bytes.Length))
                throw new Win32Exception(Marshal.GetLastWin32Error());
        }
        finally
        {
            if (!VirtualProtect(address, (UIntPtr)bytes.Length, previous, out _))
                throw new Win32Exception(Marshal.GetLastWin32Error());
        }
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool VirtualProtect(IntPtr address, UIntPtr size, uint protection, out uint previous);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool FlushInstructionCache(IntPtr process, IntPtr address, UIntPtr size);

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetCurrentProcess();
}
