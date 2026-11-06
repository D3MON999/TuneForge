using System.Runtime.InteropServices;
using System.Security.Principal;
using Microsoft.Win32;
using TuneForge.Contracts;
using TuneForge.Engine.Core.Catalog;

namespace TuneForge.Engine.Core;

public sealed record CleanupDefinition(
    string Id,
    string Title,
    string Note,
    bool NeedsAdmin,
    bool DefaultOn,
    IReadOnlyList<string> Roots,
    string Pattern = "*",
    TimeSpan? MinimumAge = null,
    bool IsRecycleBin = false);

public interface IRecycleBin
{
    (long Bytes, long Items) Query();

    void Empty();
}

public sealed class WindowsRecycleBin : IRecycleBin
{
    private const uint NoConfirmation = 0x1;
    private const uint NoProgressUi = 0x2;
    private const uint NoSound = 0x4;

    public (long Bytes, long Items) Query()
    {
        var info = new RecycleBinInfo { Size = Marshal.SizeOf<RecycleBinInfo>() };
        return SHQueryRecycleBinW(null, ref info) == 0 ? (info.Bytes, info.Items) : (0, 0);
    }

    public void Empty() => SHEmptyRecycleBinW(IntPtr.Zero, null, NoConfirmation | NoProgressUi | NoSound);

    [StructLayout(LayoutKind.Sequential)]
    private struct RecycleBinInfo
    {
        public int Size;
        public long Bytes;
        public long Items;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHQueryRecycleBinW(string? root, ref RecycleBinInfo info);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHEmptyRecycleBinW(IntPtr window, string? root, uint flags);
}

public sealed record UserPaths(string Profile)
{
    public string Local => Path.Combine(Profile, "AppData", "Local");

    public string Roaming => Path.Combine(Profile, "AppData", "Roaming");

    /// <summary>Finds a user's folders from Windows, so the elevated engine can't get the admin's by mistake.</summary>
    public static UserPaths For(SecurityIdentifier user)
    {
        using var key = Registry.LocalMachine.OpenSubKey(
            $@"SOFTWARE\Microsoft\Windows NT\CurrentVersion\ProfileList\{user.Value}");
        return new UserPaths(key?.GetValue("ProfileImagePath") as string
            ?? throw new DirectoryNotFoundException("This user's profile wasn't found."));
    }
}

/// <summary>Scans and removes temporary files. Only folders named here can ever be touched.</summary>
public sealed class CleanupService(IReadOnlyList<CleanupDefinition> definitions, IRecycleBin recycleBin)
{
    private static readonly EnumerationOptions WalkOptions = new()
    {
        RecurseSubdirectories = true,
        IgnoreInaccessible = true,
        ReturnSpecialDirectories = false,

        // Links are skipped, so cleanup can never follow one out of these folders.
        AttributesToSkip = FileAttributes.ReparsePoint,
    };

    public static IReadOnlyList<CleanupDefinition> Standard(UserPaths user)
    {
        var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        var programData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
        var day = TimeSpan.FromDays(1);

        return
        [
            new("user-temp", "Your temporary files", "Files older than a day in your Temp folder.", false, true,
                [Path.Combine(user.Local, "Temp")], MinimumAge: day),
            new("thumbnails", "Thumbnail cache", "Windows rebuilds these when needed.", false, true,
                [Path.Combine(user.Local, "Microsoft", "Windows", "Explorer")], "thumbcache_*.db"),
            new("shaders", "Graphics shader caches", "Games may stutter briefly while these rebuild.", false, false,
                [
                    Path.Combine(user.Local, "D3DSCache"),
                    Path.Combine(user.Local, "NVIDIA", "DXCache"),
                    Path.Combine(user.Local, "NVIDIA", "GLCache"),
                    Path.Combine(user.Local, "AMD", "DxCache"),
                ]),
            new("browsers", "Browser caches", "Pages load a little slower at first. Sign-ins and history stay.", false, false,
                BrowserCaches(user)),
            new("crash-dumps", "App crash reports", "Saved reports from apps that crashed.", false, true,
                [Path.Combine(user.Local, "CrashDumps")]),
            new("recycle-bin", "Recycle Bin", "Everything in it is deleted permanently.", false, false,
                [], IsRecycleBin: true),
            new("windows-temp", "Windows temporary files", "Files older than a day in the Windows Temp folder.", true, true,
                [Path.Combine(windows, "Temp")], MinimumAge: day),
            new("system-crash", "System crash files", "Memory dumps and Windows error reports.", true, false,
                [
                    Path.Combine(windows, "Minidump"),
                    Path.Combine(windows, "LiveKernelReports"),
                    Path.Combine(programData, "Microsoft", "Windows", "WER", "ReportArchive"),
                    Path.Combine(programData, "Microsoft", "Windows", "WER", "ReportQueue"),
                ]),
        ];
    }

