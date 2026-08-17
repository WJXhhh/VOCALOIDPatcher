using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;

namespace VOCALOIDPatcher.Utils;

/// <summary>
/// 对已加载的 DSE.dll 做内存 patch，让 license 结果恒为绿灯。
///
/// 不修改公开 getter：验证核心本身会读取临时对象的结果码，修改 getter 会改变内部控制流。
/// 定位从初始化 wrapper 的稳定调用关系出发，再用 PE 异常目录取得验证核心的真实函数边界；
/// 结果写入按同一栈槽上的完整结果码集合分组识别，日期检查按局部控制流关系识别。
/// 任一关系不唯一或结构不完整时均拒绝修改。
/// </summary>
internal static unsafe class DseLicensePatch
{
    private const int MinimumWrapperLength = 0x80;
    private const int MaximumWrapperLength = 0x400;
    private const int MinimumCoreLength = 0x1000;
    private const int MinimumResultStoreCount = 40;
    private const int MaximumResultStoreCount = 120;
    private const int MinimumStoresPerSlot = 16;
    private const int MinimumDistinctUnpatchedResults = 12;

    private static readonly HashSet<int> KnownResults =
        new() { 0x00, 0x01, 0x02, 0x03, 0x04, 0x05, 0x06, 0x07,
            0x09, 0x0A, 0x0B, 0x0C, 0x0D, 0x0E, 0x0F, 0x10 };

    // 编辑器绿灯的内部结果码集合（GetResult 输出 7^r mod 17 ∈ {15,9,14,8}）。
    private static readonly HashSet<int> GreenResults = new() { 0x02, 0x06, 0x0B, 0x0E };

    private const byte GreenValue = 0x0B;

    /// <summary>尝试 patch <paramref name="module"/>（DSE.dll 的模块基址）。</summary>
    public static bool TryPatch(nint module)
    {
        if (module == 0)
            return false;

        try
        {
            if (!TryGetImageLayout(module, out var layout) ||
                !TryResolveLicenseCore(module, layout, out var core))
                return false;

            if (!TryCollectPatches(core, out var patches))
                return false;
            if (patches.Count == 0)
                return true;

            var coreAddress = module + (int)core.BeginRva;
            var coreLength = core.EndRva - core.BeginRva;
            if (!VirtualProtect(coreAddress, coreLength, PageExecuteReadWrite, out var oldProtect))
                return false;

            try
            {
                foreach (var patch in patches)
                    *(byte*)patch.Address = patch.Value;

                FlushInstructionCache(GetCurrentProcess(), coreAddress, coreLength);
            }
            finally
            {
                VirtualProtect(coreAddress, coreLength, oldProtect, out _);
            }

            return true;
        }
        catch
        {
            // 内存 patch 失败不应影响补丁其余部分。
            return false;
        }
    }

    private static bool TryResolveLicenseCore(nint module, ImageLayout layout, out RuntimeFunction core)
    {
        core = default;
        var found = false;
        var count = layout.ExceptionSize / 12;

        for (uint i = 0; i < count; i++)
        {
            var entry = (byte*)module + layout.ExceptionRva + i * 12;
            var wrapper = new RuntimeFunction(*(uint*)entry, *(uint*)(entry + 4));
            var wrapperLength = wrapper.Length;
            if (wrapperLength is < MinimumWrapperLength or > MaximumWrapperLength ||
                !IsCodeRange(layout, wrapper))
                continue;

            var wrapperAddress = (byte*)module + wrapper.BeginRva;
            if (!ContainsPebGuard(wrapperAddress, wrapperLength) ||
                !TryFindCoreCall(wrapperAddress, wrapperLength, module, layout, out var candidate))
                continue;

            if (found && candidate != core)
                return false;

            core = candidate;
            found = true;
        }

        return found;
    }

