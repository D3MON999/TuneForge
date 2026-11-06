using System.Globalization;
using System.ServiceProcess;
using System.Text.Json;
using System.Text.RegularExpressions;
using TuneForge.Contracts;
using TuneForge.Engine.Core.Catalog;

namespace TuneForge.Engine.Core;

public sealed record ServiceSnapshot(string Name, string DisplayName, bool Running);

public interface IServiceSource
{
    IReadOnlyList<ServiceSnapshot> List();
}

public sealed class WindowsServiceSource : IServiceSource
{
    public IReadOnlyList<ServiceSnapshot> List() => ServiceController.GetServices().Select(service =>
    {
        using (service)
        {
            return new ServiceSnapshot(service.ServiceName, service.DisplayName, service.Status == ServiceControllerStatus.Running);
        }
    }).ToList();
}

/// <summary>Shows services and changes how they start. Windows essentials and security stay locked.</summary>
public sealed partial class ServiceManager(IRegistryStore registry, IServiceSource source)
{
    private const string ServicesPath = @"SYSTEM\CurrentControlSet\Services\";

    private static readonly HashSet<string> Locked = new(StringComparer.OrdinalIgnoreCase)
    {
        // Windows can't start or stay stable without these.
        "RpcSs", "RpcEptMapper", "DcomLaunch", "LSM", "SamSs", "EventLog", "Schedule", "Power", "PlugPlay",
        "ProfSvc", "UserManager", "CoreMessagingRegistrar", "SystemEventsBroker", "BrokerInfrastructure",
        "StateRepository", "AppXSvc", "ClipSVC", "TrustedInstaller", "gpsvc", "Winmgmt", "CryptSvc", "Dnscache",
        "Dhcp", "nsi", "NlaSvc", "netprofm", "Wcmsvc", "AudioEndpointBuilder", "Audiosrv", "TimeBrokerSvc",

        // Security stays on, as decided for this project.
        "WinDefend", "Sense", "WdNisSvc", "WdFilter", "WdBoot", "SecurityHealthService", "wscsvc", "mpssvc", "BFE",
        "SgrmBroker", "VaultSvc", "KeyIso", "EFS",
    };

    private static readonly Dictionary<int, string> StartNames = new()
    {
        [0] = "boot", [1] = "system", [2] = "auto", [3] = "manual", [4] = "disabled",
    };

    private static readonly Dictionary<string, int> StartValues = new()
    {
        ["auto"] = 2, ["manual"] = 3, ["disabled"] = 4,
    };

    public IReadOnlyList<ServiceEntry> List() => source.List()
        .Select(service =>
        {
            var start = ReadStart(service.Name);
            return new ServiceEntry(service.Name, service.DisplayName, service.Running, start, IsLocked(service.Name, start));
        })
        .OrderBy(service => service.DisplayName, StringComparer.CurrentCultureIgnoreCase)
        .ToList();

    public TweakItem BuildChange(ServiceSetRequest request)
    {
        if (!SafeName().IsMatch(request.Name) || !registry.KeyExists("HKLM", ServicesPath + request.Name))
        {
            throw new CatalogRequestException("That service doesn't exist.");
        }

        if (!StartValues.TryGetValue(request.StartType, out var value))
        {
            throw new CatalogRequestException("Unknown startup type.");
        }

        if (IsLocked(request.Name, ReadStart(request.Name)))
        {
            throw new CatalogRequestException("Windows needs this service, so TuneForge won't change it.");
        }

        return new TweakItem(
            $"service:{request.Name}",
            request.Name,
            "services",
            null,
            "medium",
            null,
            "reboot",
            [],
            null,
            null,
            "TuneForge",
            [
                new RegistryAction(
                    "HKLM",
                    ServicesPath + request.Name,
                    "Start",
                    "dword",
                    JsonSerializer.SerializeToElement(value.ToString(CultureInfo.InvariantCulture))),
            ]);
    }

    private string ReadStart(string name)
    {
        var value = registry.Read("HKLM", ServicesPath + name, "Start");
        return value is not null && int.TryParse(value.Data, CultureInfo.InvariantCulture, out var start)
            && StartNames.TryGetValue(start, out var label)
                ? label
                : "unknown";
    }

    private static bool IsLocked(string name, string start) =>
        Locked.Contains(name) || ProtectedProcesses.IsProtectedService(name) || start is "boot" or "system" or "unknown";

    [GeneratedRegex(@"^[\w.\-]{1,256}$")]
    private static partial Regex SafeName();
}
