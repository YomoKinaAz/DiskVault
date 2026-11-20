using System.Diagnostics;
using DiskVault.Platform;
using DiskVault.Source;
using DiskVault.Storage;

namespace DiskVault.Engine;

/// <summary>復元先（ボリューム・パーティション・イメージファイル）。</summary>
public interface IVolumeTarget : IDisposable
{
    string Name { get; }
    long Length { get; }
    void Write(long offset, ReadOnlySpan<byte> data);
    /// <summary>書き戻した内容を読む（ブートセクタの修正用。offset・長さはセクタ単位）。</summary>
    void Read(long offset, Span<byte> buffer);
    void Flush();
}

/// <summary>ファイル（またはデバイスのハンドル）の base から書く。</summary>
public sealed class HandleTarget : IVolumeTarget
{
    readonly Microsoft.Win32.SafeHandles.SafeFileHandle _h;
    readonly long _base;
    readonly bool _own;
    public string Name { get; }
    public long Length { get; }

    public HandleTarget(string name, Microsoft.Win32.SafeHandles.SafeFileHandle h, long @base, long length, bool own = true)
    {
        Name = name;
        _h = h;
        _base = @base;
        Length = length;
        _own = own;
    }

    /// <summary>イメージファイルを作る（既存なら上書き）。スパースにはせず、サイズだけ確保する。</summary>
    public static HandleTarget CreateFile(string path, long length)
    {
        var h = File.OpenHandle(path, FileMode.Create, FileAccess.ReadWrite, FileShare.Read);
        RandomAccess.SetLength(h, length);
        return new HandleTarget(path, h, 0, length);
    }

    public void Write(long offset, ReadOnlySpan<byte> data)
    {
        if (offset < 0 || offset + data.Length > Length) throw new ArgumentOutOfRangeException(nameof(offset), $"{Name}: 書き込みが範囲外です。");
        RandomAccess.Write(_h, data, _base + offset);
    }

    public void Read(long offset, Span<byte> buffer) => RandomAccess.Read(_h, buffer, _base + offset);
    public void Flush() => RandomAccess.FlushToDisk(_h);
    public void Dispose() { if (_own) _h.Dispose(); }
}

public sealed record RestoreProgress(string Phase, long Done, long Total, double BytesPerSecond);

/// <summary>親ターゲットの一部（パーティション）。</summary>
public sealed class SubTarget(IVolumeTarget parent, long offset, long length) : IVolumeTarget
{
    public string Name => $"{parent.Name}@{offset}";
    public long Length => length;
    public void Write(long off, ReadOnlySpan<byte> data)
    {
        if (off < 0 || off + data.Length > length) throw new ArgumentOutOfRangeException(nameof(off), "パーティションの範囲外への書き込みです。");
        parent.Write(offset + off, data);
    }
    public void Read(long off, Span<byte> buffer) => parent.Read(offset + off, buffer);
    public void Flush() => parent.Flush();
    public void Dispose() { }
}

public static class RestoreEngine
{
    /// <summary>
    /// ディスク全体（パーティションテーブル＋各パーティション）を target に復元する（ベアメタル復元）。
    /// layout を渡すとそのパーティション配置で復元する（サイズ変更。LayoutPlanner.Build で作る）。
    /// 返り値は、パーティションを広げたので復元後に NTFS の拡張（NtfsResize.ExtendOnDisk）が必要なパーティション。
    /// </summary>
    public static List<PartitionRecord> RestoreDisk(Repository repo, RecoveryPoint p, DiskRecord disk, IVolumeTarget target, bool writeUnused,
        IProgress<RestoreProgress>? progress = null, Action<string>? log = null, CancellationToken ct = default, DiskRecord? layout = null)
    {
        log ??= _ => { };
        layout ??= disk;
        var grow = new List<PartitionRecord>();
        long need = layout.Partitions.Count == 0 ? 0 : layout.Partitions.Max(x => x.Offset + x.Length) + 1024 * 1024;
        if (target.Length < need)
            throw new IOException($"復元先ディスク（{Fmt.Size(target.Length)}）が小さすぎます。少なくとも {Fmt.Size(need)} 必要です。");
        // 先頭 1MiB と末尾 1MiB を消してから新しいテーブルを書く（古い GPT や署名の残骸を消す）
        var zero = new byte[1024 * 1024];
        target.Write(0, zero);
        target.Write(target.Length - zero.Length, zero);
        Platform.PartitionTable.Write(layout, target, disk.SectorSize);
        log($"パーティションテーブル（{layout.Style}、{layout.Partitions.Count} 個）を書き込みました。");
        foreach (var part in layout.Partitions.OrderBy(x => x.Offset))
        {
            ct.ThrowIfCancellationRequested();
            var sub = new SubTarget(target, part.Offset, part.Length);
            // 古いファイルシステムの署名が残らないように先頭を消す
            sub.Write(0, zero.AsSpan(0, (int)Math.Min(zero.Length, part.Length)));
            var orig = disk.Partitions.FirstOrDefault(x => x.Number == part.Number);
            string size = orig != null && orig.Length != part.Length ? $"{Fmt.Size(orig.Length)} → {Fmt.Size(part.Length)}" : Fmt.Size(part.Length);
            if (part.Volume is not int vi)
            {
                log($"  パーティション {part.Number}（{part.Kind}、{size}）: 中身なしで作成");
                continue;
            }
            var vol = p.Volumes[vi];
            log($"  パーティション {part.Number}（{part.Kind}、{size}）← {vol.Display.Trim()}");
            if (part.Length < vol.Size)
            {
                if (vol.FileSystem != "NTFS") throw new InvalidOperationException($"パーティション {part.Number} は NTFS ではないので縮小できません。");
                RestoreVolume(repo, p, vi, sub, writeUnused, progress, ct, limit: part.Length);
                using var src = new PointVolumeSource(repo, p, vi);
                Platform.NtfsResize.ShrinkOffline(src, sub, part.Length, log);
            }
            else
            {
                RestoreVolume(repo, p, vi, sub, writeUnused, progress, ct);
                if (part.Length >= vol.Size + LayoutPlanner.Align && vol.FileSystem == "NTFS") grow.Add(part);
            }
            FixBootLocation(sub, part.Offset / disk.SectorSize, disk.SectorSize, log);
        }
        target.Flush();
        return grow;
    }

