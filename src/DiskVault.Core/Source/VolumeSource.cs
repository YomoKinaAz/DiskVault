using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace DiskVault.Source;

/// <summary>読み取り元（ボリューム・スナップショット・イメージファイルの一部）。</summary>
public interface IVolumeSource : IDisposable
{
    string Name { get; }
    long Length { get; }
    int SectorSize { get; }

    /// <summary>offset から dest を埋める。読めないセクタはゼロで埋め、その合計バイト数を返す。</summary>
    long Read(long offset, Span<byte> dest);
}

/// <summary>ファイル（またはデバイス）の一部をボリュームとして読む。デバイスの場合はセクタ境界で読む。</summary>
public sealed class HandleSource : IVolumeSource
{
    readonly SafeFileHandle _h;
    readonly long _base;
    readonly bool _device;

    public string Name { get; }
    public long Length { get; }
    public int SectorSize { get; }

    HandleSource(string name, SafeFileHandle h, long @base, long length, int sector, bool device)
    {
        Name = name;
        _h = h;
        _base = @base;
        Length = length;
        SectorSize = sector;
        _device = device;
    }

    /// <summary>イメージファイル（offset/length でパーティション部分だけ切り出せる）。</summary>
    public static HandleSource OpenFile(string path, long offset = 0, long length = -1)
    {
        var h = File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        long size = RandomAccess.GetLength(h);
        if (length < 0) length = size - offset;
        if (offset < 0 || offset + length > size) { h.Dispose(); throw new ArgumentOutOfRangeException(nameof(offset), T("イメージの範囲外です。")); }
        return new HandleSource(path, h, offset, length, 512, false);
    }

    /// <summary>\\.\C: や \\?\GLOBALROOT\Device\HarddiskVolumeShadowCopyN など（管理者権限が必要）。</summary>
    public static HandleSource OpenDevice(string path) => OpenDevice(path, 0, -1);

    /// <summary>デバイスの一部（物理ディスク上のパーティションなど）。</summary>
    public static HandleSource OpenDevice(string path, long offset, long length)
    {
        var h = Native.CreateFileW(path.TrimEnd('\\'), Native.GENERIC_READ, Native.FILE_SHARE_READ | Native.FILE_SHARE_WRITE | Native.FILE_SHARE_DELETE,
            0, Native.OPEN_EXISTING, Native.FILE_FLAG_SEQUENTIAL_SCAN, 0);
        if (h.IsInvalid)
        {
            int err = Marshal.GetLastPInvokeError();
            h.Dispose();
            throw Native.Error(path, err);
        }
        long len = Native.GetLength(h) ?? throw new IOException(T("{0} のサイズを取得できません。", path));
        int ss = Native.GetSectorSize(h) ?? 512;
        if (length < 0) length = len - offset;
        if (offset < 0 || offset + length > len) { h.Dispose(); throw new ArgumentOutOfRangeException(nameof(offset), T("{0} の範囲外です。", path)); }
        return new HandleSource(path, h, offset, length, ss, true);
    }

    public long Read(long offset, Span<byte> dest)
    {
        if (offset < 0 || offset + dest.Length > Length) throw new ArgumentOutOfRangeException(nameof(offset));
        try
        {
            ReadAll(offset, dest);
            return 0;
        }
        catch (IOException) when (_device)
        {
            // 不良セクタ：セクタ単位で読み直し、読めないところはゼロ
            long bad = 0;
            for (int p = 0; p < dest.Length; p += SectorSize)
            {
                var s = dest.Slice(p, Math.Min(SectorSize, dest.Length - p));
                try { ReadAll(offset + p, s); }
                catch (IOException) { s.Clear(); bad += s.Length; }
            }
            return bad;
        }
    }

    void ReadAll(long offset, Span<byte> dest)
    {
        int got = 0;
        while (got < dest.Length)
        {
            int n = RandomAccess.Read(_h, dest[got..], _base + offset + got);
            if (n <= 0) throw new EndOfStreamException(T("{0}: 予期しない終端（{1}）。", Name, offset + got));
            got += n;
        }
    }

    public void Dispose() => _h.Dispose();
}

/// <summary>テスト用：メモリ上のボリューム。</summary>
public sealed class MemorySource(string name, byte[] data) : IVolumeSource
{
    public string Name => name;
    public long Length => data.Length;
    public int SectorSize => 512;
    public long Read(long offset, Span<byte> dest) { data.AsSpan((int)offset, dest.Length).CopyTo(dest); return 0; }
    public void Dispose() { }
}
