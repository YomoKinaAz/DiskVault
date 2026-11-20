using System.Security.Cryptography;
using DiskVault.Engine;
using DiskVault.Platform;
using DiskVault.Source;
using DiskVault.Storage;

namespace DiskVault.Cli;

/// <summary>管理者権限なしで回せる自己テスト（RAW イメージ・GPT ディスクイメージで全経路を確認する）。</summary>
static class SelfTest
{
    static int _fail;

    static void Check(bool ok, string what)
    {
        Console.WriteLine($"  [{(ok ? "OK" : "NG")}] {what}");
        if (!ok) _fail++;
    }

    static string Sha(string path)
    {
        using var f = File.OpenRead(path);
        return Convert.ToHexStringLower(SHA256.HashData(f));
    }

    public static int Run(Args a)
    {
        // テストはメール本文などの日本語を照合するので、表示言語にかかわらず日本語で動かす
        Environment.SetEnvironmentVariable("DISKVAULT_LANG", "ja");
        Loc.Set("ja");
        var work = a.Pos(0) ?? Path.Combine(Path.GetTempPath(), "dvault-selftest-" + Environment.ProcessId);
        if (Directory.Exists(work)) Directory.Delete(work, true);
        Directory.CreateDirectory(work);
        _fail = 0;
        try
        {
            foreach (var pw in new[] { null, "テストパスワード" })
            {
                Console.WriteLine($"== RAW ボリューム（{(pw == null ? "暗号化なし" : "暗号化あり")}）");
                RawVolume(Path.Combine(work, pw == null ? "plain" : "enc"), pw);
            }
            Console.WriteLine("== GPT ディスクイメージ（ベアメタル復元の経路）");
            DiskImage(Path.Combine(work, "disk"));
            SelfTestJobs.Run(Path.Combine(work, "jobs"), Check);
        }
        finally
        {
            if (!a.Flag("keep-work")) try { Directory.Delete(work, true); } catch (IOException) { }
        }
        Console.WriteLine(_fail == 0 ? "selftest: すべて OK" : $"selftest: NG {_fail} 件");
        return _fail == 0 ? 0 : 1;
    }

    static void MakeVolume(string path, int seed, long size)
    {
        var rnd = new Random(seed);
        using var fs = new FileStream(path, FileMode.Create);
        // ブロック（256KiB）単位で種類を変える
        var block = new byte[256 * 1024];
        var repeat = new byte[256 * 1024];
        rnd.NextBytes(repeat);
        for (long p = 0; p < size; p += block.Length)
        {
            int kind = (int)(p / block.Length % 7);
            if (kind is 0 or 1) Array.Clear(block);                         // ゼロ領域
            else if (kind == 2) repeat.CopyTo(block);                        // 重複
            else if (kind == 3) for (int i = 0; i < block.Length; i++) block[i] = (byte)(i * 7 + p / 65536); // 圧縮しやすい
            else rnd.NextBytes(block);                                    // 圧縮できない
            fs.Write(block, 0, (int)Math.Min(block.Length, size - p));
        }
    }

    static void Mutate(string path, int seed, int count)
    {
        var rnd = new Random(seed);
        using var fs = new FileStream(path, FileMode.Open);
        var b = new byte[4096];
        for (int i = 0; i < count; i++)
        {
            rnd.NextBytes(b);
            fs.Position = rnd.NextInt64(fs.Length / 4096) * 4096;
            fs.Write(b);
        }
    }

