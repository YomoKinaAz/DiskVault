using DiskVault.Storage;

namespace DiskVault.Engine;

public sealed record VerifyResult(int Points, long Blocks, long Bad, List<string> Errors);

public sealed record RetentionResult(List<string> DeletedPoints, int DeletedChains, int DeletedPacks, int RewrittenPacks, long FreedBytes);

public static class Maintenance
{
    /// <summary>復元ポイントが参照するブロックを全部読み、ハッシュを検証する。</summary>
    public static VerifyResult Verify(Repository repo, IEnumerable<RecoveryPoint> points, IProgress<(long Done, long Total)>? progress = null,
        CancellationToken ct = default)
    {
        var errors = new List<string>();
        long blocks = 0, bad = 0;
        int np = 0;
        foreach (var chainGroup in points.GroupBy(p => p.Chain))
        {
            ChainStore store;
            try { store = repo.OpenChain(chainGroup.Key, forWrite: false); }
            catch (DirectoryNotFoundException e) { errors.Add(e.Message); bad++; continue; }
            using (store)
            {
                var need = new Dictionary<BlockId, int>();
                foreach (var p in chainGroup)
                {
                    np++;
                    for (int v = 0; v < p.Volumes.Count; v++)
                    {
                        BlockMap map;
                        try { map = repo.LoadMap(p, v); }
                        catch (Exception e) when (e is IOException or InvalidDataException) { errors.Add($"{p.Id} vol{v}: {e.Message}"); bad++; continue; }
                        for (long i = 0; i < map.Count; i++)
                            if (map.Kinds[i] == BlockMap.Data) need.TryAdd(map.Ids[i], map.LengthOf(i));
                    }
                }
                long done = 0, total = need.Count;
                var po = new ParallelOptions { MaxDegreeOfParallelism = Math.Clamp(Environment.ProcessorCount, 1, 8), CancellationToken = ct };
                Parallel.ForEach(need, po, kv =>
                {
                    var buf = new byte[kv.Value];
                    try { store.Read(kv.Key, buf); }
                    catch (Exception e) when (e is IOException or InvalidDataException)
                    {
                        lock (errors) { errors.Add(e.Message); bad++; }
                    }
                    long d = Interlocked.Increment(ref done);
                    if (d % 256 == 0) progress?.Report((d, total));
                });
                blocks += total;
                progress?.Report((total, total));
            }
        }
        return new VerifyResult(np, blocks, bad, errors);
    }

    /// <summary>
    /// 新しい方から keep 個の復元ポイントを残して古いものを削除し、どのポイントからも使われなくなったデータを回収する。
    /// </summary>
    public static RetentionResult ApplyRetention(Repository repo, int keep, Action<string>? log = null)
    {
        log ??= _ => { };
        if (keep < 1) keep = 1;
        repo.Lock();
        try
        {
            var points = repo.ListPoints();
            var deleted = new List<string>();
            foreach (var p in points.Take(Math.Max(0, points.Count - keep)))
            {
                log(T("古い復元ポイントを削除: {0}", p.Id));
                Directory.Delete(repo.PointDir(p.Id), true);
                deleted.Add(p.Id);
            }
            var r = CollectGarbage(repo, log);
            return r with { DeletedPoints = deleted };
        }
        finally { repo.Unlock(); }
    }

    /// <summary>参照されないチェーン・pack・書きかけポイントを消す（ロックを取ってから呼ぶ）。</summary>
    public static RetentionResult CollectGarbage(Repository repo, Action<string>? log = null)
    {
        log ??= _ => { };
        var points = repo.ListPoints();
        // point.json の無いフォルダ＝中断したバックアップ
        if (Directory.Exists(repo.PointsDir))
            foreach (var d in Directory.EnumerateDirectories(repo.PointsDir))
                if (!File.Exists(Path.Combine(d, "point.json")))
                {
                    log(T("中断したバックアップの残りを削除: {0}", Path.GetFileName(d)));
                    Directory.Delete(d, true);
                }
        int chains = 0, packs = 0, rewritten = 0;
        long freed = 0;
        if (!Directory.Exists(repo.ChainsDir)) return new([], 0, 0, 0, 0);
        foreach (var dir in Directory.EnumerateDirectories(repo.ChainsDir).ToList())
        {
            var chain = Path.GetFileName(dir);
            var users = points.Where(p => p.Chain == chain).ToList();
            if (users.Count == 0)
            {
                long size = DirSize(dir);
                log(T("使われなくなったチェーンを削除: {0}（{1}）", chain, Fmt.Size(size)));
                Directory.Delete(dir, true);
                freed += size;
                chains++;
                continue;
            }
            var live = new HashSet<BlockId>();
            foreach (var p in users)
                for (int v = 0; v < p.Volumes.Count; v++)
                {
                    var map = repo.LoadMap(p, v);
                    for (long i = 0; i < map.Count; i++)
                        if (map.Kinds[i] == BlockMap.Data) live.Add(map.Ids[i]);
                }
            using var store = repo.OpenChain(chain, forWrite: true);
            var r = store.Compact(live);
            if (r.DeletedPacks + r.RewrittenPacks > 0)
                log(T("チェーン {0}: pack 削除 {1}、詰め直し {2}（{3} 解放）", chain, r.DeletedPacks, r.RewrittenPacks, Fmt.Size(r.FreedBytes)));
            packs += r.DeletedPacks;
            rewritten += r.RewrittenPacks;
            freed += r.FreedBytes;
        }
        return new([], chains, packs, rewritten, freed);
    }

    public static long DirSize(string dir) =>
        Directory.Exists(dir) ? new DirectoryInfo(dir).EnumerateFiles("*", SearchOption.AllDirectories).Sum(f => f.Length) : 0;
}
