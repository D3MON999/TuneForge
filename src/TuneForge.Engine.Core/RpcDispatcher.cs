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
                RpcMethods.UndoAll when tweaks is not null =>
                    Success(request.Id, tweaks.UndoAll()),
                RpcMethods.BootTimes =>
                    Success(request.Id, BootHistory.Diagnose()),
                RpcMethods.RemoveLocks when tweaks is not null =>
                    Success(request.Id, tweaks.RemoveLocks(Parameters<RemoveLocksRequest>(request).Ids)),
                RpcMethods.Cleanup when system is not null =>
                    Success(request.Id, system.Cleanup.CleanWithAdminRights(Parameters<CleanupRequest>(request).Ids)),
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
                RpcMethods.AdvancedSet when system is not null =>
                    Success(request.Id, system.Advanced.Set(Parameters<AdvancedSetRequest>(request))),
                RpcMethods.SecuritySet when system is not null =>
                    Success(request.Id, system.Security.TurnOn(Parameters<SecuritySetRequest>(request))),
                RpcMethods.BandwidthSet when system is not null =>
                    Success(request.Id, system.Bandwidth.Set(Parameters<BandwidthRule>(request))),
                RpcMethods.BandwidthRemove when system is not null =>
                    Success(request.Id, system.Bandwidth.Remove(Parameters<BandwidthRule>(request).Program)),
                RpcMethods.BiosRestart when system is not null => Success(request.Id, system.Bios.Restart()),
                RpcMethods.BiosCancel when system is not null => Success(request.Id, system.Bios.Cancel()),
                RpcMethods.DriverRemove when system?.DisplayDrivers is not null =>
                    Success(request.Id, system.DisplayDrivers.Remove(Parameters<DriverPageRequest>(request).Vendor)),
                RpcMethods.DriverRestore when system?.DisplayDrivers is not null =>
                    Success(request.Id, system.DisplayDrivers.Restore(Parameters<DriverPageRequest>(request).Vendor)),
                RpcMethods.RestorePointsList when system?.RestorePoints is not null =>
                    Success(request.Id, system.RestorePoints.List()),
                RpcMethods.RestorePointDelete when system?.RestorePoints is not null =>
                    Success(request.Id, system.RestorePoints.Delete(Parameters<RestorePointRequest>(request).Sequence)),
                RpcMethods.HostsRemove when system?.Hosts is not null =>
                    Success(request.Id, system.Hosts.Remove(Parameters<HostsRemoveRequest>(request).Lines)),
                RpcMethods.HostsRestore when system?.Hosts is not null =>
                    Success(request.Id, system.Hosts.Restore()),
                RpcMethods.DriveWear =>
                    Success(request.Id, DriveHealth.Read()),
                RpcMethods.SleepBlockers when system?.Tools is not null =>
                    Success(request.Id, WakeTools.Blockers(system.Tools)),
                RpcMethods.WakeDeviceSet when system?.Tools is not null =>
                    Success(request.Id, WakeTools.SetDevice(system.Tools, Parameters<WakeDeviceRequest>(request))),
                RpcMethods.TaskSet when system?.Runner is not null =>
                    Success(request.Id, ScheduledTasks.Set(system.Runner, Parameters<TaskSetRequest>(request))),
                RpcMethods.MenuSet when tweaks is not null =>
                    Success(request.Id, tweaks.ApplyBuilt(RightClickMenu.BuildChange(Parameters<MenuSetRequest>(request)))),
                RpcMethods.FirewallBlock when system?.Firewall is not null =>
                    Success(request.Id, system.Firewall.Block(Parameters<FirewallRequest>(request).Program)),
                RpcMethods.FirewallUnblock when system?.Firewall is not null =>
                    Success(request.Id, system.Firewall.Unblock(Parameters<FirewallRequest>(request).Program)),
                RpcMethods.UpdatePause =>
                    Success(request.Id, UpdatePause.Set(Parameters<UpdatePauseRequest>(request).Weeks, DateTime.UtcNow)),
                RpcMethods.DriverExport when system?.Drivers is not null =>
                    Success(request.Id, system.Drivers.Export(DateTime.Now)),
                RpcMethods.DriverImport when system?.Drivers is not null =>
                    Success(request.Id, system.Drivers.Import(Parameters<FolderRequest>(request).Folder)),
                RpcMethods.DriverBackupDelete when system?.DisplayDrivers is not null =>
                    Success(request.Id, system.DisplayDrivers.DeleteBackup(Parameters<DriverPageRequest>(request).Vendor)),
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
