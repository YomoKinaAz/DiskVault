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
        Console.WriteLine(T("==== DiskVault 回復（ベアメタル復元） ===="));
        if (!Sys.IsWinPE) Console.WriteLine(T("注意: 回復環境（WinPE）ではありません。実行中の Windows のディスクには復元できません。"));
        while (true)
        {
            Console.WriteLine();
            Console.WriteLine(T("1) バックアップから復元する"));
            Console.WriteLine(T("2) ネットワークの状態を見る（ipconfig）"));
            Console.WriteLine(T("3) ドライバを読み込む（drvload）"));
            Console.WriteLine(T("4) コマンドプロンプトに戻る"));
            Console.WriteLine(T("5) 再起動する"));
            Console.WriteLine(T("6) ディスク全体をフルバックアップする"));
            var choice = Ask(T("番号"));
            switch (choice)
            {
                case "1":
                case "6":
                    try { if (choice == "1") Restore(); else Backup(); }
                    catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidDataException or InvalidOperationException
                                                   or ArgumentException or KeyNotFoundException or FormatException)
                    {
                        Console.WriteLine(T("エラー: {0}", e.Message));
                    }
                    break;
                case "2": Shell("ipconfig", ""); break;
                case "3":
                    var inf = Ask(T(".inf ファイルのパス"));
                    if (inf.Length > 0) { HardwareAdapt.RememberLoaded(inf); Shell("drvload", $"\"{inf}\""); }
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
        var disks = DiskLayout.ListDisks().Where(d => d.Style != "未初期化" && d.Style != T("未初期化") && d.Partitions > 0).ToList();
        if (disks.Count == 0) throw new InvalidOperationException(T("バックアップできるディスクがありません。"));
        foreach (var d in disks)
            Console.WriteLine(T("  ディスク {0}  {1}  {2}  {3}  [{4}]{5}", d.Number, d.Model, Fmt.Size(d.Size), d.Style, d.Volumes, d.HasSystem ? "  ← Windows" : ""));
        var def = disks.FirstOrDefault(d => d.HasSystem)?.Number;
        var s = Ask(T("バックアップするディスク番号（複数はカンマ区切り{0}）", def is int x ? T("、Enter で {0}", x) : ""));
        var nums = (s.Length == 0 && def is int dd ? [dd] : s.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(int.Parse)).ToList();
        if (nums.Count == 0 || nums.Any(n => disks.All(d => d.Number != n))) throw new ArgumentException(T("ディスク番号が正しくありません。"));
        if (!Sys.IsWinPE && disks.Any(d => d.HasSystem && nums.Contains(d.Number)))
            throw new InvalidOperationException(T("実行中の Windows のディスクはここではバックアップできません（回復メディアから起動して行ってください）。"));
        Console.WriteLine(T("保存先のフォルダ（USB ディスクや NAS）を入力してください。例: E:\\Backup   \\\\192.168.1.10\\share\\DiskVault"));
        Console.WriteLine(T("  ドライブ: ") + string.Join("  ", DriveInfo.GetDrives().Where(d => d.IsReady).Select(d => $"{d.Name.TrimEnd('\\')}({d.VolumeLabel})")));
        var dest = Ask(T("保存先"));
        if (dest.Length == 0) return;
        if (!dest.StartsWith(@"\\") && VolumeInfo.Find(dest[..2])?.Disk is int dn && nums.Contains(dn))
            throw new ArgumentException(T("保存先はバックアップするディスク {0} の上にあります。別のディスクか NAS を指定してください。", dn));
        NetShare? share = null;
        if (dest.StartsWith(@"\\"))
        {
            var user = Ask(T("NAS のユーザー名（不要なら空欄）"));
            if (user.Length > 0) share = NetShare.Connect(dest, user, Ask(T("NAS のパスワード"), secret: true));
        }
        using var _ = share;
        var sys = VolumeInfo.Enumerate().FirstOrDefault(v => v.IsSystem);
        var pcDef = (sys?.Letter is { } l ? OfflineWindows.ComputerName(l) : null) ?? "PC";
        var pc = Ask(T("PC 名（Enter で {0}）", pcDef));
        if (pc.Length == 0) pc = pcDef;
        var pw = Ask(T("暗号化パスワード（不要なら空欄）"), secret: true);
        if (pw.Length > 0 && Ask(T("もう一度"), secret: true) != pw) throw new ArgumentException(T("パスワードが一致しません。"));
        var repoPath = Path.Combine(dest, pc, OfflineWindows.DefaultRepoName);
        Console.WriteLine(T("保存場所: {0}", repoPath));
        var repo = Repository.OpenOrCreate(repoPath, pw.Length > 0 ? pw : null);
        using var plan = SourcePlanner.Build(nums.Select(n => $"physical:{n}"), Console.WriteLine, offline: true);
        var progress = new ConsoleProgress();
        var point = BackupEngine.Run(repo, plan.Volumes, new BackupOptions
        {
            Job = OfflineWindows.DefaultRepoName, Kind = PointKind.Full, Disks = plan.Disks, Consistency = plan.Consistency, Machine = pc,
            Comment = T("回復環境（WinPE）からのバックアップ"),
        }, new Progress<BackupProgress>(x => progress.Report(x.Phase, x.Done, x.Total, x.BytesPerSecond)), m => { progress.End(); Console.WriteLine(m); });
        progress.End();
        Console.WriteLine(T("完了: 復元ポイント {0}  使用量 {1} → 保存 {2}", point.Id, Fmt.Size(point.Stats.UsedBytes), Fmt.Size(point.Stats.StoredBytes)));
    }

    static void Restore()
    {
        Console.WriteLine();
        Console.WriteLine(T("バックアップの場所（repo.json のあるフォルダ）を入力してください。"));
        Console.WriteLine(T("  例: D:\\Backup\\PC名\\システム   \\\\192.168.1.10\\share\\DiskVault\\PC名\\システム"));
        Console.WriteLine(T("  ドライブ: ") + string.Join("  ", DriveInfo.GetDrives().Where(d => d.IsReady).Select(d => $"{d.Name.TrimEnd('\\')}({d.VolumeLabel})")));
        var path = Ask(T("場所"));
        if (path.StartsWith(@"\\"))
        {
            var user = Ask(T("NAS のユーザー名（不要なら空欄）"));
            if (user.Length > 0)
            {
                var pw = Ask(T("NAS のパスワード"), secret: true);
                NetShare.Connect(path, user, pw);
            }
        }
        Repository repo;
        try { repo = Repository.Open(path, null); }
        catch (UnauthorizedAccessException) { repo = Repository.Open(path, Ask(T("暗号化パスワード"), secret: true)); }
        var points = repo.ListPoints().Where(p => p.Disks.Count > 0).ToList();
        if (points.Count == 0) throw new InvalidOperationException(T("ディスク全体（システム）を含む復元ポイントがありません。"));
        Console.WriteLine();
        for (int i = 0; i < points.Count; i++)
        {
            var p = points[i];
            Console.WriteLine($"{i + 1,3}) {p.Time.ToLocalTime():yyyy/MM/dd HH:mm}  {(p.Kind == PointKind.Full ? T("フル") : T("増分"))}  {p.Machine}  {string.Join(" ", p.Volumes.Select(v => v.Letter ?? (v.IsBoot ? "EFI" : "-")))}  {T("使用 {0}", Fmt.Size(p.Stats.UsedBytes))}");
        }
        var sel = Ask(T("復元ポイント（1-{0}、Enter で最新）", points.Count));
        var point = sel.Length == 0 ? points[^1] : points[int.Parse(sel) - 1];
        var disk = point.Disks[0];
        long need = disk.Partitions.Max(x => x.Offset + x.Length) + (1 << 20);
        Console.WriteLine();
        Console.WriteLine(T("元のディスク: {0}  {1}  {2}  パーティション {3} 個（必要容量 {4}）", disk.Model, Fmt.Size(disk.Size), disk.Style, disk.Partitions.Count, Fmt.Size(need)));
        var disks = DiskLayout.ListDisks();
        foreach (var d in disks)
            Console.WriteLine(T("  ディスク {0}: {1}  {2}  {3}  {4}{5}{6}", d.Number, d.Model, Fmt.Size(d.Size), d.Style, d.Volumes, d.Size < need ? T("  ※縮小して復元") : "", d.HasSystem ? T("  ※起動中のシステム") : ""));
        var dn = int.Parse(Ask(T("復元先のディスク番号")));
        var target = disks.FirstOrDefault(d => d.Number == dn) ?? throw new ArgumentException(T("そのディスクはありません。"));

        // パーティションの配置（復元先の大きさが違えば Windows のパーティションを残り全体にする）
        Console.WriteLine(T("使用中の領域を調べています…"));
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
                                      (pp.Resizable ? T("（最小 {0}）", Fmt.Size(pp.MinLength)) : ""));
                }
            }
            catch (InvalidOperationException e)
            {
                Console.WriteLine($"  {e.Message}");
                layout = null!;
            }
            var ans = Ask(T("Enter = この配置で復元 / パーティション番号=GB（例 3=200、3=max）で変更"));
            if (ans.Length == 0 && layout != null) break;
            var kv = ans.Split('=', 2);
            var pick = kv.Length == 2 ? plan.FirstOrDefault(x => x.Source.Number.ToString() == kv[0].Trim()) : null;
            if (pick == null || !pick.Resizable) { Console.WriteLine(T("  変更できるのは NTFS のパーティションです。")); continue; }
            if (kv[1].Trim() == "max") { foreach (var x in plan) x.Fill = false; pick.Fill = true; }
            else if (double.TryParse(kv[1], out var gb)) { pick.Fill = false; pick.NewLength = (long)(gb * (1L << 30)) / LayoutPlanner.Align * LayoutPlanner.Align; }
        }
        Console.WriteLine();
        Console.WriteLine(T("ディスク {0}（{1}、{2}）の内容はすべて消去されます。", dn, target.Model, Fmt.Size(target.Size)));
        if (Ask(T("よろしければ YES と入力")) != "YES") { Console.WriteLine(T("中止しました。")); return; }
        bool adapt = Ask(T("別の PC（元と違うハードウェア）に復元しますか？ y = ストレージのドライバなどを調整する [y/N]")).Equals("y", StringComparison.OrdinalIgnoreCase);
        string? drv = adapt ? Ask(T("追加するドライバのフォルダ（なければ Enter）")) : null;

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
        Console.WriteLine(T("復元しました（{0}）。", Fmt.Duration(sw.Elapsed)));
        if (adapt) foreach (var l in HardwareAdapt.RunOnDisk(dn, string.IsNullOrEmpty(drv) ? null : [drv])) Console.WriteLine(l);
        foreach (var l in BootFix.Run(dn, disk)) Console.WriteLine(l);
        Console.WriteLine(T("USB メモリを抜いて再起動してください（メニューの 5）。"));
    }
}


