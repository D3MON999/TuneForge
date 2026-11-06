using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace TuneForge.Engine.Core;

/// <summary>
/// Deletes through an open handle after checking where the file really is. Folders like Windows\Temp
/// can be written by anyone, so a folder could be swapped for a link between finding a file and
/// deleting it. Going by path, admin rights would then delete something else. A handle can't move.
/// </summary>
public static class SafeDelete
{
    private const uint DeleteAccess = 0x00010000;
    private const uint ReadAttributes = 0x0080;
    private const uint ShareAll = 7;
    private const uint OpenExisting = 3;
    private const uint OpenReparsePoint = 0x00200000;
    private const uint BackupSemantics = 0x02000000;
    private const int FileDispositionInfo = 4;

    /// <summary>Returns false if the file turned out to be outside the root. It's left alone.</summary>
    public static bool Delete(string path, string root)
    {
        using var file = Open(path, DeleteAccess | ReadAttributes);
        var inside = FinalPath(file);
        var boundary = RootPath(root);
        if (!inside.StartsWith(boundary, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var disposition = new FileDisposition { DeleteFile = 1 };
        if (!SetFileInformationByHandle(file, FileDispositionInfo, ref disposition, (uint)Marshal.SizeOf<FileDisposition>()))
        {
            throw new IOException("The file couldn't be deleted.", new Win32Exception());
        }

        return true;
    }

    private static string RootPath(string root)
    {
        using var folder = Open(root, ReadAttributes);
        return FinalPath(folder).TrimEnd('\\') + '\\';
    }

    private static SafeFileHandle Open(string path, uint access)
    {
        var handle = CreateFile(path, access, ShareAll, 0, OpenExisting, OpenReparsePoint | BackupSemantics, 0);
        return handle.IsInvalid ? throw new IOException("The file couldn't be opened.", new Win32Exception()) : handle;
    }

    private static string FinalPath(SafeFileHandle handle)
    {
        var buffer = new StringBuilder(32768);
        var length = GetFinalPathNameByHandle(handle, buffer, (uint)buffer.Capacity, 0);
        return length == 0 || length >= buffer.Capacity
            ? throw new IOException("The file's location couldn't be read.", new Win32Exception())
            : buffer.ToString();
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FileDisposition
    {
        public byte DeleteFile;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "CreateFileW")]
    private static extern SafeFileHandle CreateFile(string name, uint access, uint share, nint security, uint disposition, uint flags, nint template);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "GetFinalPathNameByHandleW")]
    private static extern uint GetFinalPathNameByHandle(SafeFileHandle file, StringBuilder path, uint size, uint flags);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetFileInformationByHandle(SafeFileHandle file, int kind, ref FileDisposition information, uint size);
}
