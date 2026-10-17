using System.IO;
using System.Runtime.InteropServices;
using VOCALOIDPatcher.Evec;
using Yamaha.VOCALOID.PathResource;
using Yamaha.VOCALOID.VDM;

internal static partial class Program
{
    private static int ProbeCatalogScope()
    {
        IntPtr module = ResolveEditorLibrary("vdm");
        string path = Path.Combine(EditorDirectory, "VDM.dll");
        byte[] original = ReadCatalogInstruction(module);
        ulong before = CountCatalog();
        ulong inside;
        using (var scope = EvecCatalogDiscoveryScope.Enter(module, path))
        {
            inside = CountCatalog();
            using (EvecCatalogDiscoveryScope.Enter(module, path))
            {
                if (CountCatalog() != inside)
                    throw new InvalidOperationException("Nested catalog scope changed discovery.");
            }
            if (CountCatalog() != inside)
                throw new InvalidOperationException("Nested scope restored instructions prematurely.");
        }
        if (!ReadCatalogInstruction(module).AsSpan().SequenceEqual(original))
            throw new InvalidOperationException("Catalog instructions were not restored.");
        ulong after = CountCatalog();
        try
        {
            using var scope = EvecCatalogDiscoveryScope.Enter(module, path);
            throw new InvalidDataException("Simulated database creation failure.");
        }
        catch (InvalidDataException) { }
        if (!ReadCatalogInstruction(module).AsSpan().SequenceEqual(original) || CountCatalog() != before)
            throw new InvalidOperationException("Exception cleanup left catalog patch active.");
        bool rejected = false;
        try
        {
            using var scope = EvecCatalogDiscoveryScope.Enter(module, Path.Combine(EditorDirectory, "VOCALOID6.dll"));
        }
        catch (InvalidOperationException) { rejected = true; }
        bool passed = rejected && inside > before && after == before &&
                      ReadCatalogInstruction(module).AsSpan().SequenceEqual(original);
        Console.WriteLine($"catalog_scope.before={before};inside={inside};after={after};" +
            $"wrong_binary_rejected={rejected};passed={passed}");
        return passed ? 0 : 60;
    }

    private static byte[] ReadCatalogInstruction(IntPtr module)
    {
        byte[] bytes = new byte[6];
        Marshal.Copy(IntPtr.Add(module, 0xDC3D0), bytes, 0, bytes.Length);
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
