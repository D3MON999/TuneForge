using System.Diagnostics;
using System.IO.Pipes;
using System.Security.Principal;
using TuneForge.Contracts;
using TuneForge.Engine.Core;
using TuneForge.Engine.Core.Catalog;
using Xunit;

namespace TuneForge.Engine.Tests;

public sealed class SafeDeleteTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "tf-safe-" + Guid.NewGuid().ToString("N"));
    private readonly string _outside = Path.Combine(Path.GetTempPath(), "tf-out-" + Guid.NewGuid().ToString("N"));

    public SafeDeleteTests()
    {
        Directory.CreateDirectory(_root);
        Directory.CreateDirectory(_outside);
    }

    public void Dispose()
    {
        var link = Path.Combine(_root, "link");
        if (Directory.Exists(link))
        {
            Process.Start(new ProcessStartInfo("cmd.exe", $"/c rmdir \"{link}\"") { CreateNoWindow = true, UseShellExecute = false })!.WaitForExit();
        }

        Directory.Delete(_root, true);
        Directory.Delete(_outside, true);
    }

    [Fact]
    public void DeletesAFileInsideTheFolder()
    {
        var file = Path.Combine(_root, "a.tmp");
        File.WriteAllText(file, "x");

        Assert.True(SafeDelete.Delete(file, _root));
        Assert.False(File.Exists(file));
    }

    [Fact]
    public void WontDeleteThroughALinkOutOfTheFolder()
    {
        var target = Path.Combine(_outside, "important.txt");
        File.WriteAllText(target, "keep me");
        var link = Path.Combine(_root, "link");
        Process.Start(new ProcessStartInfo("cmd.exe", $"/c mklink /J \"{link}\" \"{_outside}\"") { CreateNoWindow = true, UseShellExecute = false })!.WaitForExit();

        Assert.False(SafeDelete.Delete(Path.Combine(link, "important.txt"), _root));
        Assert.True(File.Exists(target));
    }

    [Fact]
    public void AFolderWithASimilarNameIsStillOutside()
    {
        var sibling = _root + "-other";
        Directory.CreateDirectory(sibling);
        try
        {
            var file = Path.Combine(sibling, "a.tmp");
            File.WriteAllText(file, "x");

            Assert.False(SafeDelete.Delete(file, _root));
            Assert.True(File.Exists(file));
        }
        finally
        {
            Directory.Delete(sibling, true);
        }
    }
}

public class ProtectedFolderTests
{
    [Fact]
    public void TrustsOnlyAdministratorsAndSystemAsOwners()
    {
        Assert.True(JournalLocation.IsTrustedOwner(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null)));
        Assert.True(JournalLocation.IsTrustedOwner(new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null)));
        Assert.False(JournalLocation.IsTrustedOwner(new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null)));
        Assert.False(JournalLocation.IsTrustedOwner(new SecurityIdentifier("S-1-5-21-1-2-3-1001")));
    }
}

public class EngineChecksTests
{
    [Theory]
    [InlineData("DOTNET_STARTUP_HOOKS", "evil.dll")]
    [InlineData("CORECLR_ENABLE_PROFILING", "1")]
    [InlineData("COR_ENABLE_PROFILING", "1")]
    [InlineData("DOTNET_GCName", "evil.dll")]
    [InlineData("DOTNET_GCPath", @"C:\evil.dll")]
    [InlineData("COMPlus_GCName", "evil.dll")]
    [InlineData("COMPlus_GCPath", @"C:\evil.dll")]
    public void RefusesCodeLoadingSettings(string name, string value)
    {
        Assert.True(EngineChecks.UnsafeEnvironment(variable => variable == name ? value : null));
    }

    [Fact]
    public void AllowsACleanEnvironment()
    {
        Assert.False(EngineChecks.UnsafeEnvironment(_ => null));
        Assert.False(EngineChecks.UnsafeEnvironment(variable => variable == "CORECLR_ENABLE_PROFILING" ? "0" : null));
    }

    [Theory]
    [InlineData(@"C:\Program Files\TuneForge\", true)]
    [InlineData(@"c:\program files\tuneforge", true)]
    [InlineData(@"C:\Program Files Evil\TuneForge\", false)]
    [InlineData(@"C:\Users\Me\Downloads\", false)]
    public void KnowsProtectedFolders(string folder, bool protectedFolder)
    {
        Assert.Equal(protectedFolder, EngineChecks.InProtectedFolder(folder, @"C:\Program Files"));
    }
}

public class CleanupRightsTests
{
    [Fact]
    public void TheEngineRefusesCategoriesThatDontNeedAdmin()
    {
        var user = new CleanupDefinition("mine", "Mine", "n", false, true, []);
        var admin = new CleanupDefinition("system", "System", "n", true, true, []);
        var service = new CleanupService([user, admin], new FakeBin());

        Assert.Equal(0, service.CleanWithAdminRights(["system"]).Files);
        Assert.Throws<CatalogRequestException>(() => service.CleanWithAdminRights(["mine", "system"]));
    }
}

public class EngineIdleTests
{
    [Fact]
    public async Task AQuietClientIsDisconnected()
    {
        var name = $"TuneForge.Test.{Guid.NewGuid():N}";
        using var identity = WindowsIdentity.GetCurrent();
        var server = new EngineServer(new EngineOptions(name, identity.User!, Environment.ProcessPath!, TimeSpan.FromSeconds(10)) { IdleLimit = TimeSpan.FromMilliseconds(200) });
        var running = server.RunAsync(CancellationToken.None);
        await using var client = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.Asynchronous);
        await client.ConnectAsync(5000);

        await running.WaitAsync(TimeSpan.FromSeconds(5));
    }
}
