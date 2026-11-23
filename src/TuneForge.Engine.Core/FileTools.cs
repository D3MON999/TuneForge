using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using TuneForge.Contracts;
using TuneForge.Engine.Core.Catalog;

namespace TuneForge.Engine.Core;

/// <summary>
/// Measures a drive by writing and reading a temporary file, straight to the disk without Windows' cache,
/// so the numbers are the drive's own. The file is always deleted afterwards.
/// </summary>
public static class DiskSpeed
{
    private const int Block = 4 * 1024 * 1024;
    private const int Small = 4096;
    private const long Size = 512L * 1024 * 1024;
    private const FileOptions NoBuffering = (FileOptions)0x20000000;
    private static readonly TimeSpan RandomTime = TimeSpan.FromSeconds(3);

    public static DiskSpeedResult Run(string drive)
    {
        var info = DriveInfo.GetDrives().FirstOrDefault(item => item.Name.Equals(drive, StringComparison.OrdinalIgnoreCase) && item.IsReady)
            ?? throw new CatalogRequestException("That drive isn't available.");
        if (info.DriveType is not (DriveType.Fixed or DriveType.Removable))
        {
            throw new CatalogRequestException("Only drives in or plugged into this PC can be tested.");
        }

        if (info.AvailableFreeSpace < Size * 2)
        {
            return Failed(drive, "There isn't enough free space for the test. It needs about 1 GB.");
        }

        var running = Process.GetProcesses();
        try
        {
            if (ProtectedProcesses.AnyGameRunning(running.Select(process => process.ProcessName)))
            {
                return Failed(drive, "Close your game first. The test keeps the drive busy for about 20 seconds.");
            }
        }
        finally
        {
            foreach (var process in running)
            {
                process.Dispose();
            }
        }

        var system = Path.GetPathRoot(Environment.SystemDirectory) ?? "C:\\";
        var folder = info.Name.Equals(system, StringComparison.OrdinalIgnoreCase) ? Path.GetTempPath() : info.Name;
        var file = Path.Combine(folder, $"tuneforge-speed-{Guid.NewGuid():N}.tmp");

        // Unbuffered reads and writes need memory lined up on a 4 KB boundary.
        var raw = GC.AllocateArray<byte>(Block + Small, pinned: true);
        var shift = (int)((Small - (Marshal.UnsafeAddrOfPinnedArrayElement(raw, 0) % Small)) % Small);
        var buffer = raw.AsMemory(shift, Block);
        RandomNumberGenerator.Fill(buffer.Span);

        try
        {
            double write, read, iops;
            var timer = Stopwatch.StartNew();
            using (var handle = File.OpenHandle(file, FileMode.CreateNew, FileAccess.Write, FileShare.None, FileOptions.WriteThrough | NoBuffering))
            {
                for (long offset = 0; offset < Size; offset += Block)
                {
                    RandomAccess.Write(handle, buffer.Span, offset);
                }
            }

            write = Size / 1048576d / timer.Elapsed.TotalSeconds;

            timer.Restart();
            using (var handle = File.OpenHandle(file, FileMode.Open, FileAccess.Read, FileShare.None, NoBuffering | FileOptions.SequentialScan))
            {
                for (long offset = 0; offset < Size; offset += Block)
                {
                    RandomAccess.Read(handle, buffer.Span, offset);
                }
            }

            read = Size / 1048576d / timer.Elapsed.TotalSeconds;

            var random = new Random(1);
            var reads = 0;
            var small = buffer[..Small];
            timer.Restart();
            using (var handle = File.OpenHandle(file, FileMode.Open, FileAccess.Read, FileShare.None, NoBuffering | FileOptions.RandomAccess))
            {
                while (timer.Elapsed < RandomTime)
                {
                    RandomAccess.Read(handle, small.Span, random.NextInt64(Size / Small) * Small);
                    reads++;
                }
            }

            iops = reads / timer.Elapsed.TotalSeconds;
            return new DiskSpeedResult(drive, Math.Round(write), Math.Round(read), Math.Round(iops));
        }
        catch (UnauthorizedAccessException)
        {
            return Failed(drive, "Windows didn't allow a test file on that drive.");
        }
        finally
        {
            try
            {
                File.Delete(file);
            }
            catch (IOException)
            {
                // Left in the temporary folder, which Cleanup clears.
            }
        }
    }

    private static DiskSpeedResult Failed(string drive, string message) => throw new CatalogRequestException(message);
}

/// <summary>Finds files that are exactly the same, by content. Removing one always leaves another copy.</summary>
public static class DuplicateFinder
{
    public const long MinBytes = 10L * 1024 * 1024;
    private const int Sample = 64 * 1024;
    private const int MaxGroups = 200;

