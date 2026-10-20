using System;
using System.IO;
using System.Runtime.InteropServices;
using Yamaha.VOCALOID.VSM;

namespace VOCALOIDPatcher.ConsonantTiming;

internal static class RenderTemporaryDirectory
{
    internal static void Repair(WIVSMSequence source)
    {
        if (!source.IsOpen || !source.IsFinishedRendering || source.IsStaged)
            throw new InvalidOperationException("Temporary directory repair requires an idle source.");
        Directory.CreateDirectory(Read(source));
    }

    internal static object Inspect(WIVSMSequence source)
    {
        try { return new { exists = Directory.Exists(Read(source)), adapter = "verified" }; }
        catch (Exception error) { return new { adapter = error.GetType().Name }; }
    }

    internal static string Read(WIVSMSequence source)
    {
        // Verified in both actual binaries. Never rewrite a native string or
        // read this private field on an unknown version.
        string id = LocalTimingNative.Profile.Id;
        if (id is not ("sampled-dse-vsm-6.13.1.1-v1" or "sampled-dse-vsm-6.13.3.1-v1"))
            throw new NotSupportedException("No verified renderer temporary directory adapter.");
        if (!source.IsOpen) throw new InvalidOperationException("Renderer source has closed.");
        nint value = (nint)source + 0x598;
        long length = Marshal.ReadInt64(value, 16), capacity = Marshal.ReadInt64(value, 24);
        if (length is < 1 or > 1024 || capacity < length || capacity > 32768)
            throw new InvalidOperationException("Invalid renderer directory string.");
        string path = Marshal.PtrToStringUni(capacity > 7 ? Marshal.ReadIntPtr(value) : value, (int)length)!;
        string root = Path.GetFullPath(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "VOCALOID6", "VSM")) + Path.DirectorySeparatorChar;
        string resolved = Path.GetFullPath(path);
        if (!resolved.StartsWith(root, StringComparison.OrdinalIgnoreCase) || !Guid.TryParse(Path.GetFileName(resolved), out _))
            throw new InvalidOperationException("Renderer directory lies outside its expected temporary root.");
        return resolved;
    }
}
