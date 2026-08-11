using System.Diagnostics;
using System.Text.Json;

namespace VOCALOIDPatcher.McpBridge;

public static class InstanceRegistry
{
    public static string RegistryDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "VOCALOIDPatcher",
        "mcp",
        "instances");

    public static string GetRegistrationPath(string instanceId)
        => Path.Combine(RegistryDirectory, instanceId + ".json");

    public static void Write(InstanceRegistration registration)
    {
        Directory.CreateDirectory(RegistryDirectory);
        string path = GetRegistrationPath(registration.InstanceId);
        string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(registration, BridgeProtocol.JsonOptions));
        File.Move(temporary, path, true);
    }

    public static void Remove(string instanceId)
    {
        try
        {
            File.Delete(GetRegistrationPath(instanceId));
        }
        catch
        {
            // Stale registrations are also cleaned by readers.
        }
    }

    public static IReadOnlyList<InstanceRegistration> ReadLive()
    {
        if (!Directory.Exists(RegistryDirectory))
            return Array.Empty<InstanceRegistration>();

        var result = new List<InstanceRegistration>();
        foreach (string path in Directory.EnumerateFiles(RegistryDirectory, "*.json", SearchOption.TopDirectoryOnly))
        {
            try
            {
                var registration = JsonSerializer.Deserialize<InstanceRegistration>(File.ReadAllText(path), BridgeProtocol.JsonOptions);
                if (registration == null || registration.ProtocolVersion != BridgeProtocol.Version || !IsLive(registration))
                {
                    TryDelete(path);
                    continue;
                }

                result.Add(registration);
            }
            catch
            {
                TryDelete(path);
            }
        }

        return result.OrderBy(item => item.ProcessId).ToArray();
    }

    private static bool IsLive(InstanceRegistration registration)
    {
        try
        {
            using Process process = Process.GetProcessById(registration.ProcessId);
            return process.StartTime.ToUniversalTime().Ticks == registration.ProcessStartTimeUtcTicks;
        }
        catch
        {
            return false;
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch
        {
            // Another process may be refreshing it.
        }
    }
}
