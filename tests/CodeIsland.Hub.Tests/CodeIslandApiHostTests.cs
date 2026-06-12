using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text.Json;
using CodeIsland.Contracts;
using CodeIsland.Core.Models;
using CodeIsland.Hub;
using Xunit;

namespace CodeIsland.Hub.Tests;

public class CodeIslandApiHostTests
{
    [Fact]
    public async Task AnswerCurrentQuestionEndpoint_AdvancesCurrentQuestionAndReturnsResolvedState()
    {
        var state = new CodeIslandHubState();
        var evt = MakeEvent(new Dictionary<string, object?>
        {
            ["hook_event_name"] = "PermissionRequest",
            ["session_id"] = "session-1",
            ["tool_name"] = "AskUserQuestion",
            ["tool_input"] = new
            {
                questions = new object[]
                {
                    new { id = "first", question = "First?", options = new[] { "A", "B" } },
                    new { id = "second", question = "Second?", options = new[] { "C", "D" } }
                }
            }
        });

        var responseTask = state.HandleBlockingEventAsync(evt, TimeSpan.FromSeconds(5), CancellationToken.None);
        var actionId = Assert.Single(state.GetPendingActions()).ActionId;
        var port = GetFreeTcpPort();
        const string token = "test-token";
        await using var apiHost = new CodeIslandApiHost(CodeIslandApiOptions.Localhost(token, port), state, new StubSourceService());
        await apiHost.StartAsync();

        using var client = new HttpClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        var firstResponse = await client.PostAsJsonAsync(
            $"{apiHost.BaseUrl}/api/questions/{actionId}/answer-current",
            new QuestionCurrentAnswerRequest(["A"]));
        Assert.Equal(HttpStatusCode.OK, firstResponse.StatusCode);
        var firstResult = await firstResponse.Content.ReadFromJsonAsync<QuestionCurrentAnswerResultDto>();
        Assert.NotNull(firstResult);
        Assert.True(firstResult!.Success);
        Assert.False(firstResult.Resolved);
        Assert.Equal(1, Assert.Single(state.GetPendingActions()).Question!.CurrentQuestionIndex);

        var finalResponse = await client.PostAsJsonAsync(
            $"{apiHost.BaseUrl}/api/questions/{actionId}/answer-current",
            new QuestionCurrentAnswerRequest(["D"]));
        Assert.Equal(HttpStatusCode.OK, finalResponse.StatusCode);
        var finalResult = await finalResponse.Content.ReadFromJsonAsync<QuestionCurrentAnswerResultDto>();
        Assert.NotNull(finalResult);
        Assert.True(finalResult!.Success);
        Assert.True(finalResult.Resolved);

        var hookResponse = await responseTask.WaitAsync(TimeSpan.FromSeconds(5));
        using var doc = JsonDocument.Parse(hookResponse);
        var answers = doc.RootElement
            .GetProperty("hookSpecificOutput")
            .GetProperty("updatedInput")
            .GetProperty("answers");
        Assert.Equal("A", answers.GetProperty("first").GetString());
        Assert.Equal("D", answers.GetProperty("second").GetString());
        Assert.Empty(state.GetPendingActions());
    }

    private static HookEvent MakeEvent(Dictionary<string, object?> payload, string source = "claude")
    {
        using var doc = JsonDocument.Parse(JsonSerializer.Serialize(payload));
        return HookEvent.FromJson(doc.RootElement, source)!;
    }

    private static int GetFreeTcpPort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }

    private sealed class StubSourceService : ICodeIslandSourceService
    {
        public IReadOnlyList<SourceDto> GetSources() => [];
        public SourceStatusDto GetSourceStatus(string source) => new(source, Supported: false, Installed: false, DisplayName: source);
        public SourceOperationResultDto Install(string source) => OperationFailed(source);
        public SourceOperationResultDto Uninstall(string source) => OperationFailed(source);
        public SourceOperationResultDto Repair(string source) => OperationFailed(source);
        public bool RepairAll() => false;
        public RuntimeAssetsDto GetRuntimeAssets() => new("", "", "", Installed: false);
        public bool RepairRuntimeAssets() => false;

        private static SourceOperationResultDto OperationFailed(string source) =>
            new(source, Success: false, Installed: false, Message: "not supported");
    }
}
