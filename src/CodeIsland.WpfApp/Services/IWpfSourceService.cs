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

    WslDistrosDto ListWslDistros();
    SourceStatusDto GetWslSourceStatus(string source, string? distro = null);
    SourceOperationResultDto InstallWsl(string source, string? distro = null);
    SourceOperationResultDto UninstallWsl(string source, string? distro = null);
    SourceOperationResultDto RepairWsl(string source, string? distro = null);
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

    public WslDistrosDto ListWslDistros() =>
        new([], Message: "Runtime is not connected", Code: "wsl_unavailable");

    public SourceStatusDto GetWslSourceStatus(string source, string? distro = null) =>
        new(source, Supported: false, Installed: false, DisplayName: source, Distro: distro, ProbeOk: false, Error: "Runtime is not connected");

    public SourceOperationResultDto InstallWsl(string source, string? distro = null) =>
        new(source, Success: false, Installed: false, Message: "Runtime is not connected", Distro: distro, Code: "operation_failed");

    public SourceOperationResultDto UninstallWsl(string source, string? distro = null) =>
        new(source, Success: false, Installed: false, Message: "Runtime is not connected", Distro: distro, Code: "operation_failed");

    public SourceOperationResultDto RepairWsl(string source, string? distro = null) =>
        new(source, Success: false, Installed: false, Message: "Runtime is not connected", Distro: distro, Code: "operation_failed");
}
