using System.Security.Cryptography;

namespace CodeIsland.WpfApp.Services;

public static class WpfLocalApiTokenStore
{
    public static string EnsureToken(SettingsManager settings)
    {
        var existing = settings.Get("api_token", "");
        if (!string.IsNullOrWhiteSpace(existing))
            return existing;

        var token = CreateToken();
        settings.Set("api_token", token);
        return token;
    }

    /// <summary>生成新 token 并写入 settings（不记录明文）。</summary>
    public static string RegenerateToken(SettingsManager settings)
    {
        var token = CreateToken();
        settings.Set("api_token", token);
        return token;
    }

    public static string CreateToken()
    {
        var tokenBytes = RandomNumberGenerator.GetBytes(32);
        return Convert.ToBase64String(tokenBytes)
            .TrimEnd('=')
            .Replace("+", "-", StringComparison.Ordinal)
            .Replace("/", "_", StringComparison.Ordinal);
    }
}