    private static bool TryFindCoreCall(byte* wrapper, uint wrapperLength, nint module,
        ImageLayout layout, out RuntimeFunction core)
    {
        core = default;
        var found = false;

        for (var offset = 0; offset + 8 < wrapperLength; offset++)
        {
            if (wrapper[offset] != 0x84 || wrapper[offset + 1] != 0xC0 ||
                !TryReadConditionalTarget(wrapper, wrapperLength, offset + 2, out var successOffset))
                continue;

            var firstCallOffset = SkipPadding(wrapper, wrapperLength, successOffset);
            if (!TryReadDirectCallTarget(wrapper, wrapperLength, firstCallOffset, module, out _))
                continue;

            var searchEnd = Math.Min((int)wrapperLength, firstCallOffset + 40);
            for (var callOffset = firstCallOffset + 5; callOffset + 5 <= searchEnd; callOffset++)
            {
                if (!TryReadDirectCallTarget(wrapper, wrapperLength, callOffset, module,
                        out var targetRva) ||
                    !TryGetRuntimeFunction(module, layout, targetRva, out var candidate) ||
                    candidate.Length < MinimumCoreLength || !IsCodeRange(layout, candidate))
                    continue;

                var candidateAddress = (byte*)module + candidate.BeginRva;
                if (!ContainsPebGuard(candidateAddress, candidate.Length))
                    continue;

                if (found && candidate != core)
                    return false;

                core = candidate;
                found = true;
            }
        }

        return found;
    }

    private static bool TryReadConditionalTarget(byte* function, uint length, int offset,
        out int targetOffset)
    {
        targetOffset = 0;
        if (offset + 2 <= length && function[offset] == 0x75)
        {
            targetOffset = offset + 2 + (sbyte)function[offset + 1];
            return targetOffset >= 0 && targetOffset < length;
        }

        if (offset + 6 <= length && function[offset] == 0x0F && function[offset + 1] == 0x85)
        {
            targetOffset = offset + 6 + *(int*)(function + offset + 2);
            return targetOffset >= 0 && targetOffset < length;
        }

        return false;
    }

    private static int SkipPadding(byte* function, uint length, int offset)
    {
        while (offset < length && function[offset] is 0x90 or 0xCC)
            offset++;
        return offset;
    }

    private static bool TryReadDirectCallTarget(byte* function, uint length, int offset,
        nint module, out uint targetRva)
    {
        targetRva = 0;
        if (offset < 0 || offset + 5 > length || function[offset] != 0xE8)
            return false;

        var target = (nint)(function + offset + 5 + *(int*)(function + offset + 1));
        var relative = target - module;
        if (relative < 0 || relative > uint.MaxValue)
            return false;

        targetRva = (uint)relative;
        return true;
    }

    private static bool ContainsPebGuard(byte* function, uint length)
    {
        ReadOnlySpan<byte> peb = stackalloc byte[]
            { 0x65, 0x48, 0x8B, 0x04, 0x25, 0x60, 0x00, 0x00, 0x00 };
        ReadOnlySpan<byte> guard = stackalloc byte[]
            { 0xF6, 0x80, 0xBC, 0x00, 0x00, 0x00, 0x70 };

        for (var offset = 0; offset + peb.Length <= length; offset++)
        {
            if (!new ReadOnlySpan<byte>(function + offset, peb.Length).SequenceEqual(peb))
                continue;

            var guardEnd = Math.Min((int)length, offset + peb.Length + 16);
            for (var guardOffset = offset + peb.Length;
                 guardOffset + guard.Length <= guardEnd;
                 guardOffset++)
            {
                if (new ReadOnlySpan<byte>(function + guardOffset, guard.Length).SequenceEqual(guard))
                    return true;
            }
        }

        return false;
    }

    private static bool TryCollectPatches(RuntimeFunction core, out List<BytePatch> patches)
    {
        patches = new List<BytePatch>();
        var coreAddress = (byte*)core.Module + core.BeginRva;
        var groups = new Dictionary<StackSlot, List<ImmediateStore>>();

        for (var offset = 0; offset < core.Length; offset++)
        {
            if (!TryDecodeImmediateStore(coreAddress, core.Length, offset, out var store))
                continue;

            if (!groups.TryGetValue(store.Slot, out var stores))
            {
                stores = new List<ImmediateStore>();
                groups.Add(store.Slot, stores);
            }
            stores.Add(store);
            offset += store.Length - 1;
        }

        var resultGroups = groups.Values.Where(IsResultGroup).ToList();
        var resultStoreCount = resultGroups.Sum(group => group.Count);
        if (resultStoreCount is < MinimumResultStoreCount or > MaximumResultStoreCount ||
            resultGroups.Count is < 1 or > 4)
            return false;

        var resultStores = resultGroups.SelectMany(group => group).ToList();
        if (!TryFindDateBranch(coreAddress, core.Length, resultStores, out var branchPatches))
            return false;

        foreach (var store in resultStores)
        {
            if (!GreenResults.Contains(store.Value))
                patches.Add(new BytePatch((nint)(coreAddress + store.ImmediateOffset), GreenValue));
        }
        patches.AddRange(branchPatches);
        return true;
    }

