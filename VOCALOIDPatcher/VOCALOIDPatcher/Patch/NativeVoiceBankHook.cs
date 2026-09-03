using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using VOCALOIDPatcher.Config;
using VOCALOIDPatcher.Patch.Patches;
using Yamaha.VOCALOID.VDM;

namespace VOCALOIDPatcher.Patch;

/// <summary>
/// 对 vdm.dll 内部 VDM5::VoiceBank 的 C++ 虚函数表（VTable）进行内存挂钩。
/// 彻底打通 VSM.dll / dse.dll 等原生后台渲染流水线与 C# 托管层之间的声库语言及授权信息一致性。
/// </summary>
internal static class NativeVoiceBankHook
{
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate ulong LangIDSizeDelegate(IntPtr thisPtr);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int LangIDByIndexDelegate(IntPtr thisPtr, ulong index);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate byte IsAvailableDelegate(IntPtr thisPtr);

    // 静态代理委托，防止被 GC 回收
    private static readonly LangIDSizeDelegate S_HookLangIDSize = HookLangIDSize;
    private static readonly LangIDByIndexDelegate S_HookLangIDByIndex = HookLangIDByIndex;
    private static readonly IsAvailableDelegate S_HookIsAvailableForVoiceChanger = HookIsAvailableForVoiceChanger;

    private static LangIDSizeDelegate? s_origLangIDSize;
    private static LangIDByIndexDelegate? s_origLangIDByIndex;
    private static IsAvailableDelegate? s_origIsAvailableForVoiceChanger;

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool VirtualProtect(IntPtr lpAddress, UIntPtr dwSize, uint flNewProtect, out uint lpflOldProtect);

    private const uint PAGE_EXECUTE_READWRITE = 0x40;

    private static readonly object s_lock = new();
    private static volatile bool s_isHooked;

    // 记录所有已注册的 AI 声库原生指针
    private static readonly ConcurrentDictionary<IntPtr, bool> s_aiPointers = new();

