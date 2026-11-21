using System.Text;
using DiskVault;
using DiskVault.Engine;
using DiskVault.Ntfs;
using DiskVault.Platform;
using DiskVault.Source;
using DiskVault.Storage;

namespace DiskVault.Cli;

static class Program
{
    static int Main(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;
        Sys.InitComSecurity();
        Jobs.AppConfig.ApplyLanguage();
        if (args.Length == 0 || args[0] is "-h" or "--help" or "help")
        {
            Usage();
            return args.Length == 0 ? 1 : 0;
        }
        var a = new Args(args.Skip(1));
        try
        {
            return args[0] switch
            {
                "backup" => Backup(a),
                "list" => List(a),
                "info" => Info(a),
                "verify" => Verify(a),
                "restore-volume" => RestoreVolume(a),
                "restore-disk" => RestoreDisk(a),
                "ls" => Ls(a),
                "extract" => Extract(a),
                "prune" => Prune(a),
                "volumes" => Volumes(a),
                "shadows" => Shadows(),
                "adapt" => Adapt(a),
                "selftest" => SelfTest.Run(a),
                "bench" => Bench.Run(a),
                "recover" => Recover.Run(a),
                "media" => Media(a),
                "minsize" => MinSizeCmd(a),
                "vss-test" => VssTest(a),
                _ => Fail(T("不明なコマンド: {0}", args[0])),
            };
        }
        catch (Exception e) when (e is IOException or InvalidDataException or UnauthorizedAccessException or KeyNotFoundException
                                       or InvalidOperationException or ArgumentException or NotSupportedException)
        {
            Console.Error.WriteLine(T("エラー: {0}", e.Message));
            if (Environment.GetEnvironmentVariable("DVAULT_DEBUG") == "1") Console.Error.WriteLine(e);
            return 2;
        }
    }

    static void Usage() => Console.WriteLine(T("""
        DiskVault イメージバックアップ CLI

          dvault backup <repo> <source>... [--full] [--job 名前] [--machine PC名] [--keep N] [--password P] [--comment 文]
                              [--crash-consistent]（VSS ライターと連携しない） [--vss-full]（VSS 完全バックアップ＝ログ切り捨て）
                              [--offline]（スナップショットを使わず直接読む。WinPE では自動）
                              [--shadow ID[,ID]]（そのボリュームを既存のシャドウコピーから読む。一覧は dvault shadows）
              source: C: / \\?\Volume{...}\ （VSS スナップショットから。管理者権限が必要）
                      system                  （Windows の入ったディスク全体：ベアメタル復元用）
                      physical:N               （物理ディスク N の全体：ベアメタル復元用）
                      image.img                （ボリュームのイメージファイル）
                      image.img@offset:length  （イメージの一部）
                      disk:disk.img            （ディスクイメージ。全パーティション＋構成）
                      shadow:ID                （既存のシャドウコピー 1 つ：そのボリュームだけ）
          dvault list <repo>
          dvault info <repo> <point>
          dvault verify <repo> [point|all]
          dvault restore-volume <repo> <point> <vol> <out.img|\\.\X:> [--sparse]
          dvault restore-disk <repo> <point> <out.img|\\.\PhysicalDriveN> [--disk N] [--size 容量] [--yes]
                              [--resize auto | 3=max | 3=80GB,4=1GB]   （パーティションのサイズ変更。NTFS のみ）
                              [--gap 2=300MB,...]（パーティションの前に空きを入れて開始位置をずらす）
                              [--adapt [--drivers フォルダ]]（別の PC 向けに調整する。dvault adapt と同じ）
          dvault ls <repo> <point> <vol> [パス]
          dvault extract <repo> <point> <vol> <パス> <出力先>
          dvault prune <repo> --keep N
          dvault adapt <X:> [--drivers フォルダ]  （復元した Windows を別の PC で起動できるように調整：ストレージのドライバ・ドライバ追加）
          dvault volumes
          dvault shadows                         （既存のシャドウコピー＝システムの復元ポイントなどの一覧）
          dvault media --iso out.iso | --usb N  [--app 発行フォルダ] [--no-drivers] [--drivers フォルダ]
          dvault media --uninstall-adk           （Windows ADK と WinPE アドオンを削除）
          dvault recover                         （回復環境用の対話式ベアメタル復元）
          dvault selftest [作業フォルダ]

          point は復元ポイント名（先頭一致可）または latest。パスワードは環境変数 DVAULT_PASSWORD でも指定可。
        """));

