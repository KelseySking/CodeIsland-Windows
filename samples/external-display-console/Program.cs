using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;

return await ExternalDisplaySample.RunAsync(args);

internal static class ExternalDisplaySample
{
    public static async Task<int> RunAsync(string[] args)
    {
        var options = ClientOptions.Parse(args);
        if (options.ShowHelp)
        {
            PrintUsage();
            return 0;
        }

        if (string.IsNullOrWhiteSpace(options.Token))
        {
            Console.Error.WriteLine("缺少 API token。请传入 --token、设置 CODEISLAND_API_TOKEN，或先启动一次 CodeIsland，让 %APPDATA%\\CodeIsland\\settings.json 写入 api_token。");
            PrintUsage();
            return 1;
        }

        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, eventArgs) =>
        {
            eventArgs.Cancel = true;
            cts.Cancel();
        };

        var client = new RuntimeClient(options.BaseUrl, options.Token);
        await client.PrintStartupStateAsync(cts.Token);

        var eventsTask = options.NoEvents
            ? Task.CompletedTask
            : client.RunEventLoopAsync(cts.Token);

        await client.RunCommandLoopAsync(cts);
        cts.Cancel();

        try
        {
            await eventsTask.WaitAsync(TimeSpan.FromSeconds(2));
        }
        catch
        {
        }

        return 0;
    }

    private static void PrintUsage()
    {
        Console.WriteLine("""
        CodeIsland 外部展示控制台示例

        用法：
          dotnet run --project samples/external-display-console -- --token <api_token>
          dotnet run --project samples/external-display-console -- --base-url http://127.0.0.1:32145 --token <api_token>

        环境变量：
          CODEISLAND_API_URL     默认 http://127.0.0.1:32145
          CODEISLAND_API_TOKEN   API token。未设置时，示例会尝试读取 %APPDATA%\CodeIsland\settings.json。

        命令：
          refresh
          allow <actionId> [always]
          deny <actionId> [reason]
          answer <actionId> <answer>[,<answer>...]
          dismiss <actionId>
          quit
        """);
    }
}

internal sealed record ClientOptions(string BaseUrl, string? Token, bool NoEvents, bool ShowHelp)
{
    public static ClientOptions Parse(string[] args)
    {
        var baseUrl = Environment.GetEnvironmentVariable("CODEISLAND_API_URL") ?? "http://127.0.0.1:32145";
        var token = Environment.GetEnvironmentVariable("CODEISLAND_API_TOKEN") ?? TryReadTokenFromSettings();
        var noEvents = false;
        var showHelp = false;

        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--base-url" when i + 1 < args.Length:
                    baseUrl = args[++i];
                    break;
                case "--token" when i + 1 < args.Length:
                    token = args[++i];
                    break;
                case "--no-events":
                    noEvents = true;
                    break;
                case "-h":
                case "--help":
                    showHelp = true;
                    break;
            }
        }

        return new ClientOptions(NormalizeBaseUrl(baseUrl), token, noEvents, showHelp);
    }

    private static string NormalizeBaseUrl(string value) =>
        string.IsNullOrWhiteSpace(value) ? "http://127.0.0.1:32145" : value.Trim().TrimEnd('/');

    private static string? TryReadTokenFromSettings()
    {
        try
        {
            var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            var settingsPath = Path.Combine(appData, "CodeIsland", "settings.json");
            if (!File.Exists(settingsPath))
                return null;

            using var doc = JsonDocument.Parse(File.ReadAllText(settingsPath));
            return doc.RootElement.TryGetProperty("api_token", out var tokenElement)
                ? tokenElement.GetString()
                : null;
        }
        catch
        {
            return null;
        }
    }
}

