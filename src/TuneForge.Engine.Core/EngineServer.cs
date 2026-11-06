using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using TuneForge.Contracts;

namespace TuneForge.Engine.Core;

public sealed record EngineOptions(
    string PipeName,
    SecurityIdentifier ClientSid,
    string ExpectedClientPath,
    TimeSpan ConnectTimeout);

/// <summary>Serves one verified client over a locked-down pipe, then stops.</summary>
public sealed class EngineServer(EngineOptions options, TweakEngine? tweaks = null)
{
    public async Task RunAsync(CancellationToken cancellationToken)
    {
        using var session = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        await using var pipe = NamedPipeServerStreamAcl.Create(
            options.PipeName,
            PipeDirection.InOut,
            maxNumberOfServerInstances: 1,
            PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous | PipeOptions.FirstPipeInstance,
            inBufferSize: 65536,
            outBufferSize: 65536,
            BuildSecurity());

        using (var connect = CancellationTokenSource.CreateLinkedTokenSource(session.Token))
        {
            connect.CancelAfter(options.ConnectTimeout);
            await pipe.WaitForConnectionAsync(connect.Token).ConfigureAwait(false);
        }

        if (!PipePeer.SamePath(PipePeer.GetClientPath(pipe), options.ExpectedClientPath))
        {
            pipe.Disconnect();
            throw new UnauthorizedAccessException("The connecting program is not TuneForge.");
        }

        var channel = new LineChannel(pipe);
        var dispatcher = new RpcDispatcher(session.Cancel, tweaks);
        try
        {
            while (await channel.ReadLineAsync(session.Token).ConfigureAwait(false) is { } line)
            {
                var reply = dispatcher.Handle(line);
                await channel.WriteLineAsync(reply, CancellationToken.None).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (session.IsCancellationRequested)
        {
            // Shutdown was requested. Closing the pipe ends the session.
        }
        catch (MessageTooLargeException)
        {
            // An oversized message means a faulty or hostile peer, so drop the session.
        }
        catch (IOException)
        {
            // The client went away.
        }
    }

    private PipeSecurity BuildSecurity()
    {
        var security = new PipeSecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        security.AddAccessRule(new PipeAccessRule(
            options.ClientSid,
            PipeAccessRights.ReadWrite | PipeAccessRights.Synchronize,
            AccessControlType.Allow));

        using var self = WindowsIdentity.GetCurrent();
        security.AddAccessRule(new PipeAccessRule(
            self.User!,
            PipeAccessRights.FullControl,
            AccessControlType.Allow));
        return security;
    }
}
