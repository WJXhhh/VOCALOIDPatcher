using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text.Json;
using VOCALOIDPatcher.ConsonantTiming;

namespace VOCALOIDPatcher.PhonemeVolume;

internal static class PhonemeVolumeArchive
{
    internal const string EntryPath = "VOCALOIDPatcher/phoneme-volume.json";
    private const int MaximumBytes = 4 * 1024 * 1024;
    private static readonly JsonSerializerOptions Options = new() { MaxDepth = 16 };
    internal sealed record Entry(TimingNoteAddress Address, float[] Decibels);
    private sealed record Data(int Version, Entry[] Entries);

    internal static Entry[] Read(string path)
    {
        using var archive = ZipFile.OpenRead(path);
        var entries = archive.Entries.Where(e => e.FullName == EntryPath).ToArray();
        if (entries.Length == 0) return [];
        if (entries.Length != 1 || entries[0].Length > MaximumBytes)
            throw new InvalidDataException("Invalid phoneme volume entry.");
        using var source = entries[0].Open();
        using var buffer = new MemoryStream();
        var bytes = new byte[8192];
        int count;
        while ((count = source.Read(bytes, 0, bytes.Length)) != 0)
        {
            if (buffer.Length + count > MaximumBytes) throw new InvalidDataException("Phoneme volume data exceeds its bound.");
            buffer.Write(bytes, 0, count);
        }
        var data = JsonSerializer.Deserialize<Data>(buffer.ToArray(), Options)
            ?? throw new InvalidDataException("Empty phoneme volume data.");
        if (data.Version != 1 || data.Entries == null) throw new InvalidDataException("Unsupported phoneme volume schema.");
        Validate(data.Entries);
        return data.Entries;
    }

    private static void Validate(Entry[] entries)
    {
        if (entries.Length > 100000) throw new InvalidDataException("Too many phoneme volume entries.");
        var locators = new HashSet<(int, int, int)>();
        foreach (var entry in entries)
        {
            if (entry?.Address == null || entry.Decibels == null) throw new InvalidDataException("Null phoneme volume entry.");
            entry.Address.Validate();
            _ = new PhonemeVolumePlan(entry.Address.Topology, entry.Decibels);
            if (!locators.Add((entry.Address.Track, entry.Address.Part, entry.Address.Note)))
                throw new InvalidDataException("Duplicate phoneme volume locator.");
        }
    }

    internal static void Write(string path, Entry[] entries)
    {
        Validate(entries);
        byte[] payload = JsonSerializer.SerializeToUtf8Bytes(new Data(1, entries), Options);
        if (payload.Length > MaximumBytes) throw new InvalidDataException("Phoneme volume data exceeds its bound.");
        string absolute = Path.GetFullPath(path);
        string temporary = absolute + "." + Guid.NewGuid().ToString("N") + ".volume.tmp";
        try
        {
            File.Copy(absolute, temporary, false);
            using (var archive = ZipFile.Open(temporary, ZipArchiveMode.Update))
            {
                foreach (var old in archive.Entries.Where(e => e.FullName == EntryPath).ToArray()) old.Delete();
                if (entries.Length != 0)
                {
                    using var output = archive.CreateEntry(EntryPath, CompressionLevel.Optimal).Open();
                    output.Write(payload);
                }
            }
            File.Replace(temporary, absolute, null, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
