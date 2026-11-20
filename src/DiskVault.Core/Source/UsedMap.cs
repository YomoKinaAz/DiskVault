using DiskVault.Ntfs;

namespace DiskVault.Source;

/// <summary>ボリュームのどこが使用中か。NTFS なら $Bitmap から、それ以外は全域を使用中とみなす。</summary>
public sealed class UsedMap
{
    readonly byte[]? _bits;
    public int ClusterSize { get; }
    public long Clusters { get; }
    public long VolumeLength { get; }
    public string FileSystem { get; }
    public long ExcludedBytes { get; private set; }
    public List<string> Excluded { get; } = [];
    /// <summary>NTFS のバックアップブートセクタの位置と内容（スナップショットから末尾が読めないときに作り直す）。</summary>
    public long BackupBootOffset { get; private set; } = -1;
    public byte[]? BootSector { get; private set; }
    /// <summary>クラスタ領域の終わり（これより後ろはファイルシステムの管理外の末尾）。</summary>
    public long TailStart => _bits == null ? VolumeLength : Clusters * ClusterSize;

    UsedMap(string fs, byte[]? bits, int clusterSize, long clusters, long length)
    {
        FileSystem = fs;
        _bits = bits;
        ClusterSize = clusterSize;
        Clusters = clusters;
        VolumeLength = length;
    }

    public static UsedMap AllUsed(long length, string fs = "RAW") => new(fs, null, 4096, 0, length);

    /// <summary>NTFS の使用中クラスタ。excludeSystemFiles ならページファイル・休止ファイル・古いシャドウコピーを除く。</summary>
    public static UsedMap ForNtfs(IVolumeSource src, bool excludeSystemFiles)
    {
        var ntfs = NtfsVolume.Open(src);
        var bits = ntfs.ReadClusterBitmap();
        var m = new UsedMap("NTFS", bits, ntfs.ClusterSize, ntfs.TotalClusters, src.Length)
        {
            BootSector = ntfs.BootSector,
            BackupBootOffset = ntfs.TotalSectors * ntfs.BytesPerSector,
        };
        if (excludeSystemFiles)
        {
            List<NtfsEntry> root;
            try { root = ntfs.ListDirectory(NtfsVolume.RootDir); }
            catch (InvalidDataException) { return m; }
            foreach (var e in root.Where(e => !e.IsDirectory && e.Name.ToLowerInvariant() is "pagefile.sys" or "hiberfil.sys" or "swapfile.sys"))
                m.Exclude(ntfs, e, "\\" + e.Name);
            var svi = root.FirstOrDefault(e => e.IsDirectory && e.Name.Equals("System Volume Information", StringComparison.OrdinalIgnoreCase));
            if (svi != null)
            {
                try
                {
                    // VSS の差分領域ファイル（{GUID}{3808876b-c176-4e48-b7ae-04046e6cc752}）
                    foreach (var e in ntfs.ListDirectory(svi.Record).Where(e => !e.IsDirectory && e.Name.Contains("{3808876b-c176-4e48-b7ae-04046e6cc752}", StringComparison.OrdinalIgnoreCase)))
                        m.Exclude(ntfs, e, "\\System Volume Information\\" + e.Name);
                }
                catch (InvalidDataException) { }
            }
        }
        return m;
    }

    public static UsedMap Detect(IVolumeSource src, bool excludeSystemFiles)
    {
        if (NtfsVolume.IsNtfs(src)) return ForNtfs(src, excludeSystemFiles);
        return AllUsed(src.Length, DetectFs(src));
    }

    static string DetectFs(IVolumeSource src)
    {
        if (src.Length < 512) return "RAW";
        Span<byte> b = stackalloc byte[512];
        src.Read(0, b);
        if (b.Slice(3, 8).SequenceEqual("EXFAT   "u8)) return "exFAT";
        if (b.Slice(82, 5).SequenceEqual("FAT32"u8)) return "FAT32";
        if (b.Slice(54, 3).SequenceEqual("FAT"u8)) return "FAT";
        if (b.Slice(3, 4).SequenceEqual("ReFS"u8)) return "ReFS";
        return "RAW";
    }

    void Exclude(NtfsVolume ntfs, NtfsEntry e, string path)
    {
        List<DataRun> runs;
        try { runs = ntfs.DataRuns(e.Record); }
        catch (InvalidDataException) { return; }
        long n = 0;
        foreach (var r in runs)
        {
            if (r.Lcn < 0) continue;
            for (long c = r.Lcn; c < r.Lcn + r.Clusters && c < Clusters; c++)
            {
                if ((_bits![c >> 3] & (1 << (int)(c & 7))) != 0)
                {
                    _bits[c >> 3] &= (byte)~(1 << (int)(c & 7));
                    n++;
                }
            }
        }
        if (n > 0)
        {
            ExcludedBytes += n * ClusterSize;
            Excluded.Add(path);
        }
    }

    bool ClusterUsed(long c) => c >= Clusters || (_bits![c >> 3] & (1 << (int)(c & 7))) != 0;

    /// <summary>[off, off+len) に使用中の部分があるか。</summary>
    public bool AnyUsed(long off, int len)
    {
        if (_bits == null) return true;
        long end = off + len;
        if (end > Clusters * ClusterSize) return true; // クラスタ領域の外（NTFS のバックアップブートセクタ）は常に保存
        long c0 = off / ClusterSize, c1 = (end - 1) / ClusterSize;
        for (long c = c0; c <= c1;)
        {
            if ((c & 7) == 0 && c + 7 <= c1)
            {
                if (_bits[c >> 3] != 0) return true;
                c += 8;
                continue;
            }
            if (ClusterUsed(c)) return true;
            c++;
        }
        return false;
    }

    /// <summary>block（ボリューム上の off から）のうち未使用クラスタの部分をゼロにする。</summary>
    public void ClearUnused(long off, Span<byte> block)
    {
        if (_bits == null) return;
        long end = off + block.Length;
        for (long c = off / ClusterSize; c * ClusterSize < end; c++)
        {
            if (ClusterUsed(c)) continue;
            long s = Math.Max(c * ClusterSize, off), e = Math.Min((c + 1) * ClusterSize, end);
            block.Slice((int)(s - off), (int)(e - s)).Clear();
        }
    }

    public long UsedBytes
    {
        get
        {
            if (_bits == null) return VolumeLength;
            long n = 0, full = Clusters >> 3;
            for (long i = 0; i < full; i++) n += System.Numerics.BitOperations.PopCount(_bits[i]);
            for (long c = full << 3; c < Clusters; c++) if (ClusterUsed(c)) n++;
            return n * ClusterSize + Math.Max(0, VolumeLength - Clusters * ClusterSize);
        }
    }
}