    /// <summary>
    /// NTFS / FAT のブートセクタにある「パーティションの開始セクタ」（BPB の hidden sectors、0x1C）を新しい位置に合わせる。
    /// パーティションの開始位置が元と変わったとき（前のパーティションのサイズ変更・位置の移動）に必要。
    /// NTFS はボリューム末尾の予備ブートセクタ、FAT32 は予備ブートセクタ（通常セクタ 6）も直す。
    /// </summary>
    public static bool FixBootLocation(IVolumeTarget vol, long startLba, int sectorSize, Action<string>? log = null)
    {
        if (startLba > uint.MaxValue) return false;
        int ss = Math.Max(512, sectorSize);
        var b = new byte[ss];
        vol.Read(0, b);
        if (b[510] != 0x55 || b[511] != 0xAA) return false;
        bool ntfs = System.Text.Encoding.ASCII.GetString(b, 3, 8) == "NTFS    ";
        bool fat32 = System.Text.Encoding.ASCII.GetString(b, 0x52, 5) == "FAT32";
        bool fat16 = System.Text.Encoding.ASCII.GetString(b, 0x36, 4) == "FAT1";
        if (!ntfs && !fat32 && !fat16) return false;
        uint old = BitConverter.ToUInt32(b, 0x1C);
        if (old == (uint)startLba) return false;
        int bps = BitConverter.ToUInt16(b, 11);
        if (bps < 512 || bps > 4096 || bps % ss != 0 && ss % bps != 0) return false;
        void Patch(long at)
        {
            long aligned = at / ss * ss; // 物理セクタ単位で読み書きする
            int i = (int)(at - aligned);
            if (at < 0 || aligned + ss > vol.Length || i + 512 > ss) return;
            var s = new byte[ss];
            vol.Read(aligned, s);
            if (s[i + 510] != 0x55 || s[i + 511] != 0xAA || BitConverter.ToUInt32(s, i + 0x1C) != old) return;
            BitConverter.TryWriteBytes(s.AsSpan(i + 0x1C, 4), (uint)startLba);
            vol.Write(aligned, s);
        }
        Patch(0);
        if (ntfs) Patch(BitConverter.ToInt64(b, 0x28) * bps);
        if (fat32 && BitConverter.ToUInt16(b, 0x32) is var bk and > 0 and < 0xFFFF) Patch((long)bk * bps);
        log?.Invoke($"    ブートセクタの開始位置を {old} → {startLba} セクタに更新しました。");
        return true;
    }
    /// <summary>
    /// 復元ポイントのボリュームを target に書き戻す。
    /// writeUnused = true なら未使用領域もゼロで埋める（イメージファイル向け）。false なら使用中ブロックだけ書く（実ディスク向け・高速）。
    /// </summary>
    public static void RestoreVolume(Repository repo, RecoveryPoint p, int volume, IVolumeTarget target, bool writeUnused,
        IProgress<RestoreProgress>? progress = null, CancellationToken ct = default, int threads = 0, long limit = -1)
    {
        var map = repo.LoadMap(p, volume);
        // limit: 縮小して復元するとき、先頭 limit バイトだけ書く（後ろに使用中データが無いことは呼び出し側で確認済み）
        long end = limit > 0 && limit < map.VolumeSize ? limit : map.VolumeSize;
        long blocks = (end + map.BlockSize - 1) / map.BlockSize;
        int LenOf(long b) => (int)Math.Min(map.LengthOf(b), end - b * map.BlockSize);
        if (target.Length < end)
            throw new IOException($"復元先 {target.Name}（{Fmt.Size(target.Length)}）が元のボリューム（{Fmt.Size(map.VolumeSize)}）より小さいです。");
        using var store = repo.OpenChain(p.Chain, forWrite: false);
        int bs = map.BlockSize;
        long total = 0;
        for (long i = 0; i < blocks; i++)
            if (writeUnused || map.Kinds[i] != BlockMap.Unused) total += LenOf(i);

        threads = threads > 0 ? threads : Math.Clamp(Environment.ProcessorCount, 1, 8);
        const int Batch = 64;
        var buf = new byte[Batch * bs];
        var zero = new byte[bs];
        long done = 0;
        var sw = Stopwatch.StartNew();
        var report = Stopwatch.StartNew();
        var po = new ParallelOptions { MaxDegreeOfParallelism = threads, CancellationToken = ct };
        var idx = new List<long>(Batch);
        long i0 = 0;
        var tDecode = new Stopwatch();
        var tWrite = new Stopwatch();
        bool trace = Environment.GetEnvironmentVariable("DVAULT_TRACE") == "1";
        while (i0 < blocks)
        {
            ct.ThrowIfCancellationRequested();
            idx.Clear();
            long i = i0;
            for (; i < blocks && idx.Count < Batch; i++)
                if (writeUnused || map.Kinds[i] != BlockMap.Unused) idx.Add(i);
            i0 = i;
            if (idx.Count == 0) continue;
            tDecode.Start();
            Parallel.For(0, idx.Count, po, k =>
            {
                long b = idx[k];
                var dst = buf.AsSpan(k * bs, map.LengthOf(b));
                if (map.Kinds[b] == BlockMap.Data) store.Read(map.Ids[b], dst);
                else dst.Clear();
            });
            tDecode.Stop();
            tWrite.Start();
            // 連続したブロックはまとめて書く
            int s = 0;
            while (s < idx.Count)
            {
                int run = 1;
                while (s + run < idx.Count && idx[s + run] == idx[s] + run) run++;
                int bytes = 0;
                for (int r = 0; r < run; r++) bytes += LenOf(idx[s + r]);
                target.Write(idx[s] * bs, buf.AsSpan(s * bs, bytes));
                done += bytes;
                s += run;
            }
            tWrite.Stop();
            if (progress != null && report.ElapsedMilliseconds >= 500)
            {
                report.Restart();
                progress.Report(new RestoreProgress("復元中", done, total, done / Math.Max(0.001, sw.Elapsed.TotalSeconds)));
            }
        }
        var tf = Stopwatch.StartNew();
        target.Flush();
        if (trace) Console.Error.WriteLine($"[trace] restore decode {tDecode.ElapsedMilliseconds} ms, write {tWrite.ElapsedMilliseconds} ms, flush {tf.ElapsedMilliseconds} ms");
        progress?.Report(new RestoreProgress("完了", done, total, done / Math.Max(0.001, sw.Elapsed.TotalSeconds)));
    }
}

