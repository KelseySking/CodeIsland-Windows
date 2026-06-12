using System.Text;
using System.Security.Cryptography;
using CodeIsland.Core.Services;
using CodeIsland.Hub;

var arguments = RuntimeHostArguments.Parse(args);
var settings = new SettingsManager(arguments.SettingsDirectory);
var logger = new EventLogger();

using var singleInstance = RuntimeHostSingleInstance.TryAcquire(arguments.PortOverride ?? settings.Get("api_port", 32145));
if (singleInstance == null)
{
    Console.Error.WriteLine("CodeIsland RuntimeHost is already running for this API port.");
    return 1;
}

using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, eventArgs) =>
{
    eventArgs.Cancel = true;
    cts.Cancel();
};
AppDomain.CurrentDomain.ProcessExit += (_, _) => cts.Cancel();

var runtimeHost = new CodeIslandRuntimeHost(new CodeIslandRuntimeHostOptions
{
    Settings = settings,
    Logger = logger,
    ApiPort = arguments.PortOverride,
    ApiToken = arguments.TokenOverride,
    PipeName = arguments.PipeNameOverride,
    RepairSourcesOnStart = !arguments.SkipRepair
});

try
{
    await runtimeHost.StartAsync(cts.Token);
    Console.WriteLine($"CodeIsland RuntimeHost started at {runtimeHost.ApiBaseUrl}");
    Console.WriteLine($"Named pipe: {runtimeHost.PipeName}");

    await Task.Delay(Timeout.InfiniteTimeSpan, cts.Token);
    return 0;
}
catch (OperationCanceledException) when (cts.IsCancellationRequested)
{
    return 0;
}
catch (Exception ex)
{
    Console.Error.WriteLine($"CodeIsland RuntimeHost failed: {ex.Message}");
    return 1;
}
finally
{
    await runtimeHost.DisposeAsync();
}

internal sealed record RuntimeHostArguments(
    string? SettingsDirectory,
    int? PortOverride,
    string? TokenOverride,
    string? PipeNameOverride,
    bool SkipRepair)
{
    public static RuntimeHostArguments Parse(string[] args)
    {
        string? settingsDirectory = null;
        int? portOverride = null;
        string? tokenOverride = null;
        string? pipeNameOverride = null;
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
                case "--token" when i + 1 < args.Length:
                    tokenOverride = args[++i];
                    break;
                case "--pipe-name" when i + 1 < args.Length:
                    pipeNameOverride = args[++i];
                    break;
                case "--no-repair":
                    skipRepair = true;
                    break;
            }
        }

        return new RuntimeHostArguments(settingsDirectory, portOverride, tokenOverride, pipeNameOverride, skipRepair);
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
