using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace VOCALOIDPatcher.BreathVolume;

internal static class BreathProjectArchive
{
    public const string EntryPath = "VOCALOIDPatcher/breath-volume.json";
    internal const int MaxEntryBytes = 8 * 1024 * 1024;
    internal const int MaxEntries = 100_000;
    internal const long MaxNativeFrame = 1_000_000_000;

    public static BreathProjectData Read(string filePath)
    {
        using var archive = ZipFile.OpenRead(filePath);
        var entry = archive.GetEntry(EntryPath);
        if (entry == null)
            return new BreathProjectData();

        if (entry.Length < 0 || entry.Length > MaxEntryBytes)
            throw new InvalidDataException("The breath-volume project entry is too large.");

        using var stream = entry.Open();
        using var bounded = new MemoryStream(entry.Length > 0 ? checked((int)entry.Length) : 0);
        var buffer = new byte[81920];
        var total = 0;
        while (true)
        {
            var read = stream.Read(buffer, 0, Math.Min(buffer.Length, MaxEntryBytes + 1 - total));
            if (read == 0)
                break;
            total += read;
            if (total > MaxEntryBytes)
                throw new InvalidDataException("The breath-volume project entry is too large.");
            bounded.Write(buffer, 0, read);
        }

        bounded.Position = 0;
        BreathProjectData data;
        try
        {
            data = JsonSerializer.Deserialize(bounded, ProjectJsonContext.Default.BreathProjectData) ??
                   new BreathProjectData();
        }
        catch (JsonException e)
        {
            throw new InvalidDataException("The breath-volume project entry is not valid JSON.", e);
        }

        if (data.Entries == null)
            throw new InvalidDataException("The breath-volume project entry list is null.");
        if (data.Entries.Count > MaxEntries)
            throw new InvalidDataException("The breath-volume project entry list is too large.");
        if (data.Entries.Any(static item => item == null))
            throw new InvalidDataException("The breath-volume project entry list contains a null item.");
        if (data.NativeMarkers == null)
            throw new InvalidDataException("The breath-volume native marker list is null.");
        if (data.NativeMarkers.Count > MaxEntries)
            throw new InvalidDataException("The breath-volume native marker list is too large.");
        if (data.NativeMarkers.Any(static item => !IsValidNativeMarker(item)))
            throw new InvalidDataException("The breath-volume native marker list contains an invalid item.");
        return data;
    }

    private static bool IsValidNativeMarker(BreathProjectNativeMarker? item)
    {
        if (item == null)
            return false;
        if (item.BeginFrame >= 0 && item.EndFrame > item.BeginFrame &&
            item.EndFrame <= MaxNativeFrame)
            return true;

        // ABI 2 wrote only a start time. Keep accepting those project entries
        // so unrelated note values still load, but they cannot be restored as
        // exact breath ranges and are intentionally not emitted again.
        return item.BeginFrame == 0 && item.EndFrame == 0 &&
               item.LegacyBeginSeconds is { } seconds &&
               double.IsFinite(seconds) && seconds >= 0.0 &&
               seconds <= 24.0 * 60.0 * 60.0;
    }

    public static void Write(string filePath, BreathProjectData data)
    {
        var directory = Path.GetDirectoryName(filePath);
        if (string.IsNullOrEmpty(directory))
            directory = Directory.GetCurrentDirectory();

        var temporaryPath = Path.Combine(
            directory,
            $".{Path.GetFileName(filePath)}.{Guid.NewGuid():N}.bvl.tmp");

        try
        {
            File.Copy(filePath, temporaryPath, false);
            using (var archive = ZipFile.Open(temporaryPath, ZipArchiveMode.Update))
            {
                archive.GetEntry(EntryPath)?.Delete();
                var entry = archive.CreateEntry(EntryPath, CompressionLevel.Optimal);
                using var stream = entry.Open();
                JsonSerializer.Serialize(stream, data, ProjectJsonContext.Default.BreathProjectData);
            }

            File.Replace(temporaryPath, filePath, null, true);
        }
        finally
        {
            try
            {
                if (File.Exists(temporaryPath))
                    File.Delete(temporaryPath);
            }
            catch
            {
                // The original project is still intact; temporary cleanup is best effort.
            }
        }
    }
}

internal sealed class BreathProjectData
{
    [JsonPropertyName("version")]
    public int Version { get; set; } = 1;

    [JsonPropertyName("entries")]
    public List<BreathProjectEntry> Entries { get; set; } = new();

    [JsonPropertyName("nativeMarkers")]
    public List<BreathProjectNativeMarker> NativeMarkers { get; set; } = new();
}

internal sealed class BreathProjectNativeMarker
{
    [JsonPropertyName("track")]
    public int Track { get; set; }

    [JsonPropertyName("part")]
    public int Part { get; set; }

    [JsonPropertyName("beginFrame")]
    public long BeginFrame { get; set; }

    [JsonPropertyName("endFrame")]
    public long EndFrame { get; set; }

    [JsonPropertyName("beginSeconds")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public double? LegacyBeginSeconds { get; set; }
}

internal sealed class BreathProjectEntry
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

    [JsonPropertyName("value")]
    public int Value { get; set; }
}

[JsonSourceGenerationOptions(WriteIndented = true)]
[JsonSerializable(typeof(BreathProjectData))]
internal partial class ProjectJsonContext : JsonSerializerContext;
