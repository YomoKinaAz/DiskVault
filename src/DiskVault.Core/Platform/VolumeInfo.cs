using System.Runtime.InteropServices;
using System.Text;
using DiskVault.Storage;

namespace DiskVault.Platform;

/// <summary>この PC のボリューム（ドライブ）。</summary>
public sealed class VolumeInfo
{
    /// <summary>\\?\Volume{GUID}\</summary>
    public string GuidPath { get; init; } = "";
    public string? Letter { get; init; }
    public List<string> MountPoints { get; init; } = [];
    public string? Label { get; init; }
    public string? FileSystem { get; init; }
    public long Size { get; init; }
    public long Free { get; init; }
    public int? Disk { get; init; }
    public long? DiskOffset { get; init; }
    public long? ExtentLength { get; init; }
    public bool IsSystem { get; init; }

    public string Display => (Letter ?? "(文字なし)") + (string.IsNullOrEmpty(Label) ? "" : $" [{Label}]");

    public static unsafe List<VolumeInfo> Enumerate()
    {
        var list = new List<VolumeInfo>();
        var name = stackalloc char[300];
        nint h = Native.FindFirstVolume(name, 300);
        if (h == -1) return list;
        var buf = stackalloc char[2048];
        var label = stackalloc char[261];
        var fs = stackalloc char[261];
        var sysDrive = (Environment.GetEnvironmentVariable("SystemDrive") ?? "C:").ToUpperInvariant();
        // WinPE では SystemDrive は X:（RAM ディスク）。インストール済み Windows（オフライン）のボリュームを「Windows」とみなす
        bool pe = Sys.IsWinPE;
        try
        {
            do
            {
                var guid = new string(name);
                var mps = new List<string>();
                if (Native.GetVolumePathNamesForVolumeName(guid, buf, 2048, out _))
                {
                    var p = buf;
                    while (*p != 0)
                    {
                        var s = new string(p);
                        mps.Add(s);
                        p += s.Length + 1;
                    }
                }
                string? letter = mps.FirstOrDefault(m => m.Length == 3 && m[1] == ':')?[..2].ToUpperInvariant();
                string? fsName = null, labelStr = null;
                if (Native.GetVolumeInformation(guid, label, 261, out _, out _, out _, fs, 261))
                {
                    fsName = new string(fs);
                    labelStr = new string(label);
                }
                long total = 0, free = 0;
                if (Native.GetDiskFreeSpaceEx(guid, out _, out var t, out var f)) { total = t; free = f; }
                var ext = Native.VolumeExtent(guid);
                if (ext is { } e && total == 0) total = e.Length;
                list.Add(new VolumeInfo
                {
                    GuidPath = guid,
                    Letter = letter,
                    MountPoints = mps,
                    Label = labelStr,
                    FileSystem = fsName,
                    Size = total,
                    Free = free,
                    Disk = ext?.Disk,
                    DiskOffset = ext?.Offset,
                    ExtentLength = ext?.Length,
                    IsSystem = pe ? letter != null && letter != sysDrive && File.Exists($@"{letter}\Windows\System32\config\SYSTEM") : letter == sysDrive,
                });
            } while (Native.FindNextVolume(h, name, 300));
        }
        finally { Native.FindVolumeClose(h); }
        list.Sort((a, b) => string.Compare(a.Letter ?? "~", b.Letter ?? "~", StringComparison.Ordinal));
        return list;
    }

    public static VolumeInfo? Find(string spec)
    {
        var all = Enumerate();
        var s = spec.Trim().TrimEnd('\\');
        if (s.Length == 2 && s[1] == ':') return all.FirstOrDefault(v => string.Equals(v.Letter, s, StringComparison.OrdinalIgnoreCase));
        return all.FirstOrDefault(v => string.Equals(v.GuidPath.TrimEnd('\\'), s, StringComparison.OrdinalIgnoreCase));
    }
}

