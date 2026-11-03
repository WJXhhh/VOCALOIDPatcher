using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text.Json;
using VOCALOIDPatcher.ConsonantTiming;

namespace VOCALOIDPatcher.VoiceComp;

internal static class VoiceCompArchive
{
    internal const string EntryPath = "VOCALOIDPatcher/voice-comp.json";
    private const int MaxBytes = 4 * 1024 * 1024;
    internal sealed record Entry(TimingNoteAddress Address, VoiceCompPlan Plan);
    private sealed record Data(int Version, Entry[] Entries);
    private static readonly JsonSerializerOptions Options = new() { MaxDepth = 20 };

    internal static Entry[] Read(string path)
    {
        using var zip = ZipFile.OpenRead(path);
        var entries = zip.Entries.Where(e => e.FullName == EntryPath).ToArray();
        if (entries.Length == 0) return [];
        if (entries.Length != 1 || entries[0].Length > MaxBytes) throw new InvalidDataException("Invalid voice-comp archive entry.");
        using var input = entries[0].Open();
        using var buffer = new MemoryStream();
        var chunk = new byte[8192]; int read;
        while ((read = input.Read(chunk)) != 0)
        {
            if (buffer.Length + read > MaxBytes) throw new InvalidDataException("Voice-comp archive exceeds its bound.");
            buffer.Write(chunk, 0, read);
        }
        var data = JsonSerializer.Deserialize<Data>(buffer.ToArray(), Options) ?? throw new InvalidDataException("Empty voice-comp archive.");
        if (data.Version != 1) throw new InvalidDataException("Unsupported voice-comp archive version.");
        Validate(data.Entries); return data.Entries;
    }

    internal static void Write(string path, Entry[] entries)
    {
        Validate(entries);
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(new Data(1, entries), Options);
        if (bytes.Length > MaxBytes) throw new InvalidDataException("Voice-comp archive exceeds its bound.");
        // The caller owns the encompassing project-save transaction.
        using var zip = ZipFile.Open(path, ZipArchiveMode.Update);
        foreach (var old in zip.Entries.Where(e => e.FullName == EntryPath).ToArray()) old.Delete();
        if (entries.Length == 0) return;
        using var output = zip.CreateEntry(EntryPath, CompressionLevel.Optimal).Open(); output.Write(bytes);
    }

    private static void Validate(Entry[] entries)
    {
        if (entries == null || entries.Length > 100000) throw new InvalidDataException("Invalid voice-comp entry count.");
        var locators = new HashSet<(int, int, int)>();
        foreach (var entry in entries)
        {
            if (entry?.Address == null || entry.Plan == null) throw new InvalidDataException("Null voice-comp entry.");
            entry.Address.Validate(); entry.Plan.Validate();
            if (!locators.Add((entry.Address.Track, entry.Address.Part, entry.Address.Note)))
                throw new InvalidDataException("Duplicate voice-comp note.");
        }
    }
}
