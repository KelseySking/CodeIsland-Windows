using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text.Json;
using CodeIsland.Bridge;
using CodeIsland.Contracts;
using CodeIsland.Core.IPC;
using CodeIsland.Core.Services;
using CodeIsland.Hub;
using Xunit;

namespace CodeIsland.Hub.Tests;

public class CodeIslandRuntimeHostTests : IDisposable
{
    private readonly string? _originalPipeName;

    public CodeIslandRuntimeHostTests()
    {
        _originalPipeName = Environment.GetEnvironmentVariable(NamedPipePath.OverrideEnvironmentVariable);
    }

    [Fact]
    public async Task StartAsync_ExposesHealthEndpoint()
    {
        using var runtime = CreateHost(out _);

        await runtime.StartAsync();

        using var client = new HttpClient();
        var health = await client.GetFromJsonAsync<ApiHealthDto>($"{runtime.ApiBaseUrl}/api/health");

        Assert.NotNull(health);
        Assert.Equal("ok", health!.Status);
    }

    [Fact]
    public async Task RuntimeHost_AcceptsBridgeEventsWithoutWpfProcess()
    {
        using var runtime = CreateHost(out var pipeName);
        Environment.SetEnvironmentVariable(NamedPipePath.OverrideEnvironmentVariable, pipeName);

        await runtime.StartAsync();

        var response = await BridgeClient.SendAsync(JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["hook_event_name"] = "SessionStart",
            ["session_id"] = "runtime-only-session",
            ["cwd"] = @"D:\Work\runtime-only",
            ["_source"] = "claude"
        }), blocking: false);

        Assert.Equal("{}", response);
        await WaitForAsync(() => runtime.HubState.GetSessions().Any(session => session.SessionId == "runtime-only-session"));
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable(NamedPipePath.OverrideEnvironmentVariable, _originalPipeName);
    }

    private static CodeIslandRuntimeHost CreateHost(out string pipeName)
    {
        pipeName = $"codeisland-runtime-host-test-{Guid.NewGuid():N}";
        var settingsDir = Path.Combine(Path.GetTempPath(), $"codeisland-runtime-host-{Guid.NewGuid():N}");
        Directory.CreateDirectory(settingsDir);

        return new CodeIslandRuntimeHost(new CodeIslandRuntimeHostOptions
        {
            Settings = new SettingsManager(settingsDir),
            ApiPort = GetFreeTcpPort(),
            PipeName = pipeName,
            RepairSourcesOnStart = false
        });
    }

    private static async Task WaitForAsync(Func<bool> predicate)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!predicate())
            await Task.Delay(25, cts.Token);
    }

    private static int GetFreeTcpPort()
    {
        using var listener = new TcpListener(System.Net.IPAddress.Loopback, 0);
        listener.Start();
        return ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
    }
}