    public IReadOnlyList<CleanupCategoryInfo> Scan() => definitions.Select(definition =>
    {
        var (bytes, files) = definition.IsRecycleBin ? recycleBin.Query() : Measure(definition);
        return new CleanupCategoryInfo(
            definition.Id, definition.Title, definition.Note, definition.NeedsAdmin, definition.DefaultOn, bytes, files);
    }).ToList();

    public bool NeedsAdmin(string id) => Find(id).NeedsAdmin;

    public CleanupResult Clean(IEnumerable<string> ids, bool elevated)
    {
        long freed = 0, files = 0, skipped = 0;
        foreach (var definition in ids.Distinct(StringComparer.Ordinal).Select(Find).ToList())
        {
            if (definition.NeedsAdmin && !elevated)
            {
                throw new CatalogRequestException($"{definition.Title} needs admin rights.");
            }

            if (definition.IsRecycleBin)
            {
                var (bytes, items) = recycleBin.Query();
                recycleBin.Empty();
                freed += bytes;
                files += items;
                continue;
            }

            foreach (var root in ExistingRoots(definition))
            {
                foreach (var file in Candidates(root, definition))
                {
                    try
                    {
                        var size = file.Length;
                        file.Delete();
                        freed += size;
                        files++;
                    }
                    catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                    {
                        // In use or protected, so it stays. That's expected for temp folders.
                        skipped++;
                    }
                }

                RemoveEmptyFolders(root);
            }
        }

        return new CleanupResult(freed, files, skipped);
    }

    private CleanupDefinition Find(string id) =>
        definitions.FirstOrDefault(definition => definition.Id == id)
        ?? throw new CatalogRequestException($"Unknown cleanup category: {id}");

    private static (long Bytes, long Files) Measure(CleanupDefinition definition)
    {
        long bytes = 0, files = 0;
        foreach (var root in ExistingRoots(definition))
        {
            foreach (var file in Candidates(root, definition))
            {
                try
                {
                    bytes += file.Length;
                    files++;
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                {
                    // It vanished or is unreadable, so don't count it.
                }
            }
        }

        return (bytes, files);
    }

    private static IEnumerable<DirectoryInfo> ExistingRoots(CleanupDefinition definition) =>
        definition.Roots
            .Select(root => new DirectoryInfo(root))
            .Where(root => root.Exists && !root.Attributes.HasFlag(FileAttributes.ReparsePoint));

    private static IEnumerable<FileInfo> Candidates(DirectoryInfo root, CleanupDefinition definition)
    {
        var cutoff = DateTime.UtcNow - (definition.MinimumAge ?? TimeSpan.Zero);
        return root.EnumerateFiles(definition.Pattern, WalkOptions)
            .Where(file => definition.MinimumAge is null || file.LastWriteTimeUtc <= cutoff);
    }

    private static void RemoveEmptyFolders(DirectoryInfo root)
    {
        var folders = root.EnumerateDirectories("*", WalkOptions).OrderByDescending(folder => folder.FullName.Length);
        foreach (var folder in folders.ToList())
        {
            try
            {
                if (!folder.EnumerateFileSystemInfos().Any())
                {
                    folder.Delete();
                }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                // Something is using it, so leave it.
            }
        }
    }

    private static List<string> BrowserCaches(UserPaths user)
    {
        var caches = new List<string>();
        var chromium = new[]
        {
            Path.Combine(user.Local, "Microsoft", "Edge", "User Data"),
            Path.Combine(user.Local, "Google", "Chrome", "User Data"),
            Path.Combine(user.Local, "BraveSoftware", "Brave-Browser", "User Data"),
        };

        foreach (var data in chromium.Where(Directory.Exists))
        {
            foreach (var profile in Directory.EnumerateDirectories(data))
            {
                caches.Add(Path.Combine(profile, "Cache"));
                caches.Add(Path.Combine(profile, "Code Cache"));
                caches.Add(Path.Combine(profile, "GPUCache"));
            }
        }

        var firefox = Path.Combine(user.Local, "Mozilla", "Firefox", "Profiles");
        if (Directory.Exists(firefox))
        {
            caches.AddRange(Directory.EnumerateDirectories(firefox).Select(profile => Path.Combine(profile, "cache2")));
        }

        return caches;
    }
}
