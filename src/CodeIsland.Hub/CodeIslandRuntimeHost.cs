using CodeIsland.Core.Models;
using CodeIsland.Core.Services;

namespace CodeIsland.Hub;

public sealed class CodeIslandRuntimeHostOptions
{
    public required SettingsManager Settings { get; init; }

    public EventLogger? Logger { get; init; }

    public ICodeIslandSourceService? SourceService { get; init; }

    public Func<PermissionRequest, bool>? ShouldAutoApprovePermission { get; init; }

    public Func<TimeSpan>? SessionTimeoutProvider { get; init; }

    public string? PipeName { get; init; }

    public string? ApiToken { get; init; }

    public int? ApiPort { get; init; }

    public bool RepairSourcesOnStart { get; init; } = true;
}

public sealed class CodeIslandRuntimeHost : IAsyncDisposable, IDisposable
{
    private readonly CodeIslandRuntimeHostOptions _options;
    private bool _started;

    public CodeIslandRuntimeHost(CodeIslandRuntimeHostOptions options)
    {
        _options = options;
        SourceService = options.SourceService ?? new ConfigInstallerSourceService();
        Settings = options.Settings;
        Logger = options.Logger;
        PipeName = string.IsNullOrWhiteSpace(options.PipeName) ? CodeIsland.Core.IPC.NamedPipePath.GetPipeName() : options.PipeName.Trim();
        ApiToken = string.IsNullOrWhiteSpace(options.ApiToken) ? LocalApiTokenStore.EnsureToken(Settings) : options.ApiToken.Trim();
        ApiPort = Math.Clamp(options.ApiPort ?? Settings.Get("api_port", 32145), 1024, 65535);

        HubState = new CodeIslandHubState(options.ShouldAutoApprovePermission ?? ShouldAutoApprovePermission);
        HookServer = new CodeIslandHookServer(HubState, options.SessionTimeoutProvider ?? GetSessionTimeout, Logger, PipeName);
        ApiHost = new CodeIslandApiHost(CodeIslandApiOptions.Localhost(ApiToken, ApiPort), HubState, SourceService, Logger);
        HubState.RealtimeEventRaised += OnHubRealtimeEventRaised;
    }

    public SettingsManager Settings { get; }

    public EventLogger? Logger { get; }

    public ICodeIslandSourceService SourceService { get; }

    public CodeIslandHubState HubState { get; }

    public CodeIslandHookServer HookServer { get; }

    public CodeIslandApiHost ApiHost { get; }

    public string PipeName { get; }

    public string ApiToken { get; }

    public int ApiPort { get; }

    public string ApiBaseUrl => ApiHost.BaseUrl;

    public async Task StartAsync(CancellationToken ct = default)
    {
        if (_started)
            return;

        if (_options.RepairSourcesOnStart)
            _ = SourceService.RepairAll();

        await HookServer.StartAsync();
        await ApiHost.StartAsync(ct);
        _started = true;
    }

    public async ValueTask DisposeAsync()
    {
        HubState.RealtimeEventRaised -= OnHubRealtimeEventRaised;
        HookServer.Dispose();
        await ApiHost.DisposeAsync();
    }

    public void Dispose()
    {
        HubState.RealtimeEventRaised -= OnHubRealtimeEventRaised;
        HookServer.Dispose();
        ApiHost.Dispose();
    }

    private void OnHubRealtimeEventRaised(object? sender, HubRealtimeEventArgs e)
    {
        _ = ApiHost.Realtime.PublishAsync(e.Type, e.Data);
    }

    private TimeSpan GetSessionTimeout()
    {
        var seconds = Math.Clamp(Settings.Get("session_timeout", 300), 30, 3600);
        return TimeSpan.FromSeconds(seconds);
    }

    private bool ShouldAutoApprovePermission(PermissionRequest request)
    {
        if (!Settings.Get("auto_approve_safe_tools", false))
            return false;

        return request.ToolName is "Read" or "Grep" or "Glob" or "LS" or "TodoRead";
    }
}
