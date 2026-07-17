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
    private static readonly JsonSerializerOptions RuntimeManifestJsonOptions = new(JsonSerializerDefaults.Web);
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
        var isExternal = string.Equals(mode, ExternalMode, StringComparison.OrdinalIgnoreCase);
        if (!isExternal)
        {
            // Seed first so hook install never pins to a transient repo path under external/CodeOrbit.
            WpfRuntimeUpdateManager.EnsureSeededFromBundled(_logger);
            await WpfRuntimeUpdateManager.EnsureLatestAsync(_settings, _logger, ct).ConfigureAwait(false);
        }

        if (await IsHealthyAsync(ct).ConfigureAwait(false))
            return;

        if (isExternal)
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
        var workingDirectory = Path.GetDirectoryName(hostPath) ?? AppContext.BaseDirectory;
        // Bridge/hook path follows CodeOrbit_RUNTIME_DIR (or host directory). Prefer managed current only when usable.
        var runtimeDir = PreferManagedHostPath() != null
            ? WpfRuntimeUpdateManager.CurrentRuntimeDirectory
            : workingDirectory;
        var startInfo = new ProcessStartInfo
        {
            FileName = hostPath,
            Arguments = args,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = workingDirectory
        };
        startInfo.Environment["CodeOrbit_RUNTIME_DIR"] = runtimeDir;
        var bundledPlugins = Path.Combine(runtimeDir, "bundled-plugins");
        if (Directory.Exists(bundledPlugins))
            startInfo.Environment["CodeOrbit_BUNDLED_PLUGINS_DIR"] = bundledPlugins;

        _ownedRuntimeProcess = Process.Start(startInfo);
        _logger.Write("WpfRuntimeProcessManager", "runtime-host-started", new Dictionary<string, string?>
        {
            ["path"] = hostPath,
            ["pid"] = _ownedRuntimeProcess?.Id.ToString(),
            ["bindHost"] = ApiBindHost,
            ["runtimeDir"] = runtimeDir,
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
        // Always prefer the stable managed runtime so hook install paths stay durable.
        var preferred = PreferManagedHostPath();
        if (preferred != null)
            return preferred;

        foreach (var candidate in EnumerateRuntimeHostCandidates())
        {
            if (File.Exists(candidate) && WpfRuntimeUpdateManager.IsPreferredRuntimePath(candidate))
                return candidate;
        }

        foreach (var candidate in EnumerateRuntimeHostCandidates())
        {
            if (File.Exists(candidate))
                return candidate;
        }

        return null;
    }

    private static string? PreferManagedHostPath()
    {
        var managedDir = WpfRuntimeUpdateManager.CurrentRuntimeDirectory;
        var managedManifest = ReadManifestFile(Path.Combine(managedDir, "runtime-manifest.json"));
        if (!string.IsNullOrWhiteSpace(managedManifest?.HostExe))
        {
            var managedHost = Path.Combine(managedDir, managedManifest.HostExe);
            if (File.Exists(managedHost))
                return managedHost;
        }

        foreach (var hostName in new[] { "codeorbit-host.exe", "CodeOrbit.RuntimeHost.exe", "CodeIsland.RuntimeHost.exe" })
        {
            var path = Path.Combine(managedDir, hostName);
            if (File.Exists(path))
                return path;
        }

        return null;
    }

    private static IEnumerable<string> EnumerateRuntimeHostCandidates()
    {
        var baseDir = AppContext.BaseDirectory;
        foreach (var manifestPath in EnumerateManifestCandidates())
        {
            var manifest = ReadManifestFile(manifestPath);
            var manifestDir = Path.GetDirectoryName(manifestPath);
            if (!string.IsNullOrWhiteSpace(manifest?.HostExe) && !string.IsNullOrWhiteSpace(manifestDir))
                yield return Path.Combine(manifestDir, manifest.HostExe);
        }

        yield return Path.Combine(WpfRuntimeUpdateManager.CurrentRuntimeDirectory, "codeorbit-host.exe");
        yield return Path.Combine(baseDir, "runtime", "current", "codeorbit-host.exe");
        yield return Path.Combine(baseDir, "runtime", "codeorbit-host.exe");
        yield return Path.Combine(baseDir, "codeorbit-host.exe");

        yield return Path.Combine(WpfRuntimeUpdateManager.CurrentRuntimeDirectory, "CodeOrbit.RuntimeHost.exe");
        yield return Path.Combine(baseDir, "runtime", "current", "CodeOrbit.RuntimeHost.exe");
        yield return Path.Combine(baseDir, "runtime", "CodeOrbit.RuntimeHost.exe");
        yield return Path.Combine(baseDir, "CodeOrbit.RuntimeHost.exe");

        // Legacy paths for backward compatibility during migration
        yield return Path.Combine(WpfRuntimeUpdateManager.CurrentRuntimeDirectory, "CodeIsland.RuntimeHost.exe");
        yield return Path.Combine(baseDir, "runtime", "current", "CodeIsland.RuntimeHost.exe");
        yield return Path.Combine(baseDir, "runtime", "CodeIsland.RuntimeHost.exe");

        // Development paths for CodeOrbit repo (seed source only; launch prefers managed current)
        var current = new DirectoryInfo(baseDir);
        while (current != null)
        {
            yield return Path.Combine(current.FullName, "external", "CodeOrbit", "codeorbit-host.exe");
            yield return Path.Combine(current.FullName, "..", "CodeOrbit", "codeorbit-host.exe");
            yield return Path.Combine(current.FullName, "..", "CodeOrbit", "src", "CodeOrbit.RuntimeHost", "bin", "Debug", "net8.0", "CodeOrbit.RuntimeHost.exe");
            yield return Path.Combine(current.FullName, "..", "CodeOrbit", "src", "CodeOrbit.RuntimeHost", "bin", "Release", "net8.0", "CodeOrbit.RuntimeHost.exe");
            current = current.Parent;
        }
    }

    public WpfRuntimeManifest? ReadManifest()
    {
        foreach (var path in EnumerateManifestCandidates())
        {
            var manifest = ReadManifestFile(path);
            if (manifest != null)
                return manifest;
        }

        return null;
    }

    private static WpfRuntimeManifest? ReadManifestFile(string path)
    {
        if (!File.Exists(path))
            return null;

        try
        {
            return JsonSerializer.Deserialize<WpfRuntimeManifest>(File.ReadAllText(path), RuntimeManifestJsonOptions);
        }
        catch
        {
            return null;
        }
    }

    private static IEnumerable<string> EnumerateManifestCandidates()
    {
        var baseDir = AppContext.BaseDirectory;
        yield return Path.Combine(WpfRuntimeUpdateManager.CurrentRuntimeDirectory, "runtime-manifest.json");
        yield return Path.Combine(baseDir, "runtime", "current", "runtime-manifest.json");
        yield return Path.Combine(baseDir, "runtime", "runtime-manifest.json");

        var current = new DirectoryInfo(baseDir);
        while (current != null)
        {
            yield return Path.Combine(current.FullName, "external", "CodeOrbit", "runtime-manifest.json");
            yield return Path.Combine(current.FullName, "..", "CodeOrbit", "runtime-manifest.json");
            current = current.Parent;
        }
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
