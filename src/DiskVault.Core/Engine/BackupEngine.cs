using System.Diagnostics;
using System.Security.Cryptography;
using DiskVault.Source;
using DiskVault.Storage;

namespace DiskVault.Engine;

/// <summary>バックアップするボリューム 1 つ。Open は読み取り元（通常は VSS スナップショット）を開く。</summary>
public sealed class VolumeInput
{
    public required VolumeRecord Record { get; init; }
    public required Func<IVolumeSource> Open { get; init; }
}

public sealed class BackupOptions
{
    public string Job { get; set; } = "";
    public PointKind Kind { get; set; } = PointKind.Incremental;
    public string? Comment { get; set; }
    public List<DiskRecord> Disks { get; set; } = [];
    public bool ExcludeSystemFiles { get; set; } = true;
    public int Threads { get; set; } = Math.Clamp(Environment.ProcessorCount - 1, 1, 16);
    /// <summary>テスト用：復元ポイント名を固定する。</summary>
    public string? PointId { get; set; }
    public DateTime? Time { get; set; }
    /// <summary>スナップショット作成時の警告など、復元ポイントに残すもの。</summary>
    public List<string> Warnings { get; set; } = [];
    /// <summary>復元ポイントに記録する PC 名（回復環境では元の Windows の名前）。null ならこの PC の名前。</summary>
    public string? Machine { get; set; }
    /// <summary>"writers" / "crash" / "offline"（回復環境で停止中のディスクを直接読んだ） / ""</summary>
    public string Consistency { get; set; } = "";
}

public sealed record BackupProgress(string Phase, int Volume, int VolumeCount, long Done, long Total, long NewBytes, double BytesPerSecond);

public static class BackupEngine
{
    const int BatchBlocks = 64;

    public static RecoveryPoint Run(Repository repo, IReadOnlyList<VolumeInput> volumes, BackupOptions o,
        IProgress<BackupProgress>? progress = null, Action<string>? log = null, CancellationToken ct = default)
    {
        log ??= _ => { };
        repo.Lock();
        var sw = Stopwatch.StartNew();
        var points = repo.ListPoints();
        var last = points.LastOrDefault();
        var kind = o.Kind;
        string chain;
        if (kind == PointKind.Incremental && last != null && Directory.Exists(repo.ChainDir(last.Chain)) && SameVolumes(last, volumes))
            chain = last.Chain;
        else
        {
            if (kind == PointKind.Incremental)
                log(last == null ? "最初のバックアップなのでフルバックアップを行います。" : "前回とボリューム構成が異なるためフルバックアップを行います。");
            kind = PointKind.Full;
            chain = DateTime.UtcNow.ToString("yyyyMMddHHmmss") + "-" + Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(3));
        }

