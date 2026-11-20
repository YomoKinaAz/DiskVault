using System.Buffers.Binary;
using System.Text;
using DiskVault.Source;

namespace DiskVault.Ntfs;

public readonly record struct DataRun(long Lcn, long Clusters); // Lcn = -1 はスパース

/// <summary>MFT レコード内の属性（必要な情報だけ）。</summary>
public sealed class NtfsAttr
{
    public uint Type;
    public string Name = "";
    public bool NonResident;
    public byte[]? Value;           // 常駐
    public long StartVcn;
    public List<DataRun> Runs = []; // 非常駐（この断片の分）
    public long RealSize, InitSize, AllocSize;
    public ushort Flags;
    public int CompressionUnit;
}

public sealed class NtfsEntry
{
    public ulong Ref;
    public uint Record => (uint)(Ref & 0xFFFFFFFFFFFF);
    public string Name = "";
    public bool IsDirectory;
    public long Size;
    public DateTime Modified, Created;
    public uint Attributes;
    public override string ToString() => Name;
}

/// <summary>
/// NTFS を読み取り専用で解釈する：使用中クラスタのビットマップ取得（バックアップ用）と、
/// ディレクトリ一覧・ファイル取り出し（ファイル単位の復元用）。
/// </summary>
public sealed class NtfsVolume
{
    public const uint AttrStdInfo = 0x10, AttrList = 0x20, AttrFileName = 0x30, AttrData = 0x80,
        AttrIndexRoot = 0x90, AttrIndexAlloc = 0xA0, AttrBitmap = 0xB0, AttrEnd = 0xFFFFFFFF;
    public const uint RootDir = 5, BitmapFile = 6;

    readonly IVolumeSource _src;
    public int BytesPerSector { get; }
    public int ClusterSize { get; }
    public long TotalClusters { get; }
    /// <summary>ブートセクタの「総セクタ数」。バックアップブートセクタはこの位置（ボリューム末尾）にある。</summary>
    public long TotalSectors { get; private set; }
    public byte[] BootSector { get; private set; } = [];
    public int RecordSize { get; }
    public int IndexRecordSize { get; }
    public string? Label { get; private set; }
    readonly List<DataRun> _mftRuns;

    NtfsVolume(IVolumeSource src, int bps, int cs, long total, int rec, int idx, List<DataRun> mftRuns)
    {
        _src = src;
        BytesPerSector = bps;
        ClusterSize = cs;
        TotalClusters = total;
        RecordSize = rec;
        IndexRecordSize = idx;
        _mftRuns = mftRuns;
    }

    public static bool IsNtfs(IVolumeSource src)
    {
        if (src.Length < 512) return false;
        Span<byte> b = stackalloc byte[512];
        src.Read(0, b);
        return b.Slice(3, 8).SequenceEqual("NTFS    "u8);
    }

