using System.Buffers.Binary;
using System.Runtime.InteropServices;
using VOCALOIDPatcher.ConsonantTiming;
using VOCALOIDPatcher.Evec;
using VOCALOIDPatcher.Utils;
using Xunit;

namespace VOCALOIDPatcher.McpTests;

public sealed class NativeSignatureTests
{
    private static byte[] Image()
    {
        var bytes = new byte[0x1800];
        void U16(int at, ushort value) => BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(at), value);
        void U32(int at, int value) => BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(at), value);
        U16(0, 0x5a4d); U32(0x3c, 0x80); U32(0x80, 0x4550);
        U16(0x84, 0x8664); U16(0x86, 3); U16(0x94, 240); U16(0x98, 0x20b);
        U32(0x98 + 56, 0x4000); U32(0x98 + 60, 0x400); U32(0x98 + 108, 16);
        U32(0x98 + 112 + 24, 0x3000); U32(0x98 + 112 + 28, 12);
        void Section(int index, string name, int rva, int size, int raw, int flags)
        {
            int row = 0x188 + index * 40;
            System.Text.Encoding.ASCII.GetBytes(name).CopyTo(bytes, row);
            U32(row + 8, size); U32(row + 12, rva); U32(row + 16, size); U32(row + 20, raw); U32(row + 36, flags);
        }
        Section(0, ".text", 0x1000, 0x1000, 0x400, 0x60000020);
        Section(1, ".data", 0x2000, 0x200, 0x1400, unchecked((int)0xc0000040));
        Section(2, ".pdata", 0x3000, 0x200, 0x1600, 0x40000040);
        U32(0x1600, 0x1000); U32(0x1604, 0x2000);
        bytes.AsSpan(0x400, 0x1000).Fill(0x90);
        return bytes;
    }

    private static void Put(byte[] file, int rva, params byte[] code) => code.CopyTo(file, rva - 0x1000 + 0x400);
    private static NativeCodeSignature Entry(string name = "entry", string pattern = "48 8B 81 50 02 00 00 C3") =>
        new() { Name = name, Module = "TEST", Pattern = pattern };

    [Fact]
    public void DiscoversMovedCodeAndIgnoresPeTimestamp()
    {
        var file = Image();
        Put(file, 0x1350, 0x48, 0x8b, 0x81, 0x50, 2, 0, 0, 0xc3);
        file[0x88] = 0xab;
        Assert.Equal(0x1350, new NativeCodeImage(file).Resolve("TEST", [Entry()])["entry"]);
    }

    [Fact]
    public void MissingAmbiguousAndChangedFieldContractsAreRejected()
    {
        var file = Image();
        Assert.Throws<NotSupportedException>(() => new NativeCodeImage(file).Resolve("TEST", [Entry()]));
        Put(file, 0x1100, 0x48, 0x8b, 0x81, 0x50, 2, 0, 0, 0xc3);
        Put(file, 0x1200, 0x48, 0x8b, 0x81, 0x50, 2, 0, 0, 0xc3);
        Assert.Throws<NotSupportedException>(() => new NativeCodeImage(file).Resolve("TEST", [Entry()]));
        var contract = Entry(); contract.Guards = ["8B 89 58 40 00 00"];
        Array.Clear(file, 0x600, 8);
        Assert.Throws<NotSupportedException>(() => new NativeCodeImage(file).Resolve("TEST", [contract]));
        Put(file, 0x1300, 0x8b, 0x89, 0x58, 0x40, 0, 0);
        Assert.Equal(0x1100, new NativeCodeImage(file).Resolve("TEST", [contract])["entry"]);
        Put(file, 0x1300, 0x8b, 0x89, 0x60, 0x40, 0, 0);
        Assert.Throws<NotSupportedException>(() => new NativeCodeImage(file).Resolve("TEST", [contract]));
    }

    [Fact]
    public void RelativeReferencesMustPointToTheIndependentlyResolvedTarget()
    {
        var file = Image();
        Put(file, 0x1100, 0xe8, 0xfb, 0, 0, 0, 0xc3);
        Put(file, 0x1200, 0x48, 0x8b, 0x81, 0x50, 2, 0, 0, 0xc3);
        var call = Entry("caller", "E8 ?? ?? ?? ?? C3");
        call.Links = [new() { Name = "entry", Offset = 1, End = 5 }];
        Assert.Equal(0x1200, new NativeCodeImage(file).Resolve("TEST", [call, Entry()])["entry"]);
        nint loaded = Marshal.AllocHGlobal(0x4000);
        try
        {
            Marshal.Copy(file, 0x400, loaded + 0x1000, 0x1000);
            Marshal.WriteByte(loaded + 0x1101, 0xfa); // A wildcard must not hide a changed live call target.
            Assert.Throws<NotSupportedException>(() => new NativeCodeImage(file, loaded).Resolve("TEST", [call, Entry()]));
        }
        finally { Marshal.FreeHGlobal(loaded); }
        file[0x501] = 0xfa;
        Assert.Throws<NotSupportedException>(() => new NativeCodeImage(file).Resolve("TEST", [call, Entry()]));
    }

    [Fact]
    public void IndependentCallerDisambiguatesIdenticalHelpers()
    {
        var file = Image();
        Put(file, 0x1100, 0xe8, 0xfb, 0, 0, 0, 0xc3);
        Put(file, 0x1200, 0x48, 0x8b, 0x81, 0x50, 2, 0, 0, 0xc3);
        Put(file, 0x1300, 0x48, 0x8b, 0x81, 0x50, 2, 0, 0, 0xc3);
        var call = Entry("caller", "E8 ?? ?? ?? ?? C3");
        call.Links = [new() { Name = "entry", Offset = 1, End = 5 }];
        Assert.Equal(0x1200, new NativeCodeImage(file).Resolve("TEST", [call, Entry()])["entry"]);
    }

    [Fact]
    public void RipRelativeGlobalsAreResolvedAndCannotResolveIntoCode()
    {
        var file = Image();
        Put(file, 0x1100, 0x48, 0x8d, 0x05, 0xf9, 0x0e, 0, 0, 0xc3);
        var global = Entry("DseStates", "48 8D 05 ?? ?? ?? ?? C3");
        global.RelativeOffset = 3; global.RelativeEnd = 7;
        Assert.Equal(0x2000, new NativeCodeImage(file).Resolve("TEST", [global])["DseStates"]);
        file[0x504] = 0;
        Assert.Throws<NotSupportedException>(() => new NativeCodeImage(file).Resolve("TEST", [global]));
    }

    [Fact]
    public void VdmReplacementPreservesDestinationAndRejectsExternalOrBackwardJumps()
    {
        var file = Image(); Put(file, 0x1100, 0x0f, 0x85, 0x7a, 1, 0, 0);
        var replacement = EvecCatalogDiscoveryScope.CreateBranchReplacement(new NativeCodeImage(file), 0x1100);
        Assert.Equal(0xe9, replacement[0]); Assert.Equal(0x90, replacement[5]);
        Assert.Equal(0x1280, 0x1105 + BinaryPrimitives.ReadInt32LittleEndian(replacement.AsSpan(1, 4)));
        Put(file, 0x1100, 0x0f, 0x85, 0, 0xff, 0xff, 0xff);
        Assert.Throws<NotSupportedException>(() => EvecCatalogDiscoveryScope.CreateBranchReplacement(new NativeCodeImage(file), 0x1100));
        Put(file, 0x1100, 0x0f, 0x85, 0, 0x10, 0, 0);
        Assert.Throws<NotSupportedException>(() => EvecCatalogDiscoveryScope.CreateBranchReplacement(new NativeCodeImage(file), 0x1100));
    }

    [Fact]
    public void CatalogCoversEveryNativeAddressAndContainsBodyContracts()
    {
        var catalog = NativeCodeSignature.Catalog;
        Assert.Equal(TimingNativeProfile.AddressNames, catalog.Where(s => s.Module is "DSE" or "VSM").Select(s => s.Name).Distinct().OrderBy(n => n, StringComparer.Ordinal));
        Assert.Contains(catalog, s => s.Name == "VsmPrepare" && s.Guards.Length > 4);
        Assert.Contains(catalog, s => s.Name == "DseStates" && s.RelativeOffset >= 0);
        Assert.Contains(catalog, s => s.Name == "DurationCaller0" && s.Links.Length > 0);
    }

    [Fact]
    public void MalformedImagesAndAlteredLoadedCodeAreRejected()
    {
        var file = Image();
        file[0x84] = 0x4c;
        Assert.Throws<NotSupportedException>(() => new NativeCodeImage(file));
        file = Image();
        BinaryPrimitives.WriteInt32LittleEndian(file.AsSpan(0x188 + 12), int.MaxValue);
        Assert.Throws<InvalidDataException>(() => new NativeCodeImage(file));
        file = Image();
        Put(file, 0x1100, 0x48, 0x8b, 0x81, 0x50, 2, 0, 0, 0xc3);
        nint loaded = Marshal.AllocHGlobal(0x4000);
        try
        {
            Marshal.Copy(file, 0x400, loaded + 0x1000, 0x1000);
            Assert.Equal(0x1100, new NativeCodeImage(file, loaded).Resolve("TEST", [Entry()])["entry"]);
            Marshal.WriteByte(loaded + 0x1103, 0x58);
            Assert.Throws<NotSupportedException>(() => new NativeCodeImage(file, loaded).Resolve("TEST", [Entry()]));
        }
        finally { Marshal.FreeHGlobal(loaded); }
    }

    [SignatureSdkFact]
    public void RealSdkSignaturesResolveAllAddressesWithoutFileHashIdentity()
    {
        foreach (string directory in Environment.GetEnvironmentVariable("VOCALOID_SIGNATURE_TEST_SDKS")!.Split('|'))
        {
            var dse = new NativeCodeImage(Path.Combine(directory, "DSE.dll"));
            var vsm = new NativeCodeImage(Path.Combine(directory, "VSM.dll"));
            var profile = TimingNativeProfile.Resolve(dse, vsm);
            Assert.Equal(2, vsm.Resolve("VSMDirectory").Count);
            Assert.Equal("sampled-signatures-layout-v1", profile.Id);
            Assert.Contains(TimingNativeProfile.Supported, p => p.NativeAddresses.SequenceEqual(profile.NativeAddresses));
            byte[] changed = File.ReadAllBytes(Path.Combine(directory, "VSM.dll"));
            int pe = BinaryPrimitives.ReadInt32LittleEndian(changed.AsSpan(0x3c, 4));
            changed[pe + 8] ^= 0xff; // Timestamp, not an executable contract.
            Assert.Equal(profile.NativeAddresses, TimingNativeProfile.Resolve(dse, new NativeCodeImage(changed)).NativeAddresses);
            var vdm = new NativeCodeImage(Path.Combine(directory, "VDM.dll"));
            int branch = vdm.Resolve("VDM")["VdmCatalogBranch"];
            Assert.Equal(0xdc3d0, branch);
            Assert.Equal(0xe9, EvecCatalogDiscoveryScope.CreateBranchReplacement(vdm, branch)[0]);
        }
        var directories = Environment.GetEnvironmentVariable("VOCALOID_SIGNATURE_TEST_SDKS")!.Split('|');
        if (directories.Length > 1)
        {
            // DSE code/structures are shared: metadata-only version differences
            // must no longer reject a structurally compatible DSE/VSM pairing.
            var mixed = TimingNativeProfile.Resolve(new NativeCodeImage(Path.Combine(directories[0], "DSE.dll")),
                new NativeCodeImage(Path.Combine(directories[1], "VSM.dll")));
            Assert.Equal(TimingNativeProfile.Supported[1].NativeAddresses, mixed.NativeAddresses);
        }
    }
}

internal sealed class SignatureSdkFactAttribute : FactAttribute
{
    public SignatureSdkFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("VOCALOID_SIGNATURE_TEST_SDKS")))
            Skip = "Set VOCALOID_SIGNATURE_TEST_SDKS to legal SDK directories separated by |.";
    }
}
