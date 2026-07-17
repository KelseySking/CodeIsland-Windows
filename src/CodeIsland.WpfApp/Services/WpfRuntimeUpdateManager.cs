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

    /// <summary>
    /// Copy bundled Runtime (install dir or repo external/CodeOrbit) into the stable
    /// managed location %LOCALAPPDATA%\CodeIsland\runtime\current so hook install
    /// always writes a durable bridge path instead of a dev repo path.
    /// </summary>
    public static bool EnsureSeededFromBundled(EventLogger logger)
    {
        var sourceDir = FindSeedSourceDirectory();
        if (sourceDir == null)
        {
            var hasManaged = HasUsableRuntime(CurrentRuntimeDirectory);
            logger.Write("WpfRuntimeUpdate", "seed-source-missing", new Dictionary<string, string?>
            {
                ["managedCurrent"] = CurrentRuntimeDirectory,
                ["hasManaged"] = hasManaged.ToString()
            });
            return hasManaged;
        }

        if (AreSameDirectory(sourceDir, CurrentRuntimeDirectory))
            return HasUsableRuntime(CurrentRuntimeDirectory);

        var sourceManifest = ReadManifestFromDirectory(sourceDir);
        if (!HasUsableRuntime(sourceDir, sourceManifest))
        {
            logger.Write("WpfRuntimeUpdate", "seed-source-invalid", new Dictionary<string, string?>
            {
                ["source"] = sourceDir
            });
            return HasUsableRuntime(CurrentRuntimeDirectory);
        }

        var localManifest = ReadLocalManifest();
        if (HasUsableRuntime(CurrentRuntimeDirectory, localManifest))
        {
            // Never replace a working managed runtime with an older/equal bundled seed.
            // Auto-update may have installed a newer version than the app bundle.
            if (string.IsNullOrWhiteSpace(sourceManifest?.RuntimeVersion)
                || string.Equals(localManifest?.RuntimeVersion, sourceManifest?.RuntimeVersion, StringComparison.OrdinalIgnoreCase)
                || CompareVersionish(localManifest?.RuntimeVersion, sourceManifest?.RuntimeVersion) >= 0)
            {
                logger.Write("WpfRuntimeUpdate", "seed-already-current", new Dictionary<string, string?>
                {
                    ["runtimeVersion"] = localManifest?.RuntimeVersion,
                    ["sourceVersion"] = sourceManifest?.RuntimeVersion,
                    ["current"] = CurrentRuntimeDirectory
                });
                return true;
            }
        }

        try
        {
            Directory.CreateDirectory(RuntimeRoot);
            ResetDirectory(StagingRuntimeDirectory);
            var extractPath = Path.Combine(StagingRuntimeDirectory, "extract");
            Directory.CreateDirectory(extractPath);
            CopyDirectory(sourceDir, extractPath);
            Promote(extractPath);
            logger.Write("WpfRuntimeUpdate", "seeded", new Dictionary<string, string?>
            {
                ["source"] = sourceDir,
                ["current"] = CurrentRuntimeDirectory,
                ["runtimeVersion"] = sourceManifest?.RuntimeVersion,
                ["previousVersion"] = localManifest?.RuntimeVersion
            });
            return true;
        }
        catch (Exception ex)
        {
            logger.Write("WpfRuntimeUpdate", "seed-failed", new Dictionary<string, string?>
            {
                ["source"] = sourceDir,
                ["message"] = ex.Message,
                ["exception"] = ex.GetType().Name
            });
            return HasUsableRuntime(CurrentRuntimeDirectory);
        }
    }

    public static bool IsPreferredRuntimePath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return false;

        string full;
        try
        {
            full = Path.GetFullPath(path);
        }
        catch
        {
            return false;
        }

        if (IsUnderDirectory(full, CurrentRuntimeDirectory))
            return true;

        var appRuntime = Path.Combine(AppContext.BaseDirectory, "runtime");
        return IsUnderDirectory(full, appRuntime);
    }

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

    private static string? FindSeedSourceDirectory()
    {
        foreach (var candidate in EnumerateSeedSourceCandidates())
        {
            if (HasUsableRuntime(candidate))
                return candidate;
        }

        return null;
    }

    private static IEnumerable<string> EnumerateSeedSourceCandidates()
    {
        var baseDir = AppContext.BaseDirectory;
        yield return Path.Combine(baseDir, "runtime", "current");
        yield return Path.Combine(baseDir, "runtime");
        yield return baseDir;

        var current = new DirectoryInfo(baseDir);
        while (current != null)
        {
            yield return Path.Combine(current.FullName, "external", "CodeOrbit");
            yield return Path.GetFullPath(Path.Combine(current.FullName, "..", "CodeOrbit"));
            current = current.Parent;
        }
    }

    private static WpfRuntimeManifest? ReadManifestFromDirectory(string directory)
    {
        var path = Path.Combine(directory, "runtime-manifest.json");
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

    private static bool HasUsableRuntime(string directory, WpfRuntimeManifest? manifest = null)
    {
        if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
            return false;

        manifest ??= ReadManifestFromDirectory(directory);
        if (manifest == null)
            return false;

        var hostName = string.IsNullOrWhiteSpace(manifest.HostExe) ? "codeorbit-host.exe" : manifest.HostExe;
        var bridgeName = string.IsNullOrWhiteSpace(manifest.BridgeExe) ? "codeorbit-bridge.exe" : manifest.BridgeExe;
        return File.Exists(Path.Combine(directory, hostName))
            && File.Exists(Path.Combine(directory, bridgeName));
    }

    private static bool AreSameDirectory(string left, string right)
    {
        try
        {
            var a = Path.GetFullPath(left).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var b = Path.GetFullPath(right).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            return string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Compare loose version strings like "0.1.3" / "v0.1.3". Positive if left is newer.
    /// </summary>
    private static int CompareVersionish(string? left, string? right)
    {
        static Version? Parse(string? value)
        {
            if (string.IsNullOrWhiteSpace(value))
                return null;
            var text = value.Trim();
            if (text.StartsWith('v') || text.StartsWith('V'))
                text = text[1..];
            return Version.TryParse(text, out var version) ? version : null;
        }

        var a = Parse(left);
        var b = Parse(right);
        if (a == null && b == null)
            return 0;
        if (a == null)
            return -1;
        if (b == null)
            return 1;
        return a.CompareTo(b);
    }

    private static bool IsUnderDirectory(string path, string directory)
    {
        try
        {
            var fullPath = Path.GetFullPath(path);
            var fullDir = Path.GetFullPath(directory)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                + Path.DirectorySeparatorChar;
            return fullPath.StartsWith(fullDir, StringComparison.OrdinalIgnoreCase)
                || string.Equals(
                    fullPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                    fullDir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                    StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    private static void CopyDirectory(string sourceDir, string destinationDir)
    {
        Directory.CreateDirectory(destinationDir);
        foreach (var file in Directory.EnumerateFiles(sourceDir, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(sourceDir, file);
            var dest = Path.Combine(destinationDir, relative);
            var destParent = Path.GetDirectoryName(dest);
            if (!string.IsNullOrWhiteSpace(destParent))
                Directory.CreateDirectory(destParent);
            File.Copy(file, dest, overwrite: true);
        }
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
