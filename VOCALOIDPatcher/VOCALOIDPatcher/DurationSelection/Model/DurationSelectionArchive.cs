using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text.Json;
using VOCALOIDPatcher.ConsonantTiming;

namespace VOCALOIDPatcher.DurationSelection;

internal static class DurationSelectionArchive
{
    internal const string EntryPath = "VOCALOIDPatcher/duration-selection.json";
    private const int MaximumBytes = 4 * 1024 * 1024;
    internal sealed record Entry(TimingNoteAddress Address, float Ratio, DurationCandidatePolicy Policy, int Algorithm, bool ScopeOnly = false);
    private sealed record Data(int Version, Entry[] Entries);
    private static void Validate(Entry[] entries)
    {
        if (entries.Length > 100000) throw new InvalidDataException("Too many duration preferences.");
        var seen = new HashSet<(int, int, int)>();
        foreach (var entry in entries)
        {
            if (entry?.Address == null || entry.Algorithm != DurationSelectionPlan.AlgorithmVersion) throw new InvalidDataException("Unsupported duration algorithm.");
            entry.Address.Validate();
            var plan = new DurationSelectionPlan(entry.Address.Topology, entry.Ratio, entry.Policy);
            if (entry.ScopeOnly && !plan.IsDefault) throw new InvalidDataException("Duration scope marker cannot contain an override.");
            if (!seen.Add((entry.Address.Track, entry.Address.Part, entry.Address.Note))) throw new InvalidDataException("Duplicate duration locator.");
        }
    }
    internal static Entry[] Read(string path)
    {
        using var zip = ZipFile.OpenRead(path);
        var entries = zip.Entries.Where(e => e.FullName == EntryPath).ToArray();
        if (entries.Length == 0) return [];
        if (entries.Length != 1 || entries[0].Length > MaximumBytes) throw new InvalidDataException("Invalid duration archive entry.");
        using var stream = entries[0].Open(); using var buffer = new MemoryStream();
        byte[] block = new byte[8192]; int read;
        while ((read = stream.Read(block, 0, block.Length)) != 0)
        {
            if (buffer.Length + read > MaximumBytes) throw new InvalidDataException("Duration archive exceeds bound.");
            buffer.Write(block, 0, read);
        }
        Data data;
        try { data = JsonSerializer.Deserialize<Data>(buffer.ToArray(), new JsonSerializerOptions { MaxDepth = 16 }) ?? throw new InvalidDataException("Empty duration data."); }
        catch (JsonException e) { throw new InvalidDataException("Invalid duration JSON.", e); }
        if (data.Version != 1 || data.Entries == null) throw new InvalidDataException("Unsupported duration schema.");
        Validate(data.Entries); return data.Entries;
    }
    internal static void Write(string path, Entry[] entries)
    {
        Validate(entries);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(new Data(1, entries));
        if (bytes.Length > MaximumBytes) throw new InvalidDataException("Duration archive exceeds bound.");
        string full = Path.GetFullPath(path), temporary = full + "." + Guid.NewGuid().ToString("N") + ".duration.tmp";
        try
        {
            File.Copy(full, temporary, false);
            using (var zip = ZipFile.Open(temporary, ZipArchiveMode.Update))
            {
                foreach (var old in zip.Entries.Where(e => e.FullName == EntryPath).ToArray()) old.Delete();
                if (entries.Length > 0) { using var output = zip.CreateEntry(EntryPath, CompressionLevel.Optimal).Open(); output.Write(bytes); }
            }
            File.Replace(temporary, full, null, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
