using System.Diagnostics;
using DiskVault.Storage;

namespace DiskVault.Cli;

/// <summary>dvault bench: ハッシュ・圧縮・伸張の速度を測る（性能調査用）。</summary>
static class Bench
{
    public static int Run(Args a)
    {
        var codec = new BlockCodec(int.Parse(a.Opt("level") ?? "3"), null, null);
        const int bs = 256 * 1024, n = 512;
        var rnd = new Random(1);
        var blocks = new byte[n][];
        for (int i = 0; i < n; i++)
        {
            blocks[i] = new byte[bs];
            if (i % 2 == 0) rnd.NextBytes(blocks[i]);
            else for (int j = 0; j < bs; j += 4096) blocks[i][j] = (byte)i;
        }
        double mb = (double)n * bs / (1 << 20);
        var sw = Stopwatch.StartNew();
        var ids = new BlockId[n];
        for (int i = 0; i < n; i++) ids[i] = codec.ComputeId(blocks[i]);
        Console.WriteLine($"SHA-256 (1 スレッド): {mb / sw.Elapsed.TotalSeconds:0} MB/s");
        sw.Restart();
        var enc = new (byte, byte[])[n];
        for (int i = 0; i < n; i++) enc[i] = codec.Encode(ids[i], blocks[i]);
        Console.WriteLine($"圧縮 (1 スレッド): {mb / sw.Elapsed.TotalSeconds:0} MB/s");
        sw.Restart();
        Parallel.For(0, n, i => enc[i] = codec.Encode(ids[i], blocks[i]));
        Console.WriteLine($"圧縮 (並列): {mb / sw.Elapsed.TotalSeconds:0} MB/s");
        sw.Restart();
        var dst = new byte[bs];
        for (int i = 0; i < n; i++) codec.Decode(ids[i], enc[i].Item1, enc[i].Item2, dst);
        Console.WriteLine($"伸張+検証 (1 スレッド): {mb / sw.Elapsed.TotalSeconds:0} MB/s");

        var dir = Path.Combine(Path.GetTempPath(), "dvault-bench-" + Environment.ProcessId);
        try
        {
            var repo = Repository.Create(dir, null);
            sw.Restart();
            using (var st = repo.OpenChain("c", true))
            {
                st.PackTargetSize = long.MaxValue;
                for (int i = 0; i < n; i++) st.Add(ids[i], enc[i].Item1, bs, enc[i].Item2);
                Console.WriteLine($"  Add のみ: {sw.Elapsed.TotalMilliseconds:0} ms");
                var s2 = Stopwatch.StartNew();
                st.Flush();
                Console.WriteLine($"  Flush（fsync・改名・索引）: {s2.Elapsed.TotalMilliseconds:0} ms");
            }
            Console.WriteLine($"pack 書き込み: {mb / sw.Elapsed.TotalSeconds:0} MB/s");
            sw.Restart();
            using (var st = repo.OpenChain("c", false))
            {
                Console.WriteLine($"索引読み込み: {sw.Elapsed.TotalMilliseconds:0} ms");
                sw.Restart();
                for (int i = 0; i < n; i++) st.Read(ids[i], dst);
                Console.WriteLine($"pack 読み出し+伸張 (1 スレッド): {mb / sw.Elapsed.TotalSeconds:0} MB/s");
                sw.Restart();
                Parallel.For(0, n, () => new byte[bs], (i, _, b) => { st.Read(ids[i], b); return b; }, _ => { });
                Console.WriteLine($"pack 読み出し+伸張 (並列): {mb / sw.Elapsed.TotalSeconds:0} MB/s");
            }
        }
        finally { Directory.Delete(dir, true); }
        return 0;
    }
}
