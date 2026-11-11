using System.IO;
using System.Runtime.InteropServices;
using VOCALOIDPatcher.Evec;
using VOCALOIDPatcher.Utils;
using Yamaha.VOCALOID.PathResource;
using Yamaha.VOCALOID.VDM;

internal static partial class Program
{
    private static int ProbeCatalogScope()
    {
        IntPtr module = ResolveEditorLibrary("vdm");
        string path = Path.Combine(EditorDirectory, "VDM.dll");
        int rva = new NativeCodeImage(path, module).Resolve("VDM")["VdmCatalogBranch"];
        byte[] original = ReadCatalogInstruction(module, rva);
        byte[] expected = EvecCatalogDiscoveryScope.CreateBranchReplacement(new NativeCodeImage(path), rva);
        ulong before = CountCatalog();
        ulong inside;
        using (var scope = EvecCatalogDiscoveryScope.Enter(module, path))
        {
            if (!ReadCatalogInstruction(module, rva).AsSpan().SequenceEqual(expected))
                throw new InvalidOperationException("Catalog branch was not replaced.");
            inside = CountCatalog();
            using (EvecCatalogDiscoveryScope.Enter(module, path))
            {
                if (CountCatalog() != inside)
                    throw new InvalidOperationException("Nested catalog scope changed discovery.");
            }
            if (CountCatalog() != inside)
                throw new InvalidOperationException("Nested scope restored instructions prematurely.");
        }
        if (!ReadCatalogInstruction(module, rva).AsSpan().SequenceEqual(original))
            throw new InvalidOperationException("Catalog instructions were not restored.");
        ulong after = CountCatalog();
        try
        {
            using var scope = EvecCatalogDiscoveryScope.Enter(module, path);
            throw new InvalidDataException("Simulated database creation failure.");
        }
        catch (InvalidDataException) { }
        if (!ReadCatalogInstruction(module, rva).AsSpan().SequenceEqual(original) || CountCatalog() != before)
            throw new InvalidOperationException("Exception cleanup left catalog patch active.");
        bool rejected = false;
        try
        {
            using var scope = EvecCatalogDiscoveryScope.Enter(module, Path.Combine(EditorDirectory, "VOCALOID6.dll"));
        }
        catch (Exception error) when (error is NotSupportedException or InvalidDataException or BadImageFormatException) { rejected = true; }
        bool passed = rejected && inside >= before && after == before &&
                      ReadCatalogInstruction(module, rva).AsSpan().SequenceEqual(original);
        Console.WriteLine($"catalog_scope.before={before};inside={inside};after={after};" +
            $"catalog_expanded={inside > before};wrong_binary_rejected={rejected};scope_lifecycle_passed={passed}");
        return passed ? 0 : 60;
    }

    private static byte[] ReadCatalogInstruction(IntPtr module, int rva)
    {
        byte[] bytes = new byte[6];
        Marshal.Copy(IntPtr.Add(module, rva), bytes, 0, bytes.Length);
        return bytes;
    }

    private static ulong CountCatalog()
    {
        VDMError error = VDMError.None;
        using var database = DatabaseManagerIF.CreateDatabaseManager("VOCALOID6", DirectoryPath.ExpressionLibrary, ref error);
        if (database == null || error != VDMError.None)
            throw new InvalidOperationException("Catalog creation failed.");
        return database.NumVoiceBanks;
    }
}