    private static bool IsResultGroup(List<ImmediateStore> stores)
    {
        if (stores.Count < MinimumStoresPerSlot || stores.Any(store => !KnownResults.Contains(store.Value)))
            return false;

        var distinct = stores.Select(store => store.Value).Distinct().ToArray();
        return distinct.Length >= MinimumDistinctUnpatchedResults ||
               distinct.All(GreenResults.Contains);
    }

    private static bool TryFindDateBranch(byte* core, uint coreLength,
        List<ImmediateStore> resultStores, out List<BytePatch> patches)
    {
        patches = new List<BytePatch>();
        var candidates = 0;

        foreach (var store in resultStores)
        {
            if (!GreenResults.Contains(store.Value))
                continue;

            if (store.Offset >= 2 && core[store.Offset - 2] == 0x74)
            {
                var displacement = (sbyte)core[store.Offset - 1];
                if (displacement == 0 ||
                    IsZeroStoreAt(core, coreLength, store.Offset + displacement, store.Slot))
                {
                    candidates++;
                    if (displacement != 0)
                        patches.Add(new BytePatch((nint)(core + store.Offset - 1), 0));
                }
            }
            else if (store.Offset >= 6 && core[store.Offset - 6] == 0x0F &&
                     core[store.Offset - 5] == 0x84)
            {
                var displacement = *(int*)(core + store.Offset - 4);
                if (displacement == 0 ||
                    IsZeroStoreAt(core, coreLength, store.Offset + displacement, store.Slot))
                {
                    candidates++;
                    for (var i = 0; displacement != 0 && i < 4; i++)
                        patches.Add(new BytePatch((nint)(core + store.Offset - 4 + i), 0));
                }
            }
        }

        if (candidates == 1)
            return true;

        patches.Clear();
        return false;
    }

    private static bool IsZeroStoreAt(byte* core, uint coreLength, int offset, StackSlot slot)
    {
        if (offset < 0 || offset + 4 >= coreLength)
            return false;

        var rex = 0;
        if (core[offset] is >= 0x40 and <= 0x4F)
            rex = core[offset++];

        var opcode = core[offset++];
        if (opcode is not (0x31 or 0x33))
            return false;

        var modRm = core[offset++];
        if ((modRm & 0xC0) != 0xC0)
            return false;

        var reg = ((modRm >> 3) & 7) + ((rex & 4) != 0 ? 8 : 0);
        var rm = (modRm & 7) + ((rex & 1) != 0 ? 8 : 0);
        if (reg != rm)
            return false;

        return TryDecodeRegisterStore(core, coreLength, offset, reg, out var zeroSlot, out _) &&
               zeroSlot == slot;
    }

    private static bool TryDecodeImmediateStore(byte* code, uint length, int offset,
        out ImmediateStore store)
    {
        store = default;
        if (!TryDecodeStackOperand(code, length, offset, 0xC7, out var slot,
                out var register, out var operandEnd) || register != 0 || operandEnd + 4 > length)
            return false;

        var value = *(int*)(code + operandEnd);
        store = new ImmediateStore(slot, offset, operandEnd, operandEnd + 4 - offset, value);
        return true;
    }

    private static bool TryDecodeRegisterStore(byte* code, uint length, int offset,
        int expectedRegister, out StackSlot slot, out int instructionLength)
    {
        slot = default;
        instructionLength = 0;
        if (!TryDecodeStackOperand(code, length, offset, 0x89, out slot,
                out var register, out var operandEnd) || register != expectedRegister)
            return false;

        instructionLength = operandEnd - offset;
        return true;
    }

    private static bool TryDecodeStackOperand(byte* code, uint length, int offset, byte opcode,
        out StackSlot slot, out int register, out int operandEnd)
    {
        slot = default;
        register = 0;
        operandEnd = 0;
        if (offset < 0 || offset + 3 > length)
            return false;

        // 这里只接受编译器用于 RSP/RBP 局部变量的无 REX dword 形式。
        // 在逐字节扫描中把前一条指令的尾字节当成可选 REX 会制造伪指令。
        var cursor = offset;
        if (cursor + 2 > length || code[cursor++] != opcode)
            return false;

        var modRm = code[cursor++];
        var mode = modRm >> 6;
        if (mode is not (1 or 2))
            return false;

        register = (modRm >> 3) & 7;
        var rm = modRm & 7;
        int baseRegister;
        if (rm == 4)
        {
            if (cursor >= length)
                return false;
            var sib = code[cursor++];
            if (((sib >> 3) & 7) != 4)
                return false;
            baseRegister = sib & 7;
        }
        else
        {
            baseRegister = rm;
        }

        if (baseRegister is not (4 or 5))
            return false;

        int displacement;
        if (mode == 1)
        {
            if (cursor + 1 > length)
                return false;
            displacement = (sbyte)code[cursor++];
        }
        else
        {
            if (cursor + 4 > length)
                return false;
            displacement = *(int*)(code + cursor);
            cursor += 4;
        }

        slot = new StackSlot(baseRegister, displacement);
        operandEnd = cursor;
        return true;
    }

