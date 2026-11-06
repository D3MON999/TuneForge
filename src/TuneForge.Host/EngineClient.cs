using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Security.Principal;
using System.Text.Json;
using TuneForge.Contracts;

namespace TuneForge.Host;

public class EngineException(string message) : Exception(message);

public sealed class EngineCancelledException() : EngineException("Elevation was declined.");

/// <summary>Starts the elevated engine on request and talks to it over its private pipe.</summary>
public sealed class EngineClient : IDisposable
{
    private const int ErrorCancelled = 1223;

    private readonly SemaphoreSlim _gate = new(1, 1);
    private NamedPipeClientStream? _pipe;
    private LineChannel? _channel;
    private long _nextId;

    public async Task<PingResult> ConnectAndPingAsync()
    {
        await _gate.WaitAsync();
        try
        {
            if (_pipe is not { IsConnected: true })
            {
                await StartEngineAsync();
            }

            return await PingAsync();
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task StartEngineAsync()
    {
        CloseSession();
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

        _pipe = pipe;
        _channel = new LineChannel(pipe);
    }

    private async Task<PingResult> PingAsync()
    {
        var id = Interlocked.Increment(ref _nextId);
        var request = JsonSerializer.Serialize(
            new RpcRequest(RpcProtocol.Version, id, RpcMethods.Ping),
            RpcProtocol.Json);

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await _channel!.WriteLineAsync(request, timeout.Token);
        var line = await _channel.ReadLineAsync(timeout.Token)
            ?? throw new EngineException("The engine closed the connection.");

        var response = JsonSerializer.Deserialize<RpcResponse>(line, RpcProtocol.Json);
        if (response?.Id != id || response.Result is not { } result)
        {
            throw new EngineException("The engine sent an invalid reply.");
        }

        return result.Deserialize<PingResult>(RpcProtocol.Json)
            ?? throw new EngineException("The engine sent an invalid reply.");
    }

    private void CloseSession()
    {
        _pipe?.Dispose();
        _pipe = null;
        _channel = null;
    }

    public void Dispose()
    {
        CloseSession();
        _gate.Dispose();
    }
}
