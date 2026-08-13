using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace VOCALOIDPatcher.Utils;

/// <summary>
/// 对已加载的 DSE.dll 做内存 patch，让 license 结果恒为绿灯。
///
/// 思路与"文件级 patch"完全等价：不改任何 getter（改 GetResult 反而会破坏验证核心
/// 内部的"临时对象结果码 == 0"检查，导致验证永远进错误分支），而是直接修改
/// 验证核心 <c>FUN_1801dac80</c>（VOCALOID 6.13.0.1）里的结果码赋值：
///
/// 1. 验证核心内共有 60 处结果码赋值（三组同构阶段，对应 V6 声库 / 旧版本声库 / Splice）：
///    - <c>C7 44 24 38 XX 00 00 00</c>（MOV [RSP+0x38], imm32，立即数在 +4）→ 普通结果码 +0x50；
///    - <c>C7 45 C8 XX 00 00 00</c>（MOV [RBP-0x38], imm32，立即数在 +3）→ Splice 结果码 +0x54。
///    绿灯内部结果码集合为 {0x02, 0x06, 0x0B, 0x0E}（对应 GetResult 输出 15/9/14/8，
///    即 NoError / PaidOffLeaseFile / ValidExpiryKey / ValidLeaseFile），保持不动；
///    其余 45 处红灯一律改成 0x0B（ValidExpiryKey）。
/// 2. 动态赋值点 <c>core+0x2F0B</c>（0x1DDB8B）：<c>JZ 74 18 → 74 00</c>，短路"日期无效跳过绿灯"分支。
///
/// 验证核心用 32 字节 prologue signature 定位（唯一命中 6.13.0.1；6.10 修改版内核不命中，
/// 安全降级不修改任何字节）。patch 后验证核心写入端恒为绿灯，编辑器无论从哪个 getter 读都是绿灯。
/// </summary>
internal static unsafe class DseLicensePatch
{
    // FUN_1801dac80（验证核心）prologue signature：
    //   MOV RAX,RSP; MOV [RAX+0x10],RBX; PUSH RBP..R15; LEA RBP,[RAX-0x598]; SUB RSP,0x660
    private static readonly byte[] CoreSignature =
    {
        0x48, 0x8B, 0xC4, 0x48, 0x89, 0x58, 0x10, 0x55, 0x56, 0x57, 0x41, 0x54, 0x41, 0x55, 0x41, 0x56,
        0x41, 0x57, 0x48, 0x8D, 0xA8, 0x68, 0xFA, 0xFF, 0xFF, 0x48, 0x81, 0xEC, 0x60, 0x06, 0x00, 0x00,
    };

    private const int CoreLength = 0x3AFC;  // 验证核心函数体长度
    private const int JzOffset = 0x2F0B;    // 0x1DDB8B - 0x1DAC80：JZ 74 18 -> 74 00

    // 编辑器绿灯的内部结果码集合（GetResult 输出 7^r mod 17 ∈ {15,9,14,8}）
    private static readonly HashSet<byte> GreenResults = new() { 0x02, 0x06, 0x0B, 0x0E };

    private const byte GreenValue = 0x0B;   // 红灯统一改成 ValidExpiryKey

    /// <summary>尝试 patch <paramref name="module"/>（DSE.dll 的模块基址）。成功或已放行返回 true。</summary>
    public static bool TryPatch(nint module)
    {
        if (module == 0)
            return false;

        try
        {
            if (!TryGetTextSection(module, out var text, out var textSize))
                return false;

            var core = FindBytes(text, textSize, CoreSignature);
            if (core == 0)
                return false; // 版本不匹配（如 6.10 修改版内核），安全降级

            var patches = CollectPatches(core);
            if (patches.Count == 0)
                return true; // 已全部是绿灯（例如文件级 patch 已先行生效），无需再写

            if (!VirtualProtect(core, CoreLength, PageExecuteReadWrite, out var oldProtect))
                return false;

            try
            {
                foreach (var (address, value) in patches)
                    *(byte*)address = value;
            }
            finally
            {
                VirtualProtect(core, CoreLength, oldProtect, out _);
            }

            return true;
        }
        catch
        {
            // 内存 patch 失败不应影响补丁其余部分。
            return false;
        }
    }

