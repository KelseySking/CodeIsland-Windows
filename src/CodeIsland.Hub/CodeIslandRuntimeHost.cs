using System.Diagnostics;
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

    public string? ApiHost { get; init; }

    public bool RepairSourcesOnStart { get; init; } = true;
}

public sealed class CodeIslandRuntimeHost : IAsyncDisposable, IDisposable
{
    private static readonly TimeSpan ProcessMonitorInterval = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan ProcessStartTimeTolerance = TimeSpan.FromSeconds(2);
    private readonly CodeIslandRuntimeHostOptions _options;
    private System.Threading.Timer? _processMonitorTimer;
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
        ApiHost = NormalizeApiHost(options.ApiHost ?? Settings.Get("api_bind_host", "127.0.0.1"));

        HubState = new CodeIslandHubState(options.ShouldAutoApprovePermission ?? ShouldAutoApprovePermission);
        HookServer = new CodeIslandHookServer(HubState, options.SessionTimeoutProvider ?? GetSessionTimeout, Logger, PipeName);
        ApiHostInstance = new CodeIslandApiHost(CodeIslandApiOptions.Bind(ApiHost, ApiToken, ApiPort), HubState, SourceService, Logger);
        HubState.RealtimeEventRaised += OnHubRealtimeEventRaised;
    }

    public SettingsManager Settings { get; }

    public EventLogger? Logger { get; }

    public ICodeIslandSourceService SourceService { get; }

    public CodeIslandHubState HubState { get; }

    public CodeIslandHookServer HookServer { get; }

    public CodeIslandApiHost ApiHostInstance { get; }

    public string ApiHost { get; }

    public string PipeName { get; }

    public string ApiToken { get; }

    public int ApiPort { get; }

    public string ApiBaseUrl => ApiHostInstance.BaseUrl;

    public async Task StartAsync(CancellationToken ct = default)
    {
        if (_started)
            return;

        if (_options.RepairSourcesOnStart)
            _ = SourceService.RepairAll();

        await HookServer.StartAsync();
        await ApiHostInstance.StartAsync(ct);
        StartProcessMonitor();
        _started = true;
    }

    public async ValueTask DisposeAsync()
    {
        _processMonitorTimer?.Dispose();
        HubState.RealtimeEventRaised -= OnHubRealtimeEventRaised;
        HookServer.Dispose();
        await ApiHostInstance.DisposeAsync();
    }

    public void Dispose()
    {
        _processMonitorTimer?.Dispose();
        HubState.RealtimeEventRaised -= OnHubRealtimeEventRaised;
        HookServer.Dispose();
        ApiHostInstance.Dispose();
    }

    private void OnHubRealtimeEventRaised(object? sender, HubRealtimeEventArgs e)
    {
        _ = ApiHostInstance.Realtime.PublishAsync(e.Type, e.Data);
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

    private void StartProcessMonitor()
    {
        _processMonitorTimer?.Dispose();
        _processMonitorTimer = new System.Threading.Timer(_ =>
        {
            if (_started)
                HubState.RemoveExitedSessions(IsTrackedProcessExited);
        }, null, ProcessMonitorInterval, ProcessMonitorInterval);
    }

    private static bool IsTrackedProcessExited(SessionSnapshot session)
    {
        if (session.Pid <= 0)
            return false;

        try
        {
            using var process = Process.GetProcessById(session.Pid);
            if (process.HasExited)
                return true;

            if (session.TrackedProcessStartedAtUtc is not { } expectedStart)
                return false;

            if (!TryGetProcessStartTimeUtc(process, out var actualStart))
                return false;

            return (actualStart - expectedStart).Duration() > ProcessStartTimeTolerance;
        }
        catch (ArgumentException)
        {
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static bool TryGetProcessStartTimeUtc(Process process, out DateTime startedAtUtc)
    {
        try
        {
            startedAtUtc = process.StartTime.ToUniversalTime();
            return true;
        }
        catch
        {
            startedAtUtc = default;
            return false;
        }
    }

    private static string NormalizeApiHost(string? host)
    {
        if (string.IsNullOrWhiteSpace(host))
            return "127.0.0.1";

        var value = host.Trim();
        return value is "*" or "+" ? "0.0.0.0" : value;
    }
}