        var time = o.Time ?? DateTime.Now;
        var id = o.PointId ?? time.ToString("yyyyMMdd-HHmmss");
        for (int n = 2; Directory.Exists(repo.PointDir(id)); n++) id = (o.PointId ?? time.ToString("yyyyMMdd-HHmmss")) + "-" + n;
        var point = new RecoveryPoint
        {
            Id = id,
            Time = time.ToUniversalTime(),
            Kind = kind,
            Chain = chain,
            Job = o.Job,
            Machine = o.Machine ?? Environment.MachineName,
            OsVersion = Environment.OSVersion.VersionString,
            Comment = o.Comment,
            Disks = o.Disks,
            Consistency = o.Consistency.Length > 0 ? o.Consistency : null,
        };
        point.Warnings.AddRange(o.Warnings);
        log($"{(kind == PointKind.Full ? "フル" : "増分")}バックアップ開始: 復元ポイント {id}（チェーン {chain}）");
        Directory.CreateDirectory(repo.PointDir(id));
        try
        {
            using var store = repo.OpenChain(chain, forWrite: true);
            for (int vi = 0; vi < volumes.Count; vi++)
            {
                ct.ThrowIfCancellationRequested();
                var v = volumes[vi];
                var rec = v.Record;
                rec.Index = vi;
                using var src = v.Open();
                var used = UsedMap.Detect(src, o.ExcludeSystemFiles);
                rec.Size = src.Length;
                rec.FileSystem ??= used.FileSystem;
                rec.BlockSize = repo.Config.BlockSize;
                foreach (var ex in used.Excluded) log($"  除外: {rec.Display.Trim()} {ex}");
                log($"ボリューム {vi + 1}/{volumes.Count}: {rec.Display.Trim()} {Fmt.Size(src.Length)}（使用中 {Fmt.Size(used.UsedBytes)}）");
                var map = BackupVolume(store, repo.Codec, src, used, rec, point.Stats, o, vi, volumes.Count, progress, sw, ct);
                map.Save(repo.MapPath(id, vi));
                if (rec.UnreadableBytes > 0)
                    point.Warnings.Add($"{rec.Display.Trim()}: 読めないセクタが {Fmt.Size(rec.UnreadableBytes)} あり、ゼロで保存しました。");
                point.Volumes.Add(rec);
            }
            store.Flush();
            point.Stats.StoredBytes = store.WrittenBytes;
            point.Stats.Seconds = sw.Elapsed.TotalSeconds;
            repo.SavePoint(point);
        }
        catch
        {
            TryDeleteDir(repo.PointDir(id));
            throw;
        }
        finally
        {
            repo.Unlock();
        }
        log($"完了: 読み取り {Fmt.Size(point.Stats.ReadBytes)}、新規データ {Fmt.Size(point.Stats.NewBytes)}（保存 {Fmt.Size(point.Stats.StoredBytes)}）、{Fmt.Duration(sw.Elapsed)}");
        return point;
    }

    static bool SameVolumes(RecoveryPoint last, IReadOnlyList<VolumeInput> vols)
    {
        // 構成が変わってもデータの重複排除は効くが、チェーンを分けておくと世代管理がわかりやすい
        if (last.Volumes.Count != vols.Count) return false;
        for (int i = 0; i < vols.Count; i++)
        {
            var a = last.Volumes[i];
            var b = vols[i].Record;
            if (!string.Equals(a.VolumeGuid ?? a.Letter, b.VolumeGuid ?? b.Letter, StringComparison.OrdinalIgnoreCase)) return false;
        }
        return true;
    }

    sealed class Slot
    {
        public long Index;
        public int Length;
        public byte Kind;
        public BlockId Id;
        public byte Codec;
        public byte[]? Payload;
    }

    static BlockMap BackupVolume(ChainStore store, BlockCodec codec, IVolumeSource src, UsedMap used, VolumeRecord rec,
        PointStats stats, BackupOptions o, int vi, int vcount, IProgress<BackupProgress>? progress, Stopwatch sw, CancellationToken ct)
    {
        int bs = rec.BlockSize;
        var map = new BlockMap(bs, src.Length);
        var todo = new List<long>();
        long total = 0;
        for (long i = 0; i < map.Count; i++)
        {
            int len = map.LengthOf(i);
            if (used.AnyUsed(i * bs, len)) { todo.Add(i); total += len; }
        }
        rec.UsedBytes = used.UsedBytes;
        rec.BlockCount = todo.Count;
        stats.UsedBytes += rec.UsedBytes;

        long done = 0;
        var lastReport = Stopwatch.StartNew();
        var volSw = Stopwatch.StartNew();
        var po = new ParallelOptions { MaxDegreeOfParallelism = o.Threads, CancellationToken = ct };

        // 読み取りと処理を重ねる（ダブルバッファ）
        byte[] bufA = new byte[BatchBlocks * bs], bufB = new byte[BatchBlocks * bs];
        Task<(Slot[] Slots, long Bad)>? pending = null;
        int next = 0;

        Task<(Slot[], long)> ReadBatch(int start, byte[] buf) => Task.Run(() =>
        {
            int count = Math.Min(BatchBlocks, todo.Count - start);
            var slots = new Slot[count];
            long bad = 0;
            int k = 0;
            while (k < count)
            {
                // 連続したブロックはまとめて読む
                int run = 1;
                while (k + run < count && todo[start + k + run] == todo[start + k] + run) run++;
                long first = todo[start + k];
                long off = first * bs;
                int bytes = 0;
                for (int r = 0; r < run; r++) bytes += map.LengthOf(first + r);
                bad += ReadRange(src, used, off, buf.AsSpan(k * bs, bytes));
                for (int r = 0; r < run; r++)
                    slots[k + r] = new Slot { Index = first + r, Length = map.LengthOf(first + r) };
                k += run;
            }
            return (slots, bad);
        }, ct);

        if (todo.Count > 0) pending = ReadBatch(0, bufA);
        bool useA = true;
        while (pending != null)
        {
            var (slots, bad) = pending.GetAwaiter().GetResult();
            var cur = useA ? bufA : bufB;
            next += slots.Length;
            pending = next < todo.Count ? ReadBatch(next, useA ? bufB : bufA) : null;
            useA = !useA;
            rec.UnreadableBytes += bad;

            Parallel.For(0, slots.Length, po, k =>
            {
                var s = slots[k];
                var data = cur.AsSpan(k * bs, s.Length);
                used.ClearUnused(s.Index * bs, data);
                if (!data.ContainsAnyExcept((byte)0)) { s.Kind = BlockMap.ZeroBlock; return; }
                s.Kind = BlockMap.Data;
                s.Id = codec.ComputeId(data);
                if (store.Contains(s.Id)) return;
                (s.Codec, s.Payload) = codec.Encode(s.Id, data);
            });

            foreach (var s in slots)
            {
                map.Kinds[s.Index] = s.Kind;
                if (s.Kind == BlockMap.Data)
                {
                    map.Ids[s.Index] = s.Id;
                    if (s.Payload != null && store.Add(s.Id, s.Codec, s.Length, s.Payload) > 0)
                    {
                        stats.NewBlocks++;
                        stats.NewBytes += s.Length;
                    }
                }
                done += s.Length;
                stats.ReadBytes += s.Length;
            }
            if (progress != null && (lastReport.ElapsedMilliseconds >= 500 || pending == null))
            {
                lastReport.Restart();
                double rate = done / Math.Max(0.001, volSw.Elapsed.TotalSeconds);
                progress.Report(new BackupProgress("バックアップ中", vi, vcount, done, total, stats.NewBytes, rate));
            }
        }
        return map;
    }

    /// <summary>
    /// 読み取り。VSS スナップショットはファイルシステムの外側（NTFS の末尾＝バックアップブートセクタ）を読ませないことがあるので、
    /// そこが読めなければ先頭のブートセクタから作り直す（NTFS の仕様どおりの内容になる）。
    /// </summary>
    static long ReadRange(IVolumeSource src, UsedMap used, long off, Span<byte> dst)
    {
        long tail = used.TailStart;
        if (used.BootSector == null || off + dst.Length <= tail) return src.Read(off, dst);
        int head = (int)Math.Clamp(tail - off, 0, dst.Length);
        long bad = head > 0 ? src.Read(off, dst[..head]) : 0;
        var t = dst[head..];
        if (src.Read(off + head, t) > 0)
        {
            t.Clear();
            long bo = used.BackupBootOffset - (off + head);
            if (bo >= 0 && bo + used.BootSector.Length <= t.Length) used.BootSector.CopyTo(t[(int)bo..]);
        }
        return bad;
    }

    static void TryDeleteDir(string d)
    {
        try { if (Directory.Exists(d)) Directory.Delete(d, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
}