    public static NtfsVolume Open(IVolumeSource src)
    {
        var b = new byte[512];
        src.Read(0, b);
        if (!b.AsSpan(3, 8).SequenceEqual("NTFS    "u8)) throw new InvalidDataException(T("{0} は NTFS ではありません。", src.Name));
        int bps = BinaryPrimitives.ReadUInt16LittleEndian(b.AsSpan(0x0B));
        int spcRaw = b[0x0D];
        int spc = spcRaw <= 0x80 ? spcRaw : 1 << (256 - spcRaw);
        int cs = bps * spc;
        long totalSectors = BinaryPrimitives.ReadInt64LittleEndian(b.AsSpan(0x28));
        long mftLcn = BinaryPrimitives.ReadInt64LittleEndian(b.AsSpan(0x30));
        int Size(sbyte v) => v > 0 ? v * cs : 1 << -v;
        int rec = Size((sbyte)b[0x40]);
        int idx = Size((sbyte)b[0x44]);
        if (bps < 256 || cs <= 0 || rec < 256 || rec > 65536) throw new InvalidDataException(T("NTFS ブートセクタが不正です。"));

        // $MFT 自身のレコード 0 を読んでランを得る
        var r0 = new byte[rec];
        src.Read(mftLcn * cs, r0);
        var tmp = new NtfsVolume(src, bps, cs, totalSectors / spc, rec, idx, [new DataRun(mftLcn, Math.Max(1, (rec * 16 + cs - 1) / cs))]);
        var attrs = tmp.ParseRecord(r0, 0) ?? throw new InvalidDataException(T("$MFT レコードが壊れています。"));
        var runs = new List<DataRun>();
        foreach (var a in attrs.Where(a => a.Type == AttrData && a.Name == "").OrderBy(a => a.StartVcn)) runs.AddRange(a.Runs);
        var vol = new NtfsVolume(src, bps, cs, totalSectors / spc, rec, idx, runs);
        // 属性リストで $MFT のランが分割されている場合
        if (attrs.Any(a => a.Type == AttrList))
        {
            var all = vol.LoadAttributes(0);
            runs = [];
            foreach (var a in all.Where(a => a.Type == AttrData && a.Name == "").OrderBy(a => a.StartVcn)) runs.AddRange(a.Runs);
            vol = new NtfsVolume(src, bps, cs, totalSectors / spc, rec, idx, runs);
        }
        vol.TotalSectors = totalSectors;
        vol.BootSector = b;
        try
        {
            var volAttrs = vol.LoadAttributes(3); // $Volume
            var nameAttr = volAttrs.FirstOrDefault(a => a.Type == 0x60);
            if (nameAttr?.Value != null) vol.Label = Encoding.Unicode.GetString(nameAttr.Value);
        }
        catch (InvalidDataException) { }
        return vol;
    }

    // ---- レコード ----

    byte[]? ReadRecordRaw(uint no)
    {
        long off = (long)no * RecordSize;
        var buf = new byte[RecordSize];
        if (!ReadRuns(_mftRuns, off, buf)) return null;
        return buf;
    }

    /// <summary>ラン列で表された領域の off から buf を読む（スパースはゼロ）。範囲外なら false。</summary>
    bool ReadRuns(List<DataRun> runs, long off, Span<byte> buf)
    {
        long vcnStart = 0;
        int done = 0;
        foreach (var r in runs)
        {
            long runBytes = r.Clusters * ClusterSize;
            long runStart = vcnStart * ClusterSize;
            long runEnd = runStart + runBytes;
            vcnStart += r.Clusters;
            while (done < buf.Length && off + done >= runStart && off + done < runEnd)
            {
                long within = off + done - runStart;
                int n = (int)Math.Min(buf.Length - done, runEnd - (off + done));
                var dst = buf.Slice(done, n);
                if (r.Lcn < 0) dst.Clear();
                else ReadAligned(r.Lcn * ClusterSize + within, dst);
                done += n;
            }
            if (done >= buf.Length) return true;
        }
        if (done < buf.Length) buf[done..].Clear();
        return done > 0 || buf.Length == 0;
    }

    /// <summary>セクタ境界でなくても読めるようにする。</summary>
    void ReadAligned(long off, Span<byte> dst)
    {
        int ss = Math.Max(_src.SectorSize, 512);
        long a = off / ss * ss;
        long end = off + dst.Length;
        long ae = Math.Min((end + ss - 1) / ss * ss, _src.Length);
        if (a == off && ae == end) { _src.Read(off, dst); return; }
        var tmp = new byte[ae - a];
        _src.Read(a, tmp);
        tmp.AsSpan((int)(off - a), dst.Length).CopyTo(dst);
    }

