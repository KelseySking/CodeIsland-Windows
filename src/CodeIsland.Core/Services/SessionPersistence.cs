using System.Text.Json;
using CodeIsland.Core.Models;

namespace CodeIsland.Core.Services;

/// <summary>
/// 会话持久化，保存/加载会话状态到 JSON 文件
/// </summary>
public class SessionPersistence
{
    private readonly string _persistencePath;

    public SessionPersistence(string? dataDir = null)
    {
        var dir = dataDir ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "CodeIsland");
        Directory.CreateDirectory(dir);
        _persistencePath = Path.Combine(dir, "sessions.json");
    }

    public async Task SaveAsync(Dictionary<string, SessionSnapshot> sessions)
    {
        var data = sessions.ToDictionary(
            kvp => kvp.Key,
            kvp => new PersistedSession
            {
                SessionId = kvp.Value.SessionId,
                Source = kvp.Value.Source,
                ProjectName = kvp.Value.ProjectName,
                Status = kvp.Value.Status.ToString(),
                CreatedAt = kvp.Value.CreatedAt,
                LastUpdatedAt = kvp.Value.LastUpdatedAt
            });

        var json = JsonSerializer.Serialize(data, new JsonSerializerOptions { WriteIndented = true });
        await File.WriteAllTextAsync(_persistencePath, json);
    }

    public async Task<Dictionary<string, SessionSnapshot>> LoadAsync()
    {
        if (!File.Exists(_persistencePath))
            return new Dictionary<string, SessionSnapshot>();

        try
        {
            var json = await File.ReadAllTextAsync(_persistencePath);
            var data = JsonSerializer.Deserialize<Dictionary<string, PersistedSession>>(json);
            if (data == null) return new Dictionary<string, SessionSnapshot>();

            return data.ToDictionary(
                kvp => kvp.Key,
                kvp => new SessionSnapshot
                {
                    SessionId = kvp.Value.SessionId,
                    Source = kvp.Value.Source,
                    ProjectName = kvp.Value.ProjectName,
                    Status = Enum.TryParse<AgentStatus>(kvp.Value.Status, out var s) ? s : AgentStatus.Idle,
                    CreatedAt = kvp.Value.CreatedAt,
                    LastUpdatedAt = kvp.Value.LastUpdatedAt
                });
        }
        catch
        {
            return new Dictionary<string, SessionSnapshot>();
        }
    }

    private class PersistedSession
    {
        public string SessionId { get; set; } = "";
        public string Source { get; set; } = "";
        public string? ProjectName { get; set; }
        public string Status { get; set; } = "Idle";
        public DateTime CreatedAt { get; set; }
        public DateTime LastUpdatedAt { get; set; }
    }
}
