using System.Buffers.Binary;
using System.Runtime.InteropServices;
using DiskVault.Engine;
using DiskVault.Ntfs;
using DiskVault.Source;
using DiskVault.Storage;

namespace DiskVault.Platform;

/// <summary>
/// 復元時の NTFS のサイズ変更。
///  縮小: 復元したボリュームをオフラインで書き換える（使用中のデータが新しいサイズに収まるときだけ）。
///        ブートセクタの総セクタ数・バックアップブートセクタ・$Bitmap のサイズと末尾ビット・$BadClus:$Bad を直す。
///  拡張: 復元後に Windows / WinPE 上で FSCTL_EXTEND_VOLUME（ディスクの管理の「ボリュームの拡張」と同じ）。
/// </summary>
public static unsafe class NtfsResize
{
    const uint FSCTL_EXTEND_VOLUME = 0x000900F0;

    /// <summary>このボリュームを縮められる最小サイズ（最後の使用中クラスタまで＋バックアップブートセクタ、1MiB 単位）。</summary>
    public static long MinSize(IVolumeSource src)
    {
        var ntfs = NtfsVolume.Open(src);
        var bm = ntfs.ReadClusterBitmap();
        long last = -1;
        for (long i = (ntfs.TotalClusters + 7) / 8 - 1; i >= 0 && last < 0; i--)
        {
            if (bm[i] == 0) continue;
            for (int b = 7; b >= 0; b--)
                if ((bm[i] & (1 << b)) != 0 && i * 8 + b < ntfs.TotalClusters) { last = i * 8 + b; break; }
        }
        if (ntfs.StreamRuns(8, "$Bad").Any(r => r.Lcn >= 0)) return src.Length; // 不良クラスタの記録があるボリュームは縮めない
        long need = (last + 1) * ntfs.ClusterSize + ntfs.BytesPerSector;
        return Math.Min(src.Length, (need + (1 << 20) - 1) / (1 << 20) * (1 << 20));
    }