internal sealed class RuntimeClient
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private static readonly TimeSpan ReconnectDelay = TimeSpan.FromSeconds(2);
    private readonly HttpClient _http;
    private readonly string _baseUrl;
    private readonly string _token;
    private readonly object _consoleGate = new();

    public RuntimeClient(string baseUrl, string token)
    {
        _baseUrl = baseUrl;
        _token = token;
        _http = new HttpClient
        {
            BaseAddress = new Uri($"{_baseUrl}/api/")
        };
        _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", _token);
    }

    public async Task PrintStartupStateAsync(CancellationToken ct)
    {
        WriteLine($"Runtime API：{_baseUrl}");
        await PrintHealthAsync(ct);
        await PrintCapabilitiesAsync(ct);
        await RefreshSnapshotsAsync(ct);
        WriteCommands();
    }

    public async Task RunEventLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                using var socket = new ClientWebSocket();
                await socket.ConnectAsync(BuildWebSocketUri(), ct);
                WriteLine("WebSocket 已连接，正在监听 /api/events...");
                await RefreshSnapshotsAsync(ct);

                while (!ct.IsCancellationRequested && socket.State == WebSocketState.Open)
                {
                    var message = await ReceiveTextMessageAsync(socket, ct);
                    if (message == null)
                        break;

                    await HandleEventMessageAsync(message, ct);
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                WriteLine($"WebSocket 已断开：{ex.Message}");
                try
                {
                    await Task.Delay(ReconnectDelay, ct);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
            }
        }
    }

    public async Task RunCommandLoopAsync(CancellationTokenSource cts)
    {
        while (!cts.IsCancellationRequested)
        {
            Console.Write("> ");
            var line = Console.ReadLine();
            if (line == null)
                return;

            line = line.Trim();
            if (line.Length == 0)
                continue;

            var parts = line.Split(' ', 3, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            var command = parts[0].ToLowerInvariant();

            try
            {
                switch (command)
                {
                    case "q":
                    case "quit":
                    case "exit":
                        cts.Cancel();
                        return;
                    case "h":
                    case "help":
                        WriteCommands();
                        break;
                    case "r":
                    case "refresh":
                        await RefreshSnapshotsAsync(cts.Token);
                        break;
                    case "allow" when parts.Length >= 2:
                        await AllowAsync(parts[1], parts.Length >= 3 && parts[2].Equals("always", StringComparison.OrdinalIgnoreCase), cts.Token);
                        break;
                    case "deny" when parts.Length >= 2:
                        await DenyAsync(parts[1], parts.Length >= 3 ? parts[2] : "denied from sample display client", cts.Token);
                        break;
                    case "answer" when parts.Length >= 3:
                        await AnswerCurrentAsync(parts[1], SplitAnswers(parts[2]), cts.Token);
                        break;
                    case "dismiss" when parts.Length >= 2:
                        await DismissQuestionAsync(parts[1], cts.Token);
                        break;
                    default:
                        WriteLine("未知命令。输入 help 查看命令列表。");
                        break;
                }
            }
            catch (OperationCanceledException) when (cts.IsCancellationRequested)
            {
                return;
            }
        }
    }

    private async Task PrintHealthAsync(CancellationToken ct)
    {
        var health = await GetJsonAsync<ApiHealthDto>("health", ct);
        if (health != null)
            WriteLine($"健康状态：{health.Status}，启动时间 {health.StartedAtUtc:u}");
    }

    private async Task PrintCapabilitiesAsync(CancellationToken ct)
    {
        var capabilities = await GetJsonAsync<ApiCapabilitiesDto>("capabilities", ct);
        if (capabilities != null)
        {
            WriteLine(
                $"能力：approval={capabilities.Approval}, question={capabilities.Question}, realtime={capabilities.Realtime}, security={capabilities.SecurityMode}");
        }
    }

    private async Task RefreshSnapshotsAsync(CancellationToken ct)
    {
        var sessionsTask = GetJsonAsync<List<SessionDto>>("sessions", ct);
        var pendingTask = GetJsonAsync<List<PendingActionDto>>("pending", ct);
        await Task.WhenAll(sessionsTask, pendingTask);

        PrintSessions(sessionsTask.Result ?? []);
        PrintPending(pendingTask.Result ?? []);
    }

    private async Task AllowAsync(string actionId, bool always, CancellationToken ct)
    {
        var success = await PostJsonForSuccessAsync(
            $"permissions/{Escape(actionId)}/allow",
            new PermissionDecisionRequest(Always: always),
            ct);
        WriteLine(success ? "权限已允许。" : "权限允许失败。");
        await RefreshSnapshotsAsync(ct);
    }

    private async Task DenyAsync(string actionId, string reason, CancellationToken ct)
    {
        var success = await PostJsonForSuccessAsync(
            $"permissions/{Escape(actionId)}/deny",
            new PermissionDecisionRequest(Reason: reason),
            ct);
        WriteLine(success ? "权限已拒绝。" : "权限拒绝失败。");
        await RefreshSnapshotsAsync(ct);
    }

    private async Task AnswerCurrentAsync(string actionId, List<string> answers, CancellationToken ct)
    {
        if (answers.Count == 0)
        {
            WriteLine("回答至少需要一个值。");
            return;
        }

        try
        {
            using var response = await _http.PostAsJsonAsync(
                $"questions/{Escape(actionId)}/answer-current",
                new QuestionCurrentAnswerRequest(answers),
                JsonOptions,
                ct);

            if (!response.IsSuccessStatusCode)
            {
                WriteLine($"问题回答失败：{(int)response.StatusCode} {response.ReasonPhrase}");
                return;
            }

            var result = await response.Content.ReadFromJsonAsync<QuestionCurrentAnswerResultDto>(JsonOptions, ct);
            WriteLine(result?.Resolved == true ? "问题已完成。" : "回答已接受；问题仍在等待后续步骤。");
            await RefreshSnapshotsAsync(ct);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            WriteLine($"问题回答失败：{ex.Message}");
        }
    }

    private async Task DismissQuestionAsync(string actionId, CancellationToken ct)
    {
        var success = await PostForSuccessAsync($"questions/{Escape(actionId)}/dismiss", ct);
        WriteLine(success ? "问题已关闭。" : "问题关闭失败。");
        await RefreshSnapshotsAsync(ct);
    }

    private async Task<T?> GetJsonAsync<T>(string path, CancellationToken ct)
    {
        try
        {
            using var response = await _http.GetAsync(path, ct);
            if (!response.IsSuccessStatusCode)
            {
                var body = await response.Content.ReadAsStringAsync(ct);
                WriteLine($"GET /api/{path} 失败：{(int)response.StatusCode} {body}");
                return default;
            }

            return await response.Content.ReadFromJsonAsync<T>(JsonOptions, ct);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            WriteLine($"GET /api/{path} 失败：{ex.Message}");
            return default;
        }
    }

    private async Task<bool> PostJsonForSuccessAsync<T>(string path, T body, CancellationToken ct)
    {
        try
        {
            using var response = await _http.PostAsJsonAsync(path, body, JsonOptions, ct);
            return response.IsSuccessStatusCode;
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            WriteLine($"POST /api/{path} 失败：{ex.Message}");
            return false;
        }
    }

    private async Task<bool> PostForSuccessAsync(string path, CancellationToken ct)
    {
        try
        {
            using var response = await _http.PostAsync(path, null, ct);
            return response.IsSuccessStatusCode;
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            WriteLine($"POST /api/{path} 失败：{ex.Message}");
            return false;
        }
    }

    private Uri BuildWebSocketUri()
    {
        var builder = new UriBuilder(_baseUrl)
        {
            Scheme = _baseUrl.StartsWith("https://", StringComparison.OrdinalIgnoreCase) ? "wss" : "ws",
            Path = "/api/events",
            Query = $"token={Uri.EscapeDataString(_token)}"
        };
        return builder.Uri;
    }

    private async Task<string?> ReceiveTextMessageAsync(ClientWebSocket socket, CancellationToken ct)
    {
        var buffer = new byte[8192];
        using var message = new MemoryStream();

        while (true)
        {
            var result = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), ct);
            if (result.MessageType == WebSocketMessageType.Close)
                return null;

            message.Write(buffer, 0, result.Count);
            if (result.EndOfMessage)
                return Encoding.UTF8.GetString(message.ToArray());
        }
    }

    private async Task HandleEventMessageAsync(string message, CancellationToken ct)
    {
        try
        {
            var evt = JsonSerializer.Deserialize<HubEventDto>(message, JsonOptions);
            if (evt == null || string.IsNullOrWhiteSpace(evt.Type))
                return;

            WriteLine($"事件：{evt.Type}，时间 {evt.TimestampUtc:u}");
            if (evt.Type.StartsWith("session.", StringComparison.OrdinalIgnoreCase) ||
                evt.Type.StartsWith("pending.", StringComparison.OrdinalIgnoreCase) ||
                evt.Type.StartsWith("source.", StringComparison.OrdinalIgnoreCase))
            {
                await RefreshSnapshotsAsync(ct);
            }
        }
        catch (JsonException ex)
        {
            WriteLine($"已忽略格式错误的事件 payload：{ex.Message}");
        }
    }

    private void PrintSessions(IReadOnlyList<SessionDto> sessions)
    {
        WriteLine($"Sessions（{sessions.Count}）");
        foreach (var session in sessions.OrderByDescending(static session => session.LastUpdatedAtUtc))
        {
            var title = FirstNonEmpty(session.ProjectName, session.WorkingDirectory, session.SessionId);
            var summary = FirstNonEmpty(session.CurrentToolName, session.LastAssistantMessage, session.CompletionText, session.LastUserPrompt, "");
            WriteLine($"  {session.SessionId} [{session.SourceDisplayName}] {session.Status} - {title}");
            if (!string.IsNullOrWhiteSpace(summary))
                WriteLine($"    {Trim(summary, 96)}");
        }
    }

    private void PrintPending(IReadOnlyList<PendingActionDto> pendingActions)
    {
        WriteLine($"Pending（{pendingActions.Count}）");
        foreach (var action in pendingActions.OrderBy(static action => action.CreatedAtUtc))
        {
            WriteLine($"  {action.ActionId} [{action.SourceDisplayName}] {action.Kind}");
            if (action.Permission != null)
            {
                WriteLine($"    permission：{action.Permission.ToolName} {Trim(action.Permission.Description, 96)}");
            }
            else if (action.Question != null)
            {
                var question = action.Question;
                WriteLine($"    question {question.CurrentQuestionIndex + 1}：{Trim(question.Question, 96)}");
                if (question.Options is { Count: > 0 })
                    WriteLine($"    options：{string.Join(", ", question.Options.Select(static option => option.Value ?? option.Label))}");
            }
        }
    }

    private void WriteCommands()
    {
        WriteLine("命令：refresh | allow <actionId> [always] | deny <actionId> [reason] | answer <actionId> <answer>[,<answer>...] | dismiss <actionId> | quit");
    }

    private void WriteLine(string message)
    {
        lock (_consoleGate)
            Console.WriteLine(message);
    }

    private static List<string> SplitAnswers(string value) =>
        value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(static answer => answer.Length > 0)
            .ToList();

    private static string Escape(string value) => Uri.EscapeDataString(value);

    private static string Trim(string? value, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(value))
            return "";

        value = value.ReplaceLineEndings(" ").Trim();
        return value.Length <= maxLength ? value : value[..(maxLength - 3)] + "...";
    }

    private static string FirstNonEmpty(params string?[] values) =>
        values.FirstOrDefault(static value => !string.IsNullOrWhiteSpace(value)) ?? "";
}