    /// <summary>Game libraries share identical files between games, and removing one breaks a game.</summary>
    private static readonly HashSet<string> Skipped = new(StringComparer.OrdinalIgnoreCase)
    {
        "Windows", "Program Files", "Program Files (x86)", "ProgramData", "$Recycle.Bin", "System Volume Information", "Recovery",
        "AppData", "SteamLibrary", "steamapps", "Steam", "Epic Games", "XboxGames", "Riot Games", "GOG Games", "EA Games", "Ubisoft",
        "Battle.net", "node_modules", ".git",
    };

    public static IReadOnlyList<string> PersonalFolders(string downloads) =>
        new[]
        {
            Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
            Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
            downloads,
            Environment.GetFolderPath(Environment.SpecialFolder.MyPictures),
            Environment.GetFolderPath(Environment.SpecialFolder.MyVideos),
            Environment.GetFolderPath(Environment.SpecialFolder.MyMusic),
        }
        .Where(path => path.Length > 0 && Directory.Exists(path))
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .ToList();

    /// <summary>"personal" or another drive's root. The Windows drive is only searched through your own folders.</summary>
    public static IReadOnlyList<string> Roots(string where, string downloads)
    {
        if (where == "personal")
        {
            return PersonalFolders(downloads);
        }

        var system = Path.GetPathRoot(Environment.SystemDirectory) ?? "C:\\";
        var drive = DriveInfo.GetDrives().FirstOrDefault(item => item.Name.Equals(where, StringComparison.OrdinalIgnoreCase) && item.IsReady && item.DriveType is DriveType.Fixed or DriveType.Removable);
        if (drive is null || drive.Name.Equals(system, StringComparison.OrdinalIgnoreCase))
        {
            throw new CatalogRequestException("Pick your own folders, or a drive other than the Windows drive.");
        }

        return [drive.Name];
    }

    /// <summary>Inside a searched place, and not inside a folder the search skips, like a game library.</summary>
    public static bool IsAllowed(string path, IReadOnlyList<string> roots) =>
        Path.IsPathFullyQualified(path)
        && roots.Any(root => OldDownloads.IsInside(root, path)
            && !Path.GetRelativePath(root, Path.GetFullPath(path)).Split(Path.DirectorySeparatorChar).SkipLast(1).Any(Skipped.Contains));

    private static IEnumerable<FileInfo> Files(string root, CancellationToken cancel)
    {
        var options = new System.IO.EnumerationOptions { IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint | FileAttributes.System };
        var pending = new Stack<DirectoryInfo>();
        pending.Push(new DirectoryInfo(root));
        while (pending.Count > 0)
        {
            cancel.ThrowIfCancellationRequested();
            var folder = pending.Pop();
            IEnumerable<FileSystemInfo> items;
            try
            {
                items = folder.EnumerateFileSystemInfos("*", options).ToList();
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                continue;
            }

            foreach (var item in items)
            {
                if (item is DirectoryInfo child)
                {
                    if (!Skipped.Contains(child.Name))
                    {
                        pending.Push(child);
                    }
                }
                else if (item is FileInfo file && file.Length >= MinBytes)
                {
                    yield return file;
                }
            }
        }
    }

    public static DuplicateReport Scan(IReadOnlyList<string> roots, CancellationToken cancel = default)
    {
        var bySize = roots.SelectMany(root => Files(root, cancel))
            .DistinctBy(file => file.FullName, StringComparer.OrdinalIgnoreCase)
            .GroupBy(file => file.Length)
            .Where(group => group.Count() > 1);

        var groups = new List<DuplicateGroup>();
        foreach (var sameSize in bySize)
        {
            cancel.ThrowIfCancellationRequested();

            // A quick look at the start and end first, so most files never need reading in full.
            var candidates = sameSize.GroupBy(file => TryHash(file.FullName, quick: true)).Where(group => group.Key is not null && group.Count() > 1);
            foreach (var quick in candidates)
            {
                foreach (var same in quick.GroupBy(file => TryHash(file.FullName, quick: false)).Where(group => group.Key is not null && group.Count() > 1))
                {
                    groups.Add(new DuplicateGroup(
                        sameSize.Key,
                        same.OrderBy(file => file.LastWriteTimeUtc)
                            .Select(file => new DuplicateFile(file.FullName, file.Name, file.LastWriteTimeUtc.ToString("O", CultureInfo.InvariantCulture)))
                            .ToList()));
                }
            }
        }

        var top = groups.OrderByDescending(group => group.Bytes * (group.Files.Count - 1)).Take(MaxGroups).ToList();
        return new DuplicateReport(top, top.Sum(group => group.Bytes * (group.Files.Count - 1)));
    }

