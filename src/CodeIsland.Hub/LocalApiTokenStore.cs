using System.Security.Cryptography;
using CodeIsland.Core.Services;

namespace CodeIsland.Hub;

public static class LocalApiTokenStore
{
    public const string SettingsKey = "api_token";

    public static string EnsureToken(SettingsManager settings)
    {
        var existing = settings.Get(SettingsKey, "");
        if (existing.Length >= 32)
            return existing;

        var token = GenerateToken();
        settings.Set(SettingsKey, token);
        return token;
    }

    private static string GenerateToken()
    {
        Span<byte> bytes = stackalloc byte[32];
        RandomNumberGenerator.Fill(bytes);
        return Convert.ToBase64String(bytes)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
    }
}
