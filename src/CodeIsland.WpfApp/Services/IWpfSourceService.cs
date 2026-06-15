using CodeIsland.Contracts;

namespace CodeIsland.WpfApp.Services;

public interface IWpfSourceService
{
    IReadOnlyList<SourceDto> GetSources();
    SourceStatusDto GetSourceStatus(string source);
    SourceOperationResultDto Install(string source);
    SourceOperationResultDto Uninstall(string source);
    SourceOperationResultDto Repair(string source);
    bool RepairAll();
    RuntimeAssetsDto GetRuntimeAssets();
    bool RepairRuntimeAssets();
}

public sealed class UnavailableWpfSourceService : IWpfSourceService
{
    public IReadOnlyList<SourceDto> GetSources() => [];

    public SourceStatusDto GetSourceStatus(string source) =>
        new(source, Supported: false, Installed: false, DisplayName: source);

    public SourceOperationResultDto Install(string source) =>
        new(source, Success: false, Installed: false, Message: "Runtime is not connected");

    public SourceOperationResultDto Uninstall(string source) =>
        new(source, Success: false, Installed: false, Message: "Runtime is not connected");

    public SourceOperationResultDto Repair(string source) =>
        new(source, Success: false, Installed: false, Message: "Runtime is not connected");

    public bool RepairAll() => false;

    public RuntimeAssetsDto GetRuntimeAssets() => new("", "", "", Installed: false);

    public bool RepairRuntimeAssets() => false;
}