    static void RawVolume(string dir, string? pw)
    {
        Directory.CreateDirectory(dir);
        var vol = Path.Combine(dir, "vol.img");
        var repoDir = Path.Combine(dir, "repo");
        MakeVolume(vol, 1, 48L << 20);
        var sha1 = Sha(vol);

        var repo = Repository.Create(repoDir, pw);
        RecoveryPoint Backup(PointKind k, string id)
        {
            using var plan = SourcePlanner.Build([vol]);
            return BackupEngine.Run(repo, plan.Volumes, new BackupOptions { Job = "test", Kind = k, PointId = id });
        }
        string RestoreSha(RecoveryPoint p, string name)
        {
            var outp = Path.Combine(dir, name);
            using (var t = HandleTarget.CreateFile(outp, p.Volumes[0].Size))
                RestoreEngine.RestoreVolume(repo, p, 0, t, writeUnused: true);
            var s = Sha(outp);
            File.Delete(outp);
            return s;
        }

        var p1 = Backup(PointKind.Incremental, "p1");
        Check(p1.Kind == PointKind.Full, "最初のバックアップはフルになる");
        Check(p1.Stats.NewBytes < p1.Stats.ReadBytes, $"重複・ゼロ排除が効く（読み取り {Fmt.Size(p1.Stats.ReadBytes)} → 新規 {Fmt.Size(p1.Stats.NewBytes)}、保存 {Fmt.Size(p1.Stats.StoredBytes)}）");
        Check(RestoreSha(p1, "r1.img") == sha1, "p1 を復元すると元と一致");

        Mutate(vol, 2, 20);
        var sha2 = Sha(vol);
        var p2 = Backup(PointKind.Incremental, "p2");
        Check(p2.Kind == PointKind.Incremental && p2.Chain == p1.Chain, "2 回目は同じチェーンの増分");
        Check(p2.Stats.NewBytes <= 20L * repo.Config.BlockSize, $"増分は変更ブロックだけ（新規 {Fmt.Size(p2.Stats.NewBytes)}）");
        Check(RestoreSha(p2, "r2.img") == sha2, "p2 を復元すると変更後と一致");
        Check(RestoreSha(p1, "r1b.img") == sha1, "p2 の後でも p1 は元のまま復元できる");

        var reopened = Repository.Open(repoDir, pw);
        Check(reopened.ListPoints().Count == 2, "リポジトリを開き直すとポイントが 2 つ");
        if (pw != null)
        {
            bool denied = false;
            try { Repository.Open(repoDir, "wrong"); } catch (UnauthorizedAccessException) { denied = true; }
            Check(denied, "違うパスワードでは開けない");
            bool needPw = false;
            try { Repository.Open(repoDir, null); } catch (UnauthorizedAccessException) { needPw = true; }
            Check(needPw, "パスワードなしでは開けない");
        }

        var v = Maintenance.Verify(repo, repo.ListPoints());
        Check(v.Bad == 0, $"検証 OK（{v.Blocks} ブロック）");

        Mutate(vol, 3, 5);
        var sha3 = Sha(vol);
        var p3 = Backup(PointKind.Full, "p3");
        Check(p3.Kind == PointKind.Full && p3.Chain != p1.Chain, "フル指定で新しいチェーン");
        var r = Maintenance.ApplyRetention(repo, 1);
        Check(r.DeletedPoints.Count == 2 && r.DeletedChains == 1, $"保持数 1 で古い 2 ポイントと旧チェーンを削除（解放 {Fmt.Size(r.FreedBytes)}）");
        Check(RestoreSha(repo.GetPoint("latest"), "r3.img") == sha3, "残った p3 を復元できる");

        // 増分チェーンの途中を消しても後のポイントは復元できる（合成フル）
        Mutate(vol, 4, 8);
        var p4 = Backup(PointKind.Incremental, "p4");
        var sha4 = Sha(vol);
        Mutate(vol, 5, 8);
        var p5 = Backup(PointKind.Incremental, "p5");
        var sha5 = Sha(vol);
        Maintenance.ApplyRetention(repo, 2); // p3 (チェーンの最初のフル) を削除
        Check(repo.ListPoints().Select(p => p.Id).SequenceEqual(["p4", "p5"]), "保持数 2 でフルの p3 を削除");
        Check(RestoreSha(repo.GetPoint("p4"), "r4.img") == sha4 && RestoreSha(repo.GetPoint("p5"), "r5.img") == sha5, "フルを消しても増分 p4/p5 は復元できる");

        // 破損検出
        var pack = Directory.EnumerateFiles(repo.ChainsDir, "*.pack", SearchOption.AllDirectories).OrderByDescending(f => new FileInfo(f).Length).First();
        using (var fs = new FileStream(pack, FileMode.Open))
        {
            fs.Position = fs.Length / 2;
            int b = fs.ReadByte();
            fs.Position = fs.Length / 2;
            fs.WriteByte((byte)(b ^ 0xFF));
        }
        var bad = Maintenance.Verify(repo, repo.ListPoints());
        Check(bad.Bad > 0, $"pack の 1 バイト改変を検証で検出（{bad.Bad} 件）");

        // 中断したバックアップの残骸を回収
        Directory.CreateDirectory(repo.PointDir("broken"));
        File.WriteAllText(Path.Combine(repo.PointDir("broken"), "vol0.map"), "x");
        repo.Lock();
        Maintenance.CollectGarbage(repo);
        repo.Unlock();
        Check(!Directory.Exists(repo.PointDir("broken")), "point.json の無い中断ポイントを削除");
    }

