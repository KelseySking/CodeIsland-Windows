using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;

namespace CodeIsland.WpfApp.Services;

public static class WpfRuntimeUpdateManager
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private static readonly TimeSpan DownloadTimeout = TimeSpan.FromSeconds(30);

    public static string RuntimeRoot => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "CodeIsland",
        "runtime");

    public static string CurrentRuntimeDirectory => Path.Combine(RuntimeRoot, "current");

    public static string PreviousRuntimeDirectory => Path.Combine(RuntimeRoot, "previous");

    public static string StagingRuntimeDirectory => Path.Combine(RuntimeRoot, "staging");

    public static async Task EnsureLatestAsync(SettingsManager settings, EventLogger logger, CancellationToken ct = default)
    {
        if (!settings.Get("runtime_auto_update", true))
        {
            logger.Write("WpfRuntimeUpdate", "disabled");
            return;
        }

        var manifestUrl = settings.Get("runtime_update_manifest_url", "");
        if (string.IsNullOrWhiteSpace(manifestUrl))
        {
            logger.Write("WpfRuntimeUpdate", "manifest-url-empty");
            return;
        }

        if (!Uri.TryCreate(manifestUrl, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
        {
            logger.Write("WpfRuntimeUpdate", "manifest-url-invalid", new Dictionary<string, string?>
            {
                ["url"] = manifestUrl
            });
            return;
        }

        RuntimeUpdateManifest? remote;
        try
        {
            using var http = new HttpClient { Timeout = DownloadTimeout };
            remote = await http.GetFromJsonAsync<RuntimeUpdateManifest>(uri, JsonOptions, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            logger.Write("WpfRuntimeUpdate", "manifest-fetch-failed", new Dictionary<string, string?>
            {
                ["message"] = ex.Message,
                ["exception"] = ex.GetType().Name
            });
            return;
        }

        if (remote == null || string.IsNullOrWhiteSpace(remote.RuntimeVersion) || string.IsNullOrWhiteSpace(remote.DownloadUrl))
        {
            logger.Write("WpfRuntimeUpdate", "manifest-invalid");
            return;
        }

        var local = ReadLocalManifest();
        if (string.Equals(local?.RuntimeVersion, remote.RuntimeVersion, StringComparison.OrdinalIgnoreCase))
        {
            logger.Write("WpfRuntimeUpdate", "already-current", new Dictionary<string, string?>
            {
                ["runtimeVersion"] = remote.RuntimeVersion
            });
            return;
        }

        logger.Write("WpfRuntimeUpdate", "update-available", new Dictionary<string, string?>
        {
            ["localVersion"] = local?.RuntimeVersion,
            ["remoteVersion"] = remote.RuntimeVersion
        });

        await DownloadAndPromoteAsync(remote, logger, ct).ConfigureAwait(false);
    }

    public static WpfRuntimeManifest? ReadLocalManifest()
    {
        var path = Path.Combine(CurrentRuntimeDirectory, "runtime-manifest.json");
        if (!File.Exists(path))
            return null;

        try
        {
            return JsonSerializer.Deserialize<WpfRuntimeManifest>(File.ReadAllText(path), JsonOptions);
        }
        catch
        {
            return null;
        }
    }

    private static async Task DownloadAndPromoteAsync(RuntimeUpdateManifest manifest, EventLogger logger, CancellationToken ct)
    {
        Directory.CreateDirectory(RuntimeRoot);
        ResetDirectory(StagingRuntimeDirectory);
        Directory.CreateDirectory(StagingRuntimeDirectory);

        var zipPath = Path.Combine(StagingRuntimeDirectory, "runtime.zip");
        var extractPath = Path.Combine(StagingRuntimeDirectory, "extract");
        try
        {
            using var http = new HttpClient { Timeout = DownloadTimeout };
            await using (var source = await http.GetStreamAsync(manifest.DownloadUrl, ct).ConfigureAwait(false))
            await using (var destination = File.Create(zipPath))
            {
                await source.CopyToAsync(destination, ct).ConfigureAwait(false);
            }

            if (!string.IsNullOrWhiteSpace(manifest.Sha256) && !VerifySha256(zipPath, manifest.Sha256))
            {
                logger.Write("WpfRuntimeUpdate", "sha256-mismatch", new Dictionary<string, string?>
                {
                    ["runtimeVersion"] = manifest.RuntimeVersion
                });
                return;
            }

            ZipFile.ExtractToDirectory(zipPath, extractPath);
            var hostPath = FindRuntimeHostPath(extractPath);
            if (hostPath == null)
            {
                logger.Write("WpfRuntimeUpdate", "host-missing", new Dictionary<string, string?>
                {
                    ["runtimeVersion"] = manifest.RuntimeVersion
                });
                return;
            }

            var payloadDir = Path.GetDirectoryName(hostPath)!;
            Promote(payloadDir);
            logger.Write("WpfRuntimeUpdate", "promoted", new Dictionary<string, string?>
            {
                ["runtimeVersion"] = manifest.RuntimeVersion,
                ["current"] = CurrentRuntimeDirectory
            });
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            logger.Write("WpfRuntimeUpdate", "failed", new Dictionary<string, string?>
            {
                ["runtimeVersion"] = manifest.RuntimeVersion,
                ["message"] = ex.Message,
                ["exception"] = ex.GetType().Name
            });
        }
    }

    private static string? FindRuntimeHostPath(string extractPath)
    {
        var manifestPath = Directory.EnumerateFiles(extractPath, "runtime-manifest.json", SearchOption.AllDirectories)
            .FirstOrDefault();
        if (manifestPath == null)
            return null;

        try
        {
            var manifest = JsonSerializer.Deserialize<WpfRuntimeManifest>(File.ReadAllText(manifestPath), JsonOptions);
            var manifestDir = Path.GetDirectoryName(manifestPath);
            if (!string.IsNullOrWhiteSpace(manifest?.HostExe) && !string.IsNullOrWhiteSpace(manifestDir))
            {
                var manifestHostPath = Path.Combine(manifestDir, manifest.HostExe);
                if (File.Exists(manifestHostPath))
                    return manifestHostPath;
            }
        }
        catch
        {
        }

        return null;
    }

    private static void Promote(string payloadDir)
    {
        ResetDirectory(PreviousRuntimeDirectory);
        if (Directory.Exists(CurrentRuntimeDirectory))
            Directory.Move(CurrentRuntimeDirectory, PreviousRuntimeDirectory);

        Directory.Move(payloadDir, CurrentRuntimeDirectory);
    }

    private static void ResetDirectory(string path)
    {
        if (Directory.Exists(path))
            Directory.Delete(path, recursive: true);
    }

    private static bool VerifySha256(string path, string expectedHash)
    {
        using var stream = File.OpenRead(path);
        var actual = Convert.ToHexString(SHA256.HashData(stream));
        return string.Equals(actual, expectedHash.Replace(" ", "", StringComparison.Ordinal), StringComparison.OrdinalIgnoreCase);
    }

    private sealed record RuntimeUpdateManifest(
        string RuntimeVersion,
        string ContractVersion,
        string DownloadUrl,
        string? Sha256);
}