/// <summary>物理ディスクの構成（IOCTL_DISK_GET_DRIVE_LAYOUT_EX）。</summary>
public static unsafe class DiskLayout
{
    public static DiskRecord Read(int diskNumber)
    {
        var path = $@"\\.\PhysicalDrive{diskNumber}";
        using var h = Native.CreateFileW(path, 0, Native.FILE_SHARE_READ | Native.FILE_SHARE_WRITE, 0, Native.OPEN_EXISTING, 0, 0);
        if (h.IsInvalid) throw Native.Error(path, Marshal.GetLastPInvokeError());
        int size = 48 + 144 * 128;
        var buf = new byte[size];
        int got;
        fixed (byte* b = buf)
            if (!Native.DeviceIoControl(h, Native.IOCTL_DISK_GET_DRIVE_LAYOUT_EX, null, 0, b, size, out got, 0))
                throw Native.Error($"{path} の構成取得", Marshal.GetLastPInvokeError());
        var span = buf.AsSpan();
        int style = BitConverter.ToInt32(buf, 0);
        int count = BitConverter.ToInt32(buf, 4);
        var rec = new DiskRecord
        {
            Number = diskNumber,
            Size = Native.GetLength(h) ?? Native.GetDiskSizeEx(h) ?? 0,
            SectorSize = Native.GetSectorSize(h) ?? 512,
            Model = Model(h),
        };
        if (style == 1)
        {
            rec.Style = "GPT";
            rec.DiskGuid = new Guid(span.Slice(8, 16)).ToString();
        }
        else if (style == 0)
        {
            rec.Style = "MBR";
            rec.MbrSignature = BitConverter.ToUInt32(buf, 8);
        }
        else throw new NotSupportedException($"ディスク {diskNumber} は初期化されていません（RAW）。");
        for (int i = 0; i < count; i++)
        {
            var e = span.Slice(48 + i * 144, 144);
            long off = BitConverter.ToInt64(e[8..]);
            long len = BitConverter.ToInt64(e[16..]);
            int num = BitConverter.ToInt32(e[24..]);
            if (len == 0) continue;
            var p = new PartitionRecord { Number = num, Offset = off, Length = len };
            if (style == 1)
            {
                p.TypeGuid = new Guid(e.Slice(32, 16)).ToString();
                p.PartitionGuid = new Guid(e.Slice(48, 16)).ToString();
                p.Attributes = BitConverter.ToUInt64(e[64..]);
                p.Name = Encoding.Unicode.GetString(e.Slice(72, 72)).TrimEnd('\0');
            }
            else
            {
                p.MbrType = e[32];
                p.Active = e[33] != 0;
                if (p.MbrType is 0x05 or 0x0F) continue;
            }
            rec.Partitions.Add(p);
        }
        rec.Partitions.Sort((a, b) => a.Offset.CompareTo(b.Offset));
        return rec;
    }

    static string? Model(Microsoft.Win32.SafeHandles.SafeFileHandle h)
    {
        int* q = stackalloc int[3];
        q[0] = 0; q[1] = 0; q[2] = 0;
        var outBuf = stackalloc byte[1024];
        if (!Native.DeviceIoControl(h, Native.IOCTL_STORAGE_QUERY_PROPERTY, q, 12, outBuf, 1024, out int got, 0)) return null;
        string? Str(int off)
        {
            int o = *(int*)(outBuf + off);
            if (o <= 0 || o >= got) return null;
            return Marshal.PtrToStringAnsi((nint)(outBuf + o))?.Trim();
        }
        var model = string.Join(" ", new[] { Str(12), Str(16) }.Where(s => !string.IsNullOrEmpty(s)));
        return model.Length > 0 ? model : null;
    }

    public sealed record DiskSummary(int Number, long Size, string? Model, string Style, int Partitions, bool HasSystem, string Volumes);

    /// <summary>復元先の候補表示用：物理ディスクの一覧。</summary>
    public static List<DiskSummary> ListDisks()
    {
        var vols = VolumeInfo.Enumerate();
        var list = new List<DiskSummary>();
        foreach (var n in DiskNumbers())
        {
            var vs = vols.Where(v => v.Disk == n).ToList();
            var names = string.Join("、", vs.Select(v => v.Letter ?? v.FileSystem ?? "?"));
            try
            {
                var d = Read(n);
                list.Add(new(n, d.Size, d.Model, d.Style, d.Partitions.Count, vs.Any(v => v.IsSystem), names));
            }
            catch (Exception e) when (e is NotSupportedException or IOException or UnauthorizedAccessException)
            {
                using var h = Native.CreateFileW($@"\\.\PhysicalDrive{n}", 0, Native.FILE_SHARE_READ | Native.FILE_SHARE_WRITE, 0, Native.OPEN_EXISTING, 0, 0);
                long size = h.IsInvalid ? 0 : Native.GetDiskSizeEx(h) ?? 0;
                list.Add(new(n, size, h.IsInvalid ? null : Model(h), "未初期化", 0, false, names));
            }
        }
        return list;
    }

    public static List<int> DiskNumbers()
    {
        var list = new List<int>();
        for (int i = 0; i < 64; i++)
        {
            using var h = Native.CreateFileW($@"\\.\PhysicalDrive{i}", 0, Native.FILE_SHARE_READ | Native.FILE_SHARE_WRITE, 0, Native.OPEN_EXISTING, 0, 0);
            if (!h.IsInvalid) list.Add(i);
        }
        return list;
    }
}