internal sealed record ApiHealthDto(string Status, DateTimeOffset StartedAtUtc);

internal sealed record ApiCapabilitiesDto(
    bool HookInjection,
    bool Approval,
    bool Question,
    bool Transcript,
    bool Realtime,
    List<string> RealtimeProtocols,
    string SecurityMode);

internal sealed record SessionDto(
    string SessionId,
    string SourceDisplayName,
    string? ProjectName,
    string? WorkingDirectory,
    string Status,
    string? CurrentToolName,
    string? LastUserPrompt,
    string? LastAssistantMessage,
    string? CompletionText,
    DateTimeOffset LastUpdatedAtUtc);

internal sealed record PendingActionDto(
    string ActionId,
    string Kind,
    string SessionId,
    string SourceDisplayName,
    string? ProjectName,
    string? WorkingDirectory,
    DateTimeOffset CreatedAtUtc,
    PermissionRequestDto? Permission,
    QuestionDto? Question);

internal sealed record PermissionRequestDto(
    string ToolName,
    string? Description,
    string HookEventName,
    Dictionary<string, JsonElement>? ToolInput);

internal sealed record QuestionDto(
    string Question,
    string? Header,
    List<QuestionOptionDto>? Options,
    bool MultiSelect,
    bool IsMultiQuestion,
    int CurrentQuestionIndex,
    string CurrentAnswerKey);

internal sealed record QuestionOptionDto(string Label, string? Description, string? Value);

internal sealed record HubEventDto(string Type, DateTimeOffset TimestampUtc, JsonElement? Data);

internal sealed record PermissionDecisionRequest(bool Always = false, string? Reason = null);

internal sealed record QuestionCurrentAnswerRequest(List<string> Answers);

internal sealed record QuestionCurrentAnswerResultDto(bool Success, bool Resolved);