    public static string? TryHash(string path, bool quick)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20, FileOptions.SequentialScan);
            if (!quick || stream.Length <= Sample * 2)
            {
                return Convert.ToHexString(SHA256.HashData(stream));
            }

            var bytes = new byte[Sample * 2];
            stream.ReadExactly(bytes, 0, Sample);
            stream.Seek(-Sample, SeekOrigin.End);
            stream.ReadExactly(bytes, Sample, Sample);
            return Convert.ToHexString(SHA256.HashData(bytes));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>Each file goes only if the copy it's kept for still exists and is identical, so no file can lose its last copy.</summary>
    public static RecycleResult Recycle(IReadOnlyList<DuplicatePick> picks, IReadOnlyList<string> roots, Func<string, bool> recycle)
    {
        int moved = 0, failed = 0;
        var removed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var pick in picks)
        {
            var safe = !pick.Path.Equals(pick.Keep, StringComparison.OrdinalIgnoreCase)
                && !removed.Contains(pick.Keep)
                && IsAllowed(pick.Path, roots)
                && File.Exists(pick.Path) && File.Exists(pick.Keep)
                && !File.GetAttributes(pick.Path).HasFlag(FileAttributes.ReparsePoint)
                && new FileInfo(pick.Path).Length == new FileInfo(pick.Keep).Length
                && TryHash(pick.Path, quick: false) is { } hash && hash == TryHash(pick.Keep, quick: false);
            if (safe && recycle(pick.Path))
            {
                moved++;
                removed.Add(pick.Path);
            }
            else
            {
                failed++;
            }
        }

        return new RecycleResult(moved, failed);
    }
}

/// <summary>Finds folders a program left behind after it was uninstalled, by matching its name exactly.</summary>
public static partial class LeftoverFinder
{
    private static readonly HashSet<string> TooGeneral = new(StringComparer.OrdinalIgnoreCase)
    {
        "microsoft", "windows", "google", "nvidia", "intel", "amd", "common", "commonfiles", "temp", "packages", "programs", "cache", "app", "apps", "games", "steam",
    };

    [GeneratedRegex(@"\(.*?\)|\bv?\d+(\.\d+)+\b|\b(x64|x86|64-bit|32-bit|arm64)\b", RegexOptions.IgnoreCase)]
    private static partial Regex Noise();

    public static string Key(string name) =>
        new(Noise().Replace(name, " ").ToLowerInvariant().Where(char.IsLetterOrDigit).ToArray());

    public static IEnumerable<(string Root, bool Removable)> DefaultRoots(UserPaths user) =>
    [
        (user.Roaming, true),
        (user.Local, true),
        (Path.Combine(user.Profile, "AppData", "LocalLow"), true),
        (Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), false),
        (Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), false),
        (Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), false),
    ];

    /// <summary>Looks one level deep and one level under a publisher's folder, like Roaming\Maker\App.</summary>
    public static IReadOnlyList<LeftoverFolder> Find(string appName, IEnumerable<(string Root, bool Removable)> roots, Func<string, long> size)
    {
        var key = Key(appName);
        if (key.Length < 4 || TooGeneral.Contains(key))
        {
            return [];
        }

        var found = new List<LeftoverFolder>();
        foreach (var (root, removable) in roots)
        {
            if (string.IsNullOrEmpty(root) || !Directory.Exists(root))
            {
                continue;
            }

            foreach (var folder in Children(root))
            {
                if (Key(Path.GetFileName(folder)) == key)
                {
                    found.Add(new LeftoverFolder(folder, size(folder), removable));
                    continue;
                }

                foreach (var inner in Children(folder))
                {
                    if (Key(Path.GetFileName(inner)) == key)
                    {
                        found.Add(new LeftoverFolder(inner, size(inner), removable));
                    }
                }
            }
        }

        return found;
    }

    private static IEnumerable<string> Children(string folder)
    {
        try
        {
            return new DirectoryInfo(folder)
                .EnumerateDirectories("*", new System.IO.EnumerationOptions { IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint })
                .Select(child => child.FullName)
                .ToList();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    public static long FolderSize(string folder)
    {
        try
        {
            return new DirectoryInfo(folder)
                .EnumerateFiles("*", new System.IO.EnumerationOptions { IgnoreInaccessible = true, RecurseSubdirectories = true, AttributesToSkip = FileAttributes.ReparsePoint })
                .Sum(file => file.Length);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return 0;
        }
    }
}

/// <summary>Starts a delayed startup program the way Windows would have, from its saved command.</summary>
public static class StartupLaunch
{
    public static (string File, string Arguments)? Split(string command)
    {
        var text = Environment.ExpandEnvironmentVariables(command).Trim();
        if (text.Length == 0)
        {
            return null;
        }

        if (text.StartsWith('"'))
        {
            var close = text.IndexOf('"', 1);
            return close < 0 ? (text.Trim('"'), string.Empty) : (text[1..close], text[(close + 1)..].Trim());
        }

        var exe = text.IndexOf(".exe", StringComparison.OrdinalIgnoreCase);
        return exe < 0 ? (text, string.Empty) : (text[..(exe + 4)], text[(exe + 4)..].Trim());
    }

    public static bool Start(string command)
    {
        if (Split(command) is not { } split || !File.Exists(split.File))
        {
            return false;
        }

        try
        {
            using var _ = Process.Start(new ProcessStartInfo(split.File, split.Arguments)
            {
                UseShellExecute = true,
                WorkingDirectory = Path.GetDirectoryName(split.File) ?? string.Empty,
            });
            return true;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return false;
        }
    }
}
