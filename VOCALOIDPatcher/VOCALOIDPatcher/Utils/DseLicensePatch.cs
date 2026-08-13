using System;
using System.Runtime.InteropServices;

namespace VOCALOIDPatcher.Utils;

/// <summary>
/// 对已加载的 DSE.dll 做内存 patch，让 license 结果恒为绿灯。
///
/// 目标方法：<c>DSE5::LicenseImpl::GetResult</c>（编辑器与声库授权共用它在 vtable+0x18 的出口）。
/// 该方法开头是
/// <c>SUB RSP,0x28; MOV R10D,[RCX+0x50]; TEST R10D,R10D; JNZ +7; XOR EAX,EAX; ADD RSP,0x28; RET</c>，
/// 之后才是把结果码 <c>7^r mod 17</c> 混淆的离散对数逻辑。
/// 只要把开头 6 字节改成 <c>MOV EAX,15; RET</c>，GetResult 就恒返回
/// <c>LicenseResult.NoError</c>（绿灯集合 {8,9,14,15} 之一），
/// 编辑器授权与 V6/旧版声库授权一次性全部放行。
///
/// 定位不依赖固定 RVA：用 signature 扫描 .text 段，命中后再做一次尾随字节校验，
/// 以抵抗版本更新导致的代码位移。signature 里唯一的易变字节（JNZ 跳转距离）已用 mask 通配。
/// </summary>
internal static unsafe class DseLicensePatch
{
    // 需要被替换的 GetResult 前缀：MOV EAX,15; RET（恒返回 NoError）。
    private static readonly byte[] PatchBytes = { 0xB8, 0x0F, 0x00, 0x00, 0x00, 0xC3 };

    private static readonly Pattern[] Patterns =
    {
        new(
            // SUB RSP,0x28; MOV R10D,[RCX+0x50]; TEST R10D,R10D; JNZ +7; XOR EAX,EAX; ADD RSP,0x28; RET
            new byte[]
            {
                0x48, 0x83, 0xEC, 0x28,
                0x44, 0x8B, 0x51, 0x50, // 读 +0x50 结果码字段——语义核心，版本稳定
                0x45, 0x85, 0xD2,
                0x75, 0x00,             // JNZ，跳转距离通配
                0x33, 0xC0,
                0x48, 0x83, 0xC4, 0x28,
                0xC3,
            },
            new byte[]
            {
                0xFF, 0xFF, 0xFF, 0xFF,
                0xFF, 0xFF, 0xFF, 0xFF,
                0xFF, 0xFF, 0xFF,
                0xFF, 0x00,
                0xFF, 0xFF,
                0xFF, 0xFF, 0xFF, 0xFF,
                0xFF,
            },
            // 尾随校验：signature 之后紧跟 MOV ECX,2（混淆逻辑开端）
            new byte[] { 0xB9, 0x02, 0x00, 0x00, 0x00 }),
    };

    /// <summary>尝试 patch <paramref name="module"/>（DSE.dll 的模块基址）。成功返回 true。</summary>
    public static bool TryPatch(nint module)
    {
        if (module == 0)
            return false;

        try
        {
            if (!TryGetTextSection(module, out var text, out var textSize))
                return false;

            foreach (var pattern in Patterns)
            {
                var match = FindPattern(text, textSize, pattern);
                if (match == 0)
                    continue;

                if (!MatchesTail(match, pattern))
                    continue;

                return WriteBytes(match, PatchBytes);
            }

            return false;
        }
        catch
        {
            // 内存 patch 失败不应影响补丁其余部分。
            return false;
        }
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

    private static nint FindPattern(nint text, uint textSize, Pattern pattern)
    {
        var signature = pattern.Signature;
        var mask = pattern.Mask;
        var end = (byte*)text + textSize - signature.Length;

        for (var p = (byte*)text; p <= end; p++)
        {
            var i = 0;
            for (; i < signature.Length; i++)
            {
                if (mask[i] == 0)
                    continue;
                if (p[i] != signature[i])
                    break;
            }

            if (i == signature.Length)
                return (nint)p;
        }

        return 0;
    }

    private static bool MatchesTail(nint match, Pattern pattern)
    {
        var tail = pattern.Tail;
        for (var i = 0; i < tail.Length; i++)
        {
            if (*(byte*)(match + pattern.Signature.Length + i) != tail[i])
                return false;
        }

        return true;
    }

    private static bool WriteBytes(nint address, byte[] bytes)
    {
        if (!VirtualProtect(address, (nuint)bytes.Length, PageExecuteReadWrite, out var oldProtect))
            return false;

        try
        {
            Marshal.Copy(bytes, 0, address, bytes.Length);
            return true;
        }
        finally
        {
            VirtualProtect(address, (nuint)bytes.Length, oldProtect, out _);
        }
    }

    private sealed class Pattern
    {
        public Pattern(byte[] signature, byte[] mask, byte[] tail)
        {
            Signature = signature;
            Mask = mask;
            Tail = tail;
        }

        public byte[] Signature { get; }
        public byte[] Mask { get; }
        public byte[] Tail { get; }
    }

    private const uint PageExecuteReadWrite = 0x40;

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool VirtualProtect(nint address, nuint size, uint newProtect, out uint oldProtect);
}
