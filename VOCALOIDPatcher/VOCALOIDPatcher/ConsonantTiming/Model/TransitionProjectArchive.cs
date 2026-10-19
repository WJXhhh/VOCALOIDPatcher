using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;

namespace VOCALOIDPatcher.ConsonantTiming;

// Future versions are bounded opaque bytes: opening and saving never requires
// loading a native adapter, nor discarding an unrecognized control schema.
internal sealed class TransitionArchiveData
{
    private readonly byte[] _payload;
    internal TransitionProjectData? Data { get; }
    internal bool Executable => Data != null && !Data.HasExtensions;
    internal bool HasData => Data?.HasControls == true || Data?.Notes.Length > 0 || !Executable;
    internal string Fingerprint { get; }
    internal ReadOnlyMemory<byte> Payload => _payload;
    internal static TransitionArchiveData Empty => From(TransitionProjectData.Empty);
    private TransitionArchiveData(byte[] payload, TransitionProjectData? data)
    { _payload = payload; Data = data; Fingerprint = Convert.ToHexString(SHA256.HashData(payload)); }
    internal static TransitionArchiveData From(TransitionProjectData data)
        => Parse(JsonSerializer.SerializeToUtf8Bytes(data.Validate(), TransitionProjectArchive.Options));
    internal static TransitionArchiveData Parse(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length > TransitionProjectArchive.MaximumBytes) throw new InvalidDataException("Transition data exceeds its bound.");
        byte[] payload = bytes.ToArray();
        try
        {
            using var json = JsonDocument.Parse(payload, new JsonDocumentOptions { MaxDepth = 32 });
            void Check(JsonElement node)
            {
                if (node.ValueKind == JsonValueKind.Object)
                {
                    var names = new System.Collections.Generic.HashSet<string>(StringComparer.Ordinal);
                    foreach (var p in node.EnumerateObject()) { if (!names.Add(p.Name)) throw new InvalidDataException("Duplicate transition JSON property."); Check(p.Value); }
                }
                else if (node.ValueKind == JsonValueKind.Array) foreach (var item in node.EnumerateArray()) Check(item);
            }
            Check(json.RootElement);
            if (json.RootElement.ValueKind != JsonValueKind.Object || !json.RootElement.TryGetProperty("Version", out var version) ||
                !version.TryGetInt32(out int number) || number < 1) throw new InvalidDataException("Missing transition schema version.");
            var data = number != 1 ? null : (JsonSerializer.Deserialize<TransitionProjectData>(payload, TransitionProjectArchive.Options)
                ?? throw new InvalidDataException("Empty transition data.")).Validate();
            if (data != null)
            {
                var addressFields = new System.Collections.Generic.HashSet<string>(new[] { "Track", "Part", "Note", "Position", "Duration", "Pitch", "Bank", "Topology" }, StringComparer.Ordinal);
                if (json.RootElement.GetProperty("Notes").EnumerateArray().Any(n => n.GetProperty("Address").EnumerateObject().Any(p => !addressFields.Contains(p.Name))))
                    data = null; // Preserve unknown address semantics byte-for-byte.
            }
            return new(payload, data);
        }
        catch (JsonException error) { throw new InvalidDataException("Invalid transition JSON.", error); }
    }
}

internal static class TransitionProjectArchive
{
    internal const string EntryPath = "VOCALOIDPatcher/transitions.json";
    internal const int MaximumBytes = 4 * 1024 * 1024;
    internal static readonly JsonSerializerOptions Options = new() { MaxDepth = 32, DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull };
    internal static TransitionArchiveData Read(string path)
    {
        using var zip = ZipFile.OpenRead(path);
        var entries = zip.Entries.Where(e => e.FullName == EntryPath).ToArray();
        if (entries.Length == 0) return TransitionArchiveData.Empty;
        if (entries.Length != 1 || entries[0].Length > MaximumBytes) throw new InvalidDataException("Invalid transition archive entry.");
        using var stream = entries[0].Open(); using var buffer = new MemoryStream();
        byte[] block = new byte[8192]; int count;
        while ((count = stream.Read(block)) != 0)
        {
            if (buffer.Length + count > MaximumBytes) throw new InvalidDataException("Transition archive exceeds its bound.");
            buffer.Write(block, 0, count);
        }
        return TransitionArchiveData.Parse(buffer.ToArray());
    }
    internal static void Write(string path, TransitionArchiveData data)
    {
        string full = Path.GetFullPath(path), temporary = full + "." + Guid.NewGuid().ToString("N") + ".transitions.tmp";
        try
        {
            File.Copy(full, temporary, false);
            using (var zip = ZipFile.Open(temporary, ZipArchiveMode.Update))
            {
                foreach (var entry in zip.Entries.Where(e => e.FullName == EntryPath).ToArray()) entry.Delete();
                if (data.HasData) { using var output = zip.CreateEntry(EntryPath, CompressionLevel.Optimal).Open(); output.Write(data.Payload.Span); }
            }
            File.Replace(temporary, full, null, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
