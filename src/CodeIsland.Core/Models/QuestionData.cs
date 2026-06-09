namespace CodeIsland.Core.Models;

/// <summary>
/// AI 工具提出的问题
/// </summary>
public class QuestionData
{
    public string SessionId { get; init; } = "";
    public string? Id { get; init; }
    public string Question { get; init; } = "";
    public string? Header { get; init; }
    public List<QuestionOption>? Options { get; init; }
    public bool MultiSelect { get; init; }
    public bool IsMultiQuestion { get; init; }
    public List<QuestionItem>? Questions { get; init; }
    public string HookEventName { get; init; } = "";
    public bool IsAskUserQuestion { get; init; }
    public bool IsCodexRequestUserInput { get; init; }
    public System.Text.Json.JsonElement? OriginalInput { get; init; }
}

public class QuestionOption
{
    public string Label { get; init; } = "";
    public string? Description { get; init; }
    public string? Value { get; init; }
}

public class QuestionItem
{
    public string? Id { get; init; }
    public string Question { get; init; } = "";
    public string? Header { get; init; }
    public List<QuestionOption>? Options { get; init; }
    public bool MultiSelect { get; init; }
    public bool AllowFreeText { get; init; }
}
