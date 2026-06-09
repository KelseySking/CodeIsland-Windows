using System.IO.Pipes;
using System.Security.Principal;
using CodeIsland.Core.IPC;

namespace CodeIsland.Bridge;

/// <summary>
/// Named Pipe 客户端，连接到 CodeIsland 主应用
/// </summary>
public static class BridgeClient
{
    /// <summary>
    /// 发送消息到主应用并等待响应。
    /// 即使 non-blocking 事件也读响应：主应用现在总是写一个 ack（"{}"），
    /// 这样 client 在 dispose pipe 之前能确认 server 已收到，避免 server 端 Pipe is broken。
    /// </summary>
    public static async Task<string> SendAsync(string enrichedJson, bool blocking, CancellationToken ct = default)
    {
        var pipeName = NamedPipePath.GetPipeName();
        var connectTimeoutMs = blocking ? 3000 : 1500;

        using var pipe = new NamedPipeClientStream(
            serverName: ".",
            pipeName: pipeName,
            direction: PipeDirection.InOut,
            options: PipeOptions.Asynchronous,
            impersonationLevel: TokenImpersonationLevel.Anonymous);

        await pipe.ConnectAsync(connectTimeoutMs, ct);

        await MessageProtocol.WriteMessageAsync(pipe, enrichedJson, ct);

        if (blocking)
        {
            // 阻塞事件：让 server 决定何时返回（用户操作/AppState 超时）。
            // Claude Code 的 PermissionRequest hook 自身允许 86400s，
            // 这里不能加更短的客户端 read timeout，否则用户稍慢的审批会被静默 dismiss。
            var response = await MessageProtocol.ReadMessageAsync(pipe, ct);
            return response ?? "{}";
        }

        // 非阻塞事件：等 server 的 ack（"{}"），用短超时避免 client dispose 时 server 还在写导致 pipe broken。
        using var readCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        readCts.CancelAfter(3000);
        try
        {
            var response = await MessageProtocol.ReadMessageAsync(pipe, readCts.Token);
            return response ?? "{}";
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return "{}";
        }
    }
}
