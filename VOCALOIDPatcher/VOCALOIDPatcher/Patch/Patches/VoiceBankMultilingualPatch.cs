using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using HarmonyLib;
using VOCALOIDPatcher.Config;
using Yamaha.VOCALOID.VDM;

namespace VOCALOIDPatcher.Patch.Patches;

internal static class VoiceBankHelper
{
    private static readonly ConditionalWeakTable<VoiceBank, BoxedBool> Cache = new();

    public static readonly List<int> AllLanguagesList = new() { 0, 1, 2, 3, 4 };

    public static bool IsAiVoiceBank(VoiceBank? voiceBank)
    {
        if (voiceBank == null) return false;
        if (Cache.TryGetValue(voiceBank, out var cached))
            return cached.Value;

        bool isAi = CheckIsAi(voiceBank);
        Cache.Add(voiceBank, new BoxedBool(isAi));

        if (isAi)
        {
            NativeVoiceBankHook.RegisterVoiceBank(voiceBank, isAi: true);
        }

        return isAi;
    }

    private static bool CheckIsAi(VoiceBank voiceBank)
    {
        try
        {
            int? major = voiceBank.MajorVersion;
            if (major.HasValue && major.Value >= 6)
                return true;

            string path = voiceBank.Path;
            if (!string.IsNullOrEmpty(path))
            {
                if (path.EndsWith(".vtb2", StringComparison.OrdinalIgnoreCase))
                    return true;
                if (path.IndexOf(@"VOCALOID6\Model", StringComparison.OrdinalIgnoreCase) >= 0)
                    return true;
                if (Directory.Exists(path) && Directory.EnumerateFiles(path, "*.vtb2").Any())
                    return true;
            }

            if (voiceBank.NPIndex > 0)
                return true;
        }
        catch
        {
            // Defensive: ignore reflection or filesystem exceptions
        }
        return false;
    }

    private sealed class BoxedBool
    {
        public readonly bool Value;
        public BoxedBool(bool val) => Value = val;
    }
}

public class VoiceBankLangIDsPatch : PatchBase
{
    public override string PatchName        => "VoiceBankLangIDsPatch";
    public override Type   TargetClass      => typeof(VoiceBank);
    public override string TargetMethodName => "get_LangIDs";
    public override Type[] ArgumentTypes    => Type.EmptyTypes;

    [HarmonyPrefix]
    private static bool Prefix(VoiceBank __instance, ref List<int> __result)
    {
        if (!Settings.UnlockAllLanguages)
            return true;

        if (VoiceBankHelper.IsAiVoiceBank(__instance))
        {
            __result = VoiceBankHelper.AllLanguagesList;
            return false;
        }
        return true;
    }
}

public class VoiceBankLangIDSizePatch : PatchBase
{
    public override string PatchName        => "VoiceBankLangIDSizePatch";
    public override Type   TargetClass      => typeof(VoiceBank);
    public override string TargetMethodName => "get_LangIDSize";
    public override Type[] ArgumentTypes    => Type.EmptyTypes;

    [HarmonyPrefix]
    private static bool Prefix(VoiceBank __instance, ref ulong __result)
    {
        if (!Settings.UnlockAllLanguages)
            return true;

        if (VoiceBankHelper.IsAiVoiceBank(__instance))
        {
            __result = 5UL;
            return false;
        }
        return true;
    }
}

public class VoiceBankLangIDByIndexPatch : PatchBase
{
    public override string PatchName        => "VoiceBankLangIDByIndexPatch";
    public override Type   TargetClass      => typeof(VoiceBank);
    public override string TargetMethodName => "LangIDByIndex";
    public override Type[] ArgumentTypes    => new[] { typeof(ulong) };

    [HarmonyPrefix]
    private static bool Prefix(VoiceBank __instance, ulong index, ref int __result)
    {
        if (!Settings.UnlockAllLanguages)
            return true;

        if (VoiceBankHelper.IsAiVoiceBank(__instance))
        {
            __result = index < 5 ? (int)index : -1;
            return false;
        }
        return true;
    }
}

public class VoiceBankContainsLangIDPatch : PatchBase
{
    public override string PatchName        => "VoiceBankContainsLangIDPatch";
    public override Type   TargetClass      => typeof(VoiceBank);
    public override string TargetMethodName => "ContainsLangID";
    public override Type[] ArgumentTypes    => new[] { typeof(int) };

    [HarmonyPrefix]
    private static bool Prefix(VoiceBank __instance, int langID, ref bool __result)
    {
        if (!Settings.UnlockAllLanguages)
            return true;

        if (VoiceBankHelper.IsAiVoiceBank(__instance))
        {
            __result = langID >= 0 && langID <= 4;
            return false;
        }
        return true;
    }
}

public class VoiceBankContainsAllLangIDsPatch : PatchBase
{
    public override string PatchName        => "VoiceBankContainsAllLangIDsPatch";
    public override Type   TargetClass      => typeof(VoiceBank);
    public override string TargetMethodName => "ContainsAllLangIDs";
    public override Type[] ArgumentTypes    => new[] { typeof(List<int>) };

    [HarmonyPrefix]
    private static bool Prefix(VoiceBank __instance, List<int>? langIDs, ref bool __result)
    {
        if (!Settings.UnlockAllLanguages)
            return true;

        if (VoiceBankHelper.IsAiVoiceBank(__instance))
        {
            __result = langIDs != null && langIDs.TrueForAll(id => id >= 0 && id <= 4);
            return false;
        }
        return true;
    }
}

public class DatabaseManagerCreatePatch : PatchBase
{
    public override string PatchName        => "DatabaseManagerCreatePatch";
    public override Type   TargetClass      => typeof(DatabaseManagerIF);
    public override string TargetMethodName => "CreateDatabaseManager";
    public override Type[] ArgumentTypes    => new[] { typeof(string), typeof(string), typeof(VDMError).MakeByRefType() };

    [HarmonyPostfix]
    private static void Postfix(DatabaseManager? __result)
    {
        try
        {
            if (__result != null)
            {
                NativeVoiceBankHook.Initialize(__result);
            }
        }
        catch
        {
            // Defensive
        }
    }
}

public class VoiceBankConstructorPatch : PatchBase
{
    public override string PatchName        => "VoiceBankConstructorPatch";
    public override Type   TargetClass      => typeof(VoiceBank);
    public override string TargetMethodName => ".ctor";
    public override bool   IsConstructor    => true;
    public override Type[] ArgumentTypes    => new[] { typeof(IntPtr) };

    [HarmonyPostfix]
    private static void Postfix(VoiceBank __instance)
    {
        try
        {
            NativeVoiceBankHook.RegisterVoiceBank(__instance);
        }
        catch
        {
            // Defensive
        }
    }
}
