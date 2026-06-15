using System.Diagnostics;
using System.Text;
using System.Security.Cryptography;
using CodeIsland.Core.Services;
using CodeIsland.Hub;

var arguments = RuntimeHostArguments.Parse(args);
var settings = new SettingsManager(arguments.SettingsDirectory);
var logger = new EventLogger();
var apiPort = Math.Clamp(arguments.PortOverride ?? settings.Get("api_port", 32145), 1024, 65535);

using var singleInstance = RuntimeHostSingleInstance.TryAcquire(apiPort);
if (singleInstance == null)
{
    Console.Error.WriteLine("CodeIsland RuntimeHost is already running for this API port.");
    return 1;
}

using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, eventArgs) =>
{
    eventArgs.Cancel = true;
    logger.Write("CodeIsland.RuntimeHost", "cancel-key");
    cts.Cancel();
};
AppDomain.CurrentDomain.ProcessExit += (_, _) =>
{
    logger.Write("CodeIsland.RuntimeHost", "process-exit");
    cts.Cancel();
};

var ownerMonitorTask = arguments is { ShutdownWhenOwnerExits: true, OwnerPid: { } ownerPid }
    ? RuntimeHostOwnerMonitor.StartAsync(ownerPid, cts, logger)
    : Task.CompletedTask;

var runtimeHost = new CodeIslandRuntimeHost(new CodeIslandRuntimeHostOptions
{
    Settings = settings,
    Logger = logger,
    ApiPort = apiPort,
    ApiToken = arguments.TokenOverride,
    PipeName = arguments.PipeNameOverride,
    ApiHost = arguments.HostOverride,
    RepairSourcesOnStart = !arguments.SkipRepair
});

try
{
    logger.Write("CodeIsland.RuntimeHost", "start-begin", new Dictionary<string, string?>
    {
        ["port"] = apiPort.ToString(),
        ["host"] = arguments.HostOverride ?? settings.Get("api_bind_host", "127.0.0.1"),
        ["pipeName"] = arguments.PipeNameOverride,
        ["ownerPid"] = arguments.OwnerPid?.ToString(),
        ["shutdownWhenOwnerExits"] = arguments.ShutdownWhenOwnerExits.ToString()
    });
    await runtimeHost.StartAsync(cts.Token);
    logger.Write("CodeIsland.RuntimeHost", "start-complete", new Dictionary<string, string?>
    {
        ["baseUrl"] = runtimeHost.ApiBaseUrl,
        ["pipeName"] = runtimeHost.PipeName
    });
    Console.WriteLine($"CodeIsland RuntimeHost started at {runtimeHost.ApiBaseUrl}");
    Console.WriteLine($"Named pipe: {runtimeHost.PipeName}");

    await Task.Delay(Timeout.InfiniteTimeSpan, cts.Token);
    return 0;
}
catch (OperationCanceledException) when (cts.IsCancellationRequested)
{
    logger.Write("CodeIsland.RuntimeHost", "stopped");
    return 0;
}
catch (Exception ex)
{
    logger.Write("CodeIsland.RuntimeHost", "failed", new Dictionary<string, string?>
    {
        ["message"] = ex.Message,
        ["exception"] = ex.GetType().Name
    });
    Console.Error.WriteLine($"CodeIsland RuntimeHost failed: {ex.Message}");
    return 1;
}
finally
{
    cts.Cancel();
    try
    {
        await ownerMonitorTask;
    }
    catch (OperationCanceledException) when (cts.IsCancellationRequested)
    {
    }
    await runtimeHost.DisposeAsync();
}

internal sealed record RuntimeHostArguments(
    string? SettingsDirectory,
    int? PortOverride,
    string? HostOverride,
    string? TokenOverride,
    string? PipeNameOverride,
    int? OwnerPid,
    bool ShutdownWhenOwnerExits,
    bool SkipRepair)
{
    public static RuntimeHostArguments Parse(string[] args)
    {
        string? settingsDirectory = null;
        int? portOverride = null;
        string? hostOverride = null;
        string? tokenOverride = null;
        string? pipeNameOverride = null;
        int? ownerPid = null;
        var shutdownWhenOwnerExits = false;
        var skipRepair = false;

        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--settings-dir" when i + 1 < args.Length:
                    settingsDirectory = args[++i];
                    break;
                case "--port" when i + 1 < args.Length && int.TryParse(args[i + 1], out var port):
                    portOverride = port;
                    i++;
                    break;
                case "--host" when i + 1 < args.Length:
                    hostOverride = args[++i];
                    break;
                case "--token" when i + 1 < args.Length:
                    tokenOverride = args[++i];
                    break;
                case "--pipe-name" when i + 1 < args.Length:
                    pipeNameOverride = args[++i];
                    break;
                case "--owner-pid" when i + 1 < args.Length && int.TryParse(args[i + 1], out var pid):
                    ownerPid = pid;
                    i++;
                    break;
                case "--shutdown-when-owner-exits":
                    shutdownWhenOwnerExits = true;
                    break;
                case "--no-repair":
                    skipRepair = true;
                    break;
            }
        }

        return new RuntimeHostArguments(settingsDirectory, portOverride, hostOverride, tokenOverride, pipeNameOverride, ownerPid, shutdownWhenOwnerExits, skipRepair);
    }
}

internal static class RuntimeHostOwnerMonitor
{
    public static async Task StartAsync(int ownerPid, CancellationTokenSource cts, EventLogger logger)
    {
        try
        {
            using var owner = Process.GetProcessById(ownerPid);
            logger.Write("CodeIsland.RuntimeHost", "owner-monitor-started", new Dictionary<string, string?>
            {
                ["ownerPid"] = ownerPid.ToString()
            });

            while (!cts.IsCancellationRequested)
            {
                owner.Refresh();
                if (owner.HasExited)
                {
                    logger.Write("CodeIsland.RuntimeHost", "owner-exited", new Dictionary<string, string?>
                    {
                        ["ownerPid"] = ownerPid.ToString()
                    });
                    cts.Cancel();
                    return;
                }

                await Task.Delay(TimeSpan.FromSeconds(1), cts.Token);
            }
        }
        catch (ArgumentException)
        {
            logger.Write("CodeIsland.RuntimeHost", "owner-not-found", new Dictionary<string, string?>
            {
                ["ownerPid"] = ownerPid.ToString()
            });
            cts.Cancel();
        }
    }
}

internal sealed class RuntimeHostSingleInstance : IDisposable
{
    private readonly Mutex _mutex;

    private RuntimeHostSingleInstance(Mutex mutex)
    {
        _mutex = mutex;
    }

    public static RuntimeHostSingleInstance? TryAcquire(int port)
    {
        var mutex = new Mutex(initiallyOwned: true, GetName(port), out var createdNew);
        if (createdNew)
            return new RuntimeHostSingleInstance(mutex);

        mutex.Dispose();
        return null;
    }

    public void Dispose()
    {
        _mutex.ReleaseMutex();
        _mutex.Dispose();
    }

    private static string GetName(int port)
    {
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"CodeIsland.RuntimeHost:{port}")));
        return $@"Local\CodeIsland.RuntimeHost.{hash[..16]}";
    }
}
