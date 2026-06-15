using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;

namespace CodeIsland.WpfApp.Services;

public sealed class WpfRuntimeProcessManager : IDisposable
{
    public const string ManagedMode = "managed";
    public const string ExternalMode = "external";
    private static readonly TimeSpan HealthTimeout = TimeSpan.FromSeconds(8);
    private readonly SettingsManager _settings;
    private readonly EventLogger _logger;
    private Process? _ownedRuntimeProcess;
    private bool _shutdownOwnedRuntimeOnDispose;

    public WpfRuntimeProcessManager(SettingsManager settings, EventLogger logger)
    {
        _settings = settings;
        _logger = logger;
    }

    public int ApiPort => Math.Clamp(_settings.Get("api_port", 32145), 1024, 65535);

    public string ApiBindHost => NormalizeBindHost(_settings.Get("api_bind_host", "127.0.0.1"));

    public string ApiConnectHost => IsWildcardHost(ApiBindHost) ? "127.0.0.1" : ApiBindHost;

    public string ApiToken => WpfLocalApiTokenStore.EnsureToken(_settings);

    public string ApiBaseUrl => $"http://{ApiConnectHost}:{ApiPort}";

    public bool OwnsRuntime => _ownedRuntimeProcess != null;

    public async Task EnsureStartedAsync(CancellationToken ct = default)
    {
        var mode = _settings.Get("runtime_launch_mode", ManagedMode);
        if (!string.Equals(mode, ExternalMode, StringComparison.OrdinalIgnoreCase))
            await WpfRuntimeUpdateManager.EnsureLatestAsync(_settings, _logger, ct).ConfigureAwait(false);

        if (await IsHealthyAsync(ct).ConfigureAwait(false))
            return;

        if (string.Equals(mode, ExternalMode, StringComparison.OrdinalIgnoreCase))
        {
            _logger.Write("WpfRuntimeProcessManager", "external-runtime-unhealthy", new Dictionary<string, string?>
            {
                ["baseUrl"] = ApiBaseUrl
            });
            return;
        }

        var hostPath = ResolveRuntimeHostPath();
        if (hostPath == null)
        {
            _logger.Write("WpfRuntimeProcessManager", "runtime-host-not-found");
            return;
        }

        StartRuntimeHost(hostPath);
        await WaitForHealthAsync(ct).ConfigureAwait(false);
    }

    private void StartRuntimeHost(string hostPath)
    {
        var localPrivateRuntime = IsLocalhost(ApiBindHost);
        _shutdownOwnedRuntimeOnDispose = localPrivateRuntime;
        var ownerArgs = localPrivateRuntime
            ? $" --owner-pid {Environment.ProcessId} --shutdown-when-owner-exits"
            : "";
        var args = $"--settings-dir \"{_settings.SettingsDirectory}\" --host \"{ApiBindHost}\" --port {ApiPort} --token \"{ApiToken}\"{ownerArgs}";
        var startInfo = new ProcessStartInfo
        {
            FileName = hostPath,
            Arguments = args,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = Path.GetDirectoryName(hostPath) ?? AppContext.BaseDirectory
        };

        _ownedRuntimeProcess = Process.Start(startInfo);
        _logger.Write("WpfRuntimeProcessManager", "runtime-host-started", new Dictionary<string, string?>
        {
            ["path"] = hostPath,
            ["pid"] = _ownedRuntimeProcess?.Id.ToString(),
            ["bindHost"] = ApiBindHost,
            ["shutdownWithHud"] = _shutdownOwnedRuntimeOnDispose.ToString()
        });
    }

