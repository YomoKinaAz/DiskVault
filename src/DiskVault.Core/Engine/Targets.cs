using System.Runtime.InteropServices;
using DiskVault.Platform;
using Microsoft.Win32.SafeHandles;

namespace DiskVault.Engine;

/// <summary>復元先を開く。物理ディスク・ボリュームへの書き込みは、その上のボリュームをロック＆マウント解除してから行う。</summary>
public static unsafe class Targets
{
    public static bool IsDevice(string path) => path.StartsWith(@"\\.\", StringComparison.Ordinal) || path.StartsWith(@"\\?\Volume", StringComparison.OrdinalIgnoreCase);

    public static IVolumeTarget Open(string path, long size, bool confirmed)
    {
        if (!IsDevice(path)) return HandleTarget.CreateFile(path, size);
        if (!confirmed) throw new InvalidOperationException(T("{0} の内容はすべて上書きされます。よければ --yes を付けてください。", path));
        var m = System.Text.RegularExpressions.Regex.Match(path, @"^\\\\\.\\PhysicalDrive(\d+)$", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        if (m.Success) return OpenDisk(int.Parse(m.Groups[1].Value), size);
        return OpenVolume(path, size);
    }

    static SafeFileHandle OpenRw(string path)
    {
        var h = Native.CreateFileW(path.TrimEnd('\\'), Native.GENERIC_READ | Native.GENERIC_WRITE, Native.FILE_SHARE_READ | Native.FILE_SHARE_WRITE,
            0, Native.OPEN_EXISTING, Native.FILE_FLAG_WRITE_THROUGH, 0);
        if (h.IsInvalid)
        {
            int e = Marshal.GetLastPInvokeError();
            h.Dispose();
            throw Native.Error(path, e);
        }
        return h;
    }

    static SafeFileHandle LockAndDismount(string volPath)
    {
        var h = OpenRw(volPath);
        bool locked = false;
        for (int i = 0; i < 10 && !locked; i++)
        {
            locked = Native.DeviceIoControl(h, Native.FSCTL_LOCK_VOLUME, null, 0, null, 0, out _, 0);
            if (!locked) Thread.Sleep(500);
        }
        if (!locked)
        {
            h.Dispose();
            throw new IOException(T("{0} をロックできません。開いているファイルやエクスプローラーを閉じてください。", volPath));
        }
        Native.DeviceIoControl(h, Native.FSCTL_DISMOUNT_VOLUME, null, 0, null, 0, out _, 0);
        return h;
    }

    static IVolumeTarget OpenDisk(int n, long size)
    {
        var vols = VolumeInfo.Enumerate().Where(v => v.Disk == n).ToList();
        if (vols.Any(v => v.IsSystem) && !IsWinPE)
            throw new InvalidOperationException(T("ディスク {0} には実行中の Windows があります。システムディスクへの復元は回復環境（WinPE）から行ってください。", n));
        var locks = new List<SafeFileHandle>();
        try
        {
            foreach (var v in vols) locks.Add(LockAndDismount(v.GuidPath));
            var h = OpenRw($@"\\.\PhysicalDrive{n}");
            long len = Native.GetLength(h) ?? 0;
            if (len < size)
            {
                h.Dispose();
                throw new IOException(T("ディスク {0}（{1}）は復元に必要な容量（{2}）より小さいです。", n, Fmt.Size(len), Fmt.Size(size)));
            }
            return new DeviceTarget($@"\\.\PhysicalDrive{n}", h, len, locks, isDisk: true);
        }
        catch
        {
            foreach (var l in locks) l.Dispose();
            throw;
        }
    }

    static IVolumeTarget OpenVolume(string path, long size)
    {
        var info = VolumeInfo.Find(path.Replace(@"\\.\", ""));
        if (info?.IsSystem == true && !IsWinPE) throw new InvalidOperationException(T("実行中の Windows のボリュームには復元できません。"));
        var h = LockAndDismount(path);
        Native.DeviceIoControl(h, Native.FSCTL_ALLOW_EXTENDED_DASD_IO, null, 0, null, 0, out _, 0);
        long len = Native.GetLength(h) ?? 0;
        if (len < size)
        {
            h.Dispose();
            throw new IOException(T("{0}（{1}）は元のボリューム（{2}）より小さいです。", path, Fmt.Size(len), Fmt.Size(size)));
        }
        return new DeviceTarget(path, h, len, [], isDisk: false);
    }

    /// <summary>WinPE 上で動いているか（MiniNT キーがある）。</summary>
    public static bool IsWinPE
    {
        get
        {
            using var k = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\MiniNT");
            return k != null;
        }
    }

    sealed class DeviceTarget(string name, SafeFileHandle h, long length, List<SafeFileHandle> locks, bool isDisk) : IVolumeTarget
    {
        public string Name => name;
        public long Length => length;

        public void Write(long offset, ReadOnlySpan<byte> data)
        {
            if (offset < 0 || offset + data.Length > length) throw new ArgumentOutOfRangeException(nameof(offset));
            RandomAccess.Write(h, data, offset);
        }

        public void Read(long offset, Span<byte> buffer) => RandomAccess.Read(h, buffer, offset);
        public void Flush() => RandomAccess.FlushToDisk(h);

        public void Dispose()
        {
            if (isDisk) Native.DeviceIoControl(h, Native.IOCTL_DISK_UPDATE_PROPERTIES, null, 0, null, 0, out _, 0);
            h.Dispose();
            foreach (var l in locks) l.Dispose();
        }
    }
}
