using CodeIsland.Contracts;
using CodeIsland.Core.Services;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace CodeIsland.Hub;

public sealed class CodeIslandApiHost : IAsyncDisposable, IDisposable
{
    private static readonly DateTimeOffset StartedAtUtc = DateTimeOffset.UtcNow;
    private readonly CodeIslandApiOptions _options;
    private readonly ICodeIslandHubState _state;
    private readonly ICodeIslandSourceService _sources;
    private readonly EventLogger? _logger;
    private WebApplication? _app;

    public CodeIslandApiHost(
        CodeIslandApiOptions options,
        ICodeIslandHubState state,
        ICodeIslandSourceService sources,
        EventLogger? logger = null,
        CodeIslandRealtimeHub? realtime = null)
    {
        _options = options;
        _state = state;
        _sources = sources;
        _logger = logger;
        Realtime = realtime ?? new CodeIslandRealtimeHub(logger);
    }

    public CodeIslandRealtimeHub Realtime { get; }
    public string BaseUrl => _options.BaseUrl;

    public async Task StartAsync(CancellationToken ct = default)
    {
        if (_app != null)
            return;

        var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions
        {
            Args = [],
            ContentRootPath = AppContext.BaseDirectory
        });
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls(_options.BaseUrl);

        var app = builder.Build();
        app.UseWebSockets();
        app.Use(AuthorizeRequestAsync);
        MapRoutes(app);

