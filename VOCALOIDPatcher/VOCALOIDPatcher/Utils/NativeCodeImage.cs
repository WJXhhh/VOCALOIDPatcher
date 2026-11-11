using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection.PortableExecutable;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text;

namespace VOCALOIDPatcher.Utils;

// File reads and signature discovery are read-only. Only callers that explicitly
// supply a loaded module also validate the selected bytes in process memory.
internal sealed class NativeCodeImage
{
    private const int MaximumImageSize = 128 * 1024 * 1024;
    private readonly byte[] _image;
    private readonly SectionHeader[] _sections;
    private readonly (int Begin, int End)[] _functions;
    private readonly nint _module;

    internal NativeCodeImage(string path, nint module = 0) : this(ReadFile(path, module), module)
    {
        if (module != 0 && (!GetModuleInformation(GetCurrentProcess(), module, out var info, Marshal.SizeOf<ModuleInfo>()) ||
            info.Base != module || info.Size != _image.Length))
            throw new NotSupportedException("Native file layout differs from the loaded module size.");
    }

    private static byte[] ReadFile(string path, nint module)
    {
        if (module != 0)
        {
            var loadedPath = new StringBuilder(32768);
            uint length = GetModuleFileName(module, loadedPath, loadedPath.Capacity);
            if (length == 0 || length >= loadedPath.Capacity ||
                !string.Equals(Path.GetFullPath(path), Path.GetFullPath(loadedPath.ToString()), StringComparison.OrdinalIgnoreCase))
                throw new NotSupportedException("Native signature source does not name the loaded module.");
        }
        if (new FileInfo(path).Length > MaximumImageSize)
            throw new NotSupportedException("Native image exceeds the signature reader limit.");
        return File.ReadAllBytes(path);
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, EntryPoint = "GetModuleFileNameW")]
    private static extern uint GetModuleFileName(nint module, StringBuilder path, int capacity);

    [StructLayout(LayoutKind.Sequential)]
    private struct ModuleInfo { internal nint Base; internal uint Size; internal nint EntryPoint; }
    [DllImport("kernel32.dll", EntryPoint = "K32GetModuleInformation")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetModuleInformation(nint process, nint module, out ModuleInfo information, int size);
    [DllImport("kernel32.dll")]
    private static extern nint GetCurrentProcess();

    internal NativeCodeImage(byte[] file, nint module = 0)
    {
        using var stream = new MemoryStream(file, false);
        using var pe = new PEReader(stream);
        var headers = pe.PEHeaders;
        var optional = headers.PEHeader;
        if (headers.CoffHeader.Machine != Machine.Amd64 || optional?.Magic != PEMagic.PE32Plus ||
            optional.SizeOfImage <= 0 || optional.SizeOfImage > MaximumImageSize)
            throw new NotSupportedException("Native signatures require a bounded x64 PE image.");
        _image = new byte[optional.SizeOfImage];
        _sections = headers.SectionHeaders.ToArray();
        foreach (var section in _sections)
        {
            RequireRange(section.VirtualAddress, Math.Max(section.VirtualSize, section.SizeOfRawData));
            if (section.SizeOfRawData < 0 || section.PointerToRawData < 0 || section.PointerToRawData > file.Length - section.SizeOfRawData)
                throw new InvalidDataException("Invalid native section data.");
            file.AsSpan(section.PointerToRawData, section.SizeOfRawData).CopyTo(_image.AsSpan(section.VirtualAddress));
        }
        var directory = optional.ExceptionTableDirectory;
        RequireRange(directory.RelativeVirtualAddress, directory.Size);
        if (directory.Size <= 0 || directory.Size % 12 != 0)
            throw new InvalidDataException("Missing x64 native function boundaries.");
        _functions = new (int, int)[directory.Size / 12];
        for (int i = 0; i < _functions.Length; i++)
        {
            int row = directory.RelativeVirtualAddress + i * 12;
            int begin = ReadInt32(row), end = ReadInt32(row + 4);
            if (begin >= end || !IsCode(begin, end - begin) || (i > 0 && begin <= _functions[i - 1].Begin))
                throw new InvalidDataException("Invalid x64 native function boundaries.");
            _functions[i] = (begin, end);
        }
        _module = module;
    }

    internal int ReadInt32(int rva)
    {
        RequireRange(rva, 4);
        return BinaryPrimitives.ReadInt32LittleEndian(_image.AsSpan(rva, 4));
    }

    internal byte[] Read(int rva, int count)
    {
        RequireRange(rva, count);
        return _image.AsSpan(rva, count).ToArray();
    }

    internal (int Begin, int End)? Function(int rva)
    {
        int lo = 0, hi = _functions.Length - 1;
        while (lo <= hi)
        {
            int middle = lo + (hi - lo) / 2;
            var function = _functions[middle];
            if (rva < function.Begin) hi = middle - 1;
            else if (rva >= function.End) lo = middle + 1;
            else return function;
        }
        return null;
    }

    internal bool IsCode(int rva, int size = 1) => InSection(rva, size, SectionCharacteristics.MemExecute);

    private bool InSection(int rva, int size, SectionCharacteristics required) =>
        size > 0 && _sections.Any(s => (s.SectionCharacteristics & required) == required &&
            rva >= s.VirtualAddress && (long)rva + size <= (long)s.VirtualAddress + s.VirtualSize);

    private void RequireRange(int rva, int size)
    {
        if (rva < 0 || size < 0 || rva > _image.Length - size)
            throw new InvalidDataException("Native image range is out of bounds.");
    }

    internal Dictionary<string, int> Resolve(string module, IReadOnlyList<NativeCodeSignature>? signatures = null)
    {
        signatures ??= NativeCodeSignature.Catalog;
        var result = new Dictionary<string, int>(StringComparer.Ordinal);
        var candidates = new Dictionary<string, List<Match>>(StringComparer.Ordinal);
        foreach (var group in signatures.Where(s => s.Module == module).GroupBy(s => s.Name))
        {
            var matches = new List<Match>();
            foreach (var signature in group)
            {
                var pattern = new NativeCodePattern(signature.Pattern);
                foreach (var section in _sections.Where(s => (s.SectionCharacteristics & SectionCharacteristics.MemExecute) != 0))
                {
                    foreach (int anchor in pattern.Find(_image, section.VirtualAddress, section.VirtualSize))
                    {
                        var function = Function(anchor);
                        if (signature.FunctionEntry && function?.Begin != anchor) continue;
                        if (signature.Guards.Length > 0 && function == null) continue;
                        var guards = signature.Guards.Select(g => new NativeCodePattern(g)).ToArray();
                        var guardLocations = new List<(NativeCodePattern Pattern, int Rva)>();
                        bool valid = true;
                        foreach (var guard in guards)
                        {
                            int location = guard.Find(_image, function!.Value.Begin, function.Value.End - function.Value.Begin).FirstOrDefault(-1);
                            if (location < 0) { valid = false; break; }
                            guardLocations.Add((guard, location));
                        }
                        if (!valid) continue;
                        int target = signature.RelativeOffset >= 0
                            ? checked(anchor + signature.RelativeEnd + ReadInt32(checked(anchor + signature.RelativeOffset)))
                            : checked(anchor + signature.ResultOffset);
                        bool data = signature.Name is "DseEngineVtable" or "DseStates" or "DseStartMutex" or "DseDurationRandom";
                        bool writable = InSection(target, 8, SectionCharacteristics.MemWrite);
                        if (data ? !InSection(target, 8, SectionCharacteristics.MemRead) || IsCode(target) ||
                            (signature.Name == "DseEngineVtable" ? writable : !writable) : !IsCode(target))
                            throw new NotSupportedException("Native signature target has an incompatible section: " + group.Key);
                        if (matches.Count >= 128) throw new NotSupportedException("Too many native signature candidates: " + group.Key);
                        matches.Add(new(signature, anchor, target, pattern, guardLocations));
                    }
                }
            }
            if (matches.Count == 0)
                throw new NotSupportedException("Native code signature or layout contract was not found: " + group.Key);
            candidates.Add(group.Key, matches);
        }
        // Identical compiler-generated helpers can have identical complete code.
        // Restrict them using callers independently identified by their own code.
        bool changed;
        do
        {
            changed = false;
            foreach (var group in candidates)
            {
                if (group.Value.Select(m => m.Target).Distinct().Count() != 1) continue;
                foreach (var match in group.Value.ToArray())
                    foreach (var link in match.Signature.Links)
                    {
                        int expected = checked(match.Anchor + link.End + ReadInt32(checked(match.Anchor + link.Offset)));
                        if (!candidates.TryGetValue(link.Name, out var targets))
                            throw new InvalidDataException("Unknown native signature reference.");
                        changed |= targets.RemoveAll(m => m.Target != expected) > 0;
                        if (targets.Count == 0) throw new NotSupportedException("Native signature reference does not match: " + group.Key);
                    }
            }
        } while (changed);
        foreach (var group in candidates)
        {
            if (group.Value.Select(m => m.Target).Distinct().Count() != 1)
                throw new NotSupportedException("Native code signature is ambiguous: " + group.Key);
            result.Add(group.Key, group.Value[0].Target);
        }
        if (result.Count == 0) throw new InvalidDataException("Missing native code signatures.");
        if (result.Values.Distinct().Count() != result.Count)
            throw new NotSupportedException("Native signatures resolved overlapping targets.");
        foreach (var match in candidates.Values.SelectMany(m => m))
        {
            var signature = match.Signature;
            int anchor = match.Anchor;
            ValidateLive(anchor, match.Pattern);
            if (signature.RelativeOffset >= 0) ValidateLiveRelative(anchor + signature.RelativeOffset);
            foreach (var guard in match.Guards) ValidateLive(guard.Rva, guard.Pattern);
            foreach (var link in signature.Links)
            {
                ValidateLiveRelative(anchor + link.Offset);
                if (!result.TryGetValue(link.Name, out int expected) ||
                    checked(anchor + link.End + ReadInt32(checked(anchor + link.Offset))) != expected)
                    throw new NotSupportedException("Native signature reference does not match: " + signature.Name);
            }
        }
        return result;
    }

    private sealed record Match(NativeCodeSignature Signature, int Anchor, int Target, NativeCodePattern Pattern,
        List<(NativeCodePattern Pattern, int Rva)> Guards);

    private void ValidateLive(int rva, NativeCodePattern pattern)
    {
        if (_module == 0) return;
        var live = new byte[pattern.Length];
        Marshal.Copy(_module + rva, live, 0, live.Length);
        if (!pattern.Matches(live, 0))
            throw new NotSupportedException("Loaded native code differs from the discovered signature.");
    }

    private void ValidateLiveRelative(int rva)
    {
        if (_module != 0 && Marshal.ReadInt32(_module + rva) != ReadInt32(rva))
            throw new NotSupportedException("Loaded native reference differs from the discovered layout.");
    }
}