/// <summary>復元ポイントのボリュームを読み取り専用のボリュームとして見せる（ファイル単位の復元用）。</summary>
public sealed class PointVolumeSource : IVolumeSource
{
    readonly BlockMap _map;
    readonly ChainStore _store;
    readonly Dictionary<long, byte[]> _cache = [];
    readonly LinkedList<long> _lru = new();
    const int CacheBlocks = 64;

    public string Name { get; }
    public long Length => _map.VolumeSize;
    public int SectorSize => 512;

    public PointVolumeSource(Repository repo, RecoveryPoint p, int volume)
    {
        _map = repo.LoadMap(p, volume);
        _store = repo.OpenChain(p.Chain, forWrite: false);
        Name = $"{p.Id}:vol{volume}";
    }

    public long Read(long offset, Span<byte> dest)
    {
        int bs = _map.BlockSize;
        int done = 0;
        while (done < dest.Length)
        {
            long pos = offset + done;
            long b = pos / bs;
            int within = (int)(pos - b * bs);
            int n = Math.Min(dest.Length - done, _map.LengthOf(b) - within);
            var block = Block(b);
            if (block == null) dest.Slice(done, n).Clear();
            else block.AsSpan(within, n).CopyTo(dest[done..]);
            done += n;
        }
        return 0;
    }

    byte[]? Block(long b)
    {
        if (_map.Kinds[b] != BlockMap.Data) return null;
        lock (_cache)
        {
            if (_cache.TryGetValue(b, out var c))
            {
                _lru.Remove(b);
                _lru.AddFirst(b);
                return c;
            }
        }
        var data = new byte[_map.LengthOf(b)];
        _store.Read(_map.Ids[b], data);
        lock (_cache)
        {
            if (_cache.Count >= CacheBlocks)
            {
                var old = _lru.Last!.Value;
                _lru.RemoveLast();
                _cache.Remove(old);
            }
            if (_cache.TryAdd(b, data)) _lru.AddFirst(b);
        }
        return data;
    }

    public void Dispose() => _store.Dispose();
}


