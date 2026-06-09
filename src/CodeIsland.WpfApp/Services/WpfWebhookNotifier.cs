using System.Net.Http;
using System.Net.Http.Json;
using CodeIsland.Core.Models;
using CodeIsland.Core.Services;

namespace CodeIsland.WpfApp.Services;

public sealed class WpfWebhookNotifier : IDisposable
{
    private readonly SettingsManager _settings;
    private readonly HttpClient _httpClient;
    private readonly bool _ownsClient;

    public WpfWebhookNotifier(SettingsManager settings, HttpClient? httpClient = null)
    {
        _settings = settings;
        _httpClient = httpClient ?? new HttpClient { Timeout = TimeSpan.FromSeconds(3) };
        _ownsClient = httpClient == null;
    }

    public void NotifySessionChanged(SessionSnapshot snapshot)
    {
        FireAndForget(new
        {
            type = "session_status_changed",
            sessionId = snapshot.SessionId,
            source = snapshot.Source,
            status = snapshot.Status.ToString(),
            projectName = snapshot.ProjectName,
            updatedAt = snapshot.LastUpdatedAt
        });
    }

    public void NotifyApproval(PermissionRequest request)
    {
        FireAndForget(new
        {
            type = "permission_approval_requested",
            sessionId = request.SessionId,
            toolName = request.ToolName,
            toolUseId = request.ToolUseId,
            description = request.Description
        });
    }

    public void NotifyQuestion(QuestionData question)
    {
        FireAndForget(new
        {
            type = "question_requested",
            sessionId = question.SessionId,
            question = question.Question,
            options = question.Options?.Select(static option => new { option.Label, option.Value }).ToArray()
        });
    }

    public static bool TryNormalizeWebhookUri(string? url, out Uri uri)
    {
        if (Uri.TryCreate(url, UriKind.Absolute, out uri!) && uri.Scheme is "http" or "https")
            return true;

        uri = null!;
        return false;
    }

    private void FireAndForget(object payload)
    {
        var url = _settings.Get("webhook_url", "");
        if (!TryNormalizeWebhookUri(url, out var uri))
            return;

        _ = Task.Run(async () =>
        {
            try
            {
                using var response = await _httpClient.PostAsJsonAsync(uri, payload).ConfigureAwait(false);
                if (!response.IsSuccessStatusCode)
                    System.Diagnostics.Debug.WriteLine($"[WpfWebhook] 投递失败：HTTP {(int)response.StatusCode}");
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[WpfWebhook] 投递失败：{ex.Message}");
            }
        });
    }

    public void Dispose()
    {
        if (_ownsClient)
            _httpClient.Dispose();
    }
}