internal sealed class NativeCodeSignature
{
    private static readonly Lazy<NativeCodeSignature[]> Embedded = new(() =>
    {
        using var stream = typeof(NativeCodeSignature).Assembly.GetManifestResourceStream("VOCALOIDPatcher.NativeSignatures")
            ?? throw new InvalidDataException("Missing native signatures.");
        return JsonSerializer.Deserialize<NativeCodeSignature[]>(stream) ?? throw new InvalidDataException("Invalid native signatures.");
    });
    internal static IReadOnlyList<NativeCodeSignature> Catalog => Embedded.Value;
    public string Name { get; set; } = "";
    public string Module { get; set; } = "";
    public string Pattern { get; set; } = "";
    public int ResultOffset { get; set; }
    public int RelativeOffset { get; set; } = -1;
    public int RelativeEnd { get; set; } = -1;
    public bool FunctionEntry { get; set; }
    public string[] Guards { get; set; } = [];
    public NativeCodeLink[] Links { get; set; } = [];
}

internal sealed class NativeCodeLink
{
    public string Name { get; set; } = "";
    public int Offset { get; set; }
    public int End { get; set; }
}

internal sealed class NativeCodePattern
{
    private readonly byte?[] _bytes;
    private readonly byte[] _anchor;
    private readonly int _anchorOffset;
    internal int Length => _bytes.Length;

