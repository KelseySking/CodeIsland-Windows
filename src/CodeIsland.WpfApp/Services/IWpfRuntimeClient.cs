using CodeIsland.Hub;

namespace CodeIsland.WpfApp.Services;

public interface IWpfRuntimeClient : IDisposable
{
    event EventHandler<HubStateChangedEventArgs>? StateChanged;

    Task StartAsync(CancellationToken ct = default);

    Task<bool> AllowPermissionAsync(string actionId, bool always, CancellationToken ct = default);

    Task<bool> DenyPermissionAsync(string actionId, string reason, CancellationToken ct = default);

    Task<QuestionCurrentAnswerResult> AnswerCurrentQuestionAsync(string actionId, IReadOnlyList<string> answers, CancellationToken ct = default);

    Task<bool> DismissQuestionAsync(string actionId, string reason, CancellationToken ct = default);

    Task<bool> ActivateTerminalAsync(string sessionId, CancellationToken ct = default);
}

public sealed record QuestionCurrentAnswerResult(bool Success, bool Resolved);
