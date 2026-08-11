using System.Security.Cryptography;

namespace VOCALOIDPatcher.McpBridge;

public static class HttpTokenStore
{
    private static readonly object Gate = new();

    public static string TokenFile => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "VOCALOIDPatcher",
        "mcp",
        "http-token.txt");

    public static string GetOrCreate()
    {
        lock (Gate)
        {
            try
            {
                if (File.Exists(TokenFile))
                {
                    string existing = File.ReadAllText(TokenFile).Trim();
                    if (existing.Length >= 32)
                        return existing;
                }
            }
            catch
            {
                // A fresh token is safer than starting without authentication.
            }

            return WriteFreshToken();
        }
    }

    public static string Rotate()
    {
        lock (Gate)
            return WriteFreshToken();
    }

    private static string WriteFreshToken()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(TokenFile)!);
        string token = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
        string temporary = TokenFile + "." + Guid.NewGuid().ToString("N") + ".tmp";
        File.WriteAllText(temporary, token);
        File.Move(temporary, TokenFile, true);
        return token;
    }
}
