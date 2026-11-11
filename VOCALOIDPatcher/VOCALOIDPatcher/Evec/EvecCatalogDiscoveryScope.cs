using System;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using VOCALOIDPatcher.Utils;

namespace VOCALOIDPatcher.Evec;

/// <summary>
/// Allows the legacy VDM catalog reader to retain registered V3/V4 entries
/// without key descriptors. Only active during synchronous database creation;
/// does not alter DSE initialization or render results, or the DLL on disk.
/// </summary>
internal sealed class EvecCatalogDiscoveryScope : IDisposable
{
    private static readonly object Sync = new();
    private static IntPtr _activeAddress;
    private static IntPtr _activeModule;
    private static byte[]? _original;
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
            if (_depth > 0)
            {
                if (module != _activeModule)
                    throw new InvalidOperationException("VDM catalog module changed during discovery.");
                _depth++;
                return new EvecCatalogDiscoveryScope();
            }

            var image = new NativeCodeImage(path, module);
            int rva = image.Resolve("VDM")["VdmCatalogBranch"];
            byte[] original = image.Read(rva, 6);
            var replacement = CreateBranchReplacement(image, rva);
            IntPtr address = module + rva;
            byte[] actual = new byte[original.Length];
            Marshal.Copy(address, actual, 0, actual.Length);
            if (!actual.AsSpan().SequenceEqual(original))
                throw new InvalidOperationException("VDM catalog instruction mismatch.");

            try
            {
                Write(address, replacement);
            }
            catch
            {
                // A protection/cache-flush failure after the copy must not
                // leave a patch active when no scope was returned to the caller.
                Write(address, original);
                throw;
            }
            _activeAddress = address;
            _activeModule = module;
            _original = original;
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
                Write(_activeAddress, _original!);
                _activeAddress = IntPtr.Zero;
                _activeModule = IntPtr.Zero;
                _original = null;
            }
        }
        finally
        {
            _disposed = true;
            Monitor.Exit(Sync);
        }
    }

    internal static byte[] CreateBranchReplacement(NativeCodeImage image, int rva)
    {
        var original = image.Read(rva, 6);
        if (original[0] != 0x0F || original[1] != 0x85)
            throw new NotSupportedException("VDM catalog branch is not a near conditional jump.");
        int target = checked(rva + 6 + image.ReadInt32(rva + 2));
        var function = image.Function(rva);
        if (function == null || target <= rva + 6 || target >= function.Value.End || !image.IsCode(target))
            throw new NotSupportedException("VDM catalog branch leaves its validated function.");
        var replacement = new byte[] { 0xE9, 0, 0, 0, 0, 0x90 };
        System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(replacement.AsSpan(1, 4), checked(target - rva - 5));
        return replacement;
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
