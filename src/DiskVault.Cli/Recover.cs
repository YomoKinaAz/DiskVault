using System.Diagnostics;
using DiskVault.Engine;
using DiskVault.Jobs;
using DiskVault.Platform;
using DiskVault.Storage;

namespace DiskVault.Cli;

/// <summary>
/// dvault recover: 回復環境（WinPE）用の対話式ベアメタル復元。GUI が使えないときの予備。
/// バックアップの場所 → 復元ポイント → 復元先ディスク → 確認 → 復元 → ブート構成 の順に聞く。
/// </summary>
static class Recover
{
    public static int Run(Args a)
    {
        Console.WriteLine("==== DiskVault 回復（ベアメタル復元） ====");
        if (!Sys.IsWinPE) Console.WriteLine("注意: 回復環境（WinPE）ではありません。実行中の Windows のディスクには復元できません。");
        while (true)
        {
            Console.WriteLine();
            Console.WriteLine("1) バックアップから復元する");
            Console.WriteLine("2) ネットワークの状態を見る（ipconfig）");
            Console.WriteLine("3) ドライバを読み込む（drvload）");
            Console.WriteLine("4) コマンドプロンプトに戻る");
            Console.WriteLine("5) 再起動する");
            Console.WriteLine("6) ディスク全体をフルバックアップする");
            var choice = Ask("番号");
            switch (choice)
            {
                case "1":
                case "6":
                    try { if (choice == "1") Restore(); else Backup(); }
                    catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidDataException or InvalidOperationException
                                                   or ArgumentException or KeyNotFoundException or FormatException)
                    {
                        Console.WriteLine($"エラー: {e.Message}");
                    }
                    break;
                case "2": Shell("ipconfig", ""); break;
                case "3":
                    var inf = Ask(".inf ファイルのパス");
                    if (inf.Length > 0) Shell("drvload", $"\"{inf}\"");
                    break;
                case "4": return 0;
                case "5": Shell("wpeutil", "reboot"); return 0;
            }
        }
    }

    static string Ask(string prompt, bool secret = false)
    {
        Console.Write($"{prompt}: ");
        if (!secret) return (Console.ReadLine() ?? "").Trim();
        var s = new System.Text.StringBuilder();
        while (true)
        {
            var k = Console.ReadKey(true);
            if (k.Key == ConsoleKey.Enter) break;
            if (k.Key == ConsoleKey.Backspace) { if (s.Length > 0) s.Length--; continue; }
            s.Append(k.KeyChar);
        }
        Console.WriteLine();
        return s.ToString();
    }

    static void Shell(string exe, string args)
    {
        try { Process.Start(new ProcessStartInfo(exe, args) { UseShellExecute = false })?.WaitForExit(); }
        catch (System.ComponentModel.Win32Exception e) { Console.WriteLine(e.Message); }
    }

    /// <summary>停止中の Windows のディスク全体を、スナップショットなしでフルバックアップする。</summary>
    static void Backup()
    {
        Console.WriteLine();
        var disks = DiskLayout.ListDisks().Where(d => d.Style != "未初期化" && d.Partitions > 0).ToList();
        if (disks.Count == 0) throw new InvalidOperationException("バックアップできるディスクがありません。");
        foreach (var d in disks)
            Console.WriteLine($"  ディスク {d.Number}  {d.Model}  {Fmt.Size(d.Size)}  {d.Style}  [{d.Volumes}]{(d.HasSystem ? "  ← Windows" : "")}");
        var def = disks.FirstOrDefault(d => d.HasSystem)?.Number;
        var s = Ask($"バックアップするディスク番号（複数はカンマ区切り{(def is int x ? $"、Enter で {x}" : "")}）");
        var nums = (s.Length == 0 && def is int dd ? [dd] : s.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(int.Parse)).ToList();
        if (nums.Count == 0 || nums.Any(n => disks.All(d => d.Number != n))) throw new ArgumentException("ディスク番号が正しくありません。");
        if (!Sys.IsWinPE && disks.Any(d => d.HasSystem && nums.Contains(d.Number)))
            throw new InvalidOperationException("実行中の Windows のディスクはここではバックアップできません（回復メディアから起動して行ってください）。");
        Console.WriteLine("保存先のフォルダ（USB ディスクや NAS）を入力してください。例: E:\\Backup   \\\\192.168.1.10\\share\\DiskVault");
        Console.WriteLine("  ドライブ: " + string.Join("  ", DriveInfo.GetDrives().Where(d => d.IsReady).Select(d => $"{d.Name.TrimEnd('\\')}({d.VolumeLabel})")));
        var dest = Ask("保存先");
        if (dest.Length == 0) return;
        if (!dest.StartsWith(@"\\") && VolumeInfo.Find(dest[..2])?.Disk is int dn && nums.Contains(dn))
            throw new ArgumentException($"保存先はバックアップするディスク {dn} の上にあります。別のディスクか NAS を指定してください。");
        NetShare? share = null;
        if (dest.StartsWith(@"\\"))
        {
            var user = Ask("NAS のユーザー名（不要なら空欄）");
            if (user.Length > 0) share = NetShare.Connect(dest, user, Ask("NAS のパスワード", secret: true));
        }
        using var _ = share;
        var sys = VolumeInfo.Enumerate().FirstOrDefault(v => v.IsSystem);
        var pcDef = (sys?.Letter is { } l ? OfflineWindows.ComputerName(l) : null) ?? "PC";
        var pc = Ask($"PC 名（Enter で {pcDef}）");
        if (pc.Length == 0) pc = pcDef;
        var pw = Ask("暗号化パスワード（不要なら空欄）", secret: true);
        if (pw.Length > 0 && Ask("もう一度", secret: true) != pw) throw new ArgumentException("パスワードが一致しません。");
        var repoPath = Path.Combine(dest, pc, "回復環境");
        Console.WriteLine($"保存場所: {repoPath}");
        var repo = Repository.OpenOrCreate(repoPath, pw.Length > 0 ? pw : null);
        using var plan = SourcePlanner.Build(nums.Select(n => $"physical:{n}"), Console.WriteLine, offline: true);
        var progress = new ConsoleProgress();
        var point = BackupEngine.Run(repo, plan.Volumes, new BackupOptions
        {
            Job = "回復環境", Kind = PointKind.Full, Disks = plan.Disks, Consistency = plan.Consistency, Machine = pc,
            Comment = "回復環境（WinPE）からのバックアップ",
        }, new Progress<BackupProgress>(x => progress.Report(x.Phase, x.Done, x.Total, x.BytesPerSecond)), m => { progress.End(); Console.WriteLine(m); });
        progress.End();
        Console.WriteLine($"完了: 復元ポイント {point.Id}  使用量 {Fmt.Size(point.Stats.UsedBytes)} → 保存 {Fmt.Size(point.Stats.StoredBytes)}");
    }

    static void Restore()
    {
        Console.WriteLine();
        Console.WriteLine("バックアップの場所（repo.json のあるフォルダ）を入力してください。");
        Console.WriteLine("  例: D:\\Backup\\PC名\\システム   \\\\192.168.1.10\\share\\DiskVault\\PC名\\システム");
        Console.WriteLine("  ドライブ: " + string.Join("  ", DriveInfo.GetDrives().Where(d => d.IsReady).Select(d => $"{d.Name.TrimEnd('\\')}({d.VolumeLabel})")));
        var path = Ask("場所");
        if (path.StartsWith(@"\\"))
        {
            var user = Ask("NAS のユーザー名（不要なら空欄）");
            if (user.Length > 0)
            {
                var pw = Ask("NAS のパスワード", secret: true);
                NetShare.Connect(path, user, pw);
            }
        }
        Repository repo;
        try { repo = Repository.Open(path, null); }
        catch (UnauthorizedAccessException) { repo = Repository.Open(path, Ask("暗号化パスワード", secret: true)); }
        var points = repo.ListPoints().Where(p => p.Disks.Count > 0).ToList();
        if (points.Count == 0) throw new InvalidOperationException("ディスク全体（システム）を含む復元ポイントがありません。");
        Console.WriteLine();
        for (int i = 0; i < points.Count; i++)
        {
            var p = points[i];
            Console.WriteLine($"{i + 1,3}) {p.Time.ToLocalTime():yyyy/MM/dd HH:mm}  {(p.Kind == PointKind.Full ? "フル" : "増分")}  {p.Machine}  {string.Join(" ", p.Volumes.Select(v => v.Letter ?? (v.IsBoot ? "EFI" : "-")))}  使用 {Fmt.Size(p.Stats.UsedBytes)}");
        }
        var sel = Ask($"復元ポイント（1-{points.Count}、Enter で最新）");
        var point = sel.Length == 0 ? points[^1] : points[int.Parse(sel) - 1];
        var disk = point.Disks[0];
        long need = disk.Partitions.Max(x => x.Offset + x.Length) + (1 << 20);
        Console.WriteLine();
        Console.WriteLine($"元のディスク: {disk.Model}  {Fmt.Size(disk.Size)}  {disk.Style}  パーティション {disk.Partitions.Count} 個（必要容量 {Fmt.Size(need)}）");
        var disks = DiskLayout.ListDisks();
        foreach (var d in disks)
            Console.WriteLine($"  ディスク {d.Number}: {d.Model}  {Fmt.Size(d.Size)}  {d.Style}  {d.Volumes}{(d.Size < need ? "  ※縮小して復元" : "")}{(d.HasSystem ? "  ※起動中のシステム" : "")}");
        var dn = int.Parse(Ask("復元先のディスク番号"));
        var target = disks.FirstOrDefault(d => d.Number == dn) ?? throw new ArgumentException("そのディスクはありません。");

        // パーティションの配置（復元先の大きさが違えば Windows のパーティションを残り全体にする）
        Console.WriteLine("使用中の領域を調べています…");
        var plan = LayoutPlanner.Analyze(repo, point, disk);
        if (Math.Abs(target.Size - disk.Size) > 64L << 20) LayoutPlanner.AutoFill(plan, point);
        DiskRecord layout;
        while (true)
        {
            try
            {
                layout = LayoutPlanner.Build(disk, plan, target.Size);
                Console.WriteLine();
                foreach (var pp in plan)
                {
                    var np = layout.Partitions.First(x => x.Number == pp.Source.Number);
                    Console.WriteLine($"  #{pp.Source.Number} {pp.Source.Kind,-10} {Fmt.Size(pp.Source.Length),10} → {Fmt.Size(np.Length),10}" +
                                      (pp.Resizable ? $"（最小 {Fmt.Size(pp.MinLength)}）" : ""));
                }
            }
            catch (InvalidOperationException e)
            {
                Console.WriteLine($"  {e.Message}");
                layout = null!;
            }
            var ans = Ask("Enter = この配置で復元 / パーティション番号=GB（例 3=200、3=max）で変更");
            if (ans.Length == 0 && layout != null) break;
            var kv = ans.Split('=', 2);
            var pick = kv.Length == 2 ? plan.FirstOrDefault(x => x.Source.Number.ToString() == kv[0].Trim()) : null;
            if (pick == null || !pick.Resizable) { Console.WriteLine("  変更できるのは NTFS のパーティションです。"); continue; }
            if (kv[1].Trim() == "max") { foreach (var x in plan) x.Fill = false; pick.Fill = true; }
            else if (double.TryParse(kv[1], out var gb)) { pick.Fill = false; pick.NewLength = (long)(gb * (1L << 30)) / LayoutPlanner.Align * LayoutPlanner.Align; }
        }
        Console.WriteLine();
        Console.WriteLine($"ディスク {dn}（{target.Model}、{Fmt.Size(target.Size)}）の内容はすべて消去されます。");
        if (Ask("よろしければ YES と入力") != "YES") { Console.WriteLine("中止しました。"); return; }

        var sw = Stopwatch.StartNew();
        List<PartitionRecord> grow;
        using (var t = Targets.Open($@"\\.\PhysicalDrive{dn}", target.Size, confirmed: true))
        {
            var prog = new ConsoleProgress();
            grow = RestoreEngine.RestoreDisk(repo, point, disk, t, writeUnused: false,
                new Progress<RestoreProgress>(x => prog.Report(x.Phase, x.Done, x.Total, x.BytesPerSecond)), m => { prog.End(); Console.WriteLine(m); },
                layout: layout);
            prog.End();
        }
        foreach (var l in NtfsResize.ExtendOnDisk(dn, grow)) Console.WriteLine(l);
        Console.WriteLine($"復元しました（{Fmt.Duration(sw.Elapsed)}）。");
        foreach (var l in BootFix.Run(dn, disk)) Console.WriteLine(l);
        Console.WriteLine("USB メモリを抜いて再起動してください（メニューの 5）。");
    }
}


