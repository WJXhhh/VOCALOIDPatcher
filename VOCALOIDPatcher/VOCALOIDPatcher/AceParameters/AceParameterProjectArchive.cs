using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace VOCALOIDPatcher.AceParameters;

// Custom ZIP entry inside .vpr used to persist the ACE carrier curves. The
// carrier VSMControllerTypes (HMM family in particular) are accepted by the
// native API but are not written to Project/sequence.json, so without this
// archive the curves would reload flat. The entry is the source of truth: on
// load every ACE carrier controller is cleared and rebuilt from it.
internal static class AceParameterProjectArchive
{
    internal const string EntryPath = "VOCALOIDPatcher/ace-parameters.json";
    internal const int CurrentVersion = 1;
    internal const int MaxEntryBytes = 8 * 1024 * 1024;
    internal const int MaxEntries = 100_000;
    internal const int MaxPointsPerEntry = 200_000;
    internal const long MaxNativeFrame = 1_000_000_000;

    internal static AceProjectData? Read(string filePath)
    {
        using var archive = ZipFile.OpenRead(filePath);
        var entry = archive.GetEntry(EntryPath);
        if (entry == null)
            return null;
        if (entry.Length < 0 || entry.Length > MaxEntryBytes)
            throw new InvalidDataException("The ACE parameter project entry is too large.");

        using var stream = entry.Open();
        using var bounded = new MemoryStream(entry.Length > 0 ? checked((int)entry.Length) : 0);
        var buffer = new byte[81920];
        int total = 0;
        while (true)
        {
            int read = stream.Read(buffer, 0, Math.Min(buffer.Length, MaxEntryBytes + 1 - total));
            if (read == 0)
                break;
            total += read;
            if (total > MaxEntryBytes)
                throw new InvalidDataException("The ACE parameter project entry is too large.");
            bounded.Write(buffer, 0, read);
        }

        bounded.Position = 0;
        AceProjectData? data;
        try
        {
            data = JsonSerializer.Deserialize(bounded, AceProjectJsonContext.Default.AceProjectData);
        }
        catch (JsonException e)
        {
            throw new InvalidDataException("The ACE parameter project entry is not valid JSON.", e);
        }
        if (data == null)
            return null;
        Validate(data);
        return data;
    }

    private static void Validate(AceProjectData data)
    {
        if (data.Version < 1 || data.Version > CurrentVersion)
            throw new InvalidDataException($"Unsupported ACE parameter project entry version {data.Version}.");
        if (data.Entries == null)
            throw new InvalidDataException("The ACE parameter project entry list is null.");
        if (data.Entries.Count > MaxEntries)
            throw new InvalidDataException("The ACE parameter project entry list is too large.");
        foreach (var entry in data.Entries)
        {
            if (entry == null || entry.Track < 0 || entry.Part < 0 || entry.Kind < 0 ||
                entry.Points == null || entry.Points.Count > MaxPointsPerEntry)
                throw new InvalidDataException("Invalid ACE parameter project entry.");
            if (entry.AbsPosTick < 0 || entry.AbsPosTick > MaxNativeFrame)
                throw new InvalidDataException("Invalid ACE parameter project position.");
            long lastTick = -1;
            foreach (var point in entry.Points)
            {
                if (point == null || point.Tick < 0 || point.Tick > MaxNativeFrame ||
                    point.Tick < lastTick || point.Value < -1000 || point.Value > 1000)
                    throw new InvalidDataException("Invalid ACE parameter project point.");
                lastTick = point.Tick;
            }
        }
    }

    internal static void Write(string filePath, AceProjectData data)
    {
        var directory = Path.GetDirectoryName(filePath);
        if (string.IsNullOrEmpty(directory))
            directory = Directory.GetCurrentDirectory();

        var temporaryPath = Path.Combine(
            directory,
            $".{Path.GetFileName(filePath)}.{Guid.NewGuid():N}.ace.tmp");
        try
        {
            File.Copy(filePath, temporaryPath, false);
            using (var archive = ZipFile.Open(temporaryPath, ZipArchiveMode.Update))
            {
                archive.GetEntry(EntryPath)?.Delete();
                var entry = archive.CreateEntry(EntryPath, CompressionLevel.Optimal);
                using var stream = entry.Open();
                JsonSerializer.Serialize(stream, data, AceProjectJsonContext.Default.AceProjectData);
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

internal sealed class AceProjectData
{
    [JsonPropertyName("version")]
    public int Version { get; set; } = AceParameterProjectArchive.CurrentVersion;

    [JsonPropertyName("entries")]
    public List<AceProjectEntry> Entries { get; set; } = new();
}

internal sealed class AceProjectEntry
{
    [JsonPropertyName("track")]
    public int Track { get; set; }

    [JsonPropertyName("part")]
    public int Part { get; set; }

    [JsonPropertyName("absPosTick")]
    public long AbsPosTick { get; set; }

    [JsonPropertyName("kind")]
    public int Kind { get; set; }

    [JsonPropertyName("points")]
    public List<AceProjectPoint> Points { get; set; } = new();
}

internal sealed class AceProjectPoint
{
    [JsonPropertyName("tick")]
    public long Tick { get; set; }

    [JsonPropertyName("value")]
    public int Value { get; set; }
}

[JsonSourceGenerationOptions(WriteIndented = false)]
[JsonSerializable(typeof(AceProjectData))]
internal partial class AceProjectJsonContext : JsonSerializerContext;