    private async Task<bool> IsHealthyAsync(CancellationToken ct)
    {
        try
        {
            using var http = new HttpClient { BaseAddress = new Uri($"{ApiBaseUrl}/api/"), Timeout = TimeSpan.FromSeconds(2) };
            var health = await http.GetFromJsonAsync<ApiHealthProbe>("health", ct).ConfigureAwait(false);
            return string.Equals(health?.Status, "ok", StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    private async Task WaitForHealthAsync(CancellationToken ct)
    {
        var deadline = DateTime.UtcNow + HealthTimeout;
        while (DateTime.UtcNow < deadline && !ct.IsCancellationRequested)
        {
            if (await IsHealthyAsync(ct).ConfigureAwait(false))
                return;

            await Task.Delay(250, ct).ConfigureAwait(false);
        }

        _logger.Write("WpfRuntimeProcessManager", "runtime-host-health-timeout");
    }

    private static string? ResolveRuntimeHostPath()
    {
        foreach (var candidate in EnumerateRuntimeHostCandidates())
        {
            if (File.Exists(candidate))
                return candidate;
        }

        return null;
    }

    private static IEnumerable<string> EnumerateRuntimeHostCandidates()
    {
        var baseDir = AppContext.BaseDirectory;
        yield return Path.Combine(WpfRuntimeUpdateManager.CurrentRuntimeDirectory, "CodeIsland.RuntimeHost.exe");
        yield return Path.Combine(baseDir, "runtime", "current", "CodeIsland.RuntimeHost.exe");
        yield return Path.Combine(baseDir, "runtime", "CodeIsland.RuntimeHost.exe");
        yield return Path.Combine(baseDir, "CodeIsland.RuntimeHost.exe");

        var current = new DirectoryInfo(baseDir);
        while (current != null)
        {
            yield return Path.Combine(current.FullName, "src", "CodeIsland.RuntimeHost", "bin", "Debug", "net8.0", "CodeIsland.RuntimeHost.exe");
            yield return Path.Combine(current.FullName, "src", "CodeIsland.RuntimeHost", "bin", "Release", "net8.0", "CodeIsland.RuntimeHost.exe");
            current = current.Parent;
        }
    }

    public WpfRuntimeManifest? ReadManifest()
    {
        foreach (var path in EnumerateManifestCandidates())
        {
            if (!File.Exists(path))
                continue;

            try
            {
                return JsonSerializer.Deserialize<WpfRuntimeManifest>(File.ReadAllText(path), new JsonSerializerOptions(JsonSerializerDefaults.Web));
            }
            catch
            {
                return null;
            }
        }

        return null;
    }

    private static IEnumerable<string> EnumerateManifestCandidates()
    {
        var baseDir = AppContext.BaseDirectory;
        yield return Path.Combine(WpfRuntimeUpdateManager.CurrentRuntimeDirectory, "runtime-manifest.json");
        yield return Path.Combine(baseDir, "runtime", "current", "runtime-manifest.json");
        yield return Path.Combine(baseDir, "runtime", "runtime-manifest.json");
    }

    public void Dispose()
    {
        if (_ownedRuntimeProcess == null)
        {
            _logger.Write("WpfRuntimeProcessManager", "runtime-not-owned");
            return;
        }

        if (!_shutdownOwnedRuntimeOnDispose)
        {
            _logger.Write("WpfRuntimeProcessManager", "runtime-left-running", new Dictionary<string, string?>
            {
                ["reason"] = "shared-remote-mode"
            });
            return;
        }

        if (_ownedRuntimeProcess is not { HasExited: false } process)
            return;

        try
        {
            _logger.Write("WpfRuntimeProcessManager", "runtime-kill-owned", new Dictionary<string, string?>
            {
                ["pid"] = process.Id.ToString()
            });
            process.Kill(entireProcessTree: true);
            process.Dispose();
        }
        catch
        {
        }
    }

    private static string NormalizeBindHost(string? host)
    {
        if (string.IsNullOrWhiteSpace(host))
            return "127.0.0.1";
        var value = host.Trim();
        return value is "*" or "+" ? "0.0.0.0" : value;
    }

    private static bool IsWildcardHost(string host) =>
        host is "0.0.0.0" or "::" or "*" or "+";

    private static bool IsLocalhost(string host) =>
        host.Equals("127.0.0.1", StringComparison.OrdinalIgnoreCase) ||
        host.Equals("localhost", StringComparison.OrdinalIgnoreCase) ||
        host.Equals("::1", StringComparison.OrdinalIgnoreCase);

    private sealed record ApiHealthProbe(string Status);
}

public sealed record WpfRuntimeManifest(
    string RuntimeVersion,
    string ContractVersion,
    string HostExe,
    string BridgeExe,
    int DefaultPort);