        await app.StartAsync(ct);
        _app = app;
        _logger?.Write("CodeIslandApiHost", "started", new Dictionary<string, string?>
        {
            ["url"] = _options.BaseUrl
        });
    }

    private async Task AuthorizeRequestAsync(HttpContext context, Func<Task> next)
    {
        if (context.Request.Path.Equals("/api/health", StringComparison.OrdinalIgnoreCase))
        {
            await next();
            return;
        }

        if (IsAuthorized(context))
        {
            await next();
            return;
        }

        context.Response.StatusCode = StatusCodes.Status401Unauthorized;
        await context.Response.WriteAsJsonAsync(new ApiErrorDto("unauthorized", "Missing or invalid CodeIsland API token"));
    }

    private bool IsAuthorized(HttpContext context)
    {
        if (string.IsNullOrWhiteSpace(_options.Token))
            return false;

        if (context.Request.Headers.Authorization.ToString() is { Length: > 7 } authorization &&
            authorization.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) &&
            string.Equals(authorization[7..], _options.Token, StringComparison.Ordinal))
        {
            return true;
        }

        if (context.Request.Headers.TryGetValue("X-CodeIsland-Token", out var headerToken) &&
            string.Equals(headerToken.ToString(), _options.Token, StringComparison.Ordinal))
        {
            return true;
        }

        return context.Request.Query.TryGetValue("token", out var queryToken) &&
               string.Equals(queryToken.ToString(), _options.Token, StringComparison.Ordinal);
    }

    private void MapRoutes(WebApplication app)
    {
        var api = app.MapGroup("/api");

        api.MapGet("/health", () => new ApiHealthDto("ok", StartedAtUtc));
        api.MapGet("/version", () => new ApiVersionDto("CodeIsland Runtime", typeof(CodeIslandApiHost).Assembly.GetName().Version?.ToString() ?? "unknown"));
        api.MapGet("/capabilities", () => new ApiCapabilitiesDto(
            HookInjection: true,
            Approval: true,
            Question: true,
            Transcript: true,
            Realtime: true,
            RealtimeProtocols: ["websocket"],
            SecurityMode: IsLoopbackHost(_options.Host) ? "localhost-token" : "remote-token"));

        api.MapGet("/sources", _sources.GetSources);
        api.MapGet("/sources/{source}", (string source) => _sources.GetSourceStatus(source));
        api.MapGet("/sources/{source}/status", (string source) => _sources.GetSourceStatus(source));
        api.MapPost("/sources/{source}/install", async (string source) => await RunSourceOperationAsync("source.statusChanged", () => _sources.Install(source)));
        api.MapPost("/sources/{source}/uninstall", async (string source) => await RunSourceOperationAsync("source.statusChanged", () => _sources.Uninstall(source)));
        api.MapPost("/sources/{source}/repair", async (string source) => await RunSourceOperationAsync("source.statusChanged", () => _sources.Repair(source)));
        api.MapPost("/sources/repair-all", async () =>
        {
            var success = _sources.RepairAll();
            await Realtime.PublishAsync("source.statusChanged", _sources.GetSources());
            return Results.Ok(new { success });
        });

        api.MapGet("/runtime-assets", _sources.GetRuntimeAssets);
        api.MapPost("/runtime-assets/repair", async () =>
        {
            var success = _sources.RepairRuntimeAssets();
            var assets = _sources.GetRuntimeAssets();
            await Realtime.PublishAsync("source.statusChanged", _sources.GetSources());
            return Results.Ok(new { success, assets });
        });

        api.MapGet("/sessions", _state.GetSessions);
        api.MapGet("/sessions/{sessionId}", (string sessionId) => ToResult(_state.GetSession(sessionId)));
        api.MapGet("/sessions/{sessionId}/messages", (string sessionId) => Results.Ok(_state.GetSessionMessages(sessionId)));
        api.MapPost("/sessions/{sessionId}/dismiss", (string sessionId) =>
        {
            var success = _state.DismissSession(sessionId);
            return success ? Results.Ok(new { success }) : Results.NotFound(new ApiErrorDto("not_found", "Session not found"));
        });
        api.MapPost("/sessions/{sessionId}/activate-terminal", (string sessionId) =>
            _state.ActivateTerminal(sessionId)
                ? Results.Ok(new { success = true })
                : Results.NotFound(new ApiErrorDto("not_found", "Session not found")));

        api.MapGet("/pending", _state.GetPendingActions);
        api.MapGet("/pending/{actionId}", (string actionId) => ToResult(_state.GetPendingAction(actionId)));
        api.MapPost("/permissions/{actionId}/allow", async (string actionId, HttpContext context) =>
        {
            var request = await ReadBodyAsync<PermissionDecisionRequest>(context) ?? new PermissionDecisionRequest();
            return await RunPendingOperationAsync(actionId, () => _state.AllowPermission(actionId, request.Always));
        });
        api.MapPost("/permissions/{actionId}/deny", async (string actionId, HttpContext context) =>
        {
            var request = await ReadBodyAsync<PermissionDecisionRequest>(context) ?? new PermissionDecisionRequest();
            return await RunPendingOperationAsync(actionId, () => _state.DenyPermission(actionId, request.Reason ?? "user denied"));
        });
        api.MapPost("/questions/{actionId}/answer", async (string actionId, HttpContext context) =>
        {
            var request = await ReadBodyAsync<QuestionAnswerRequest>(context) ?? new QuestionAnswerRequest();
            return await RunPendingOperationAsync(actionId, () => _state.AnswerQuestion(actionId, request));
        });
        api.MapPost("/questions/{actionId}/answer-current", async (string actionId, HttpContext context) =>
        {
            var request = await ReadBodyAsync<QuestionCurrentAnswerRequest>(context) ?? new QuestionCurrentAnswerRequest([]);
            return RunCurrentQuestionOperation(actionId, request.Answers ?? []);
        });
        api.MapPost("/questions/{actionId}/dismiss", async (string actionId) =>
            await RunPendingOperationAsync(actionId, () => _state.DismissQuestion(actionId, "dismissed")));

        api.Map("/events", async context =>
        {
            if (!context.WebSockets.IsWebSocketRequest)
            {
                context.Response.StatusCode = StatusCodes.Status400BadRequest;
                await context.Response.WriteAsJsonAsync(new ApiErrorDto("websocket_required", "Use WebSocket for /api/events"));
                return;
            }

            using var socket = await context.WebSockets.AcceptWebSocketAsync();
            await Realtime.AcceptAsync(socket, context.RequestAborted);
        });
    }

    private async Task<IResult> RunSourceOperationAsync(string eventType, Func<SourceOperationResultDto> operation)
    {
        var result = operation();
        await Realtime.PublishAsync(eventType, result);
        return result.Success ? Results.Ok(result) : Results.BadRequest(result);
    }

    private Task<IResult> RunPendingOperationAsync(string actionId, Func<bool> operation)
    {
        var success = operation();
        if (!success)
            return Task.FromResult<IResult>(Results.NotFound(new ApiErrorDto("not_found", "Pending action not found")));

        return Task.FromResult<IResult>(Results.Ok(new { success }));
    }

    private IResult RunCurrentQuestionOperation(string actionId, IReadOnlyList<string> answers)
    {
        if (!_state.AnswerCurrentQuestion(actionId, answers, out var resolved))
            return Results.NotFound(new ApiErrorDto("not_found", "Pending action not found"));

        return Results.Ok(new QuestionCurrentAnswerResultDto(Success: true, Resolved: resolved));
    }

    private static IResult ToResult<T>(T? value) where T : class =>
        value == null ? Results.NotFound(new ApiErrorDto("not_found", "Resource not found")) : Results.Ok(value);

    private static async Task<T?> ReadBodyAsync<T>(HttpContext context)
    {
        if (context.Request.ContentLength == 0)
            return default;

        try
        {
            return await context.Request.ReadFromJsonAsync<T>();
        }
        catch
        {
            return default;
        }
    }

    private static bool IsLoopbackHost(string host) =>
        host.Equals("127.0.0.1", StringComparison.OrdinalIgnoreCase) ||
        host.Equals("localhost", StringComparison.OrdinalIgnoreCase) ||
        host.Equals("::1", StringComparison.OrdinalIgnoreCase);

    public async ValueTask DisposeAsync()
    {
        if (_app != null)
            await _app.DisposeAsync();
    }

    public void Dispose()
    {
        _app?.DisposeAsync().AsTask().GetAwaiter().GetResult();
    }
}
