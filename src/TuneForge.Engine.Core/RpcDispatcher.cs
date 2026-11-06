using System.Reflection;
using System.Security.Principal;
using System.Text.Json;
using TuneForge.Contracts;
using TuneForge.Engine.Core.Catalog;

namespace TuneForge.Engine.Core;

/// <summary>Maps the fixed list of method names to handlers. Unknown names are refused.</summary>
public sealed class RpcDispatcher(Action requestShutdown, TweakEngine? tweaks = null, SystemOperations? system = null)
{
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
                RpcMethods.ApplyTweaks when tweaks is not null =>
                    Success(request.Id, tweaks.Apply(Parameters<ApplyRequest>(request))),
                RpcMethods.Undo when tweaks is not null =>
                    Success(request.Id, tweaks.Undo(Parameters<UndoRequest>(request).ChangeSetId)),
                RpcMethods.Cleanup when system is not null =>
                    Success(request.Id, system.Cleanup.Clean(Parameters<CleanupRequest>(request).Ids, elevated: true)),
                RpcMethods.StartupSet when system is not null && tweaks is not null =>
                    Success(request.Id, tweaks.ApplyBuilt(system.Startup.BuildChange(Parameters<StartupSetRequest>(request)))),
                RpcMethods.ServiceSet when system is not null && tweaks is not null =>
                    Success(request.Id, tweaks.ApplyBuilt(system.Services.BuildChange(Parameters<ServiceSetRequest>(request)))),
                RpcMethods.FeatureSet when system is not null =>
                    Success(request.Id, system.Features.Set(Parameters<FeatureSetRequest>(request))),
                RpcMethods.Repair when system is not null =>
                    Success(request.Id, system.Repair.Run(Parameters<RepairRequest>(request))),
                RpcMethods.BuiltInRemove when system is not null =>
                    Success(request.Id, system.BuiltIn.Remove(Parameters<BuiltInRemoveRequest>(request).Ids)),
                RpcMethods.DnsSet when system is not null =>
                    Success(request.Id, system.Dns.Set(Parameters<DnsSetRequest>(request))),
                _ => Failure(request.Id, RpcErrorCodes.MethodNotFound, "Unknown method."),
            };
        }
        catch (CatalogRequestException exception)
        {
            return Failure(request.Id, RpcErrorCodes.InvalidParams, exception.Message);
        }
        catch (JsonException)
        {
            return Failure(request.Id, RpcErrorCodes.InvalidParams, "Invalid parameters.");
        }
        catch (Exception)
        {
            return Failure(request.Id, RpcErrorCodes.InternalError, "The engine could not complete the request.");
        }
    }

    private static T Parameters<T>(RpcRequest request) =>
        (request.Params ?? throw new JsonException()).Deserialize<T>(RpcProtocol.Json) ?? throw new JsonException();

    private string Shutdown(long id)
    {
        requestShutdown();
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