    /// <summary>
    /// src（復元ポイントのボリューム）を newLen に縮めた形で dst に書いたあと、メタデータを直す。
    /// 呼ぶ前に、先頭 newLen バイトぶんの使用中ブロックを dst に復元しておくこと。
    /// </summary>
    public static void ShrinkOffline(IVolumeSource src, IVolumeTarget dst, long newLen, Action<string>? log = null)
    {
        var ntfs = NtfsVolume.Open(src);
        int bps = ntfs.BytesPerSector, cs = ntfs.ClusterSize;
        long newTotalSectors = newLen / bps - 1;
        long newClusters = newTotalSectors * bps / cs;
        if (newClusters >= ntfs.TotalClusters) throw new ArgumentException(T("縮小後のサイズが元以上です。"));
        if (ntfs.HasAttributeList(6) || ntfs.HasAttributeList(8))
            throw new NotSupportedException(T("$Bitmap / $BadClus が分割されているボリュームは縮小できません。"));
        if (ntfs.StreamRuns(8, "$Bad").Any(r => r.Lcn >= 0))
            throw new NotSupportedException(T("不良クラスタが記録されているボリュームは縮小できません。"));
        var bm = ntfs.ReadClusterBitmap();
        for (long c = newClusters; c < ntfs.TotalClusters; c++)
            if ((bm[c >> 3] & (1 << (int)(c & 7))) != 0)
                throw new InvalidOperationException(T("縮小後の範囲の外に使用中のデータがあります（最小 {0}）。", Fmt.Size(MinSize(src))));

        // 1. $Bitmap：サイズは 8 バイト単位、ボリュームの外のビットは 1（使用中）にする決まり
        long newBmBytes = (newClusters + 63) / 64 * 8;
        var bmRuns = ntfs.DataRuns(NtfsVolume.BitmapFile);
        var newBm = bm.AsSpan(0, (int)newBmBytes).ToArray();
        for (long c = newClusters; c < newBmBytes * 8; c++) newBm[c >> 3] |= (byte)(1 << (int)(c & 7));
        long firstByte = newClusters / 8 / cs * cs; // 変わるのは末尾のクラスタだけ
        WriteStream(bmRuns, cs, newBm, firstByte, newBmBytes, dst);

        // 2. $Bitmap の $DATA のサイズ
        var rec6 = ntfs.ReadRecordForEdit(NtfsVolume.BitmapFile);
        int a6 = NtfsVolume.FindAttribute(rec6, NtfsVolume.AttrData, "");
        if (a6 < 0 || rec6[a6 + 8] == 0) throw new InvalidDataException(T("$Bitmap の $DATA が見つかりません。"));
        BinaryPrimitives.WriteInt64LittleEndian(rec6.AsSpan(a6 + 48), newBmBytes);
        BinaryPrimitives.WriteInt64LittleEndian(rec6.AsSpan(a6 + 56), newBmBytes);
        WriteRecord(ntfs, NtfsVolume.BitmapFile, rec6, dst);

        // 3. $BadClus:$Bad（ボリューム全体の大きさのスパースストリーム）
        var rec8 = ntfs.ReadRecordForEdit(8);
        int a8 = NtfsVolume.FindAttribute(rec8, NtfsVolume.AttrData, "$Bad");
        if (a8 >= 0 && rec8[a8 + 8] != 0)
        {
            var a = rec8.AsSpan(a8);
            int runOff = BinaryPrimitives.ReadUInt16LittleEndian(a[32..]);
            int hdr = a[runOff];
            int lenBytes = hdr & 0x0F;
            if ((hdr >> 4) != 0 || a[runOff + 1 + lenBytes] != 0) throw new NotSupportedException(T("$BadClus のランリストが想定外です。"));
            for (int i = 0; i < lenBytes; i++) a[runOff + 1 + i] = (byte)(newClusters >> (8 * i));
            BinaryPrimitives.WriteInt64LittleEndian(a[24..], newClusters - 1);          // 最終 VCN
            BinaryPrimitives.WriteInt64LittleEndian(a[40..], newClusters * cs);          // 割り当てサイズ
            BinaryPrimitives.WriteInt64LittleEndian(a[48..], newClusters * cs);          // データサイズ
            WriteRecord(ntfs, 8, rec8, dst);
        }

        // 4. ブートセクタ（先頭と、新しい末尾のバックアップ）
        var boot = new byte[bps];
        ntfs.BootSector.AsSpan(0, Math.Min(512, bps)).CopyTo(boot);
        if (bps > 512) src.Read(0, boot);
        BinaryPrimitives.WriteInt64LittleEndian(boot.AsSpan(0x28), newTotalSectors);
        dst.Write(0, boot);
        dst.Write(newTotalSectors * bps, boot);
        log?.Invoke(T("  NTFS を {0} → {1} に縮小しました。", Fmt.Size(ntfs.TotalClusters * cs), Fmt.Size(newClusters * cs)));
    }

    static void WriteRecord(NtfsVolume ntfs, uint no, byte[] rec, IVolumeTarget dst)
    {
        NtfsVolume.WriteFixup(rec, ntfs.BytesPerSector);
        dst.Write(ntfs.RecordVolumeOffset(no), rec);
    }

    /// <summary>ストリームの [from, to) をクラスタ単位で書き戻す（デバイスはセクタ境界でしか書けないため）。</summary>
    static void WriteStream(List<DataRun> runs, int cs, byte[] data, long from, long to, IVolumeTarget dst)
    {
        long vcnStart = 0;
        var buf = new byte[cs];
        foreach (var r in runs)
        {
            for (long k = 0; k < r.Clusters; k++)
            {
                long sOff = (vcnStart + k) * cs;
                if (sOff + cs <= from || sOff >= to) continue;
                if (r.Lcn < 0) throw new InvalidDataException(T("$Bitmap がスパースです。"));
                Array.Clear(buf);
                int n = (int)Math.Clamp(data.Length - sOff, 0, cs);
                data.AsSpan((int)sOff, n).CopyTo(buf);
                dst.Write((r.Lcn + k) * cs, buf);
            }
            vcnStart += r.Clusters;
        }
    }

