using System.IO;
using System.IO.Pipes;
using System.Text.Json;
using CodeIsland.Core.IPC;
using CodeIsland.Core.Models;
using CodeIsland.Core.Services;

namespace CodeIsland.Hub;

public sealed class CodeIslandHookServer : IDisposable
{
    private const int AcceptParallelism = 4;
    private readonly CodeIslandHubState _hubState;
    private readonly Func<TimeSpan> _timeoutProvider;
    private readonly EventLogger? _logger;
    private readonly string _pipeName;
    private CancellationTokenSource? _cts;

    public CodeIslandHookServer(CodeIslandHubState hubState, Func<TimeSpan> timeoutProvider, EventLogger? logger = null)
        : this(hubState, timeoutProvider, logger, NamedPipePath.GetPipeName())
    {
    }

    internal CodeIslandHookServer(CodeIslandHubState hubState, Func<TimeSpan> timeoutProvider, EventLogger? logger, string pipeName)
    {
        _hubState = hubState;
        _timeoutProvider = timeoutProvider;
        _logger = logger;
        _pipeName = pipeName;
    }

    public Task StartAsync()
    {
        _cts = new CancellationTokenSource();
        for (var i = 0; i < AcceptParallelism; i++)
            _ = AcceptLoopAsync(_cts.Token);
        return Task.CompletedTask;
    }

    public void Dispose()
    {
        _cts?.Cancel();
        _cts?.Dispose();
    }

    private async Task AcceptLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            NamedPipeServerStream? pipe = null;
            try
            {
                pipe = new NamedPipeServerStream(_pipeName, PipeDirection.InOut, NamedPipeServerStream.MaxAllowedServerInstances, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
                await pipe.WaitForConnectionAsync(ct);
                var accepted = pipe;
                pipe = null;
                _ = HandleConnectionAsync(accepted, ct);
            }
            catch (OperationCanceledException)
            {
                pipe?.Dispose();
                break;
            }
            catch (Exception ex)
            {
                pipe?.Dispose();
                _logger?.Write("CodeIslandHookServer", "accept-error", new Dictionary<string, string?> { ["message"] = ex.Message, ["exception"] = ex.GetType().Name });
            }
        }
    }

    private async Task HandleConnectionAsync(NamedPipeServerStream pipe, CancellationToken ct)
    {
        try
        {
            var json = await MessageProtocol.ReadMessageAsync(pipe, ct);
            if (string.IsNullOrWhiteSpace(json))
            {
                await TryWriteResponseAsync(pipe, "{}", ct);
                return;
            }

            HookEvent? evt = null;
            try
            {
                using var doc = JsonDocument.Parse(json);
                evt = HookEvent.FromJson(doc.RootElement);
            }
            catch (JsonException ex)
            {
                _logger?.Write("CodeIslandHookServer", "parse-error", new Dictionary<string, string?> { ["message"] = ex.Message });
            }

            if (evt == null)
            {
                await TryWriteResponseAsync(pipe, "{}", ct);
                return;
            }

            var blocking = IsBlocking(evt);
            string response;
            if (blocking)
            {
                response = await _hubState.HandleBlockingEventAsync(evt, _timeoutProvider(), ct);
                await TryWriteResponseAsync(pipe, response, ct);
            }
            else
            {
                response = "{}";
                await TryWriteResponseAsync(pipe, response, ct);
                try { _hubState.HandleEvent(evt); }
                catch (Exception ex) { _logger?.Write(nameof(CodeIslandHubState), "handle-event-error", new Dictionary<string, string?> { ["message"] = ex.Message, ["exception"] = ex.GetType().Name }); }
            }

            LogHookResponse(evt, blocking, response);
        }
        catch (Exception ex)
        {
            _logger?.Write("CodeIslandHookServer", "handle-error", new Dictionary<string, string?> { ["message"] = ex.Message, ["exception"] = ex.GetType().Name });
        }
        finally
        {
            await pipe.DisposeAsync();
        }
    }

    private static bool IsBlocking(HookEvent evt)
    {
        var name = EventNormalizer.NormalizeEventName(evt.Source ?? "unknown", evt.EventName);
        if (name == "PermissionRequest") return true;
        if (name == "PreToolUse" && HookToolClassifier.ShouldBlockQuestionTool(evt, name)) return true;
        if ((name == "Notification" || name.StartsWith("Question", StringComparison.OrdinalIgnoreCase)) &&
            (ContainsAny(evt.RawJson, "question", "questions") || ContainsAny(evt.ToolInput, "question", "questions"))) return true;
        return name == "PreToolUse" && (HasApprovalSignal(evt.RawJson) || HasApprovalSignal(evt.ToolInput));
    }

    private static bool ContainsAny(JsonElement? element, params string[] names)
    {
        if (element is not { ValueKind: JsonValueKind.Object } obj) return false;
        foreach (var property in obj.EnumerateObject())
        {
            if (names.Any(n => string.Equals(n, property.Name, StringComparison.OrdinalIgnoreCase))) return true;
            if (property.Value.ValueKind == JsonValueKind.Object && ContainsAny(property.Value, names)) return true;
        }
        return false;
    }

    private static bool HasApprovalSignal(JsonElement? element)
    {
        if (element is not { ValueKind: JsonValueKind.Object } obj) return false;
        foreach (var property in obj.EnumerateObject())
        {
            if (IsApprovalKey(property.Name) && IsTruthy(property.Value)) return true;
            if (property.Value.ValueKind == JsonValueKind.Object && HasApprovalSignal(property.Value)) return true;
        }
        return false;
    }

    private static bool IsApprovalKey(string key) => key is "permission_request" or "permissionRequest" or "requires_approval" or "requiresApproval" or "approval_required" or "approvalRequired";

    private static bool IsTruthy(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.True => true,
        JsonValueKind.False or JsonValueKind.Null or JsonValueKind.Undefined => false,
        JsonValueKind.String => value.GetString() is { } s && !string.Equals(s, "false", StringComparison.OrdinalIgnoreCase) && s != "0" && !string.IsNullOrWhiteSpace(s),
        JsonValueKind.Number => value.TryGetInt32(out var i) ? i != 0 : true,
        _ => true
    };

    private async Task TryWriteResponseAsync(NamedPipeServerStream pipe, string response, CancellationToken ct)
    {
        try { await MessageProtocol.WriteMessageAsync(pipe, response, ct); }
        catch (IOException ex) { _logger?.Write("CodeIslandHookServer", "write-response-skip", new Dictionary<string, string?> { ["message"] = ex.Message }); }
        catch (OperationCanceledException) { }
    }

    private void LogHookResponse(HookEvent evt, bool blocking, string response)
    {
        _logger?.Write("CodeIslandHookServer", "response", new Dictionary<string, string?>
        {
            ["event"] = EventNormalizer.NormalizeEventName(evt.Source ?? "unknown", evt.EventName),
            ["tool"] = HookToolClassifier.GetToolName(evt),
            ["blocking"] = blocking.ToString(),
            ["response_type"] = HookResponseDiagnostics.GetResponseType(response),
            ["response_len"] = response.Length.ToString()
        });
    }
}