    /// <summary>在验证核心内收集需要改写的字节（红灯立即数 + JZ 动态赋值）。</summary>
    private static List<(nint Address, byte Value)> CollectPatches(nint core)
    {
        var patches = new List<(nint, byte)>();

        for (var p = core; p < core + CoreLength - 7; p++)
        {
            var b = (byte*)p;

            // C7 44 24 38 XX 00 00 00：MOV dword [RSP+0x38], imm32（立即数在 +4）
            if (b[0] == 0xC7 && b[1] == 0x44 && b[2] == 0x24 && b[3] == 0x38 &&
                b[5] == 0x00 && b[6] == 0x00 && b[7] == 0x00)
            {
                var value = b[4];
                if (!GreenResults.Contains(value))
                    patches.Add((p + 4, GreenValue));
                p += 7;
            }
            // C7 45 C8 XX 00 00 00：MOV dword [RBP-0x38], imm32（3 字节前缀，立即数在 +3）
            else if (b[0] == 0xC7 && b[1] == 0x45 && b[2] == 0xC8 &&
                     b[4] == 0x00 && b[5] == 0x00 && b[6] == 0x00)
            {
                var value = b[3];
                if (!GreenResults.Contains(value))
                    patches.Add((p + 3, GreenValue));
                p += 7;
            }
        }

        // JZ 74 18 -> 74 00 @ core+0x2F0B（短路"日期无效跳过绿灯"的分支）
        var jz = (byte*)(core + JzOffset);
        if (jz[0] == 0x74 && jz[1] == 0x18)
            patches.Add((core + JzOffset + 1, 0x00));

        return patches;
    }

    private static bool TryGetTextSection(nint module, out nint text, out uint textSize)
    {
        text = 0;
        textSize = 0;

        var image = (byte*)module;
        if (*(ushort*)image != 0x5A4D) // MZ
            return false;

        var peOffset = *(int*)(image + 0x3C);
        if (peOffset <= 0)
            return false;

        var ntHeaders = image + peOffset;
        if (*(uint*)ntHeaders != 0x00004550) // PE\0\0
            return false;

        var optionalHeader = ntHeaders + 24;
        if (*(ushort*)optionalHeader != 0x20B) // PE32+
            return false;

        var sectionCount = *(ushort*)(ntHeaders + 6);
        var sectionTable = optionalHeader + *(ushort*)(ntHeaders + 20); // SizeOfOptionalHeader

        for (var i = 0; i < sectionCount; i++)
        {
            var section = sectionTable + i * 40;
            if (section[0] != (byte)'.' || section[1] != (byte)'t' ||
                section[2] != (byte)'e' || section[3] != (byte)'x' || section[4] != (byte)'t')
                continue;

            var virtualAddress = *(uint*)(section + 12);
            var virtualSize = *(uint*)(section + 8);
            if (virtualSize == 0)
                continue;

            text = (nint)(image + virtualAddress);
            textSize = virtualSize;
            return true;
        }

        return false;
    }

    private static nint FindBytes(nint baseAddress, uint length, byte[] signature)
    {
        var end = (byte*)baseAddress + length - signature.Length;
        for (var p = (byte*)baseAddress; p <= end; p++)
        {
            var i = 0;
            for (; i < signature.Length; i++)
            {
                if (p[i] != signature[i])
                    break;
            }

            if (i == signature.Length)
                return (nint)p;
        }

        return 0;
    }

    private const uint PageExecuteReadWrite = 0x40;

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool VirtualProtect(nint address, nuint size, uint newProtect, out uint oldProtect);
}