    static string KindText(PointKind k) => k == PointKind.Full ? T("フル") : T("増分");

    /// <summary>種別の列（英語の "Incremental" は長いので幅を変える）。</summary>
    static string KindCol(string s) => s.PadRight(IsEnglish ? 11 : 4);

    static int Fail(string msg)
    {
        Console.Error.WriteLine(msg);
        return 1;
    }

    static string? Password(Args a) => a.Opt("password") ?? Environment.GetEnvironmentVariable("DVAULT_PASSWORD");

    static Repository OpenRepo(Args a, int pos = 0) => Repository.Open(a.Pos(pos) ?? throw new ArgumentException(T("リポジトリを指定してください。")), Password(a));

    // ---- adapt ----

    static int Adapt(Args a)
    {
        var drive = a.Pos(0) ?? throw new ArgumentException(T("復元した Windows のドライブ（例 E:）を指定してください。"));
        if (!Sys.IsAdmin) throw new UnauthorizedAccessException(T("管理者として実行してください。"));
        if (a.Opt("drivers") is { } d && !Directory.Exists(d)) throw new ArgumentException(T("ドライバのフォルダが見つかりません: {0}", d));
        foreach (var l in HardwareAdapt.Run(drive, a.Opt("drivers") is { } dd ? [dd] : null)) Console.WriteLine(l);
        return 0;
    }

    // ---- backup ----

    static int Backup(Args a)
    {
        var root = a.Pos(0) ?? throw new ArgumentException(T("リポジトリを指定してください。"));
        var sources = a.Positional.Skip(1).ToList();
        if (sources.Count == 0) throw new ArgumentException(T("バックアップ元を指定してください。"));
        var repo = Repository.OpenOrCreate(root, Password(a), int.Parse(a.Opt("block-size") ?? "262144"), int.Parse(a.Opt("level") ?? "3"));
        var o = new BackupOptions
        {
            Job = a.Opt("job") ?? "cli",
            Kind = a.Flag("full") ? PointKind.Full : PointKind.Incremental,
            Comment = a.Opt("comment"),
            PointId = a.Opt("point-id"),
            Machine = a.Opt("machine"),
            ExcludeSystemFiles = !a.Flag("no-exclude"),
        };
        var shadows = a.Opt("shadow")?.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        using var plan = SourcePlanner.Build(sources, Console.WriteLine, useWriters: !a.Flag("crash-consistent"), vssFullBackup: a.Flag("vss-full"), offline: a.Flag("offline") ? true : null, shadows: shadows);
        o.Disks = plan.Disks;
        o.SourceTime = plan.SourceTime;
        if (plan.SourceTime is { } shadowTime) o.Comment ??= T("シャドウコピー（{0:yyyy/MM/dd HH:mm}）の内容", shadowTime);
        o.Warnings = [.. plan.Warnings];
        o.Consistency = plan.Consistency;
        var progress = new ConsoleProgress();
        var p = BackupEngine.Run(repo, plan.Volumes, o, progress, Console.WriteLine);
        progress.End();
        plan.Complete();
        foreach (var (wn, st, err) in plan.Writers) Console.WriteLine(T("  VSS ライター: {0}  {1}{2}", wn, st, err != 0 ? $"  0x{err:X8}" : ""));
        foreach (var w in p.Warnings) Console.WriteLine(T("警告: {0}", w));
        if (a.Opt("keep") is { } keep)
            Maintenance.ApplyRetention(repo, int.Parse(keep), Console.WriteLine);
        Console.WriteLine(T("復元ポイント: {0}", p.Id));
        return 0;
    }

    // ---- list / info ----

