using System;
using System.IO;
using System.Runtime.InteropServices;
using HarmonyLib;
using VOCALOIDPatcher.Config;
using VOCALOIDPatcher.Evec;
using VOCALOIDPatcher.Utils;
using Yamaha.VOCALOID.VDM;

namespace VOCALOIDPatcher.Patch.Patches;

public sealed class EvecVoiceBankDiscoveryPatch : PatchBase
{
    private static readonly Lazy<IntPtr> VdmModule = new(() => NativeLibrary.Load(VdmPath));
    private static string VdmPath => Path.Combine(
        Path.GetDirectoryName(typeof(DatabaseManagerIF).Assembly.Location) ?? string.Empty, "VDM.dll");

    public override string PatchName => nameof(EvecVoiceBankDiscoveryPatch);
    public override Type TargetClass => typeof(DatabaseManagerIF);
    public override string TargetMethodName => nameof(DatabaseManagerIF.CreateDatabaseManager);
    public override Type[] ArgumentTypes =>
        [typeof(string), typeof(string), typeof(VDMError).MakeByRefType()];

    [HarmonyPrefix]
    private static void Prefix(out EvecCatalogDiscoveryScope? __state)
    {
        __state = null;
        if (!Settings.EvecEnabled)
            return;
        try
        {
            __state = EvecCatalogDiscoveryScope.Enter(VdmModule.Value, VdmPath);
            Debug.Print("[EVEC] Legacy voicebank catalog discovery enabled for database creation.");
        }
        catch (Exception exception)
        {
            Debug.Print($"[EVEC] Legacy voicebank discovery unavailable: {exception.GetType().Name}.");
        }
    }

    [HarmonyFinalizer]
    private static Exception? Finalizer(Exception? __exception, EvecCatalogDiscoveryScope? __state)
    {
        try
        {
            __state?.Dispose();
        }
        catch (Exception exception)
        {
            Debug.Print($"[EVEC] Legacy voicebank discovery cleanup failed: {exception.GetType().Name}.");
        }
        return __exception;
    }
}
