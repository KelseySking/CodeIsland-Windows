using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace CodeIsland.Core.Services;

/// <summary>
/// 国际化服务，支持 5 种语言
/// </summary>
public sealed class L10n : INotifyPropertyChanged
{
    private static readonly Lazy<L10n> _instance = new(() => new L10n());
    public static L10n Instance => _instance.Value;

    private string _language = "zh";
    public string Language
    {
        get => _language;
        set
        {
            if (_language == value) return;
            _language = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(Current));
        }
    }

    /// <summary>
    /// 解析后的实际语言
    /// </summary>
    public string EffectiveLanguage => _language == "system"
        ? System.Globalization.CultureInfo.CurrentUICulture.TwoLetterISOLanguageName
        : _language;

    /// <summary>
    /// 当前语言的翻译字典
    /// </summary>
    public IReadOnlyDictionary<string, string> Current =>
        Translations.TryGetValue(EffectiveLanguage, out var dict) ? dict : Translations["en"];

    /// <summary>
    /// 索引器访问翻译
    /// </summary>
    public string this[string key] => Current.TryGetValue(key, out var value) ? value : key;

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    private static readonly Dictionary<string, Dictionary<string, string>> Translations = new()
    {
        ["en"] = new()
        {
            ["app.name"] = "CodeIsland",
            ["panel.noSessions"] = "No active sessions",
            ["panel.sessionCount"] = "{0} active sessions",
            ["panel.oneSession"] = "1 active session",
            ["approval.title"] = "Permission Request",
            ["approval.deny"] = "DENY",
            ["approval.dismiss"] = "DISMISS",
            ["approval.allowOnce"] = "ALLOW ONCE",
            ["approval.alwaysAllow"] = "ALWAYS ALLOW",
            ["question.skip"] = "SKIP",
            ["question.submit"] = "SUBMIT",
            ["settings.title"] = "Settings",
            ["settings.general"] = "General",
            ["settings.behavior"] = "Behavior",
            ["settings.appearance"] = "Appearance",
            ["settings.mascots"] = "Mascots",
            ["settings.sound"] = "Sound",
            ["settings.hooks"] = "Tool Connections",
            ["settings.about"] = "About",
            ["settings.language"] = "Language",
            ["settings.launchAtLogin"] = "Launch at login",
            ["settings.autoApprove"] = "Auto-approve safe tools",
            ["settings.soundEnabled"] = "Enable sound effects",
            ["settings.volume"] = "Volume",
            ["tray.tooltip"] = "CodeIsland",
            ["tray.show"] = "Show Panel",
            ["tray.settings"] = "Settings",
            ["tray.quit"] = "Quit",
            ["status.idle"] = "Idle",
            ["status.processing"] = "Processing",
            ["status.running"] = "Running",
            ["status.waitingApproval"] = "Waiting for approval",
            ["status.waitingQuestion"] = "Waiting for answer",
        },
        ["zh"] = new()
        {
            ["app.name"] = "CodeIsland",
            ["panel.noSessions"] = "没有活跃会话",
            ["panel.sessionCount"] = "{0} 个活跃会话",
            ["panel.oneSession"] = "1 个活跃会话",
            ["approval.title"] = "权限请求",
            ["approval.deny"] = "拒绝",
            ["approval.dismiss"] = "忽略",
            ["approval.allowOnce"] = "允许一次",
            ["approval.alwaysAllow"] = "始终允许",
            ["question.skip"] = "跳过",
            ["question.submit"] = "提交",
            ["settings.title"] = "设置",
            ["settings.general"] = "通用",
            ["settings.behavior"] = "行为",
            ["settings.appearance"] = "外观",
            ["settings.mascots"] = "吉祥物",
            ["settings.sound"] = "音效",
            ["settings.hooks"] = "工具连接",
            ["settings.about"] = "关于",
            ["settings.language"] = "语言",
            ["settings.launchAtLogin"] = "开机自启",
            ["settings.autoApprove"] = "自动审批安全工具",
            ["settings.soundEnabled"] = "启用音效",
            ["settings.volume"] = "音量",
            ["tray.tooltip"] = "CodeIsland",
            ["tray.show"] = "显示面板",
            ["tray.settings"] = "设置",
            ["tray.quit"] = "退出",
            ["status.idle"] = "空闲",
            ["status.processing"] = "处理中",
            ["status.running"] = "运行中",
            ["status.waitingApproval"] = "等待审批",
            ["status.waitingQuestion"] = "等待回答",
        },
        ["ja"] = new()
        {
            ["app.name"] = "CodeIsland",
            ["panel.noSessions"] = "アクティブなセッションなし",
            ["approval.title"] = "権限リクエスト",
            ["approval.deny"] = "拒否",
            ["approval.allowOnce"] = "1回許可",
            ["approval.alwaysAllow"] = "常に許可",
            ["tray.show"] = "パネルを表示",
            ["tray.settings"] = "設定",
            ["tray.quit"] = "終了",
        },
        ["ko"] = new()
        {
            ["app.name"] = "CodeIsland",
            ["panel.noSessions"] = "활성 세션 없음",
            ["approval.title"] = "권한 요청",
            ["approval.deny"] = "거부",
            ["approval.allowOnce"] = "한번 허용",
            ["approval.alwaysAllow"] = "항상 허용",
            ["tray.show"] = "패널 표시",
            ["tray.settings"] = "설정",
            ["tray.quit"] = "종료",
        },
        ["tr"] = new()
        {
            ["app.name"] = "CodeIsland",
            ["panel.noSessions"] = "Aktif oturum yok",
            ["approval.title"] = "İzin İsteği",
            ["approval.deny"] = "REDDET",
            ["approval.allowOnce"] = "BİR KEZ İZİN VER",
            ["approval.alwaysAllow"] = "HER ZAMAN İZİN VER",
            ["tray.show"] = "Paneli Göster",
            ["tray.settings"] = "Ayarlar",
            ["tray.quit"] = "Çıkış",
        }
    };
}
