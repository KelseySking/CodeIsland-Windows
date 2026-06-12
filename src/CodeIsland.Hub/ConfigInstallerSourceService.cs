using CodeIsland.Contracts;
using CodeIsland.Core.Models;
using CodeIsland.Core.Services;

namespace CodeIsland.Hub;

public sealed class ConfigInstallerSourceService : ICodeIslandSourceService
{
    private static readonly SourceCapabilitiesDto DefaultCapabilities = new(
        HookInstall: true,
        Approval: true,
        Question: true,
        Transcript: true,
        AlwaysAllow: true);

    public IReadOnlyList<SourceDto> GetSources() =>
        ConfigInstaller.SupportedSources
            .Select(source => new SourceDto(
                source,
                SupportedSource.GetDisplayName(source),
                SupportedSource.GetIconName(source),
                ConfigInstaller.IsInstalled(source),
                DefaultCapabilities))
            .OrderBy(static source => source.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ToList();

    public SourceStatusDto GetSourceStatus(string source)
    {
        var supported = ConfigInstaller.SupportedSources.Contains(source, StringComparer.OrdinalIgnoreCase);
        var normalized = NormalizeSource(source);
        return new SourceStatusDto(
            normalized,
            supported,
            supported && ConfigInstaller.IsInstalled(normalized),
            SupportedSource.GetDisplayName(normalized));
    }

    public SourceOperationResultDto Install(string source) =>
        RunSourceOperation(source, "installed", ConfigInstaller.Install);

    public SourceOperationResultDto Uninstall(string source) =>
        RunSourceOperation(source, "uninstalled", ConfigInstaller.Uninstall);

    public SourceOperationResultDto Repair(string source) =>
        RunSourceOperation(source, "repaired", ConfigInstaller.Install);

    public bool RepairAll() => ConfigInstaller.RepairInstalledHookConfigurations();

    public RuntimeAssetsDto GetRuntimeAssets() => new(
        ConfigInstaller.RuntimeDirectory,
        ConfigInstaller.RuntimeHookScriptPath,
        ConfigInstaller.RuntimeBridgeExePath,
        ConfigInstaller.AreRuntimeAssetsInstalled());

    public bool RepairRuntimeAssets() => ConfigInstaller.RepairRuntimeAssets();

    private static SourceOperationResultDto RunSourceOperation(
        string source,
        string successVerb,
        Func<string, bool> operation)
    {
        var normalized = NormalizeSource(source);
        if (!ConfigInstaller.SupportedSources.Contains(normalized, StringComparer.OrdinalIgnoreCase))
        {
            return new SourceOperationResultDto(
                normalized,
                Success: false,
                Installed: false,
                Message: $"Unsupported source: {source}");
        }

        var success = operation(normalized);
        return new SourceOperationResultDto(
            normalized,
            success,
            ConfigInstaller.IsInstalled(normalized),
            success ? $"{normalized} {successVerb}" : $"{normalized} operation failed");
    }

    private static string NormalizeSource(string source) =>
        source.Trim().ToLowerInvariant();
}
