using CodeIsland.Contracts;

namespace CodeIsland.Hub;

public interface ICodeIslandHubState
{
    IReadOnlyList<SessionDto> GetSessions();
    SessionDto? GetSession(string sessionId);
    IReadOnlyList<ChatMessageDto> GetSessionMessages(string sessionId);
    IReadOnlyList<PendingActionDto> GetPendingActions();
    PendingActionDto? GetPendingAction(string actionId);
    bool DismissSession(string sessionId);
    bool ActivateTerminal(string sessionId);
    bool AllowPermission(string actionId, bool always);
    bool DenyPermission(string actionId, string reason);
    bool AnswerQuestion(string actionId, QuestionAnswerRequest request);
    bool AnswerCurrentQuestion(string actionId, IReadOnlyList<string> answers, out bool resolved);
    bool DismissQuestion(string actionId, string reason);
}

public interface ICodeIslandSourceService
{
    IReadOnlyList<SourceDto> GetSources();
    SourceStatusDto GetSourceStatus(string source);
    SourceOperationResultDto Install(string source);
    SourceOperationResultDto Uninstall(string source);
    SourceOperationResultDto Repair(string source);
    bool RepairAll();
    RuntimeAssetsDto GetRuntimeAssets();
    bool RepairRuntimeAssets();
}

public sealed record CodeIslandApiOptions(
    string Host,
    int Port,
    string Token)
{
    public static CodeIslandApiOptions Localhost(string token, int port = 32145) =>
        new("127.0.0.1", port, token);

    public static CodeIslandApiOptions Bind(string host, string token, int port = 32145) =>
        new(string.IsNullOrWhiteSpace(host) ? "127.0.0.1" : host.Trim(), port, token);

    public string BaseUrl => $"http://{Host}:{Port}";
}
