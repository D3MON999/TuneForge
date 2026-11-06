using System.Runtime.InteropServices;

namespace TuneForge.Engine.Core;

public interface IRestorePointService
{
    /// <summary>Returns "requested", or "unavailable" when System Restore can't make one.</summary>
    string Create(string description);
}

/// <summary>Asks Windows for a restore point. Windows may skip it if one was made recently.</summary>
public sealed class SystemRestorePointService : IRestorePointService
{
    private const int BeginSystemChange = 100;
    private const int EndSystemChange = 101;
    private const int ModifySettings = 12;

    public string Create(string description)
    {
        var start = new RestorePointInfo
        {
            EventType = BeginSystemChange,
            RestorePointType = ModifySettings,
            Description = description,
        };

        try
        {
            if (!SRSetRestorePointW(ref start, out var status))
            {
                return "unavailable";
            }

            var finish = new RestorePointInfo
            {
                EventType = EndSystemChange,
                RestorePointType = ModifySettings,
                SequenceNumber = status.SequenceNumber,
                Description = string.Empty,
            };
            SRSetRestorePointW(ref finish, out _);
            return "requested";
        }
        catch (Exception exception) when (exception is DllNotFoundException or EntryPointNotFoundException)
        {
            return "unavailable";
        }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode, Pack = 1)]
    private struct RestorePointInfo
    {
        public int EventType;
        public int RestorePointType;
        public long SequenceNumber;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
        public string Description;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    private struct ManagerStatus
    {
        public uint Status;
        public long SequenceNumber;
    }

    [DllImport("srclient.dll", CharSet = CharSet.Unicode)]
    private static extern bool SRSetRestorePointW(ref RestorePointInfo info, out ManagerStatus status);
}
