using System.Security.Cryptography;

namespace CodeIsland.WpfApp.Services;

public static class WpfLocalApiTokenStore
{
    public static string EnsureToken(SettingsManager settings)
    {
        var existing = settings.Get("api_token", "");
        if (!string.IsNullOrWhiteSpace(existing))
            return existing;

        var tokenBytes = RandomNumberGenerator.GetBytes(32);
        var token = Convert.ToBase64String(tokenBytes)
            .TrimEnd('=')
            .Replace("+", "-", StringComparison.Ordinal)
            .Replace("/", "_", StringComparison.Ordinal);
        settings.Set("api_token", token);
        return token;
    }
}