    private static readonly PropertyInfo? SCppObjPtrProp =
        typeof(VoiceBank).GetProperty("CppObjPtr", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
    private static readonly FieldInfo? SCppObjPtrField =
        typeof(VoiceBank).GetField("<CppObjPtr>k__BackingField", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
     ?? typeof(VoiceBank).GetField("cppObjPtr", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);

    public static IntPtr GetNativePointer(VoiceBank? voiceBank)
    {
        if (voiceBank == null) return IntPtr.Zero;
        try
        {
            if (SCppObjPtrProp != null)
            {
                return (IntPtr)(SCppObjPtrProp.GetValue(voiceBank) ?? IntPtr.Zero);
            }
            if (SCppObjPtrField != null)
            {
                return (IntPtr)(SCppObjPtrField.GetValue(voiceBank) ?? IntPtr.Zero);
            }
        }
        catch
        {
            // Defensive
        }
        return IntPtr.Zero;
    }

    /// <summary>
    /// 当 DatabaseManager 初始化完成后，批量注册所有声库并挂钩虚表
    /// </summary>
    public static void Initialize(DatabaseManager? db)
    {
        if (db == null) return;

        try
        {
            // 1. 注册所有 AI 声库 (DNN)
            ulong numAi = db.GetNumVoiceBanks(VDMVoiceBankType.Dnn);
            for (ulong i = 0; i < numAi; i++)
            {
                var vb = db.GetVoiceBankByIndex(i, VDMVoiceBankType.Dnn);
                if (vb != null)
                {
                    RegisterVoiceBank(vb, isAi: true);
                }
            }

            // 2. 注册所有传统声库 (DSE)
            ulong numDse = db.GetNumVoiceBanks(VDMVoiceBankType.Dse);
            for (ulong i = 0; i < numDse; i++)
            {
                var vb = db.GetVoiceBankByIndex(i, VDMVoiceBankType.Dse);
                if (vb != null)
                {
                    RegisterVoiceBank(vb, isAi: false);
                }
            }

            Debug.Print($"[NativeVoiceBankHook] Initialized with {s_aiPointers.Count} AI voicebanks registered.");
        }
        catch (Exception ex)
        {
            Debug.Print("[NativeVoiceBankHook] Initialize failed: " + ex);
        }
    }

    /// <summary>
    /// 注册单个声库实例并确保虚表已挂钩
    /// </summary>
    public static void RegisterVoiceBank(VoiceBank? voiceBank, bool? isAi = null)
    {
        if (voiceBank == null) return;
        IntPtr cppPtr = GetNativePointer(voiceBank);
        if (cppPtr == IntPtr.Zero) return;

        bool ai = isAi ?? VoiceBankHelper.IsAiVoiceBank(voiceBank);
        if (ai)
        {
            s_aiPointers[cppPtr] = true;

            try
            {
                // 在原生 VoiceBank C++ 结构体中直接写入可用性标志：
                // +0x1c0: isAvailableInSequence (1 = 允许在工程序列中渲染)
                // +0x1c1: isAvailableForVoiceChanger (1 = 解锁 Vocalo Changer)
                Marshal.WriteByte(cppPtr + 0x1c0, 1);
                if (Settings.UnlockVocaloChanger)
                {
                    Marshal.WriteByte(cppPtr + 0x1c1, 1);
                }
            }
            catch (Exception ex)
            {
                Debug.Print("[NativeVoiceBankHook] Failed to write native availability flags: " + ex.Message);
            }
        }

        EnsureVTableHooked(cppPtr);
    }

    /// <summary>
    /// 当设置发生变更时同步刷新原生内存标志
    /// </summary>
    public static void SyncSettings()
    {
        try
        {
            byte vcVal = Settings.UnlockVocaloChanger ? (byte)1 : (byte)0;
            foreach (var kv in s_aiPointers)
            {
                if (kv.Key != IntPtr.Zero)
                {
                    Marshal.WriteByte(kv.Key + 0x1c0, 1);
                    Marshal.WriteByte(kv.Key + 0x1c1, vcVal);
                }
            }
        }
        catch
        {
            // Defensive
        }
    }

    private static void EnsureVTableHooked(IntPtr cppPtr)
    {
        if (s_isHooked) return;
        lock (s_lock)
        {
            if (s_isHooked) return;

            try
            {
                IntPtr vtable = Marshal.ReadIntPtr(cppPtr);
                if (vtable == IntPtr.Zero) return;

                // VDM5::VoiceBank 虚表偏移定义：
                // +0x50 (Index 10): langIDSize()
                // +0x58 (Index 11): langIDByIndex(index)
                // +0xD8 (Index 27): isAvailableForVoiceChanger()
                IntPtr pEntry10 = vtable + 0x50;
                IntPtr pEntry11 = vtable + 0x58;
                IntPtr pEntry27 = vtable + 0xD8;

                IntPtr orig10 = Marshal.ReadIntPtr(pEntry10);
                IntPtr orig11 = Marshal.ReadIntPtr(pEntry11);
                IntPtr orig27 = Marshal.ReadIntPtr(pEntry27);

                s_origLangIDSize = Marshal.GetDelegateForFunctionPointer<LangIDSizeDelegate>(orig10);
                s_origLangIDByIndex = Marshal.GetDelegateForFunctionPointer<LangIDByIndexDelegate>(orig11);
                s_origIsAvailableForVoiceChanger = Marshal.GetDelegateForFunctionPointer<IsAvailableDelegate>(orig27);

                IntPtr fn10 = Marshal.GetFunctionPointerForDelegate(S_HookLangIDSize);
                IntPtr fn11 = Marshal.GetFunctionPointerForDelegate(S_HookLangIDByIndex);
                IntPtr fn27 = Marshal.GetFunctionPointerForDelegate(S_HookIsAvailableForVoiceChanger);

                // 将虚表内存页修改为可写（从 +0x50 到 +0xE0 共 0x90 字节）
                if (VirtualProtect(pEntry10, (UIntPtr)0x90, PAGE_EXECUTE_READWRITE, out uint oldProtect))
                {
                    Marshal.WriteIntPtr(pEntry10, fn10);
                    Marshal.WriteIntPtr(pEntry11, fn11);
                    Marshal.WriteIntPtr(pEntry27, fn27);
                    VirtualProtect(pEntry10, (UIntPtr)0x90, oldProtect, out _);
                    s_isHooked = true;
                    Debug.Print("[NativeVoiceBankHook] Successfully hooked VDM5::VoiceBank vtable at 0x" + vtable.ToString("X"));
                }
                else
                {
                    Debug.Print("[NativeVoiceBankHook] VirtualProtect failed with error: " + Marshal.GetLastWin32Error());
                }
            }
            catch (Exception ex)
            {
                Debug.Print("[NativeVoiceBankHook] Exception while hooking vtable: " + ex);
            }
        }
    }

    private static ulong HookLangIDSize(IntPtr thisPtr)
    {
        try
        {
            if (Settings.UnlockAllLanguages && IsAiPointer(thisPtr))
            {
                return 5UL;
            }
            return s_origLangIDSize != null ? s_origLangIDSize(thisPtr) : 1UL;
        }
        catch
        {
            return 1UL;
        }
    }

    private static int HookLangIDByIndex(IntPtr thisPtr, ulong index)
    {
        try
        {
            if (Settings.UnlockAllLanguages && IsAiPointer(thisPtr))
            {
                return index < 5 ? (int)index : -1;
            }
            return s_origLangIDByIndex != null ? s_origLangIDByIndex(thisPtr, index) : -1;
        }
        catch
        {
            return -1;
        }
    }

    private static byte HookIsAvailableForVoiceChanger(IntPtr thisPtr)
    {
        try
        {
            if (Settings.UnlockVocaloChanger && IsAiPointer(thisPtr))
            {
                return 1;
            }
            return s_origIsAvailableForVoiceChanger != null ? s_origIsAvailableForVoiceChanger(thisPtr) : (byte)0;
        }
        catch
        {
            return 0;
        }
    }

    private static bool IsAiPointer(IntPtr thisPtr)
    {
        if (s_aiPointers.ContainsKey(thisPtr))
            return true;

        // 回退检查：检查原生结构体字段
        try
        {
            // 在 AI 声库中，+0x184 是 NPIndex
            int npIdx = Marshal.ReadInt32(thisPtr + 0x184);
            if (npIdx > 0)
            {
                s_aiPointers[thisPtr] = true;
                return true;
            }
        }
        catch
        {
            // Defensive
        }

        return false;
    }
}
