namespace CodeIsland.Core.Models;

/// <summary>
/// AI 代理的运行状态
/// </summary>
public enum AgentStatus
{
    Idle,
    Processing,
    Running,
    WaitingQuestion,
    WaitingApproval,
    Completed,
    Error
}
