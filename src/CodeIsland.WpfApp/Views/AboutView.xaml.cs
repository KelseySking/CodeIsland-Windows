using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Windows.Controls;
using System.Windows.Input;
using CodeIsland.WpfApp.Services;
using CodeIsland.WpfApp.ViewModels;
using UserControl = System.Windows.Controls.UserControl;

namespace CodeIsland.WpfApp.Views;

public partial class AboutView : UserControl, INotifyPropertyChanged
{
    private const string GitHubRepositoryUrl = "https://github.com/KelseySking/CodeIsland-Windows";
    private const string LatestReleaseUrl = "https://github.com/KelseySking/CodeIsland-Windows/releases/latest";
    private readonly WpfUpdateChecker _updateChecker = new();
    private string _updateStatusText;
    private string _downloadUrl = "";
    private bool _isCheckingUpdate;

    public AboutView()
    {
        InitializeComponent();
        VersionText = $"版本 {WpfUpdateChecker.FormatVersion(WpfUpdateChecker.GetCurrentVersion())}";
        _updateStatusText = "尚未检查更新";
        CheckUpdateCommand = new RelayCommand(async () => await CheckUpdateAsync(), () => CanCheckUpdate);
        OpenDownloadCommand = new RelayCommand(() => OpenUrl(_downloadUrl));
        OpenGitHubCommand = new RelayCommand(() => OpenUrl(GitHubRepositoryUrl));
        DataContext = this;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public string VersionText { get; }

    public string UpdateStatusText
    {
        get => _updateStatusText;
        private set
        {
            if (string.Equals(_updateStatusText, value, StringComparison.Ordinal)) return;
            _updateStatusText = value;
            OnPropertyChanged();
        }
    }

    public bool HasDownloadUrl => !string.IsNullOrWhiteSpace(_downloadUrl);
    public bool CanCheckUpdate => !_isCheckingUpdate;

    public ICommand CheckUpdateCommand { get; }
    public ICommand OpenDownloadCommand { get; }
    public ICommand OpenGitHubCommand { get; }

    private async Task CheckUpdateAsync()
    {
        if (_isCheckingUpdate)
            return;

        _isCheckingUpdate = true;
        OnPropertyChanged(nameof(CanCheckUpdate));
        (CheckUpdateCommand as RelayCommand)?.RaiseCanExecuteChanged();
        UpdateStatusText = "正在检查更新...";
        SetDownloadUrl("");

        var result = await _updateChecker.CheckForUpdateAsync();

        _isCheckingUpdate = false;
        OnPropertyChanged(nameof(CanCheckUpdate));
        (CheckUpdateCommand as RelayCommand)?.RaiseCanExecuteChanged();

        if (!result.IsSuccess)
        {
            UpdateStatusText = result.ErrorMessage;
            return;
        }

        var current = WpfUpdateChecker.FormatVersion(result.CurrentVersion);
        var latest = result.LatestVersion == null ? "未知" : WpfUpdateChecker.FormatVersion(result.LatestVersion);
        if (result.HasUpdate)
        {
            UpdateStatusText = $"发现新版本 {latest}，当前版本 {current}";
            SetDownloadUrl(ResolveLatestReleasePageUrl(result.ReleaseUrl));
        }
        else
        {
            UpdateStatusText = $"当前已是最新版本（{current}）";
            SetDownloadUrl(ResolveLatestReleasePageUrl(result.ReleaseUrl));
        }
    }

    private void SetDownloadUrl(string value)
    {
        if (string.Equals(_downloadUrl, value, StringComparison.Ordinal)) return;
        _downloadUrl = value;
        OnPropertyChanged(nameof(HasDownloadUrl));
    }

    private static string ResolveLatestReleasePageUrl(string releaseUrl) =>
        !string.IsNullOrWhiteSpace(releaseUrl) ? releaseUrl : LatestReleaseUrl;

    private static void OpenUrl(string url)
    {
        if (string.IsNullOrWhiteSpace(url))
            return;

        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = url,
                UseShellExecute = true
            });
        }
        catch
        {
            // 浏览器启动失败时不影响设置窗口。
        }
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
