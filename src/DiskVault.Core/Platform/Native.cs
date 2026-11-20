using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace DiskVault;

internal static unsafe partial class Native
{
    public const uint GENERIC_READ = 0x80000000, GENERIC_WRITE = 0x40000000;
    public const uint FILE_SHARE_READ = 1, FILE_SHARE_WRITE = 2, FILE_SHARE_DELETE = 4;
    public const uint OPEN_EXISTING = 3;
    public const uint FILE_FLAG_SEQUENTIAL_SCAN = 0x08000000, FILE_FLAG_NO_BUFFERING = 0x20000000, FILE_FLAG_WRITE_THROUGH = 0x80000000;
    public const uint FILE_FLAG_BACKUP_SEMANTICS = 0x02000000;

    public const uint IOCTL_DISK_GET_LENGTH_INFO = 0x0007405C;
    public const uint IOCTL_DISK_GET_DRIVE_GEOMETRY = 0x00070000;
    public const uint IOCTL_DISK_GET_DRIVE_GEOMETRY_EX = 0x000700A0;
    public const uint IOCTL_DISK_GET_DRIVE_LAYOUT_EX = 0x00070050;
    public const uint IOCTL_DISK_UPDATE_PROPERTIES = 0x00070140;
    public const uint IOCTL_STORAGE_GET_DEVICE_NUMBER = 0x002D1080;
    public const uint IOCTL_STORAGE_QUERY_PROPERTY = 0x002D1400;
    public const uint IOCTL_VOLUME_GET_VOLUME_DISK_EXTENTS = 0x00560000;
    public const uint FSCTL_LOCK_VOLUME = 0x00090018, FSCTL_DISMOUNT_VOLUME = 0x00090020, FSCTL_UNLOCK_VOLUME = 0x0009001C;
    public const uint FSCTL_ALLOW_EXTENDED_DASD_IO = 0x00090083;
    public const uint IOCTL_DISK_SET_DISK_ATTRIBUTES = 0x0007C0F4;
    public const uint IOCTL_DISK_GET_DISK_ATTRIBUTES = 0x000700F0;

    public const int ERROR_ACCESS_DENIED = 5, ERROR_MORE_DATA = 234, ERROR_INSUFFICIENT_BUFFER = 122, ERROR_NO_MORE_FILES = 18;

    [LibraryImport("kernel32.dll", EntryPoint = "CreateFileW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    public static partial SafeFileHandle CreateFileW(string name, uint access, uint share, nint sa, uint disp, uint flags, nint template);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool DeviceIoControl(SafeFileHandle h, uint code, void* inBuf, int inSize, void* outBuf, int outSize, out int returned, nint overlapped);

    [LibraryImport("kernel32.dll", EntryPoint = "FindFirstVolumeW", SetLastError = true)]
    public static partial nint FindFirstVolume(char* name, int len);

    [LibraryImport("kernel32.dll", EntryPoint = "FindNextVolumeW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool FindNextVolume(nint h, char* name, int len);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool FindVolumeClose(nint h);

    [LibraryImport("kernel32.dll", EntryPoint = "GetVolumePathNamesForVolumeNameW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool GetVolumePathNamesForVolumeName(string vol, char* names, int len, out int needed);

    [LibraryImport("kernel32.dll", EntryPoint = "GetVolumeInformationW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool GetVolumeInformation(string root, char* label, int labelLen, out uint serial, out uint maxComp, out uint flags, char* fs, int fsLen);

    [LibraryImport("kernel32.dll", EntryPoint = "GetDiskFreeSpaceExW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool GetDiskFreeSpaceEx(string root, out long freeToCaller, out long total, out long free);

    [LibraryImport("kernel32.dll", EntryPoint = "GetVolumeNameForVolumeMountPointW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool GetVolumeNameForVolumeMountPoint(string mountPoint, char* name, int len);

    public static Exception Error(string what, int err) => err == ERROR_ACCESS_DENIED
        ? new UnauthorizedAccessException($"{what} を開けません: アクセスが拒否されました（管理者として実行してください）。")
        : new IOException($"{what}: {new Win32Exception(err).Message} ({err})");

    public static long? GetLength(SafeFileHandle h)
    {
        long len;
        return DeviceIoControl(h, IOCTL_DISK_GET_LENGTH_INFO, null, 0, &len, 8, out _, 0) ? len : null;
    }

    /// <summary>IOCTL_DISK_GET_DRIVE_GEOMETRY_EX のディスクサイズ（アクセス権 0 のハンドルでも取れる）。</summary>
    public static long? GetDiskSizeEx(SafeFileHandle h)
    {
        byte* g = stackalloc byte[256];
        if (!DeviceIoControl(h, IOCTL_DISK_GET_DRIVE_GEOMETRY_EX, null, 0, g, 256, out _, 0)) return null;
        return *(long*)(g + 24);
    }

    public static bool IsAdmin()
    {
        using var id = System.Security.Principal.WindowsIdentity.GetCurrent();
        return new System.Security.Principal.WindowsPrincipal(id).IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
    }

    public static int? GetSectorSize(SafeFileHandle h)
    {
        byte* g = stackalloc byte[24];
        if (!DeviceIoControl(h, IOCTL_DISK_GET_DRIVE_GEOMETRY, null, 0, g, 24, out _, 0)) return null;
        int bps = *(int*)(g + 20);
        return bps is >= 512 and <= 65536 ? bps : null;
    }

    /// <summary>ボリュームのディスク上の位置（単一エクステントのときだけ）。</summary>
    public static (int Disk, long Offset, long Length)? VolumeExtent(string volumeGuidPath)
    {
        using var h = CreateFileW(volumeGuidPath.TrimEnd('\\'), 0, FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE, 0, OPEN_EXISTING, 0, 0);
        if (h.IsInvalid) return null;
        byte* buf = stackalloc byte[256];
        if (!DeviceIoControl(h, IOCTL_VOLUME_GET_VOLUME_DISK_EXTENTS, null, 0, buf, 256, out _, 0)) return null;
        int count = *(int*)buf;
        if (count != 1) return null; // スパン／ダイナミックディスクは非対応
        return (*(int*)(buf + 8), *(long*)(buf + 16), *(long*)(buf + 24));
    }
}