    static int List(Args a)
    {
        var repo = OpenRepo(a);
        var pts = repo.ListPoints();
        Console.WriteLine(T("{0}  （ブロック {1}、{2}、使用量 {3}）", repo.Root, Fmt.Size(repo.Config.BlockSize), repo.IsEncrypted ? T("暗号化あり") : T("暗号化なし"), Fmt.Size(Maintenance.DirSize(repo.Root))));
        Console.WriteLine($"{T("復元ポイント"),-20} {KindCol(T("種別"))} {T("ジョブ"),-12} {T("使用中"),10} {T("新規"),10} {T("保存"),10}  {T("ボリューム")}");
        foreach (var p in pts)
            Console.WriteLine($"{p.Id,-20} {KindCol(KindText(p.Kind))} {p.Job,-12} {Fmt.Size(p.Stats.UsedBytes),10} {Fmt.Size(p.Stats.NewBytes),10} {Fmt.Size(p.Stats.StoredBytes),10}  " +
                              string.Join(", ", p.Volumes.Select(v => v.Display.Trim())) + (p.BareMetalCapable ? "  [BMR]" : ""));
        Console.WriteLine(T("{0} 個", pts.Count));
        return 0;
    }

    static int Info(Args a)
    {
        var repo = OpenRepo(a);
        var p = repo.GetPoint(a.Pos(1) ?? "latest");
        Console.WriteLine(T("復元ポイント {0}  {1:yyyy-MM-dd HH:mm:ss}  {2}  チェーン {3}", p.Id, p.Time.ToLocalTime(), KindText(p.Kind), p.Chain));
        Console.WriteLine(T("  ジョブ {0}  PC {1}  OS {2}", p.Job, p.Machine, p.OsVersion));
        if (p.Comment != null) Console.WriteLine(T("  コメント: {0}", p.Comment));
        Console.WriteLine(T("  所要 {0}、読み取り {1}、新規 {2}、保存 {3}", Fmt.Duration(TimeSpan.FromSeconds(p.Stats.Seconds)), Fmt.Size(p.Stats.ReadBytes), Fmt.Size(p.Stats.NewBytes), Fmt.Size(p.Stats.StoredBytes)));
        foreach (var v in p.Volumes)
            Console.WriteLine(T("  vol{0}: {1}  {2}（使用中 {3}）{4}{5}", v.Index, v.Display.Trim(), Fmt.Size(v.Size), Fmt.Size(v.UsedBytes), v.IsSystem ? T(" [システム]") : "", v.IsBoot ? T(" [ブート]") : ""));
        foreach (var d in p.Disks)
        {
            Console.WriteLine(T("  ディスク {0}（{1}、{2}、{3}）", d.Index, d.Model ?? "?", Fmt.Size(d.Size), d.Style));
            foreach (var part in d.Partitions)
                Console.WriteLine(T("    #{0} {1,-10} {2,10} 〜 {3,10}  {4}", part.Number, part.Kind, Fmt.Size(part.Offset), Fmt.Size(part.Length), part.Volume is int vi ? $"vol{vi}" : T("(中身なし)")));
        }
        foreach (var w in p.Warnings) Console.WriteLine(T("  警告: {0}", w));
        return 0;
    }

    // ---- verify ----

    static int Verify(Args a)
    {
        var repo = OpenRepo(a);
        var which = a.Pos(1) ?? "all";
        var pts = which == "all" ? repo.ListPoints() : [repo.GetPoint(which)];
        var r = Maintenance.Verify(repo, pts, new Progress<(long Done, long Total)>(x => Console.Write("\r" + T("検証中 {0}/{1}", x.Done, x.Total) + "   ")));
        Console.WriteLine();
        foreach (var e in r.Errors.Take(20)) Console.WriteLine($"  NG: {e}");
        Console.WriteLine(r.Bad == 0 ? T("OK: {0} ポイント、{1} ブロックを検証しました。", r.Points, r.Blocks) : T("NG: {0} 件のエラー", r.Bad));
        return r.Bad == 0 ? 0 : 3;
    }

    // ---- restore ----

