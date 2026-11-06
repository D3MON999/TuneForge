using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Security.Principal;
using System.Text.Json;
using TuneForge.Contracts;

namespace TuneForge.Host;

public class EngineException(string message) : Exception(message);

public sealed class EngineCancelledException() : EngineException("Admin permission was declined.");

/// <summary>Starts the elevated engine for one job, sends it, then lets the engine exit.</summary>
public sealed class EngineClient : IDisposable
{
    private const int ErrorCancelled = 1223;

    private readonly SemaphoreSlim _gate = new(1, 1);
    private long _nextId;

    public Task<PingResult> PingAsync() =>
        RunAsync<PingResult>(RpcMethods.Ping, null, TimeSpan.FromSeconds(10));

    public Task<ApplyResult> ApplyAsync(ApplyRequest request) =>
        RunAsync<ApplyResult>(RpcMethods.ApplyTweaks, request, TimeSpan.FromMinutes(5));

    public Task<UndoResult> UndoAsync(long changeSetId) =>
        RunAsync<UndoResult>(RpcMethods.Undo, new UndoRequest(changeSetId), TimeSpan.FromMinutes(2));

    public Task<CleanupResult> CleanAsync(IReadOnlyList<string> ids) =>
        RunAsync<CleanupResult>(RpcMethods.Cleanup, new CleanupRequest(ids), TimeSpan.FromMinutes(30));

    public Task<OperationResult> SetStartupAsync(StartupSetRequest request) =>
        RunAsync<OperationResult>(RpcMethods.StartupSet, request, TimeSpan.FromMinutes(1));

    public Task<OperationResult> SetServiceAsync(ServiceSetRequest request) =>
        RunAsync<OperationResult>(RpcMethods.ServiceSet, request, TimeSpan.FromMinutes(1));

    public Task<OperationResult> SetFeatureAsync(FeatureSetRequest request) =>
        RunAsync<OperationResult>(RpcMethods.FeatureSet, request, TimeSpan.FromHours(1));

    public Task<OperationResult> RepairAsync(RepairRequest request) =>
        RunAsync<OperationResult>(RpcMethods.Repair, request, TimeSpan.FromHours(2));

    public Task<BuiltInRemoveResult> RemoveBuiltInAsync(BuiltInRemoveRequest request) =>
        RunAsync<BuiltInRemoveResult>(RpcMethods.BuiltInRemove, request, TimeSpan.FromMinutes(35));

    public Task<OperationResult> SetDnsAsync(DnsSetRequest request) =>
        RunAsync<OperationResult>(RpcMethods.DnsSet, request, TimeSpan.FromMinutes(3));

    /// <summary>Each job gets a fresh engine, so admin rights last only as long as the work.</summary>
    private async Task<T> RunAsync<T>(string method, object? parameters, TimeSpan timeLimit)
    {
        await _gate.WaitAsync();
        try
        {
            await using var pipe = await StartEngineAsync();
            var channel = new LineChannel(pipe);
            var result = await CallAsync<T>(channel, method, parameters, timeLimit);
            await CallAsync<bool>(channel, RpcMethods.Shutdown, null, TimeSpan.FromSeconds(5));
            return result;
        }
        finally
        {
            _gate.Release();
        }
    }

    private static async Task<NamedPipeClientStream> StartEngineAsync()
    {
        var enginePath = Path.Combine(AppContext.BaseDirectory, "TuneForge.Engine.exe");
        var pipeName = $"TuneForge.Engine.{Guid.NewGuid():N}";
        using var identity = WindowsIdentity.GetCurrent();

        var start = new ProcessStartInfo(enginePath)
        {
            UseShellExecute = true,
            Verb = "runas",
            WindowStyle = ProcessWindowStyle.Hidden,
            Arguments = $"--pipe {pipeName} --client-sid {identity.User!.Value} --parent-pid {Environment.ProcessId}",
        };

        try
        {
            using var _ = Process.Start(start);
        }
        catch (Win32Exception exception) when (exception.NativeErrorCode == ErrorCancelled)
        {
            throw new EngineCancelledException();
        }

        var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        try
        {
            await pipe.ConnectAsync(TimeSpan.FromSeconds(15), CancellationToken.None);
            if (!PipePeer.SamePath(PipePeer.GetServerPath(pipe), enginePath))
            {
                throw new EngineException("The pipe does not belong to the TuneForge engine.");
            }
        }
        catch
        {
            await pipe.DisposeAsync();
            throw;
        }

        return pipe;
    }

    private async Task<T> CallAsync<T>(LineChannel channel, string method, object? parameters, TimeSpan timeLimit)
    {
        var id = Interlocked.Increment(ref _nextId);
        var payload = parameters is null
            ? (JsonElement?)null
            : JsonSerializer.SerializeToElement(parameters, parameters.GetType(), RpcProtocol.Json);
        var request = JsonSerializer.Serialize(
            new RpcRequest(RpcProtocol.Version, id, method, payload),
            RpcProtocol.Json);

        using var timeout = new CancellationTokenSource(timeLimit);
        await channel.WriteLineAsync(request, timeout.Token);
        var line = await channel.ReadLineAsync(timeout.Token)
            ?? throw new EngineException("The engine closed the connection.");

        var response = JsonSerializer.Deserialize<RpcResponse>(line, RpcProtocol.Json);
        if (response?.Id != id)
        {
            throw new EngineException("The engine sent an invalid reply.");
        }

        if (response.Error is { } error)
        {
            throw new EngineException(error.Message);
        }

        return response.Result is { } result
            ? result.Deserialize<T>(RpcProtocol.Json) ?? throw new EngineException("The engine sent an invalid reply.")
            : throw new EngineException("The engine sent an invalid reply.");
    }

    public void Dispose() => _gate.Dispose();
}
