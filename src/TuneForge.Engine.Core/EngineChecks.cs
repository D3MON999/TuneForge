namespace TuneForge.Engine.Core;

/// <summary>Refuses to run with admin rights in conditions that would let other code in.</summary>
public static class EngineChecks
{
    /// <summary>Settings that make .NET load extra code. Any program a person runs can set these for them.</summary>
    private static readonly string[] CodeLoadingVariables =
    [
        "DOTNET_STARTUP_HOOKS",
        "CORECLR_ENABLE_PROFILING",
        "COR_ENABLE_PROFILING",
        "DOTNET_ENABLE_PROFILING",
        "DOTNET_AdditionalDeps",
        "DOTNET_ADDITIONAL_DEPS",
        "DOTNET_GCName",
        "DOTNET_GCPath",
        "COMPlus_GCName",
        "COMPlus_GCPath",
    ];

    public static bool UnsafeEnvironment(Func<string, string?> read) =>
        CodeLoadingVariables.Any(name => !string.IsNullOrEmpty(read(name)) && read(name) != "0");

    public static bool InProtectedFolder(string folder, string programFiles) =>
        Path.GetFullPath(folder).StartsWith(Path.TrimEndingDirectorySeparator(Path.GetFullPath(programFiles)) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
}
