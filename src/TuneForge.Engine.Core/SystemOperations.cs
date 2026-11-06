using TuneForge.Contracts;

namespace TuneForge.Engine.Core;

/// <summary>Everything the elevated engine can do besides catalog tweaks.</summary>
public sealed record SystemOperations(
    CleanupService Cleanup,
    StartupService Startup,
    ServiceManager Services,
    FeatureService Features,
    RepairService Repair,
    BuiltInAppService BuiltIn,
    DnsService Dns,
    AdvancedService Advanced,
    SecurityService Security,
    BandwidthLimits Bandwidth,
    BiosRestart Bios);