    static bool ApplyFixup(Span<byte> rec, int sectorSize)
    {
        int usaOff = BinaryPrimitives.ReadUInt16LittleEndian(rec[4..]);
        int usaCount = BinaryPrimitives.ReadUInt16LittleEndian(rec[6..]);
        if (usaCount < 1 || usaOff + usaCount * 2 > rec.Length || (usaCount - 1) * sectorSize > rec.Length) return false;
        ushort usn = BinaryPrimitives.ReadUInt16LittleEndian(rec[usaOff..]);
        for (int i = 1; i < usaCount; i++)
        {
            int pos = i * sectorSize - 2;
            if (BinaryPrimitives.ReadUInt16LittleEndian(rec[pos..]) != usn) return false;
            rec[pos] = rec[usaOff + i * 2];
            rec[pos + 1] = rec[usaOff + i * 2 + 1];
        }
        return true;
    }

    sealed class Rec
    {
        public List<NtfsAttr> Attrs = [];
        public bool InUse, IsDir;
        public ulong BaseRef;
    }

    List<NtfsAttr>? ParseRecord(byte[] rec, uint no) => ParseRec(rec, no)?.Attrs;

    Rec? ParseRec(byte[] rec, uint no)
    {
        if (!rec.AsSpan(0, 4).SequenceEqual("FILE"u8)) return null;
        if (!ApplyFixup(rec, BytesPerSector)) return null;
        var r = new Rec();
        ushort flags = BinaryPrimitives.ReadUInt16LittleEndian(rec.AsSpan(22));
        r.InUse = (flags & 1) != 0;
        r.IsDir = (flags & 2) != 0;
        r.BaseRef = BinaryPrimitives.ReadUInt64LittleEndian(rec.AsSpan(32)) & 0xFFFFFFFFFFFF;
        int used = (int)Math.Min(BinaryPrimitives.ReadUInt32LittleEndian(rec.AsSpan(24)), (uint)rec.Length);
        int off = BinaryPrimitives.ReadUInt16LittleEndian(rec.AsSpan(20));
        while (off + 16 <= used)
        {
            uint type = BinaryPrimitives.ReadUInt32LittleEndian(rec.AsSpan(off));
            if (type == AttrEnd || type == 0) break;
            int len = (int)BinaryPrimitives.ReadUInt32LittleEndian(rec.AsSpan(off + 4));
            if (len < 16 || off + len > used) break;
            var a = rec.AsSpan(off, len);
            var at = new NtfsAttr { Type = type, NonResident = a[8] != 0, Flags = BinaryPrimitives.ReadUInt16LittleEndian(a[12..]) };
            int nameLen = a[9], nameOff = BinaryPrimitives.ReadUInt16LittleEndian(a[10..]);
            if (nameLen > 0 && nameOff + nameLen * 2 <= len) at.Name = Encoding.Unicode.GetString(a.Slice(nameOff, nameLen * 2));
            if (!at.NonResident)
            {
                int vlen = (int)BinaryPrimitives.ReadUInt32LittleEndian(a[16..]);
                int voff = BinaryPrimitives.ReadUInt16LittleEndian(a[20..]);
                if (voff + vlen <= len) at.Value = a.Slice(voff, vlen).ToArray();
                at.RealSize = at.InitSize = vlen;
            }
            else if (len >= 64)
            {
                at.StartVcn = BinaryPrimitives.ReadInt64LittleEndian(a[16..]);
                int runOff = BinaryPrimitives.ReadUInt16LittleEndian(a[32..]);
                at.CompressionUnit = BinaryPrimitives.ReadUInt16LittleEndian(a[34..]);
                at.AllocSize = BinaryPrimitives.ReadInt64LittleEndian(a[40..]);
                at.RealSize = BinaryPrimitives.ReadInt64LittleEndian(a[48..]);
                at.InitSize = BinaryPrimitives.ReadInt64LittleEndian(a[56..]);
                if (runOff < len) at.Runs = DecodeRuns(a[runOff..]);
            }
            r.Attrs.Add(at);
            off += len;
        }
        return r;
    }