    static int RestoreVolume(Args a)
    {
        var repo = OpenRepo(a);
        var p = repo.GetPoint(a.Pos(1) ?? throw new ArgumentException(T("復元ポイントを指定してください。")));
        int vol = int.Parse(a.Pos(2) ?? throw new ArgumentException(T("ボリューム番号を指定してください。")));
        var outPath = a.Pos(3) ?? throw new ArgumentException(T("復元先を指定してください。"));
        var v = p.Volumes[vol];
        long newSize = a.Opt("size") is { } sz ? ParseSize(sz) / LayoutPlanner.Align * LayoutPlanner.Align : v.Size;
        bool device = Targets.IsDevice(outPath);
        using (var target = Targets.Open(outPath, newSize, a.Flag("yes")))
        {
            var progress = new ConsoleProgress();
            var prog = new Progress<RestoreProgress>(x => progress.Report(x.Phase, x.Done, x.Total, x.BytesPerSecond));
            if (newSize < v.Size)
            {
                if (v.FileSystem != "NTFS") throw new InvalidOperationException(T("縮小できるのは NTFS だけです。"));
                using var src = new PointVolumeSource(repo, p, vol);
                long min = NtfsResize.MinSize(src);
                if (newSize < min) throw new InvalidOperationException(T("{0} より小さくできません。", Fmt.Size(min)));
                RestoreEngine.RestoreVolume(repo, p, vol, target, writeUnused: !device && !a.Flag("sparse"), prog, limit: newSize);
                NtfsResize.ShrinkOffline(src, target, newSize, Console.WriteLine);
            }
            else RestoreEngine.RestoreVolume(repo, p, vol, target, writeUnused: !device && !a.Flag("sparse"), prog);
            progress.End();
        }
        if (device && v.FileSystem == "NTFS" && newSize >= v.Size && a.Flag("extend"))
            Console.WriteLine(NtfsResize.Extend(outPath));
        Console.WriteLine(T("{0} を {1} に復元しました。", v.Display.Trim(), outPath));
        return 0;
    }