    static void DiskImage(string dir)
    {
        Directory.CreateDirectory(dir);
        var img = Path.Combine(dir, "disk.img");
        long size = 80L << 20;
        var layout = new DiskRecord
        {
            Style = "GPT",
            DiskGuid = Guid.NewGuid().ToString(),
            Size = size,
            Partitions =
            [
                new PartitionRecord { Number = 1, Offset = 1 << 20, Length = 8 << 20, TypeGuid = PartitionRecord.EspType, PartitionGuid = Guid.NewGuid().ToString(), Name = "EFI system partition", Attributes = 0x8000000000000001 },
                new PartitionRecord { Number = 2, Offset = 9 << 20, Length = 4 << 20, TypeGuid = PartitionRecord.MsrType, PartitionGuid = Guid.NewGuid().ToString(), Name = "Microsoft reserved partition" },
                new PartitionRecord { Number = 3, Offset = 13 << 20, Length = 40 << 20, TypeGuid = PartitionRecord.BasicDataType, PartitionGuid = Guid.NewGuid().ToString(), Name = "Basic data partition" },
                new PartitionRecord { Number = 4, Offset = 53 << 20, Length = 20 << 20, TypeGuid = PartitionRecord.RecoveryType, PartitionGuid = Guid.NewGuid().ToString(), Name = "", Attributes = 0x8000000000000001 },
            ],
        };
        var partFiles = new Dictionary<int, string>();
        using (var t = HandleTarget.CreateFile(img, size))
        {
            PartitionTable.Write(layout, t);
            foreach (var p in layout.Partitions.Where(p => p.Number != 2))
            {
                var pf = Path.Combine(dir, $"p{p.Number}.bin");
                MakeVolume(pf, 10 + p.Number, p.Length);
                t.Write(p.Offset, File.ReadAllBytes(pf));
                partFiles[p.Number] = Sha(pf);
                File.Delete(pf);
            }
        }
        DiskRecord parsed;
        using (var s = HandleSource.OpenFile(img)) parsed = PartitionTable.Read(s);
        Check(parsed.Partitions.Count == 4 && parsed.DiskGuid == layout.DiskGuid
              && parsed.Partitions.Zip(layout.Partitions).All(z => z.First.Offset == z.Second.Offset && z.First.Length == z.Second.Length
                  && z.First.PartitionGuid == z.Second.PartitionGuid && z.First.Name == z.Second.Name && z.First.Attributes == z.Second.Attributes),
            "書いた GPT を読み戻すと一致");

        var repo = Repository.Create(Path.Combine(dir, "repo"), null);
        RecoveryPoint p1;
        using (var plan = SourcePlanner.Build(["disk:" + img]))
            p1 = BackupEngine.Run(repo, plan.Volumes, new BackupOptions { Job = "disk", Disks = plan.Disks });
        Check(p1.Volumes.Count == 3 && p1.Disks.Count == 1 && p1.Disks[0].Partitions.Count == 4, "MSR 以外の 3 パーティションとディスク構成を保存");
        Check(p1.BareMetalCapable, "ベアメタル復元可能なポイント");

        // 元より大きいディスクに復元
        var outImg = Path.Combine(dir, "restored.img");
        using (var t = HandleTarget.CreateFile(outImg, size + (16 << 20)))
            RestoreEngine.RestoreDisk(repo, p1, p1.Disks[0], t, writeUnused: true);
        DiskRecord back;
        using (var s = HandleSource.OpenFile(outImg)) back = PartitionTable.Read(s);
        Check(back.Partitions.Count == 4 && back.DiskGuid == layout.DiskGuid && back.Partitions[0].PartitionGuid == layout.Partitions[0].PartitionGuid,
            "復元したディスクのパーティション構成（GUID 含む）が一致");
        bool same = true;
        using (var s = HandleSource.OpenFile(outImg))
            foreach (var p in layout.Partitions.Where(p => p.Number != 2))
            {
                var buf = new byte[p.Length];
                s.Read(p.Offset, buf);
                same &= Convert.ToHexStringLower(SHA256.HashData(buf)) == partFiles[p.Number];
            }
        Check(same, "各パーティションの中身が元と一致");
        // バックアップ GPT が新しいディスク末尾にあるか
        using (var s = HandleSource.OpenFile(outImg))
        {
            var last = new byte[512];
            s.Read(s.Length - 512, last);
            Check(last.AsSpan(0, 8).SequenceEqual("EFI PART"u8), "バックアップ GPT ヘッダが新しいディスクの末尾にある");
        }
    }
}