    public static List<DataRun> DecodeRuns(ReadOnlySpan<byte> s)
    {
        var runs = new List<DataRun>();
        long lcn = 0;
        int p = 0;
        while (p < s.Length && s[p] != 0)
        {
            int lenBytes = s[p] & 0x0F, offBytes = s[p] >> 4;
            p++;
            if (lenBytes == 0 || lenBytes > 8 || offBytes > 8 || p + lenBytes + offBytes > s.Length) break;
            long count = 0;
            for (int i = 0; i < lenBytes; i++) count |= (long)s[p + i] << (8 * i);
            p += lenBytes;
            if (offBytes == 0) { runs.Add(new DataRun(-1, count)); continue; }
            long delta = 0;
            for (int i = 0; i < offBytes; i++) delta |= (long)s[p + i] << (8 * i);
            if ((s[p + offBytes - 1] & 0x80) != 0 && offBytes < 8) delta -= 1L << (8 * offBytes);
            p += offBytes;
            lcn += delta;
            if (count <= 0 || lcn < 0) break;
            runs.Add(new DataRun(lcn, count));
        }
        return runs;
    }

    /// <summary>レコードの全属性（属性リストがあれば拡張レコードの分も）を返す。</summary>
    public List<NtfsAttr> LoadAttributes(uint no)
    {
        var raw = ReadRecordRaw(no) ?? throw new InvalidDataException(T("MFT レコード {0} を読めません。", no));
        var r = ParseRec(raw, no) ?? throw new InvalidDataException(T("MFT レコード {0} が壊れています。", no));
        var list = r.Attrs;
        var al = list.FirstOrDefault(a => a.Type == AttrList);
        if (al == null) return list;
        byte[] alv = al.Value ?? ReadAttrContent(al, [al]);
        var ext = new HashSet<uint>();
        int p = 0;
        while (p + 26 <= alv.Length)
        {
            int len = BinaryPrimitives.ReadUInt16LittleEndian(alv.AsSpan(p + 4));
            if (len < 26) break;
            uint rf = (uint)(BinaryPrimitives.ReadUInt64LittleEndian(alv.AsSpan(p + 16)) & 0xFFFFFFFFFFFF);
            if (rf != no) ext.Add(rf);
            p += len;
        }
        foreach (var e in ext)
        {
            var er = ReadRecordRaw(e);
            if (er == null) continue;
            var parsed = ParseRec(er, e);
            if (parsed != null && parsed.BaseRef == no) list.AddRange(parsed.Attrs);
        }
        return list;
    }

    /// <summary>属性（複数断片を含む）の中身を全部読む（小さい属性用）。</summary>
    byte[] ReadAttrContent(NtfsAttr first, IEnumerable<NtfsAttr> pieces)
    {
        if (!first.NonResident) return first.Value ?? [];
        var runs = new List<DataRun>();
        foreach (var a in pieces.OrderBy(a => a.StartVcn)) runs.AddRange(a.Runs);
        var size = first.RealSize;
        if (size > 1L << 31) throw new InvalidDataException(T("属性が大きすぎます。"));
        var buf = new byte[size];
        ReadRuns(runs, 0, buf);
        if (first.InitSize < size) buf.AsSpan((int)Math.Max(0, first.InitSize)).Clear();
        return buf;
    }

    /// <summary>名前付き属性ストリームの中身（全断片）を読む。</summary>
    byte[] ReadStream(List<NtfsAttr> attrs, uint type, string name)
    {
        var pieces = attrs.Where(a => a.Type == type && a.Name == name).ToList();
        if (pieces.Count == 0) return [];
        var first = pieces.FirstOrDefault(a => a.StartVcn == 0) ?? pieces[0];
        return ReadAttrContent(first, pieces);
    }

    // ---- 書き換え用（オフラインのサイズ変更） ----

    /// <summary>MFT レコードがボリューム上のどこにあるか（バイト位置）。</summary>
    public long RecordVolumeOffset(uint no)
    {
        long off = (long)no * RecordSize, vcnStart = 0;
        foreach (var r in _mftRuns)
        {
            long start = vcnStart * ClusterSize, end = start + r.Clusters * ClusterSize;
            vcnStart += r.Clusters;
            if (off >= start && off + RecordSize <= end && r.Lcn >= 0) return r.Lcn * ClusterSize + (off - start);
        }
        throw new InvalidDataException(T("MFT レコード {0} の位置を特定できません。", no));
    }

