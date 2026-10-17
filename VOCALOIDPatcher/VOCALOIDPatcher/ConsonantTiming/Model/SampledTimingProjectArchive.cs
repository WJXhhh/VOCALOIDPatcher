using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text.Json;

namespace VOCALOIDPatcher.ConsonantTiming;

internal static class SampledTimingProjectArchive
{
    internal const string EntryPath = "VOCALOIDPatcher/sampled-timing.json";
    private const int MaxBytes = 4 * 1024 * 1024;
    private static readonly JsonSerializerOptions Options = new() { MaxDepth = 16 };
    internal sealed record Entry(TimingNoteAddress Address, TimingAdjustment[] Adjustments);
    internal sealed record Data(int Version, Entry[] Entries);

    internal static IReadOnlyDictionary<TimingNoteAddress, SampledTimingPlan> Read(string path)
    {
        using var archive = ZipFile.OpenRead(path);
        var entries = archive.Entries.Where(e => e.FullName == EntryPath).ToArray();
        if (entries.Length == 0) return new Dictionary<TimingNoteAddress, SampledTimingPlan>();
        if (entries.Length != 1 || entries[0].Length > MaxBytes) throw new InvalidDataException("Invalid timing project entry.");
        using var source = entries[0].Open();
        using var buffer = new MemoryStream();
        var bytes = new byte[8192];
        int read;
        while ((read = source.Read(bytes, 0, bytes.Length)) != 0)
        {
            if (buffer.Length + read > MaxBytes) throw new InvalidDataException("Timing entry exceeds its bound.");
            buffer.Write(bytes, 0, read);
        }
        Data data;
        try { data = JsonSerializer.Deserialize<Data>(buffer.ToArray(), Options) ?? throw new InvalidDataException("Empty timing data."); }
        catch (JsonException error) { throw new InvalidDataException("Invalid timing JSON.", error); }
        if (data.Version != 1 || data.Entries == null || data.Entries.Length > 100000) throw new InvalidDataException("Unsupported timing schema.");
        var result = new Dictionary<TimingNoteAddress, SampledTimingPlan>();
        foreach (var entry in data.Entries)
        {
            if (entry?.Address == null || entry.Adjustments == null) throw new InvalidDataException("Null timing entry.");
            entry.Address.Validate();
            if (!result.TryAdd(entry.Address, new SampledTimingPlan(entry.Address.Topology, entry.Adjustments)))
                throw new InvalidDataException("Duplicate timing note.");
        }
        if (result.Keys.GroupBy(a => (a.Track, a.Part, a.Note)).Any(g => g.Count() > 1)) throw new InvalidDataException("Duplicate timing locator.");
        return result;
    }

    internal static void Write(string path, IReadOnlyDictionary<TimingNoteAddress, SampledTimingPlan> plans)
    {
        if (plans.Count > 100000) throw new InvalidDataException("Too many timing notes.");
        if (plans.Keys.GroupBy(a => (a.Track, a.Part, a.Note)).Any(g => g.Count() > 1)) throw new InvalidDataException("Duplicate timing locator.");
        var data = new Data(1, plans.Select(p =>
        {
            p.Key.Validate();
            if (p.Key.Topology != p.Value.Topology) throw new InvalidDataException("Timing topology mismatch.");
            return new Entry(p.Key, p.Value.Adjustments.ToArray());
        }).ToArray());
        byte[] payload = JsonSerializer.SerializeToUtf8Bytes(data, Options);
        if (payload.Length > MaxBytes) throw new InvalidDataException("Timing data exceeds its bound.");
        string absolute = Path.GetFullPath(path);
        string temporary = Path.Combine(Path.GetDirectoryName(absolute)!, "." + Path.GetFileName(absolute) + "." + Guid.NewGuid().ToString("N") + ".timing.tmp");
        try
        {
            File.Copy(absolute, temporary, false);
            using (var zip = ZipFile.Open(temporary, ZipArchiveMode.Update))
            {
                foreach (var old in zip.Entries.Where(e => e.FullName == EntryPath).ToArray()) old.Delete();
                if (plans.Count != 0)
                {
                    using var stream = zip.CreateEntry(EntryPath, CompressionLevel.Optimal).Open();
                    stream.Write(payload);
                }
            }
            File.Replace(temporary, absolute, null, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
