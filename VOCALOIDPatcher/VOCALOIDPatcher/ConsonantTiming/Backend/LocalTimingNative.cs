using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace VOCALOIDPatcher.ConsonantTiming;

internal static class LocalTimingNative
{
    private static readonly object Gate = new();
    private static volatile bool _verified;
    private static TimingNativeProfile? _profile;
    private static string? _reason;
    private static (nint Dse, nint Vsm, string Reason)? _rejected;
    internal static string Status => _verified ? "identified:" + _profile!.Id : _reason ?? "not-requested";
    internal static TimingNativeProfile Profile { get { VerifyImages(); return _profile!; } }

    internal static unsafe void VerifyBridge(nint library)
    {
        var profile = Profile;
        if (!NativeLibrary.TryGetExport(library, "v6_timing_profile_abi_version", out nint abi) ||
            ((delegate* unmanaged[Cdecl]<uint>)abi)() != 2 ||
            !NativeLibrary.TryGetExport(library, "v6_timing_profile_supported", out nint entry) ||
            ((delegate* unmanaged[Cdecl]<ulong, int>)entry)(profile.Fingerprint) != 1)
            throw new NotSupportedException("Managed/native timing compatibility profiles do not match.");
        if (!NativeLibrary.TryGetExport(library, "v6_timing_select_profile", out nint select) ||
            ((delegate* unmanaged[Cdecl]<ulong, nint, nint, int>)select)(profile.Fingerprint, Dse, Vsm) != 0)
            throw new NotSupportedException("Native timing profile selection rejected the loaded modules.");
    }

    internal static nint Dse => RequireModule("DSE.dll");
    internal static nint Vsm => RequireModule("VSM.dll");

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
                var profile = TimingNativeProfile.Identify(Hash(dse), Hash(vsm));
                if (profile == null)
                {
                    const string reason = "This DSE/VSM component tuple has not been validated; project controls are preserved.";
                    // Repeated hover/query requests on an unknown build must
                    // not hash both libraries on every UI interaction. Missing
                    // modules and transient I/O failures remain retryable.
                    _rejected = (dse, vsm, reason);
                    throw new NotSupportedException(reason);
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

    private static string Hash(nint module)
    {
        var path = new StringBuilder(32768);
        uint length = GetModuleFileName(module, path, path.Capacity);
        if (length == 0 || length >= path.Capacity)
            throw new NotSupportedException("Cannot identify the loaded timing library.");
        using var stream = File.OpenRead(path.ToString());
        return Convert.ToHexString(SHA256.HashData(stream));
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, EntryPoint = "GetModuleHandleW")]
    private static extern nint GetModuleHandle(string name);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, EntryPoint = "GetModuleFileNameW")]
    private static extern uint GetModuleFileName(nint module, StringBuilder path, int capacity);
}