    /// <summary>マウント済みの NTFS ボリュームをパーティションいっぱいまで広げる（管理者権限が必要）。</summary>
    public static string Extend(string volumeGuidPath)
    {
        using var h = Native.CreateFileW(volumeGuidPath.TrimEnd('\\'), Native.GENERIC_READ | Native.GENERIC_WRITE,
            Native.FILE_SHARE_READ | Native.FILE_SHARE_WRITE, 0, Native.OPEN_EXISTING, 0, 0);
        if (h.IsInvalid) throw Native.Error(volumeGuidPath, Marshal.GetLastPInvokeError());
        long len = Native.GetLength(h) ?? throw new IOException(T("ボリュームのサイズを取得できません。"));
        int ss = Native.GetSectorSize(h) ?? 512;
        long sectors = len / ss;
        if (!Native.DeviceIoControl(h, FSCTL_EXTEND_VOLUME, &sectors, 8, null, 0, out _, 0))
            throw Native.Error(T("ボリュームの拡張"), Marshal.GetLastPInvokeError());
        return T("  NTFS をパーティションいっぱい（{0}）まで拡張しました。", Fmt.Size(len));
    }

    /// <summary>復元したディスク上で、広げたパーティションの NTFS を拡張する（ボリュームが現れるのを待つ）。</summary>
    public static List<string> ExtendOnDisk(int diskNumber, IEnumerable<PartitionRecord> parts)
    {
        var log = new List<string>();
        foreach (var p in parts)
        {
            VolumeInfo? v = null;
            for (int i = 0; i < 30 && v == null; i++)
            {
                v = VolumeInfo.Enumerate().FirstOrDefault(x => x.Disk == diskNumber && x.DiskOffset == p.Offset && x.FileSystem == "NTFS");
                if (v == null) Thread.Sleep(1000);
            }
            if (v == null) { log.Add(T("  パーティション {0}: ボリュームが見つからないため NTFS を拡張できませんでした（ディスクの管理で拡張してください）。", p.Number)); continue; }
            try { log.Add(T("  パーティション {0}:", p.Number) + Extend(v.GuidPath).TrimStart()); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { log.Add(T("  パーティション {0}: NTFS の拡張に失敗しました: {1}", p.Number, e.Message)); }
        }
        return log;
    }
}

public sealed class PartitionPlan
{
    public required PartitionRecord Source { get; init; }
    public long NewLength { get; set; }
    public long MinLength { get; init; }
    /// <summary>サイズを変えられるか（中身なし・EFI などは不可）。</summary>
    public bool Resizable { get; init; }
    public bool Fill { get; set; }
    /// <summary>このパーティションの前に空ける領域（1MiB 単位）。開始位置を後ろにずらすのに使う。</summary>
    public long GapBefore { get; set; }
}

/// <summary>復元先ディスクのパーティション配置を決める（サイズ変更）。</summary>
public static class LayoutPlanner
{
    public const long Align = 1 << 20;
    /// <summary>ディスク末尾の予約（バックアップ GPT ＋余裕）。</summary>
    public const long TailReserve = 1 << 20;

    static long Up(long v) => (v + Align - 1) / Align * Align;

    /// <summary>各パーティションの最小サイズとサイズ変更可否を調べる（NTFS は復元ポイントのビットマップから）。</summary>
    public static List<PartitionPlan> Analyze(Repository repo, RecoveryPoint p, DiskRecord disk)
    {
        var list = new List<PartitionPlan>();
        foreach (var part in disk.Partitions.OrderBy(x => x.Offset))
        {
            var vol = part.Volume is int vi ? p.Volumes[vi] : null;
            bool ntfs = vol?.FileSystem == "NTFS";
            long min = part.Length;
            if (ntfs)
            {
                try
                {
                    using var src = new PointVolumeSource(repo, p, vol!.Index);
                    min = NtfsResize.MinSize(src);
                }
                catch (InvalidDataException) { }
            }
            list.Add(new PartitionPlan { Source = part, NewLength = part.Length, MinLength = min, Resizable = ntfs });
        }
        return list;
    }

