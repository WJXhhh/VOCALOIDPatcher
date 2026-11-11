using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using VOCALOIDPatcher.ConsonantTiming;

internal static partial class Program
{
    private static unsafe int ProbeTimingProfile(string directory)
    {
        nint library = NativeLibrary.Load(Environment.GetEnvironmentVariable("VOCALOID_TIMING_PROBE_NATIVE")!);
        try
        {
            var fingerprint = (delegate* unmanaged[Cdecl]<ulong>)NativeLibrary.GetExport(library, "v6_timing_profile_fingerprint");
            var select = (delegate* unmanaged[Cdecl]<ulong, nint, nint, uint*, uint, int>)NativeLibrary.GetExport(library, "v6_timing_select_layout");
            var install = (delegate* unmanaged[Cdecl]<int>)NativeLibrary.GetExport(library, "v6_timing_install");
            var profile = LocalTimingNative.Profile;
            LocalTimingNative.VerifyRendererDirectory();
            ulong managed = profile.Fingerprint;
            var supports = (delegate* unmanaged[Cdecl]<ulong, int>)NativeLibrary.GetExport(library, "v6_timing_profile_supported");
            Require(supports(managed) == 1, "Native bridge does not contain this profile.");
            var addresses = profile.NativeAddresses;
            fixed (uint* table = addresses)
            {
                Require(select(managed ^ 1, LocalTimingNative.Dse, LocalTimingNative.Vsm, table, (uint)addresses.Length) == -1, "Wrong profile was selected.");
                Require(install() == -30, "Unselected timing profile installed private hooks.");
                Require(select(managed, 1, 1, table, (uint)addresses.Length) == -2, "Wrong native handles were selected.");
                Require(select(managed, LocalTimingNative.Dse, LocalTimingNative.Vsm, table, (uint)addresses.Length - 1) == -1, "Incomplete address table was selected.");
            }
            LocalTimingNative.VerifyBridge(library);
            Require(fingerprint() == managed, "Managed and Rust selected profiles differ.");
            var changedAddresses = profile.NativeAddresses;
            changedAddresses[0]++;
            fixed (uint* changed = changedAddresses)
                Require(select(managed, LocalTimingNative.Dse, LocalTimingNative.Vsm, changed, (uint)changedAddresses.Length) == -3,
                    "A selected native layout accepted different addresses.");
            string? oldPath = Environment.GetEnvironmentVariable("VOCALOID_TIMING_OLD_NATIVE");
            bool oldRejected = false;
            if (oldPath != null)
            {
                nint old = NativeLibrary.Load(Path.GetFullPath(oldPath));
                try { LocalTimingNative.VerifyBridge(old); }
                catch (NotSupportedException) { oldRejected = true; }
                finally { NativeLibrary.Free(old); }
                Require(oldRejected, "A bridge without the selected compatibility profile was accepted.");
            }
            Directory.CreateDirectory(directory);
            var result = new { profile = profile.Id, fingerprint = managed.ToString("X16"),
                managedSdk = typeof(Yamaha.VOCALOID.VSM.WIVSMSequence).Assembly.GetName().Version?.ToString(),
                managedSdkSha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(typeof(Yamaha.VOCALOID.VSM.WIVSMSequence).Assembly.Location))),
                fingerprintMatched = true, badProfileRejected = true, badHandlesRejected = true, unselectedInstallRejected = true,
                oldBridgeTested = oldPath != null, oldBridgeRejected = oldRejected, validProfileSelected = true };
            File.WriteAllText(Path.Combine(directory, "timing-profile.json"), JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true }));
            Console.WriteLine("timing.profile=" + JsonSerializer.Serialize(result));
            return 0;
        }
        finally { NativeLibrary.Free(library); }
    }
}
