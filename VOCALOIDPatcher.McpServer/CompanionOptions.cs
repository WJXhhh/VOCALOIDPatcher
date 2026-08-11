using VOCALOIDPatcher.McpBridge;

namespace VOCALOIDPatcher.McpServer;

public sealed record CompanionOptions(
    string Transport,
    string? DefaultInstanceId,
    int Port,
    string? ExplicitToken,
    bool PrintConfig)
{
    public static CompanionOptions Parse(string[] args)
    {
        string transport = "stdio";
        string? instance = null;
        int port = 39266;
        string? token = null;
        bool printConfig = false;

        for (int index = 0; index < args.Length; index++)
        {
            string arg = args[index];
            string? value = index + 1 < args.Length ? args[index + 1] : null;
            switch (arg)
            {
                case "--transport" when value != null:
                    transport = value.ToLowerInvariant();
                    index++;
                    break;
                case "--instance" when value != null:
                    instance = string.Equals(value, "auto", StringComparison.OrdinalIgnoreCase) ? null : value;
                    index++;
                    break;
                case "--port" when value != null && int.TryParse(value, out int parsed):
                    port = parsed;
                    index++;
                    break;
                case "--token" when value != null:
                    token = value;
                    index++;
                    break;
                case "--print-config":
                    printConfig = true;
                    break;
            }
        }

        if (transport is not ("stdio" or "http"))
            throw new ArgumentException("--transport must be 'stdio' or 'http'.");
        if (port is < 1 or > 65535)
            throw new ArgumentOutOfRangeException(nameof(port), "Port must be between 1 and 65535.");

        return new CompanionOptions(transport, instance, port, token, printConfig);
    }
}
