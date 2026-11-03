using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using VOCALOIDPatcher.PhonemeVolume;

internal static partial class Program
{
    // PCM-only replay: no native engine initialization or live Editor changes.
    private static int ProbePhonemeGainFile(string[] args)
    {
        string[] paths = args.Where(a => !a.StartsWith("--", StringComparison.Ordinal)).ToArray();
        if (paths.Length != 3) throw new ArgumentException("Expected source WAVE, regions JSON, and destination WAVE.");
        if (new FileInfo(paths[1]).Length > 1024 * 1024) throw new InvalidDataException("Region fixture is too large.");
        var regions = JsonSerializer.Deserialize<PhonemeGainRegion[]>(File.ReadAllText(paths[1]),
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? throw new InvalidDataException("Missing gain regions.");
        string Hash(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));
        string original = Hash(paths[0]);
        PhonemeGainWaveProcessor.Create(paths[0], paths[2], regions);
        Require(Hash(paths[0]) == original, "File replay modified its input.");
        Console.WriteLine(JsonSerializer.Serialize(new { sourceSha256 = original, outputSha256 = Hash(paths[2]),
            regions = regions.Length, bytes = new FileInfo(paths[2]).Length, sourceUnchanged = true }));
        return 0;
    }
}