    private static bool TryGetImageLayout(nint module, out ImageLayout layout)
    {
        layout = default;
        var image = (byte*)module;
        if (*(ushort*)image != 0x5A4D)
            return false;

        var peOffset = *(int*)(image + 0x3C);
        if (peOffset <= 0)
            return false;

        var ntHeaders = image + peOffset;
        if (*(uint*)ntHeaders != 0x00004550)
            return false;

        var optionalHeader = ntHeaders + 24;
        if (*(ushort*)optionalHeader != 0x20B)
            return false;

        var sizeOfImage = *(uint*)(optionalHeader + 56);
        var directoryCount = *(uint*)(optionalHeader + 108);
        if (sizeOfImage == 0 || directoryCount <= 3)
            return false;

        var exceptionRva = *(uint*)(optionalHeader + 112 + 3 * 8);
        var exceptionSize = *(uint*)(optionalHeader + 112 + 3 * 8 + 4);
        if (exceptionSize < 12 || exceptionSize % 12 != 0 ||
            !IsImageRange(sizeOfImage, exceptionRva, exceptionSize))
            return false;

        var sectionCount = *(ushort*)(ntHeaders + 6);
        var sectionTable = optionalHeader + *(ushort*)(ntHeaders + 20);
        for (var i = 0; i < sectionCount; i++)
        {
            var section = sectionTable + i * 40;
            if (section[0] != (byte)'.' || section[1] != (byte)'t' ||
                section[2] != (byte)'e' || section[3] != (byte)'x' || section[4] != (byte)'t')
                continue;

            var textRva = *(uint*)(section + 12);
            var textSize = *(uint*)(section + 8);
            if (textSize == 0 || !IsImageRange(sizeOfImage, textRva, textSize))
                return false;

            layout = new ImageLayout(textRva, textSize, exceptionRva, exceptionSize, sizeOfImage);
            return true;
        }

        return false;
    }

    private static bool TryGetRuntimeFunction(nint module, ImageLayout layout, uint targetRva,
        out RuntimeFunction function)
    {
        function = default;
        var count = layout.ExceptionSize / 12;
        for (uint i = 0; i < count; i++)
        {
            var entry = (byte*)module + layout.ExceptionRva + i * 12;
            var begin = *(uint*)entry;
            var end = *(uint*)(entry + 4);
            if (targetRva < begin || targetRva >= end)
                continue;

            function = new RuntimeFunction(begin, end, module);
            return begin < end && IsImageRange(layout.SizeOfImage, begin, end - begin);
        }

        return false;
    }

    private static bool IsCodeRange(ImageLayout layout, RuntimeFunction function)
    {
        var textEnd = (ulong)layout.TextRva + layout.TextSize;
        return function.BeginRva >= layout.TextRva && function.BeginRva < function.EndRva &&
               function.EndRva <= textEnd;
    }

    private static bool IsImageRange(uint sizeOfImage, uint rva, uint size) =>
        rva < sizeOfImage && size <= sizeOfImage - rva;

    private readonly record struct ImageLayout(uint TextRva, uint TextSize, uint ExceptionRva,
        uint ExceptionSize, uint SizeOfImage);

    private readonly record struct RuntimeFunction(uint BeginRva, uint EndRva, nint Module = 0)
    {
        public uint Length => EndRva - BeginRva;
    }

    private readonly record struct StackSlot(int BaseRegister, int Displacement);
    private readonly record struct ImmediateStore(StackSlot Slot, int Offset, int ImmediateOffset,
        int Length, int Value);
    private readonly record struct BytePatch(nint Address, byte Value);

    private const uint PageExecuteReadWrite = 0x40;

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool VirtualProtect(nint address, nuint size, uint newProtect,
        out uint oldProtect);

    [DllImport("kernel32.dll")]
    private static extern nint GetCurrentProcess();

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool FlushInstructionCache(nint process, nint baseAddress, nuint size);
}
