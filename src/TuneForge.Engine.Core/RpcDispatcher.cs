using System.Reflection;
using System.Security.Principal;
using System.Text.Json;
using TuneForge.Contracts;

namespace TuneForge.Engine.Core;

/// <summary>Maps the fixed list of method names to handlers. Unknown names are refused.</summary>
public sealed class RpcDispatcher
{
    private readonly Action _requestShutdown;

    public RpcDispatcher(Action requestShutdown) => _requestShutdown = requestShutdown;

    public string Handle(string line)
    {
        RpcRequest? request;
        try
        {
            request = JsonSerializer.Deserialize<RpcRequest>(line, RpcProtocol.Json);
        }
        catch (JsonException)
        {
            return Failure(null, RpcErrorCodes.ParseError, "Invalid JSON.");
        }

        if (request is null || request.JsonRpc != RpcProtocol.Version || string.IsNullOrEmpty(request.Method))
        {
            return Failure(request?.Id, RpcErrorCodes.ParseError, "Invalid request.");
        }

        try
        {
            return request.Method switch
            {
                RpcMethods.Ping => Success(request.Id, BuildPing()),
                RpcMethods.Shutdown => Shutdown(request.Id),
                _ => Failure(request.Id, RpcErrorCodes.MethodNotFound, "Unknown method."),
            };
        }
        catch (Exception)
        {
            return Failure(request.Id, RpcErrorCodes.InternalError, "The engine could not complete the request.");
        }
    }

    private string Shutdown(long id)
    {
        _requestShutdown();
        return Success(id, true);
    }

    private static PingResult BuildPing() => new(
        RpcProtocol.ProtocolVersion,
        Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "0.0.0",
        IsElevated(),
        Environment.ProcessId);

    private static bool IsElevated()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
    }

    private static string Success<T>(long id, T result) => JsonSerializer.Serialize(
        new RpcResponse(
            RpcProtocol.Version,
            id,
            JsonSerializer.SerializeToElement(result, RpcProtocol.Json),
            null),
        RpcProtocol.Json);

    private static string Failure(long? id, int code, string message) => JsonSerializer.Serialize(
        new RpcResponse(RpcProtocol.Version, id, null, new RpcError(code, message)),
        RpcProtocol.Json);
}
