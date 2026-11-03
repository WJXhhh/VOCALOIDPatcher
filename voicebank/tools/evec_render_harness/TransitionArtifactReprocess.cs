using System.IO;
using System.Text.Json;
using VOCALOIDPatcher.ConsonantTiming.Smoothing;

internal static partial class Program
{
    // Offline PCM-only regression of the production boundary selection policy.
    private static int ReprocessTransitionArtifacts(string directory)
    {
        using var document = JsonDocument.Parse(File.ReadAllText(Path.Combine(directory, "transition-artifacts.json")));
        var report = new List<object>();
        foreach (var row in document.RootElement.EnumerateArray())
        {
            if (!row.TryGetProperty("file", out var file)) continue;
            var junctions = new SortedDictionary<long, WaveJunctionProcessor.JunctionInfo>();
            long gainBegin = -1, gainEnd = -1;
            double gain = 0;
            if (row.TryGetProperty("smoothing", out var smoothing) && smoothing.ValueKind == JsonValueKind.Object)
            {
                gainBegin = smoothing.GetProperty("begin").GetInt64(); gainEnd = smoothing.GetProperty("end").GetInt64();
                gain = smoothing.GetProperty("Decibels").GetDouble();
            }
            foreach (var segment in row.GetProperty("rows").EnumerateArray())
                foreach (string endpoint in new[] { "Begin", "Split", "End" })
                {
                    long at = (segment.GetProperty(endpoint).GetInt64() - 3087) * 256;
                    if (at <= 0) continue;
                    junctions[at] = new(at, at > gainBegin && at <= gainEnd ? gain : 0,
                        at >= gainBegin && at < gainEnd ? gain : 0);
                }
            string source = file.GetString()!, output = source + ".repaired.wav";
            var results = WaveJunctionProcessor.ProcessWaveFile(Path.Combine(directory, source), Path.Combine(directory, output),
                junctions.Values.ToArray());
            report.Add(new { file = source, output, junctions = junctions.Values, results });
        }
        File.WriteAllText(Path.Combine(directory, "reprocessed.json"), JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine($"reprocessed={report.Count}");
        return 0;
    }
}
