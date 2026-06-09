using System.Text.Json;
using CodeIsland.Bridge;
using CodeIsland.Core.IPC;
using CodeIsland.Core.Services;

var logger = new EventLogger();
logger.Write("Bridge", "start", new Dictionary<string, string?>
{
    ["pid"] = Environment.ProcessId.ToString(),
    ["args"] = string.Join(" ", args)
});

// 1. 从 stdin 读取 JSON
string input;
try
{
    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
    using var reader = new StreamReader(Console.OpenStandardInput());
    input = await reader.ReadToEndAsync(cts.Token);
}
catch (OperationCanceledException)
{
    logger.Write("Bridge", "exit-stdin-timeout", null);
    Environment.Exit(0);
    return;
}

if (string.IsNullOrWhiteSpace(input))
{
    logger.Write("Bridge", "exit-empty-stdin", null);
    Environment.Exit(0);
}

// 2. 解析原始 JSON
JsonDocument doc;
try
{
    doc = JsonDocument.Parse(input);
}
catch (Exception ex)
{
    logger.Write("Bridge", "exit-json-parse-error", new Dictionary<string, string?>
    {
        ["message"] = ex.Message,
        ["len"] = input.Length.ToString()
    });
    Environment.Exit(1);
    return;
}

using (doc)
{
    var root = doc.RootElement;

    // 3. 识别来源
    var explicitSource = ParseArgsForSource(args);
    var parentPid = ProcessAncestry.GetParentPid();
    var ancestry = ProcessAncestry.BuildAncestry(parentPid);
    var terminalEnvironment = EnvironmentCollector.Collect();
    var source = SourceResolver.InferSource(ancestry, explicitSource, root);
    var trackedProcess = TrackedProcessResolver.Resolve(ancestry, parentPid, terminalEnvironment);

    var rawEventName = root.TryGetProperty("hook_event_name", out var en) ? en.GetString() :
                       root.TryGetProperty("hookEventName", out var en2) ? en2.GetString() : null;
    var sessionId = root.TryGetProperty("session_id", out var sid) ? sid.GetString() :
                    root.TryGetProperty("sessionId", out var sid2) ? sid2.GetString() : null;

    // 4. 构建富化 payload
    var payload = new Dictionary<string, object?>();

    // 复制原始 JSON 的所有字段
    foreach (var prop in root.EnumerateObject())
    {
        payload[prop.Name] = prop.Value.ValueKind == JsonValueKind.String
            ? prop.Value.GetString()
            : prop.Value.Clone();
    }

    // 注入 Bridge 元数据
    payload["_source"] = source;
    payload["_ppid"] = parentPid;
    payload["_hook_ppid"] = Environment.ProcessId;
    payload["_tracked_pid"] = trackedProcess.Pid;
    payload["_tracked_pid_kind"] = trackedProcess.Kind;
    if (trackedProcess.StartedAtUtc is { } trackedProcessStartedAt)
        payload["_tracked_process_started_at_utc"] = trackedProcessStartedAt.ToString("O");

    // 注入终端环境变量
    EnvironmentCollector.InjectIntoPayload(payload, terminalEnvironment);

    // 标准化字段名
    NormalizeFieldNames(payload);

    // 5. 通过 Named Pipe 发送到主应用
    var enrichedJson = BridgePayloadSerializer.Serialize(payload);
    var blocking = BridgeEventClassifier.IsBlockingEvent(payload);
    var normalizedEventName = EventNormalizer.NormalizeEventName(source, rawEventName ?? "");
    var toolName = HookToolClassifier.GetToolName(payload);

    logger.Write("Bridge", "sending", new Dictionary<string, string?>
    {
        ["source"] = source,
        ["raw_event"] = rawEventName,
        ["event"] = normalizedEventName,
        ["tool"] = toolName,
        ["session_id"] = sessionId,
        ["blocking"] = blocking.ToString(),
        ["payload_len"] = enrichedJson.Length.ToString()
    });

    try
    {
        var response = await BridgeClient.SendAsync(enrichedJson, blocking);

        logger.Write("Bridge", "sent-ok", new Dictionary<string, string?>
        {
            ["raw_event"] = rawEventName,
            ["event"] = normalizedEventName,
            ["tool"] = toolName,
            ["blocking"] = blocking.ToString(),
            ["response_type"] = HookResponseDiagnostics.GetResponseType(response),
            ["response_len"] = response.Length.ToString()
        });

        // 6. 将响应写回 stdout
        Console.Write(response);
    }
    catch (Exception ex)
    {
        logger.Write("Bridge", "send-failed", new Dictionary<string, string?>
        {
            ["raw_event"] = rawEventName,
            ["exception"] = ex.GetType().Name,
            ["message"] = ex.Message
        });
        // 连接失败时静默退出。CodeIsland 是状态观察 HUD，主应用未运行/不可达时
        // 不应让宿主 CLI 的 hook 失败（Codex 会把非零退出码显示为 "hook (failed)"），因此退出码用 0。
        Environment.Exit(0);
    }
}

/// <summary>
/// 从命令行参数中提取 --source
/// </summary>
static string? ParseArgsForSource(string[] args)
{
    for (int i = 0; i < args.Length - 1; i++)
    {
        if (args[i] == "--source" && !string.IsNullOrEmpty(args[i + 1]))
            return args[i + 1];
    }
    return null;
}

/// <summary>
/// 标准化字段名（兼容不同 CLI 的命名风格）
/// </summary>
static void NormalizeFieldNames(Dictionary<string, object?> payload)
{
    // 事件名标准化
    if (payload.TryGetValue("hookEventName", out var v) && !payload.ContainsKey("hook_event_name"))
        payload["hook_event_name"] = v;
    if (payload.TryGetValue("eventName", out v) && !payload.ContainsKey("hook_event_name"))
        payload["hook_event_name"] = v;
    if (payload.TryGetValue("event", out v) && !payload.ContainsKey("hook_event_name"))
        payload["hook_event_name"] = v;

    // 会话 ID 标准化
    if (payload.TryGetValue("sessionId", out v) && !payload.ContainsKey("session_id"))
        payload["session_id"] = v;

    // Copilot 特殊处理
    if (payload.TryGetValue("toolName", out v) && !payload.ContainsKey("tool_name"))
        payload["tool_name"] = v;
}
