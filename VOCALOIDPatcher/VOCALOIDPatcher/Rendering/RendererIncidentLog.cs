using System;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using System.Threading.Tasks;

namespace VOCALOIDPatcher.Rendering;

internal static class RendererIncidentLog
{
    private static readonly Channel<string> Pending = Channel.CreateBounded<string>(new BoundedChannelOptions(32)
    { SingleReader = true, FullMode = BoundedChannelFullMode.DropOldest });
    internal static string DirectoryPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "VOCALOIDPatcher", "diagnostics");
    private static readonly Task Writer = Task.Run(WriteLoop);
    private static readonly object FileGate = new();

    internal static void Fatal(object incident)
    {
        try { WriteLine(JsonSerializer.Serialize(incident)); }
        catch { }
    }

    // Serialize the fault snapshot before recovery can retire its native state.
    internal static string Capture(object incident)
    {
        string json = JsonSerializer.Serialize(incident);
        if (json.Length > 131072) json = JsonSerializer.Serialize(new { kind = "incident-too-large", utc = DateTime.UtcNow });
        Pending.Writer.TryWrite(json);
        return json;
    }

    private static async Task WriteLoop()
    {
        await foreach (string line in Pending.Reader.ReadAllAsync())
        {
            try
            {
                WriteLine(line);
            }
            catch { /* Diagnostics must never interrupt a render or recovery. */ }
        }
    }
    private static void WriteLine(string line)
    {
        lock (FileGate)
        {
            Directory.CreateDirectory(DirectoryPath);
            string path = Path.Combine(DirectoryPath, "renderer.jsonl");
            if (File.Exists(path) && new FileInfo(path).Length >= 2 * 1024 * 1024)
            {
                if (File.Exists(path + ".1")) File.Move(path + ".1", path + ".2", true);
                File.Move(path, path + ".1", true);
            }
            File.AppendAllText(path, line + Environment.NewLine, Encoding.UTF8);
        }
    }
}
