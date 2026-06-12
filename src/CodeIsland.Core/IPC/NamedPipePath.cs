namespace CodeIsland.Core.IPC;

/// <summary>
/// Named Pipe 路径常量
/// </summary>
public static class NamedPipePath
{
    public const string OverrideEnvironmentVariable = "CODEISLAND_PIPE_NAME";

    /// <summary>
    /// 生成当前用户的 Named Pipe 名称
    /// 格式: codeisland-{userName}
    /// </summary>
    public static string GetPipeName()
    {
        var overrideName = Environment.GetEnvironmentVariable(OverrideEnvironmentVariable);
        if (!string.IsNullOrWhiteSpace(overrideName))
            return overrideName.Trim();

        var userName = Environment.UserName;
        return $"codeisland-{userName}";
    }

    /// <summary>
    /// 完整的 Named Pipe 路径
    /// </summary>
    public static string GetFullPath() => $@"\\.\pipe\{GetPipeName()}";
}
