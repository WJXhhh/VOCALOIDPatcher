using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace VOCALOIDPatcher.Evec;

internal static class EvecProjectArchive
{
    public const string EntryPath = "VOCALOIDPatcher/evec.json";
    internal const int MaxEntryBytes = 4 * 1024 * 1024;
    internal const int MaxEntries = 100_000;

    public static EvecProjectData Read(string filePath)
    {
        using var archive = ZipFile.OpenRead(filePath);
        var entry = archive.GetEntry(EntryPath);
        if (entry == null)
            return new EvecProjectData();
        if (entry.Length < 0 || entry.Length > MaxEntryBytes)
            throw new InvalidDataException("The evec project entry is too large.");

        using var source = entry.Open();
        using var bounded = new MemoryStream(entry.Length > 0 ? checked((int)entry.Length) : 0);
        var buffer = new byte[81920];
        var total = 0;
        while (true)
        {
            var read = source.Read(buffer, 0, Math.Min(buffer.Length, MaxEntryBytes + 1 - total));
            if (read == 0)
                break;
            total += read;
            if (total > MaxEntryBytes)
                throw new InvalidDataException("The evec project entry is too large.");
            bounded.Write(buffer, 0, read);
        }

        bounded.Position = 0;
        EvecProjectData data;
        try
        {
            data = JsonSerializer.Deserialize(bounded, EvecJsonContext.Default.EvecProjectData)
                   ?? new EvecProjectData();
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("The evec project entry is not valid JSON.", exception);
        }

        if (data.Version != 1 || data.Entries == null || data.Entries.Count > MaxEntries ||
            data.Entries.Any(item => item == null || item.Track < 0 || item.Part < 0 ||
                item.Note < 0 || item.RelPosTick < 0 || item.NoteNumber is < 0 or > 127 ||
                item.Occurrence < 0 ||
                !EvecConstants.IsValidVoiceColorId(item.VoiceColorId) ||
                !EvecConstants.IsValidAttackId(item.AttackId) ||
                !EvecConstants.IsValidReleaseId(item.ReleaseId) ||
                !EvecConstants.IsValidConsonantExtension(item.ConsonantExtension)))
            throw new InvalidDataException("The evec project entry is invalid.");

        return data;
    }

    public static void Write(string filePath, EvecProjectData data)
    {
        if (data.Version != 1 || data.Entries == null || data.Entries.Count > MaxEntries ||
            data.Entries.Any(item => item == null || item.Track < 0 || item.Part < 0 ||
                item.Note < 0 || item.RelPosTick < 0 || item.NoteNumber is < 0 or > 127 ||
                item.Occurrence < 0 ||
                !EvecConstants.IsValidVoiceColorId(item.VoiceColorId) ||
                !EvecConstants.IsValidAttackId(item.AttackId) ||
                !EvecConstants.IsValidReleaseId(item.ReleaseId) ||
                !EvecConstants.IsValidConsonantExtension(item.ConsonantExtension)))
            throw new InvalidDataException("The evec project data is invalid.");

        byte[]? payload = null;
        if (data.Entries.Count > 0)
        {
            payload = JsonSerializer.SerializeToUtf8Bytes(data,
                EvecJsonContext.Default.EvecProjectData);
            if (payload.Length > MaxEntryBytes)
                throw new InvalidDataException("The evec project entry is too large.");
        }

        var directory = Path.GetDirectoryName(filePath);
        if (string.IsNullOrEmpty(directory))
            directory = Directory.GetCurrentDirectory();
        var temporaryPath = Path.Combine(directory,
            $".{Path.GetFileName(filePath)}.{Guid.NewGuid():N}.evec.tmp");
        try
        {
            File.Copy(filePath, temporaryPath, false);
            using (var archive = ZipFile.Open(temporaryPath, ZipArchiveMode.Update))
            {
                archive.GetEntry(EntryPath)?.Delete();
                if (payload != null)
                {
                    var entry = archive.CreateEntry(EntryPath, CompressionLevel.Optimal);
                    using var stream = entry.Open();
                    stream.Write(payload);
                }
            }
            File.Replace(temporaryPath, filePath, null, true);
        }
        finally
        {
            try { if (File.Exists(temporaryPath)) File.Delete(temporaryPath); }
            catch { }
        }
    }
}

internal sealed class EvecProjectData
{
    [JsonPropertyName("version")]
    public int Version { get; set; } = 1;

    [JsonPropertyName("entries")]
    public List<EvecProjectEntry> Entries { get; set; } = new();
}

internal sealed class EvecProjectEntry
{
    [JsonPropertyName("track")]
    public int Track { get; set; }
    [JsonPropertyName("part")]
    public int Part { get; set; }
    [JsonPropertyName("note")]
    public int Note { get; set; }
    [JsonPropertyName("relPosTick")]
    public long RelPosTick { get; set; }
    [JsonPropertyName("noteNumber")]
    public int NoteNumber { get; set; }
    [JsonPropertyName("occurrence")]
    public int Occurrence { get; set; }
    [JsonPropertyName("voiceColorId")]
    public int VoiceColorId { get; set; }
    [JsonPropertyName("attackId")]
    public int AttackId { get; set; }
    [JsonPropertyName("releaseId")]
    public int ReleaseId { get; set; }
    [JsonPropertyName("consonantExtension")]
    public int ConsonantExtension { get; set; }
}

[JsonSourceGenerationOptions(WriteIndented = true)]
[JsonSerializable(typeof(EvecProjectData))]
internal partial class EvecJsonContext : JsonSerializerContext;
