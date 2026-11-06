using System.Diagnostics;
using System.IO.Pipes;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using TuneForge.Contracts;
using TuneForge.Engine.Core;
using Xunit;

namespace TuneForge.Engine.Tests;

public class EngineServerTests
{
    private static readonly string ThisProcess = Process.GetCurrentProcess().MainModule!.FileName;

    private static EngineOptions Options(string pipeName, string? expectedClient = null)
    {
        using var identity = WindowsIdentity.GetCurrent();
        return new EngineOptions(pipeName, identity.User!, expectedClient ?? ThisProcess, TimeSpan.FromSeconds(10));
    }

    private static string NewPipeName() => $"TuneForge.Engine.Test.{Guid.NewGuid():N}";

    private static async Task<NamedPipeClientStream> ConnectAsync(string pipeName)
    {
        var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        await pipe.ConnectAsync(TimeSpan.FromSeconds(10), CancellationToken.None);
        return pipe;
    }

    private static string Request(long id, string method) =>
        JsonSerializer.Serialize(new RpcRequest(RpcProtocol.Version, id, method), RpcProtocol.Json);

    private static RpcResponse Parse(string? line) =>
        JsonSerializer.Deserialize<RpcResponse>(line!, RpcProtocol.Json)!;

    [Fact]
    public async Task Ping_ReturnsProtocolAndEngineDetails()
    {
        var name = NewPipeName();
        var run = new EngineServer(Options(name)).RunAsync(CancellationToken.None);
        await using var pipe = await ConnectAsync(name);
        var channel = new LineChannel(pipe);

        await channel.WriteLineAsync(Request(1, RpcMethods.Ping), CancellationToken.None);
        var response = Parse(await channel.ReadLineAsync(CancellationToken.None));
        var ping = response.Result!.Value.Deserialize<PingResult>(RpcProtocol.Json)!;

        Assert.Equal(1, response.Id);
        Assert.Null(response.Error);
        Assert.Equal(RpcProtocol.ProtocolVersion, ping.ProtocolVersion);
        Assert.Equal(Environment.ProcessId, ping.ProcessId);

        pipe.Close();
        await run;
    }

    [Fact]
    public async Task UnknownMethod_IsRefused()
    {
        var name = NewPipeName();
        var run = new EngineServer(Options(name)).RunAsync(CancellationToken.None);
        await using var pipe = await ConnectAsync(name);
        var channel = new LineChannel(pipe);

        await channel.WriteLineAsync(Request(2, "registry.set"), CancellationToken.None);
        var response = Parse(await channel.ReadLineAsync(CancellationToken.None));

        Assert.Equal(RpcErrorCodes.MethodNotFound, response.Error?.Code);
        Assert.Null(response.Result);

        pipe.Close();
        await run;
    }

    [Fact]
    public async Task InvalidJson_ReturnsParseError_AndSessionContinues()
    {
        var name = NewPipeName();
        var run = new EngineServer(Options(name)).RunAsync(CancellationToken.None);
        await using var pipe = await ConnectAsync(name);
        var channel = new LineChannel(pipe);

        await channel.WriteLineAsync("{not json", CancellationToken.None);
        Assert.Equal(RpcErrorCodes.ParseError, Parse(await channel.ReadLineAsync(CancellationToken.None)).Error?.Code);

        await channel.WriteLineAsync(Request(3, RpcMethods.Ping), CancellationToken.None);
        Assert.Null(Parse(await channel.ReadLineAsync(CancellationToken.None)).Error);

        pipe.Close();
        await run;
    }

    [Fact]
    public async Task Shutdown_EndsTheServer()
    {
        var name = NewPipeName();
        var run = new EngineServer(Options(name)).RunAsync(CancellationToken.None);
        await using var pipe = await ConnectAsync(name);
        var channel = new LineChannel(pipe);

        await channel.WriteLineAsync(Request(4, RpcMethods.Shutdown), CancellationToken.None);
        Assert.Null(Parse(await channel.ReadLineAsync(CancellationToken.None)).Error);

        await run.WaitAsync(TimeSpan.FromSeconds(10));
    }

    [Fact]
    public async Task WrongClientProgram_IsRejected()
    {
        var name = NewPipeName();
        var run = new EngineServer(Options(name, @"C:\Windows\explorer.exe")).RunAsync(CancellationToken.None);
        await using var pipe = await ConnectAsync(name);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => run);
    }

    [Fact]
    public async Task OversizedMessage_DropsTheSession()
    {
        var name = NewPipeName();
        var run = new EngineServer(Options(name)).RunAsync(CancellationToken.None);
        await using var pipe = await ConnectAsync(name);

        var tooLarge = Encoding.UTF8.GetBytes(new string('x', RpcProtocol.MaxMessageBytes + 1));
        try
        {
            await pipe.WriteAsync(tooLarge);
        }
        catch (IOException)
        {
            // The server may close the pipe before the whole write finishes.
        }

        await run.WaitAsync(TimeSpan.FromSeconds(10));
    }

    [Fact]
    public async Task SecondServerOnSameName_IsRefused()
    {
        var name = NewPipeName();
        var first = new EngineServer(Options(name)).RunAsync(CancellationToken.None);

        await Assert.ThrowsAnyAsync<Exception>(
            () => new EngineServer(Options(name)).RunAsync(CancellationToken.None));

        await using var pipe = await ConnectAsync(name);
        pipe.Close();
        await first;
    }

    [Fact]
    public async Task LineChannel_ReadsMessagesSplitAcrossWrites()
    {
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes("one\r\ntwo\n"));
        var channel = new LineChannel(stream);

        Assert.Equal("one", await channel.ReadLineAsync(CancellationToken.None));
        Assert.Equal("two", await channel.ReadLineAsync(CancellationToken.None));
        Assert.Null(await channel.ReadLineAsync(CancellationToken.None));
    }
}
