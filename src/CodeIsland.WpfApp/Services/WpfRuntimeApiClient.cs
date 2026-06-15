using System.IO;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net.Http;
using System.Net.WebSockets;
using System.Text.Json;
using CodeIsland.Contracts;
using CodeIsland.WpfApp.Models;

namespace CodeIsland.WpfApp.Services;

public sealed class WpfRuntimeApiClient : IWpfRuntimeClient, IWpfSourceService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private static readonly TimeSpan ReconnectDelay = TimeSpan.FromSeconds(2);
    private const string RuntimeUnavailableMessage = "Runtime is not connected";
    private readonly HttpClient _http;
    private readonly string _baseUrl;
    private readonly string _token;
    private readonly EventLogger? _logger;
    private readonly CancellationTokenSource _disposeCts = new();
    private readonly SemaphoreSlim _refreshLock = new(1, 1);
    private readonly object _stateGate = new();
    private List<SessionDto> _sessions = [];
    private List<PendingActionDto> _pendingActions = [];
    private List<SourceDto> _sources = [];
    private Task? _webSocketTask;
    private bool _started;
    private bool _disposed;

    public WpfRuntimeApiClient(string baseUrl, string token, EventLogger? logger = null)
    {
        _baseUrl = NormalizeBaseUrl(baseUrl);
        _token = token;
        _logger = logger;
        _http = new HttpClient
        {
            BaseAddress = new Uri($"{_baseUrl}/api/")
        };
        _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", _token);
    }

    public event EventHandler<WpfRuntimeStateChangedEventArgs>? StateChanged;

    public async Task StartAsync(CancellationToken ct = default)
    {
        if (_started)
            return;

        _started = true;
        _logger?.Write("WpfRuntimeApiClient", "start", new Dictionary<string, string?>
        {
            ["baseUrl"] = _baseUrl
        });
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, _disposeCts.Token);
        await RefreshSourcesAsync(linkedCts.Token).ConfigureAwait(false);
        await RefreshRuntimeStateAsync(realtimeEventType: null, initial: true, linkedCts.Token).ConfigureAwait(false);
        _webSocketTask = Task.Run(() => RunWebSocketLoopAsync(_disposeCts.Token));
    }

    public async Task<bool> AllowPermissionAsync(string actionId, bool always, CancellationToken ct = default)
    {
        var success = await PostJsonForSuccessAsync($"permissions/{Escape(actionId)}/allow", new PermissionDecisionRequest(always), ct).ConfigureAwait(false);
        if (success)
            await RefreshRuntimeStateAsync("pending.resolved", initial: false, ct).ConfigureAwait(false);
        return success;
    }

    public async Task<bool> DenyPermissionAsync(string actionId, string reason, CancellationToken ct = default)
    {
        var success = await PostJsonForSuccessAsync($"permissions/{Escape(actionId)}/deny", new PermissionDecisionRequest(Reason: reason), ct).ConfigureAwait(false);
        if (success)
            await RefreshRuntimeStateAsync("pending.resolved", initial: false, ct).ConfigureAwait(false);
        return success;
    }

    public async Task<QuestionCurrentAnswerResult> AnswerCurrentQuestionAsync(string actionId, IReadOnlyList<string> answers, CancellationToken ct = default)
    {
        try
        {
            using var response = await _http.PostAsJsonAsync(
                $"questions/{Escape(actionId)}/answer-current",
                new QuestionCurrentAnswerRequest(answers),
                JsonOptions,
                ct).ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                _logger?.Write("WpfRuntimeApiClient", "answer-current-failed", new Dictionary<string, string?>
                {
                    ["actionId"] = actionId,
                    ["status"] = ((int)response.StatusCode).ToString()
                });
                return new QuestionCurrentAnswerResult(false, false);
            }

            var result = await response.Content.ReadFromJsonAsync<QuestionCurrentAnswerResultDto>(JsonOptions, ct).ConfigureAwait(false);
            await RefreshRuntimeStateAsync(result?.Resolved == true ? "pending.resolved" : "pending.updated", initial: false, ct).ConfigureAwait(false);
            return new QuestionCurrentAnswerResult(true, result?.Resolved == true);
        }
        catch
        {
            _logger?.Write("WpfRuntimeApiClient", "answer-current-exception", new Dictionary<string, string?>
            {
                ["actionId"] = actionId
            });
            return new QuestionCurrentAnswerResult(false, false);
        }
    }

    public async Task<bool> DismissQuestionAsync(string actionId, string reason, CancellationToken ct = default)
    {
        var success = await PostForSuccessAsync($"questions/{Escape(actionId)}/dismiss", ct).ConfigureAwait(false);
        if (success)
            await RefreshRuntimeStateAsync("pending.resolved", initial: false, ct).ConfigureAwait(false);
        return success;
    }

    public async Task<bool> ActivateTerminalAsync(string sessionId, CancellationToken ct = default)
    {
        var success = await PostForSuccessAsync($"sessions/{Escape(sessionId)}/activate-terminal", ct).ConfigureAwait(false);
        if (!success)
            return false;

        if (TryGetSession(sessionId, out var session))
            WpfTerminalActivator.Activate(MapSession(session));

        return true;
    }

    public IReadOnlyList<SourceDto> GetSources()
    {
        var sources = RunSyncNullable(() => GetJsonAsync<List<SourceDto>>("sources", CancellationToken.None));
        if (sources != null)
        {
            lock (_stateGate)
                _sources = sources;
            return sources;
        }

        lock (_stateGate)
            return _sources.ToList();
    }

    public SourceStatusDto GetSourceStatus(string source) =>
        RunSyncNullable(() => GetJsonAsync<SourceStatusDto>($"sources/{Escape(source)}/status", CancellationToken.None)) ??
        new SourceStatusDto(source, Supported: false, Installed: false, DisplayName: source);

    public SourceOperationResultDto Install(string source) =>
        RunSourceOperation(source, "install");

    public SourceOperationResultDto Uninstall(string source) =>
        RunSourceOperation(source, "uninstall");

    public SourceOperationResultDto Repair(string source) =>
        RunSourceOperation(source, "repair");

    public bool RepairAll() =>
        RunRuntimeSync("repair-all", "sources/repair-all", false, async () =>
        {
            using var response = await _http.PostAsync("sources/repair-all", content: null, CancellationToken.None).ConfigureAwait(false);
            LogFailedStatus(response, "sources/repair-all", "repair-all-failed");
            if (!response.IsSuccessStatusCode)
                return false;
            var result = await ReadResponseJsonAsync<SuccessResponse>(response, "sources/repair-all", CancellationToken.None).ConfigureAwait(false);
            await RefreshSourcesAsync(CancellationToken.None).ConfigureAwait(false);
            return result?.Success == true;
        });

    public RuntimeAssetsDto GetRuntimeAssets() =>
        RunSyncNullable(() => GetJsonAsync<RuntimeAssetsDto>("runtime-assets", CancellationToken.None)) ??
        new RuntimeAssetsDto("", "", "", Installed: false);

    public bool RepairRuntimeAssets() =>
        RunRuntimeSync("repair-runtime-assets", "runtime-assets/repair", false, async () =>
        {
            using var response = await _http.PostAsync("runtime-assets/repair", content: null, CancellationToken.None).ConfigureAwait(false);
            LogFailedStatus(response, "runtime-assets/repair", "repair-runtime-assets-failed");
            if (!response.IsSuccessStatusCode)
                return false;
            var result = await ReadResponseJsonAsync<RuntimeAssetsRepairResponse>(response, "runtime-assets/repair", CancellationToken.None).ConfigureAwait(false);
            await RefreshSourcesAsync(CancellationToken.None).ConfigureAwait(false);
            return result?.Success == true;
        });

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        _disposeCts.Cancel();
        _http.Dispose();
    }

    private SourceOperationResultDto RunSourceOperation(string source, string operation)
    {
        var path = $"sources/{Escape(source)}/{operation}";
        var result = RunRuntimeSync<SourceOperationResultDto?>(
            $"source-{operation}",
            path,
            fallback: null,
            async () =>
        {
            using var response = await _http.PostAsync(path, content: null, CancellationToken.None).ConfigureAwait(false);
            LogFailedStatus(response, path, "source-operation-failed", new Dictionary<string, string?>
            {
                ["source"] = source,
                ["operation"] = operation
            });
            var dto = await ReadResponseJsonAsync<SourceOperationResultDto>(response, path, CancellationToken.None).ConfigureAwait(false);
            await RefreshSourcesAsync(CancellationToken.None).ConfigureAwait(false);
            return dto;
        });

        return result ?? SourceOperationFailed(source, RuntimeUnavailableMessage);
    }

    private async Task RefreshSourcesAsync(CancellationToken ct)
    {
        try
        {
            var sources = await GetJsonAsync<List<SourceDto>>("sources", ct).ConfigureAwait(false);
            if (sources == null)
                return;

            lock (_stateGate)
                _sources = sources;
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            _logger?.Write("WpfRuntimeApiClient", "refresh-sources-failed", new Dictionary<string, string?>
            {
                ["message"] = ex.Message,
                ["exception"] = ex.GetType().Name
            });
        }
    }

    private async Task RefreshRuntimeStateAsync(string? realtimeEventType, bool initial, CancellationToken ct)
    {
        try
        {
            await _refreshLock.WaitAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        try
        {
            var sessionsTask = GetJsonAsync<List<SessionDto>>("sessions", ct);
            var pendingTask = GetJsonAsync<List<PendingActionDto>>("pending", ct);
            var sessions = await sessionsTask.ConfigureAwait(false) ?? [];
            var pendingActions = await pendingTask.ConfigureAwait(false) ?? [];

            List<SessionDto> previousSessions;
            List<PendingActionDto> previousPendingActions;
            lock (_stateGate)
            {
                previousSessions = _sessions;
                previousPendingActions = _pendingActions;
                _sessions = sessions;
                _pendingActions = pendingActions;
            }

            var change = BuildStateChangedEvent(previousSessions, previousPendingActions, sessions, pendingActions, realtimeEventType, initial);
            StateChanged?.Invoke(this, change);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            _logger?.Write("WpfRuntimeApiClient", "refresh-state-failed", new Dictionary<string, string?>
            {
                ["message"] = ex.Message,
                ["exception"] = ex.GetType().Name
            });
        }
        finally
        {
            _refreshLock.Release();
        }
    }

    private async Task RunWebSocketLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                using var socket = new ClientWebSocket();
                await socket.ConnectAsync(BuildWebSocketUri(), ct).ConfigureAwait(false);
                _logger?.Write("WpfRuntimeApiClient", "websocket-connected");
                await RefreshRuntimeStateAsync(realtimeEventType: null, initial: true, ct).ConfigureAwait(false);
                await ReceiveWebSocketMessagesAsync(socket, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                _logger?.Write("WpfRuntimeApiClient", "websocket-reconnect", new Dictionary<string, string?>
                {
                    ["message"] = ex.Message,
                    ["exception"] = ex.GetType().Name
                });
                try
                {
                    await Task.Delay(ReconnectDelay, ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                }
            }
        }
    }

    private async Task ReceiveWebSocketMessagesAsync(ClientWebSocket socket, CancellationToken ct)
    {
        var buffer = new byte[8192];
        while (!ct.IsCancellationRequested && socket.State == WebSocketState.Open)
        {
            using var message = new MemoryStream();
            WebSocketReceiveResult result;
            do
            {
                result = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), ct).ConfigureAwait(false);
                if (result.MessageType == WebSocketMessageType.Close)
                    return;
                message.Write(buffer, 0, result.Count);
            }
            while (!result.EndOfMessage);

            if (result.MessageType == WebSocketMessageType.Text)
                await HandleRealtimeMessageAsync(message.ToArray(), ct).ConfigureAwait(false);
        }
    }

    private async Task HandleRealtimeMessageAsync(byte[] payload, CancellationToken ct)
    {
        try
        {
            using var doc = JsonDocument.Parse(payload);
            if (!doc.RootElement.TryGetProperty("type", out var typeElement))
                return;

            var type = typeElement.GetString();
            if (string.IsNullOrWhiteSpace(type))
                return;

            if (type.StartsWith("source.", StringComparison.OrdinalIgnoreCase))
            {
                await RefreshSourcesAsync(ct).ConfigureAwait(false);
                return;
            }

            if (type.StartsWith("session.", StringComparison.OrdinalIgnoreCase) ||
                type.StartsWith("pending.", StringComparison.OrdinalIgnoreCase))
            {
                await RefreshRuntimeStateAsync(type, initial: false, ct).ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            _logger?.Write("WpfRuntimeApiClient", "handle-realtime-failed", new Dictionary<string, string?>
            {
                ["message"] = ex.Message,
                ["exception"] = ex.GetType().Name
            });
        }
    }

    private async Task<T?> GetJsonAsync<T>(string path, CancellationToken ct)
    {
        try
        {
            return await _http.GetFromJsonAsync<T>(path, JsonOptions, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger?.Write("WpfRuntimeApiClient", "get-json-failed", new Dictionary<string, string?>
            {
                ["path"] = path,
                ["message"] = ex.Message,
                ["exception"] = ex.GetType().Name
            });
            return default;
        }
    }

    private async Task<T?> ReadResponseJsonAsync<T>(HttpResponseMessage response, string path, CancellationToken ct)
    {
        try
        {
            return await response.Content.ReadFromJsonAsync<T>(JsonOptions, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            _logger?.Write("WpfRuntimeApiClient", "read-json-failed", new Dictionary<string, string?>
            {
                ["path"] = path,
                ["status"] = ((int)response.StatusCode).ToString(),
                ["message"] = ex.Message,
                ["exception"] = ex.GetType().Name
            });
            return default;
        }
    }

    private T RunRuntimeSync<T>(string operation, string path, T fallback, Func<Task<T>> action)
    {
        try
        {
            return RunSync(action);
        }
        catch (Exception ex)
        {
            _logger?.Write("WpfRuntimeApiClient", "runtime-operation-exception", new Dictionary<string, string?>
            {
                ["operation"] = operation,
                ["path"] = path,
                ["message"] = ex.Message,
                ["exception"] = ex.GetType().Name
            });
            return fallback;
        }
    }

    private void LogFailedStatus(
        HttpResponseMessage response,
        string path,
        string eventName,
        Dictionary<string, string?>? extra = null)
    {
        if (response.IsSuccessStatusCode)
            return;

        var fields = extra == null
            ? new Dictionary<string, string?>()
            : new Dictionary<string, string?>(extra, StringComparer.Ordinal);
        fields["path"] = path;
        fields["status"] = ((int)response.StatusCode).ToString();
        _logger?.Write("WpfRuntimeApiClient", eventName, fields);
    }

    private async Task<bool> PostForSuccessAsync(string path, CancellationToken ct)
    {
        try
        {
            using var response = await _http.PostAsync(path, content: null, ct).ConfigureAwait(false);
            var success = response.IsSuccessStatusCode;
            if (!success)
            {
                _logger?.Write("WpfRuntimeApiClient", "post-failed", new Dictionary<string, string?>
                {
                    ["path"] = path,
                    ["status"] = ((int)response.StatusCode).ToString()
                });
            }
            return success;
        }
        catch (Exception ex)
        {
            _logger?.Write("WpfRuntimeApiClient", "post-exception", new Dictionary<string, string?>
            {
                ["path"] = path,
                ["message"] = ex.Message,
                ["exception"] = ex.GetType().Name
            });
            return false;
        }
    }

    private async Task<bool> PostJsonForSuccessAsync<T>(string path, T body, CancellationToken ct)
    {
        try
        {
            using var response = await _http.PostAsJsonAsync(path, body, JsonOptions, ct).ConfigureAwait(false);
            var success = response.IsSuccessStatusCode;
            if (!success)
            {
                _logger?.Write("WpfRuntimeApiClient", "post-json-failed", new Dictionary<string, string?>
                {
                    ["path"] = path,
                    ["status"] = ((int)response.StatusCode).ToString()
                });
            }
            return success;
        }
        catch (Exception ex)
        {
            _logger?.Write("WpfRuntimeApiClient", "post-json-exception", new Dictionary<string, string?>
            {
                ["path"] = path,
                ["message"] = ex.Message,
                ["exception"] = ex.GetType().Name
            });
            return false;
        }
    }

    private bool TryGetSession(string sessionId, out SessionDto session)
    {
        lock (_stateGate)
        {
            session = _sessions.FirstOrDefault(candidate => string.Equals(candidate.SessionId, sessionId, StringComparison.Ordinal))!;
            return session != null;
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

    private static WpfRuntimeStateChangedEventArgs BuildStateChangedEvent(
        IReadOnlyList<SessionDto> previousSessions,
        IReadOnlyList<PendingActionDto> previousPendingActions,
        IReadOnlyList<SessionDto> sessions,
        IReadOnlyList<PendingActionDto> pendingActions,
        string? realtimeEventType,
        bool initial)
    {
        var change = initial
            ? (SessionId: (string?)null, ActionId: (string?)null, NormalizedEventName: (string?)null, Effect: (SideEffect)new SideEffect.None())
            : DetermineChange(previousSessions, previousPendingActions, sessions, pendingActions);

        return new WpfRuntimeStateChangedEventArgs(
            sessions.Select(MapSession).ToList(),
            pendingActions.Select(MapPendingAction).ToList(),
            change.SessionId,
            change.ActionId,
            change.NormalizedEventName,
            change.Effect,
            realtimeEventType);
    }

    private static (string? SessionId, string? ActionId, string? NormalizedEventName, SideEffect Effect) DetermineChange(
        IReadOnlyList<SessionDto> previousSessions,
        IReadOnlyList<PendingActionDto> previousPendingActions,
        IReadOnlyList<SessionDto> sessions,
        IReadOnlyList<PendingActionDto> pendingActions)
    {
        var previousSessionById = previousSessions.ToDictionary(static session => session.SessionId, StringComparer.Ordinal);
        var sessionById = sessions.ToDictionary(static session => session.SessionId, StringComparer.Ordinal);
        var previousPendingById = previousPendingActions.ToDictionary(static action => action.ActionId, StringComparer.Ordinal);

        var newPending = pendingActions.FirstOrDefault(action => !previousPendingById.ContainsKey(action.ActionId));
        if (newPending?.Permission is { } permission)
            return (newPending.SessionId, newPending.ActionId, null, new SideEffect.ShowApprovalCard(newPending.SessionId, MapPermission(permission)));
        if (newPending?.Question is { } question)
            return (newPending.SessionId, newPending.ActionId, null, new SideEffect.ShowQuestionCard(newPending.SessionId, MapQuestion(question)));

        var changedPending = pendingActions.FirstOrDefault(action =>
            action.Question != null &&
            previousPendingById.TryGetValue(action.ActionId, out var previous) &&
            previous.Question != null &&
            (previous.Question.CurrentQuestionIndex != action.Question.CurrentQuestionIndex ||
             !string.Equals(previous.Question.CurrentAnswerKey, action.Question.CurrentAnswerKey, StringComparison.Ordinal)));
        if (changedPending != null)
            return (changedPending.SessionId, changedPending.ActionId, null, new SideEffect.None());

        var removedSession = previousSessions.FirstOrDefault(session => !sessionById.ContainsKey(session.SessionId));
        if (removedSession != null)
            return (removedSession.SessionId, null, "SessionEnd", new SideEffect.None());

        var newSession = sessions.FirstOrDefault(session => !previousSessionById.ContainsKey(session.SessionId));
        if (newSession != null)
            return (newSession.SessionId, null, "SessionStart", new SideEffect.PlaySound("start"));

        var completed = sessions.FirstOrDefault(session =>
            previousSessionById.TryGetValue(session.SessionId, out var previous) &&
            HasCompletionContent(session) &&
            !HasCompletionContent(previous));
        if (completed != null)
            return (completed.SessionId, null, "Stop", new SideEffect.PlaySound("complete"));

        var changedSession = sessions.FirstOrDefault(session =>
            previousSessionById.TryGetValue(session.SessionId, out var previous) &&
            (previous.LastUpdatedAtUtc != session.LastUpdatedAtUtc ||
             !string.Equals(previous.Status, session.Status, StringComparison.Ordinal)));
        if (changedSession != null)
            return (changedSession.SessionId, null, null, new SideEffect.None());

        return (null, null, null, new SideEffect.None());
    }

    private static bool HasCompletionContent(SessionDto session) =>
        !string.IsNullOrWhiteSpace(session.CompletionText) ||
        !string.IsNullOrWhiteSpace(session.LastAssistantMessage);

    private static SessionSnapshot MapSession(SessionDto dto)
    {
        _ = Enum.TryParse<AgentStatus>(dto.Status, ignoreCase: true, out var status);
        return new SessionSnapshot
        {
            SessionId = dto.SessionId,
            Source = dto.Source,
            SourceDisplayName = dto.SourceDisplayName,
            ProjectName = dto.ProjectName,
            WorkingDirectory = dto.WorkingDirectory,
            Status = status,
            CurrentToolName = dto.CurrentToolName,
            CurrentToolDescription = dto.CurrentToolDescription,
            CreatedAt = dto.CreatedAtUtc.UtcDateTime,
            LastUpdatedAt = dto.LastUpdatedAtUtc.UtcDateTime,
            Pid = dto.TrackedPid ?? 0,
            TrackedProcessStartedAtUtc = dto.TrackedProcessStartedAtUtc?.UtcDateTime,
            LastUserPrompt = dto.LastUserPrompt,
            LastAssistantMessage = dto.LastAssistantMessage,
            CompletionText = dto.CompletionText,
            TranscriptPath = dto.TranscriptPath,
            TranscriptPosition = dto.TranscriptPosition,
            TerminalApp = dto.TerminalApp,
            TerminalSessionId = dto.TerminalSessionId,
            RecentMessages = dto.RecentMessages.Select(MapMessage).ToList(),
            ToolHistory = dto.ToolHistory.Select(MapToolHistory).ToList()
        };
    }

    private static WpfPendingActionSnapshot MapPendingAction(PendingActionDto dto) => new(
        dto.ActionId,
        dto.Kind,
        dto.CreatedAtUtc.UtcDateTime,
        dto.SessionId,
        dto.Source,
        dto.ProjectName,
        dto.WorkingDirectory,
        dto.Permission is { } permission ? MapPermission(permission) : null,
        dto.Question is { } question ? MapQuestion(question) : null,
        dto.Question?.CurrentQuestionIndex ?? 0,
        dto.Question?.CurrentAnswerKey);

    private static PermissionRequest MapPermission(PermissionRequestDto dto) => new()
    {
        SessionId = dto.SessionId,
        ToolName = dto.ToolName,
        ToolUseId = dto.ToolUseId,
        ToolInput = dto.ToolInput?.ToDictionary(static pair => pair.Key, static pair => pair.Value, StringComparer.Ordinal),
        Description = dto.Description,
        HookEventName = dto.HookEventName
    };

    private static QuestionData MapQuestion(QuestionDto dto) => new()
    {
        SessionId = dto.SessionId,
        Id = dto.Id,
        Question = dto.Question,
        Header = dto.Header,
        Options = dto.Options.Select(MapQuestionOption).ToList(),
        MultiSelect = dto.MultiSelect,
        IsMultiQuestion = dto.IsMultiQuestion,
        Questions = dto.Questions.Select(MapQuestionItem).ToList(),
        HookEventName = dto.HookEventName,
        IsAskUserQuestion = dto.IsAskUserQuestion,
        IsCodexRequestUserInput = dto.IsCodexRequestUserInput
    };

    private static QuestionItem MapQuestionItem(QuestionItemDto dto) => new()
    {
        Id = dto.Id,
        Question = dto.Question,
        Header = dto.Header,
        Options = dto.Options.Select(MapQuestionOption).ToList(),
        MultiSelect = dto.MultiSelect,
        AllowFreeText = dto.AllowFreeText
    };

    private static QuestionOption MapQuestionOption(QuestionOptionDto dto) => new()
    {
        Label = dto.Label,
        Description = dto.Description,
        Value = dto.Value
    };

    private static ChatMessage MapMessage(ChatMessageDto dto) => new()
    {
        IsUser = dto.IsUser,
        Text = dto.Text,
        Timestamp = dto.TimestampUtc.UtcDateTime
    };

    private static ToolHistoryEntry MapToolHistory(ToolHistoryEntryDto dto) => new()
    {
        ToolName = dto.ToolName,
        Timestamp = dto.TimestampUtc.UtcDateTime,
        Description = dto.Description,
        Success = dto.Success
    };

    private static T? RunSyncNullable<T>(Func<Task<T?>> action) =>
        action().ConfigureAwait(false).GetAwaiter().GetResult();

    private static T RunSync<T>(Func<Task<T>> action) =>
        action().ConfigureAwait(false).GetAwaiter().GetResult();

    private static string NormalizeBaseUrl(string baseUrl) =>
        string.IsNullOrWhiteSpace(baseUrl) ? "http://127.0.0.1:32145" : baseUrl.Trim().TrimEnd('/');

    private static string Escape(string value) =>
        Uri.EscapeDataString(value);

    private static SourceOperationResultDto SourceOperationFailed(string source, string message) =>
        new(source, Success: false, Installed: false, Message: message);

    private sealed record SuccessResponse(bool Success);

    private sealed record RuntimeAssetsRepairResponse(bool Success, RuntimeAssetsDto Assets);
}
