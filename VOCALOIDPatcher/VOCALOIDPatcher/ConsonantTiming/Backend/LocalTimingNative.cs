using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using VOCALOIDPatcher.Utils;

namespace VOCALOIDPatcher.ConsonantTiming;

internal static class LocalTimingNative
{
    private static readonly object Gate = new();
    private static volatile bool _verified;
    private static bool _rendererDirectoryVerified;
    private static TimingNativeProfile? _profile;
    private static string? _reason;
    private static (nint Dse, nint Vsm, string Reason)? _rejected;
    internal static string Status => _verified ? "identified:" + _profile!.Id : _reason ?? "not-requested";
    internal static TimingNativeProfile Profile { get { VerifyImages(); return _profile!; } }

    internal static unsafe void VerifyBridge(nint library)
    {
        var profile = Profile;
        if (!NativeLibrary.TryGetExport(library, "v6_timing_profile_abi_version", out nint abi) ||
            ((delegate* unmanaged[Cdecl]<uint>)abi)() != 3 ||
            !NativeLibrary.TryGetExport(library, "v6_timing_profile_supported", out nint entry) ||
            ((delegate* unmanaged[Cdecl]<ulong, int>)entry)(profile.Fingerprint) != 1)
            throw new NotSupportedException("Managed/native timing compatibility profiles do not match.");
        var addresses = profile.NativeAddresses;
        fixed (uint* table = addresses)
            if (!NativeLibrary.TryGetExport(library, "v6_timing_select_layout", out nint select) ||
                ((delegate* unmanaged[Cdecl]<ulong, nint, nint, uint*, uint, int>)select)(profile.Fingerprint, Dse, Vsm, table, (uint)addresses.Length) != 0)
                throw new NotSupportedException("Native timing layout selection rejected the loaded modules.");
    }

    internal static nint Dse => RequireModule("DSE.dll");
    internal static nint Vsm => RequireModule("VSM.dll");

    internal static void VerifyRendererDirectory()
    {
        VerifyImages();
        lock (Gate)
        {
            if (_rendererDirectoryVerified) return;
            nint module = Vsm;
            _ = new NativeCodeImage(ModulePath(module), module).Resolve("VSMDirectory");
            _rendererDirectoryVerified = true;
        }
    }

    internal static void VerifyImages()
    {
        if (_verified) return;
        lock (Gate)
        {
            if (_verified) return;
            try
            {
                if (!OperatingSystem.IsWindows() || IntPtr.Size != 8)
                    throw new NotSupportedException("Timing backend requires Windows x64.");
                nint dse = Dse, vsm = Vsm;
                if (_rejected is { } previous && previous.Dse == dse && previous.Vsm == vsm)
                    throw new NotSupportedException(previous.Reason);
                TimingNativeProfile profile;
                try
                {
                    profile = ResolveProfile(dse, vsm);
                }
                catch (Exception error) when (error is NotSupportedException or InvalidDataException or BadImageFormatException)
                {
                    // Cache structural rejection, but keep missing modules and
                    // transient file access failures retryable.
                    _rejected = (dse, vsm, error.Message);
                    throw new NotSupportedException(error.Message, error);
                }
                _profile = profile; _verified = true; _reason = null;
            }
            catch (Exception error) { _reason = error.Message; throw; }
        }
    }

    private static nint RequireModule(string name)
    {
        nint module = GetModuleHandle(name);
        return module != 0 ? module : throw new NotSupportedException("Timing libraries have not been initialized.");
    }

    private static TimingNativeProfile ResolveProfile(nint dse, nint vsm) =>
        TimingNativeProfile.Resolve(new NativeCodeImage(ModulePath(dse), dse), new NativeCodeImage(ModulePath(vsm), vsm));

    private static string ModulePath(nint module)
    {
        var path = new StringBuilder(32768);
        uint length = GetModuleFileName(module, path, path.Capacity);
        if (length == 0 || length >= path.Capacity)
            throw new NotSupportedException("Cannot identify the loaded timing library.");
        return path.ToString();
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, EntryPoint = "GetModuleHandleW")]
    private static extern nint GetModuleHandle(string name);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, EntryPoint = "GetModuleFileNameW")]
    private static extern uint GetModuleFileName(nint module, StringBuilder path, int capacity);
}