    internal NativeCodePattern(string pattern)
    {
        _bytes = pattern.Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(s => s == "??" ? (byte?)null : Convert.ToByte(s, 16)).ToArray();
        if (_bytes.Length == 0 || _bytes.Length > 4096 || _bytes.All(b => !b.HasValue))
            throw new InvalidDataException("Invalid native code pattern.");
        int best = 0, start = 0;
        for (int i = 0; i <= _bytes.Length; i++)
        {
            if (i < _bytes.Length && _bytes[i].HasValue) continue;
            if (i - start > best) { best = i - start; _anchorOffset = start; }
            start = i + 1;
        }
        _anchor = _bytes.Skip(_anchorOffset).Take(best).Select(b => b!.Value).ToArray();
    }

    internal bool Matches(byte[] data, int offset)
    {
        if (offset < 0 || offset > data.Length - Length) return false;
        for (int i = 0; i < Length; i++)
            if (_bytes[i] is byte expected && data[offset + i] != expected) return false;
        return true;
    }

    internal IEnumerable<int> Find(byte[] data, int start, int count)
    {
        if (start < 0 || count < 0 || start > data.Length - count)
            throw new InvalidDataException("Invalid native search range.");
        int end = start + count - Length;
        while (start <= end)
        {
            int relative = data.AsSpan(start + _anchorOffset, end - start + _anchor.Length).IndexOf(_anchor);
            if (relative < 0) yield break;
            int candidate = start + relative;
            if (Matches(data, candidate)) yield return candidate;
            start = candidate + 1;
        }
    }
}