    /// <summary>Windows のパーティション（なければ最大の NTFS）を残り全体にする既定の配置。</summary>
    public static void AutoFill(List<PartitionPlan> plan, RecoveryPoint p)
    {
        foreach (var x in plan) { x.Fill = false; x.NewLength = x.Source.Length; x.GapBefore = 0; }
        var sys = plan.FirstOrDefault(x => x.Resizable && x.Source.Volume is int v && p.Volumes[v].IsSystem)
                  ?? plan.Where(x => x.Resizable).OrderByDescending(x => x.Source.Length).FirstOrDefault();
        if (sys != null) sys.Fill = true;
    }

    /// <summary>配置を確定して新しいパーティション構成を返す。収まらなければ InvalidOperationException。</summary>
    public static DiskRecord Build(DiskRecord src, List<PartitionPlan> plan, long targetSize)
    {
        if (plan.Count(x => x.Fill) > 1) throw new InvalidOperationException(T("「残り全体」にできるのは 1 つだけです。"));
        var ordered = plan.OrderBy(x => x.Source.Offset).ToList();
        long usableEnd = (targetSize - TailReserve) / Align * Align;
        long first = ordered.Count > 0 ? ordered[0].Source.Offset : Align;
        int fi = ordered.FindIndex(x => x.Fill);
        if (fi >= 0)
        {
            long before = first;
            for (int i = 0; i < fi; i++) before = Up(before + Up(ordered[i].GapBefore) + ordered[i].NewLength);
            before += Up(ordered[fi].GapBefore);
            long after = 0;
            for (int i = fi + 1; i < ordered.Count; i++) after += Up(ordered[i].GapBefore) + Up(ordered[i].NewLength);
            ordered[fi].NewLength = (usableEnd - after - before) / Align * Align;
        }
        var result = new DiskRecord
        {
            Index = src.Index, Number = src.Number, Model = src.Model, Size = targetSize, SectorSize = src.SectorSize,
            Style = src.Style, DiskGuid = src.DiskGuid, MbrSignature = src.MbrSignature,
        };
        long off = first;
        foreach (var x in ordered)
        {
            var s = x.Source;
            if (x.GapBefore < 0) throw new InvalidOperationException(T("パーティション {0} の前の空きが負です。", s.Number));
            off += Up(x.GapBefore);
            if (x.NewLength != s.Length)
            {
                if (!x.Resizable) throw new InvalidOperationException(T("パーティション {0}（{1}）はサイズを変えられません。", s.Number, s.Kind));
                if (x.NewLength < x.MinLength)
                    throw new InvalidOperationException(T("パーティション {0} は {1} より小さくできません（使用中のデータがあるため）。", s.Number, Fmt.Size(x.MinLength)));
            }
            if (x.NewLength <= 0) throw new InvalidOperationException(T("復元先ディスクが小さすぎます。"));
            result.Partitions.Add(new PartitionRecord
            {
                Number = s.Number, Offset = off, Length = x.NewLength, TypeGuid = s.TypeGuid, PartitionGuid = s.PartitionGuid,
                Name = s.Name, Attributes = s.Attributes, MbrType = s.MbrType, Active = s.Active, Volume = s.Volume,
            });
            off = Up(off + x.NewLength);
        }
        if (result.Partitions.Count > 0 && result.Partitions[^1].Offset + result.Partitions[^1].Length > usableEnd)
            throw new InvalidOperationException(T("復元先ディスク（{0}）に収まりません。パーティションを小さくしてください。", Fmt.Size(targetSize)));
        if (src.Style == "MBR" && off > 2L * 1024 * 1024 * 1024 * 1024)
            throw new InvalidOperationException(T("MBR ディスクは 2TB を超えて使えません。"));
        return result;
    }
}
