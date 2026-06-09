namespace CodeIsland.Core.Models;

public class ChatMessage
{
    public bool IsUser { get; init; }
    public string Text { get; init; } = "";
    public DateTime Timestamp { get; init; } = DateTime.UtcNow;
}