    static int RestoreDisk(Args a)
    {
        var repo = OpenRepo(a);
        var p = repo.GetPoint(a.Pos(1) ?? throw new ArgumentException(T("復元ポイントを指定してください。")));
        var outPath = a.Pos(2) ?? throw new ArgumentException(T("復元先を指定してください。"));
        int di = int.Parse(a.Opt("disk") ?? "0");
        if (di >= p.Disks.Count) throw new ArgumentException(T("この復元ポイントにはディスク構成がありません（ベアメタル復元に非対応）。"));
        var disk = p.Disks[di];
        bool device = Targets.IsDevice(outPath);
        // 実ディスクでサイズ変更するときは、ディスクの実容量に合わせて配置する（元より小さいディスクも可）
        long size = a.Opt("size") is { } s ? ParseSize(s) : device && (a.Opt("resize") ?? a.Opt("gap")) != null ? 0 : disk.Size;
        List<PartitionRecord> grow;
        DiskRecord layout = disk;
        using (var target = Targets.Open(outPath, size, a.Flag("yes")))
        {
            if ((a.Opt("resize") ?? (a.Opt("gap") != null ? "" : null)) is { } rz)
            {
                var plan = LayoutPlanner.Analyze(repo, p, disk);
                if (rz == "auto") LayoutPlanner.AutoFill(plan, p);
                else
                    foreach (var item in rz.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                    {
                        var kv = item.Split('=', 2);
                        var pp = plan.FirstOrDefault(x => x.Source.Number == int.Parse(kv[0])) ?? throw new ArgumentException(T("パーティション {0} はありません。", kv[0]));
                        if (kv[1] == "max") pp.Fill = true; else pp.NewLength = ParseSize(kv[1]) / LayoutPlanner.Align * LayoutPlanner.Align;
                    }
                // --gap 2=100MB,3=0 … パーティションの前に空きを入れて開始位置を後ろへずらす
                foreach (var item in (a.Opt("gap") ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                {
                    var kv = item.Split('=', 2);
                    var pp = plan.FirstOrDefault(x => x.Source.Number == int.Parse(kv[0])) ?? throw new ArgumentException(T("パーティション {0} はありません。", kv[0]));
                    pp.GapBefore = (ParseSize(kv[1]) + LayoutPlanner.Align - 1) / LayoutPlanner.Align * LayoutPlanner.Align;
                }
                layout = LayoutPlanner.Build(disk, plan, target.Length);
                foreach (var pp in plan)
                    Console.WriteLine($"  #{pp.Source.Number} {pp.Source.Kind,-10} {Fmt.Size(pp.Source.Length),10} → {Fmt.Size(layout.Partitions.First(x => x.Number == pp.Source.Number).Length),10}  {T("開始 {0} MiB", layout.Partitions.First(x => x.Number == pp.Source.Number).Offset / (1 << 20))}" +
                                      (pp.Resizable ? T("（最小 {0}）", Fmt.Size(pp.MinLength)) : T("（変更不可）")));
            }
            var progress = new ConsoleProgress();
            grow = RestoreEngine.RestoreDisk(repo, p, disk, target, writeUnused: !device,
                new Progress<RestoreProgress>(x => progress.Report(x.Phase, x.Done, x.Total, x.BytesPerSecond)), m => { progress.End(); Console.WriteLine(m); },
                layout: layout);
            progress.End();
        }
        if (grow.Count > 0)
        {
            var m = System.Text.RegularExpressions.Regex.Match(outPath, @"PhysicalDrive(\d+)$", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            if (device && m.Success) foreach (var l in NtfsResize.ExtendOnDisk(int.Parse(m.Groups[1].Value), grow)) Console.WriteLine(l);
            else Console.WriteLine(T("  イメージファイルのため NTFS の拡張は行いません（使うときに「ディスクの管理」でボリュームを拡張してください）。"));
        }
        Console.WriteLine(T("ディスク {0} を {1} に復元しました。", di, outPath));
        if (a.Flag("adapt"))
        {
            var m2 = System.Text.RegularExpressions.Regex.Match(outPath, @"PhysicalDrive(\d+)$", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            if (device && m2.Success) foreach (var l in HardwareAdapt.RunOnDisk(int.Parse(m2.Groups[1].Value), a.Opt("drivers") is { } d ? [d] : null)) Console.WriteLine(l);
            else Console.WriteLine(T("イメージファイルのため、別の PC 向けの調整は行いませんでした。"));
        }
        return 0;
    }

    // ---- file level ----

    static int Ls(Args a)
    {
        var repo = OpenRepo(a);
        var p = repo.GetPoint(a.Pos(1) ?? "latest");
        int vol = int.Parse(a.Pos(2) ?? "0");
        using var src = new PointVolumeSource(repo, p, vol);
        var ntfs = NtfsVolume.Open(src);
        var path = a.Pos(3) ?? "\\";
        var e = ntfs.Resolve(path) ?? throw new KeyNotFoundException(T("{0} が見つかりません。", path));
        if (!e.IsDirectory) { Console.WriteLine($"{e.Name}  {ntfs.FileInfo(e.Record).Size}"); return 0; }
        foreach (var c in ntfs.ListDirectory(e.Record))
            Console.WriteLine(c.IsDirectory ? $"{"<DIR>",12}  {c.Modified.ToLocalTime():yyyy-MM-dd HH:mm}  {c.Name}" : $"{ntfs.FileInfo(c.Record).Size,12}  {c.Modified.ToLocalTime():yyyy-MM-dd HH:mm}  {c.Name}");
        return 0;
    }

    static int Extract(Args a)
    {
        var repo = OpenRepo(a);
        var p = repo.GetPoint(a.Pos(1) ?? "latest");
        int vol = int.Parse(a.Pos(2) ?? "0");
        var path = a.Pos(3) ?? throw new ArgumentException(T("取り出すパスを指定してください。"));
        var outDir = a.Pos(4) ?? throw new ArgumentException(T("出力先を指定してください。"));
        using var src = new PointVolumeSource(repo, p, vol);
        var ntfs = NtfsVolume.Open(src);
        var e = ntfs.Resolve(path) ?? throw new KeyNotFoundException(T("{0} が見つかりません。", path));
        var r = FileExtractor.Extract(ntfs, e, outDir, Console.WriteLine);
        Console.WriteLine(T("{0} ファイル（{1}）を取り出しました。{2}", r.Files, Fmt.Size(r.Bytes), r.Errors > 0 ? T("エラー {0} 件", r.Errors) : ""));
        return r.Errors == 0 ? 0 : 3;
    }

    static int Prune(Args a)
    {
        var repo = OpenRepo(a);
        int keep = int.Parse(a.Opt("keep") ?? throw new ArgumentException(T("--keep N を指定してください。")));
        var r = Maintenance.ApplyRetention(repo, keep, Console.WriteLine);
        Console.WriteLine(T("削除 {0} ポイント、{1} 解放", r.DeletedPoints.Count, Fmt.Size(r.FreedBytes)));
        return 0;
    }

    /// <summary>"500MB" / "80GB" / "1.5TB" / バイト数。</summary>
    public static long ParseSize(string s)
    {
        s = s.Trim().ToUpperInvariant();
        (string suf, long mul)[] units = [("TB", 1L << 40), ("GB", 1L << 30), ("MB", 1L << 20), ("KB", 1L << 10), ("T", 1L << 40), ("G", 1L << 30), ("M", 1L << 20)];
        foreach (var (suf, mul) in units)
            if (s.EndsWith(suf)) return (long)(double.Parse(s[..^suf.Length], System.Globalization.CultureInfo.InvariantCulture) * mul);
        return long.Parse(s);
    }

    /// <summary>dvault vss-test C: ... : ライター連携のスナップショットを作って状態を表示し、すぐ消す（読み取りはブートセクタだけ）。</summary>
    static int VssTest(Args a)
    {
        var vols = a.Positional.Select(s => VolumeInfo.Find(s)?.GuidPath ?? s).ToList();
        int before = CountShadows();
        using (var set = VssSet.Create(vols, fullBackup: false, Console.WriteLine))
        {
            foreach (var v in vols)
            {
                using var src = HandleSource.OpenDevice(set.DeviceFor(v));
                var b = new byte[512];
                src.Read(0, b);
                Console.WriteLine($"  {v} → {set.DeviceFor(v)}  {Fmt.Size(src.Length)}  {System.Text.Encoding.ASCII.GetString(b, 3, 8).Trim()}");
            }
            foreach (var (w, st, err) in set.Writers) Console.WriteLine($"  {T("ライター:")} {w,-40} {st}{(err != 0 ? $" 0x{err:X8}" : "")}");
            foreach (var w in set.Warnings) Console.WriteLine(T("  警告: {0}", w));
            set.BackupComplete();
        }
        int after = CountShadows();
        Console.WriteLine(T("シャドウコピー数: 前 {0} / 後 {1}", before, after));
        return after == before ? 0 : 3;
    }

    static int CountShadows()
    {
        using var s = new System.Management.ManagementObjectSearcher(@"\\.\root\cimv2", "SELECT ID FROM Win32_ShadowCopy");
        return s.Get().Count;
    }

    static int MinSizeCmd(Args a)
    {
        var repo = OpenRepo(a);
        var p = repo.GetPoint(a.Pos(1) ?? "latest");
        foreach (var d in p.Disks)
            foreach (var pp in LayoutPlanner.Analyze(repo, p, d))
                Console.WriteLine($"disk{d.Index} #{pp.Source.Number} {pp.Source.Kind,-10} {pp.Source.Length,15} {T("最小")} {pp.MinLength,15}{(pp.Resizable ? "" : T(" 変更不可"))}");
        for (int i = 0; i < p.Volumes.Count; i++)
        {
            if (p.Volumes[i].FileSystem != "NTFS") continue;
            using var src = new PointVolumeSource(repo, p, i);
            Console.WriteLine($"vol{i} {p.Volumes[i].Display.Trim()} {p.Volumes[i].Size} {T("最小")} {NtfsResize.MinSize(src)}");
        }
        return 0;
    }

    static int Media(Args a)
    {
        if (a.Flag("uninstall-adk"))
        {
            var list = MediaBuilder.InstalledAdk();
            if (list.Count == 0) { Console.WriteLine(T("アンインストールできる Windows ADK はありません。")); return 0; }
            foreach (var p in list) Console.WriteLine($"  {p.Name} {p.Version}");
            MediaBuilder.UninstallAdk(Console.WriteLine);
            return 0;
        }
        var o = new MediaOptions
        {
            IsoPath = a.Opt("iso"),
            UsbDisk = a.Opt("usb") is { } u ? int.Parse(u) : null,
            DriversFromThisPC = !a.Flag("no-drivers"),
            Japanese = !a.Flag("english"),
            ExtraDrivers = a.Opt("drivers"),
        };
        if (a.Opt("app") is { } app) o.AppDir = app;
        if (a.Opt("work") is { } w) o.WorkDir = w;
        var sw = System.Diagnostics.Stopwatch.StartNew();
        MediaBuilder.Build(o, m => Console.WriteLine($"{DateTime.Now:HH:mm:ss} {m}"));
        Console.WriteLine(T("完了（{0}）", Fmt.Duration(sw.Elapsed)));
        return 0;
    }

    static int Shadows()
    {
        var list = ShadowCopies.List();
        if (list.Count == 0) { Console.WriteLine(T("シャドウコピーはありません。")); return 0; }
        foreach (var x in list)
            Console.WriteLine($"{x.Created:yyyy/MM/dd HH:mm}  {x.VolumeText,-6} {x.Id}  {x.RestorePoint}");
        Console.WriteLine();
        Console.WriteLine(T("バックアップ: dvault backup <repo> <元> --shadow <ID>[,<ID>]   またはボリューム 1 つだけ: dvault backup <repo> shadow:<ID>"));
        return 0;
    }

    static int Volumes(Args a)
    {
        foreach (var v in VolumeInfo.Enumerate())
            Console.WriteLine($"{v.Letter ?? "  ",-3} {v.FileSystem ?? "-",-6} {Fmt.Size(v.Size),10} {(v.Disk is int d ? $"disk{d}@{Fmt.Size(v.DiskOffset ?? 0)}" : ""),-18} {v.Label}  {v.GuidPath}" +
                              (v.IsSystem ? T(" [システム]") : ""));
        return 0;
    }
}

/// <summary>--name value / --flag / 位置引数。</summary>
sealed class Args
{
    readonly Dictionary<string, string?> _opts = [];
    public List<string> Positional { get; } = [];

    public Args(IEnumerable<string> args)
    {
        var list = args.ToList();
        for (int i = 0; i < list.Count; i++)
        {
            if (list[i].StartsWith("--"))
            {
                var name = list[i][2..];
                if (i + 1 < list.Count && !list[i + 1].StartsWith("--") && !IsFlag(name)) _opts[name] = list[++i];
                else _opts[name] = null;
            }
            else Positional.Add(list[i]);
        }
    }

    static bool IsFlag(string n) => n is "full" or "sparse" or "yes" or "no-exclude" or "keep-work" or "no-drivers" or "english" or "extend" or "crash-consistent" or "vss-full" or "offline" or "uninstall-adk" or "adapt";

    public string? Pos(int i) => i < Positional.Count ? Positional[i] : null;
    public string? Opt(string n) => _opts.TryGetValue(n, out var v) ? v : null;
    public bool Flag(string n) => _opts.ContainsKey(n);
}

sealed class ConsoleProgress : IProgress<BackupProgress>
{
    bool _dirty;
    public void Report(BackupProgress p) =>
        Report($"{p.Phase} {p.Volume + 1}/{p.VolumeCount}", p.Done, p.Total, p.BytesPerSecond, T(" 新規 {0}", Fmt.Size(p.NewBytes)));

    public void Report(string phase, long done, long total, double rate, string extra = "")
    {
        if (Console.IsOutputRedirected) return;
        int pct = total > 0 ? (int)(done * 100 / total) : 100;
        Console.Write($"\r{phase} {pct,3}% {Fmt.Size(done)}/{Fmt.Size(total)} {Fmt.Rate(rate)}{extra}      ");
        _dirty = true;
    }

    public void End()
    {
        if (_dirty) Console.WriteLine();
        _dirty = false;
    }
}



