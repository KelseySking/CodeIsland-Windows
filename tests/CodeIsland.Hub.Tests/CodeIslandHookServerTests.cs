using System.IO.Pipes;
using System.Text.Json;
using CodeIsland.Core.IPC;
using CodeIsland.Hub;
using Xunit;

namespace CodeIsland.Hub.Tests;

public class CodeIslandHookServerTests
{
    [Fact]
    public async Task NonBlockingEvent_WritesAckBeforeReducingIntoHubState()
    {
        var state = new CodeIslandHubState();
        var pipeName = $"codeisland-test-{Guid.NewGuid():N}";
        using var server = new CodeIslandHookServer(state, () => TimeSpan.FromSeconds(5), logger: null, pipeName);

        await server.StartAsync();

        var response = await SendAsync(pipeName, JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["hook_event_name"] = "SessionStart",
            ["session_id"] = "session-1",
            ["cwd"] = @"D:\Work\sample"
        }));

        Assert.Equal("{}", response);
        await WaitForAsync(() => state.GetSessions().Count == 1);
        Assert.Equal("session-1", Assert.Single(state.GetSessions()).SessionId);
    }

    [Fact]
    public async Task BlockingPermissionEvent_UsesHubStateResponseCompletion()
    {
        var state = new CodeIslandHubState();
        var pipeName = $"codeisland-test-{Guid.NewGuid():N}";
        using var server = new CodeIslandHookServer(state, () => TimeSpan.FromSeconds(5), logger: null, pipeName);

        await server.StartAsync();

        var responseTask = SendAsync(pipeName, JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["hook_event_name"] = "PreToolUse",
            ["session_id"] = "session-1",
            ["tool_name"] = "Bash",
            ["tool_input"] = new { command = "dotnet test", requires_approval = true }
        }));

        await WaitForAsync(() => state.GetPendingActions().Count == 1);
        var action = Assert.Single(state.GetPendingActions());
        Assert.True(state.AllowPermission(action.ActionId, always: false));

        var response = await responseTask.WaitAsync(TimeSpan.FromSeconds(5));
        using var doc = JsonDocument.Parse(response);
        Assert.Equal("allow", doc.RootElement
            .GetProperty("hookSpecificOutput")
            .GetProperty("permissionDecision")
            .GetString());
    }

    private static async Task<string> SendAsync(string pipeName, string json)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        using var pipe = await ConnectAsync(pipeName, cts.Token);
        await MessageProtocol.WriteMessageAsync(pipe, json, cts.Token);
        return await MessageProtocol.ReadMessageAsync(pipe, cts.Token) ?? "{}";
    }

    private static async Task<NamedPipeClientStream> ConnectAsync(string pipeName, CancellationToken ct)
    {
        while (true)
        {
            var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
            try
            {
                await pipe.ConnectAsync(200, ct);
                return pipe;
            }
            catch (TimeoutException)
            {
                pipe.Dispose();
                await Task.Delay(25, ct);
            }
        }
    }

    private static async Task WaitForAsync(Func<bool> predicate)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!predicate())
        {
            await Task.Delay(25, cts.Token);
        }
    }
}