    /// <summary>fixup を外した MFT レコード（書き換えて WriteFixup してから書き戻す）。</summary>
    public byte[] ReadRecordForEdit(uint no)
    {
        var raw = ReadRecordRaw(no) ?? throw new InvalidDataException(T("MFT レコード {0} を読めません。", no));
        if (!raw.AsSpan(0, 4).SequenceEqual("FILE"u8) || !ApplyFixup(raw, BytesPerSector))
            throw new InvalidDataException(T("MFT レコード {0} が壊れています。", no));
        return raw;
    }

    /// <summary>書き戻す前に更新シーケンス配列（fixup）を入れ直す。</summary>
    public static void WriteFixup(Span<byte> rec, int sectorSize)
    {
        int usaOff = BinaryPrimitives.ReadUInt16LittleEndian(rec[4..]);
        int usaCount = BinaryPrimitives.ReadUInt16LittleEndian(rec[6..]);
        ushort usn = BinaryPrimitives.ReadUInt16LittleEndian(rec[usaOff..]);
        for (int i = 1; i < usaCount; i++)
        {
            int pos = i * sectorSize - 2;
            rec[usaOff + i * 2] = rec[pos];
            rec[usaOff + i * 2 + 1] = rec[pos + 1];
            BinaryPrimitives.WriteUInt16LittleEndian(rec[pos..], usn);
        }
    }

    /// <summary>fixup 済みレコードの中で属性（種類・名前）の位置を探す。無ければ -1。</summary>
    public static int FindAttribute(ReadOnlySpan<byte> rec, uint type, string name)
    {
        int used = (int)Math.Min(BinaryPrimitives.ReadUInt32LittleEndian(rec[24..]), (uint)rec.Length);
        int off = BinaryPrimitives.ReadUInt16LittleEndian(rec[20..]);
        while (off + 16 <= used)
        {
            uint t = BinaryPrimitives.ReadUInt32LittleEndian(rec[off..]);
            if (t == AttrEnd || t == 0) break;
            int len = (int)BinaryPrimitives.ReadUInt32LittleEndian(rec[(off + 4)..]);
            if (len < 16 || off + len > used) break;
            int nameLen = rec[off + 9], nameOff = BinaryPrimitives.ReadUInt16LittleEndian(rec[(off + 10)..]);
            var n = nameLen > 0 ? Encoding.Unicode.GetString(rec.Slice(off + nameOff, nameLen * 2)) : "";
            if (t == type && n == name) return off;
            off += len;
        }
        return -1;
    }

    /// <summary>ストリームのランが分割（属性リスト経由）されているか。</summary>
    public bool HasAttributeList(uint record) => LoadAttributes(record).Any(a => a.Type == AttrList);

    /// <summary>名前付きストリームのラン（$BadClus:$Bad など）。</summary>
    public List<DataRun> StreamRuns(uint record, string name)
    {
        var runs = new List<DataRun>();
        foreach (var a in LoadAttributes(record).Where(a => a.Type == AttrData && a.Name == name && a.NonResident).OrderBy(a => a.StartVcn)) runs.AddRange(a.Runs);
        return runs;
    }

    // ---- バックアップ用 ----

    /// <summary>$Bitmap（1 ビット = 1 クラスタ、1 = 使用中）。</summary>
    public byte[] ReadClusterBitmap()
    {
        var attrs = LoadAttributes(BitmapFile);
        var bm = ReadStream(attrs, AttrData, "");
        if (bm.Length * 8L < TotalClusters) throw new InvalidDataException(T("$Bitmap がボリュームより小さいです。"));
        return bm;
    }

    /// <summary>ファイルのデータのラン（ページファイルなど除外対象のクラスタを得るため）。</summary>
    public List<DataRun> DataRuns(uint record)
    {
        var attrs = LoadAttributes(record);
        var runs = new List<DataRun>();
        foreach (var a in attrs.Where(a => a.Type == AttrData && a.Name == "" && a.NonResident).OrderBy(a => a.StartVcn)) runs.AddRange(a.Runs);
        return runs;
    }

    // ---- ディレクトリ ----

    public List<NtfsEntry> ListDirectory(uint record)
    {
        var attrs = LoadAttributes(record);
        var root = attrs.FirstOrDefault(a => a.Type == AttrIndexRoot && a.Name == "$I30")?.Value
                   ?? throw new InvalidDataException(T("レコード {0} はディレクトリではありません。", record));
        var allocPieces = attrs.Where(a => a.Type == AttrIndexAlloc && a.Name == "$I30").OrderBy(a => a.StartVcn).ToList();
        var allocRuns = new List<DataRun>();
        foreach (var a in allocPieces) allocRuns.AddRange(a.Runs);

        var result = new Dictionary<(ulong, string), NtfsEntry>();
        var visited = new HashSet<long>();
        void Node(ReadOnlySpan<byte> node, int depth)
        {
            if (depth > 64) return;
            int entOff = (int)BinaryPrimitives.ReadUInt32LittleEndian(node);
            int total = (int)BinaryPrimitives.ReadUInt32LittleEndian(node[4..]);
            int p = entOff;
            total = Math.Min(total, node.Length);
            while (p + 16 <= total)
            {
                var e = node[p..];
                ulong fref = BinaryPrimitives.ReadUInt64LittleEndian(e);
                int elen = BinaryPrimitives.ReadUInt16LittleEndian(e[8..]);
                int klen = BinaryPrimitives.ReadUInt16LittleEndian(e[10..]);
                uint flags = BinaryPrimitives.ReadUInt32LittleEndian(e[12..]);
                if (elen < 16 || p + elen > total) break;
                if ((flags & 1) != 0 && allocRuns.Count > 0)
                {
                    long vcn = BinaryPrimitives.ReadInt64LittleEndian(e[(elen - 8)..]);
                    if (visited.Add(vcn))
                    {
                        var ib = new byte[IndexRecordSize];
                        long off = IndexRecordSize >= ClusterSize ? vcn * ClusterSize : vcn * 512;
                        if (ReadRuns(allocRuns, off, ib) && ib.AsSpan(0, 4).SequenceEqual("INDX"u8) && ApplyFixup(ib, BytesPerSector))
                            Node(ib.AsSpan(0x18), depth + 1);
                    }
                }
                if ((flags & 2) != 0) break;
                if (klen >= 66)
                {
                    var k = e.Slice(16, klen);
                    int nlen = k[64];
                    byte ns = k[65];
                    if (ns != 2 && 66 + nlen * 2 <= klen) // DOS 短縮名は除く
                    {
                        var name = Encoding.Unicode.GetString(k.Slice(66, nlen * 2));
                        uint fattr = BinaryPrimitives.ReadUInt32LittleEndian(k[56..]);
                        var ent = new NtfsEntry
                        {
                            Ref = fref & 0xFFFFFFFFFFFF,
                            Name = name,
                            Attributes = fattr,
                            IsDirectory = (fattr & 0x10000000) != 0,
                            Size = BinaryPrimitives.ReadInt64LittleEndian(k[48..]),
                            Created = Ft(BinaryPrimitives.ReadInt64LittleEndian(k[8..])),
                            Modified = Ft(BinaryPrimitives.ReadInt64LittleEndian(k[16..])),
                        };
                        result.TryAdd((ent.Ref, name), ent);
                    }
                }
                p += elen;
            }
        }
        if (root.Length >= 32) Node(root.AsSpan(16), 0);
        // 自分自身（ルートの "."）と NTFS のシステムファイル（$MFT, $Extend など）は除く
        var list = result.Values.Where(e => e.Record != record && !(e.Name.StartsWith('$') && e.Record < 64)).ToList();
        list.Sort((a, b) => a.IsDirectory != b.IsDirectory ? (a.IsDirectory ? -1 : 1) : string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
        return list;
    }

    static DateTime Ft(long ft)
    {
        if (ft <= 0 || ft > DateTime.MaxValue.ToFileTimeUtc()) return DateTime.MinValue;
        try { return DateTime.FromFileTimeUtc(ft); } catch { return DateTime.MinValue; }
    }

    /// <summary>"\Users\foo\a.txt" のようなパスをレコード番号に解決する（大文字小文字区別なし）。</summary>
    public NtfsEntry? Resolve(string path)
    {
        var parts = path.Replace('/', '\\').Split('\\', StringSplitOptions.RemoveEmptyEntries);
        var cur = new NtfsEntry { Ref = RootDir, Name = "\\", IsDirectory = true };
        foreach (var part in parts)
        {
            if (!cur.IsDirectory) return null;
            var next = ListDirectory(cur.Record).FirstOrDefault(e => string.Equals(e.Name, part, StringComparison.OrdinalIgnoreCase));
            if (next == null) return null;
            cur = next;
        }
        return cur;
    }

    /// <summary>ファイルの実際のサイズと属性・時刻（$STANDARD_INFORMATION と $DATA から）。</summary>
    public (long Size, DateTime Created, DateTime Modified, uint Attributes) FileInfo(uint record)
    {
        var attrs = LoadAttributes(record);
        long size = attrs.Where(a => a.Type == AttrData && a.Name == "").Select(a => a.NonResident ? (a.StartVcn == 0 ? a.RealSize : -1) : a.RealSize).Where(s => s >= 0).DefaultIfEmpty(0).Max();
        var si = attrs.FirstOrDefault(a => a.Type == AttrStdInfo)?.Value;
        DateTime c = DateTime.MinValue, m = DateTime.MinValue;
        uint fa = 0;
        if (si is { Length: >= 36 })
        {
            c = Ft(BinaryPrimitives.ReadInt64LittleEndian(si));
            m = Ft(BinaryPrimitives.ReadInt64LittleEndian(si.AsSpan(8)));
            fa = BinaryPrimitives.ReadUInt32LittleEndian(si.AsSpan(32));
        }
        return (size, c, m, fa);
    }

    /// <summary>ファイルの既定データストリームを dest に書き出す（圧縮・スパース対応、EFS 暗号化は不可）。</summary>
    public long CopyFile(uint record, Stream dest, CancellationToken ct = default)
    {
        var attrs = LoadAttributes(record);
        var pieces = attrs.Where(a => a.Type == AttrData && a.Name == "").OrderBy(a => a.StartVcn).ToList();
        if (pieces.Count == 0) return 0;
        var first = pieces[0];
        if (!first.NonResident)
        {
            dest.Write(first.Value ?? []);
            return first.RealSize;
        }
        if ((first.Flags & 0x4000) != 0) throw new NotSupportedException(T("EFS で暗号化されたファイルは取り出せません。"));
        var runs = new List<DataRun>();
        foreach (var a in pieces) runs.AddRange(a.Runs);
        long size = first.RealSize, init = Math.Min(first.InitSize, size);
        if ((first.Flags & 0x0001) != 0 && first.CompressionUnit > 0)
        {
            CopyCompressed(runs, 1 << first.CompressionUnit, size, dest, ct);
            return size;
        }
        var buf = new byte[1 << 20];
        long pos = 0;
        while (pos < size)
        {
            ct.ThrowIfCancellationRequested();
            int n = (int)Math.Min(buf.Length, size - pos);
            var span = buf.AsSpan(0, n);
            if (pos >= init) span.Clear();
            else
            {
                ReadRuns(runs, pos, span);
                if (pos + n > init) span[(int)(init - pos)..].Clear();
            }
            dest.Write(span);
            pos += n;
        }
        return size;
    }

    void CopyCompressed(List<DataRun> runs, int unitClusters, long length, Stream dest, CancellationToken ct)
    {
        int unitBytes = unitClusters * ClusterSize;
        var outBuf = new byte[unitBytes];
        var inBuf = new byte[unitBytes];
        long remaining = length;
        int ri = 0;
        long rOff = 0;
        while (remaining > 0)
        {
            ct.ThrowIfCancellationRequested();
            var lcns = new List<long>(unitClusters);
            for (int i = 0; i < unitClusters; i++)
            {
                if (ri >= runs.Count) { lcns.Add(-1); continue; }
                var r = runs[ri];
                lcns.Add(r.Lcn < 0 ? -1 : r.Lcn + rOff);
                if (++rOff >= r.Clusters) { ri++; rOff = 0; }
            }
            int real = lcns.Count(l => l >= 0);
            if (real == 0) Array.Clear(outBuf);
            else
            {
                int p = 0;
                foreach (var l in lcns)
                {
                    if (l < 0) continue;
                    ReadAligned(l * ClusterSize, inBuf.AsSpan(p, ClusterSize));
                    p += ClusterSize;
                }
                if (real == unitClusters) Buffer.BlockCopy(inBuf, 0, outBuf, 0, unitBytes);
                else
                {
                    Array.Clear(outBuf);
                    Lznt1.Decompress(inBuf.AsSpan(0, p), outBuf);
                }
            }
            int n = (int)Math.Min(unitBytes, remaining);
            dest.Write(outBuf, 0, n);
            remaining -= n;
        }
    }
}

public static class Lznt1
{
    /// <summary>LZNT1 を展開する。各チャンクは 4096 バイトに展開される。</summary>
    public static int Decompress(ReadOnlySpan<byte> src, Span<byte> dst)
    {
        int sp = 0, dp = 0;
        while (sp + 2 <= src.Length && dp < dst.Length)
        {
            ushort hdr = BinaryPrimitives.ReadUInt16LittleEndian(src[sp..]);
            if (hdr == 0) break;
            int chunkLen = (hdr & 0x0FFF) + 1;
            bool compressed = (hdr & 0x8000) != 0;
            sp += 2;
            int chunkEnd = Math.Min(sp + chunkLen, src.Length);
            int chunkStart = dp;
            if (!compressed)
            {
                int n = Math.Min(chunkEnd - sp, dst.Length - dp);
                src.Slice(sp, n).CopyTo(dst[dp..]);
                dp += n;
                sp = chunkEnd;
                continue;
            }
            while (sp < chunkEnd && dp < dst.Length)
            {
                byte flags = src[sp++];
                for (int bit = 0; bit < 8 && sp < chunkEnd && dp < dst.Length; bit++)
                {
                    if ((flags & (1 << bit)) == 0) { dst[dp++] = src[sp++]; continue; }
                    if (sp + 2 > chunkEnd) { sp = chunkEnd; break; }
                    ushort tok = BinaryPrimitives.ReadUInt16LittleEndian(src[sp..]);
                    sp += 2;
                    int pos = dp - chunkStart;
                    int dispBits = 4;
                    for (int q = pos - 1; q >= 0x10; q >>= 1) dispBits++;
                    int lenMask = 0xFFFF >> dispBits;
                    int disp = (tok >> (16 - dispBits)) + 1;
                    int len = (tok & lenMask) + 3;
                    int from = dp - disp;
                    if (from < chunkStart) return dp;
                    for (int k = 0; k < len && dp < dst.Length; k++) dst[dp++] = dst[from + k];
                }
            }
            sp = chunkEnd;
            // 短いチャンク（最後以外）は 4096 バイト境界までゼロ
            int chunkOut = dp - chunkStart;
            if (chunkOut < 4096 && sp + 2 <= src.Length && BinaryPrimitives.ReadUInt16LittleEndian(src[sp..]) != 0)
            {
                int pad = Math.Min(4096 - chunkOut, dst.Length - dp);
                dst.Slice(dp, pad).Clear();
                dp += pad;
            }
        }
        return dp;
    }
}

