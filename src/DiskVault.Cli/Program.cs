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
                "selftest" => SelfTest.Run(a),
                "bench" => Bench.Run(a),
                "recover" => Recover.Run(a),
                "media" => Media(a),
                "minsize" => MinSizeCmd(a),
                "vss-test" => VssTest(a),
                _ => Fail($"不明なコマンド: {args[0]}"),
            };
        }
        catch (Exception e) when (e is IOException or InvalidDataException or UnauthorizedAccessException or KeyNotFoundException
                                       or InvalidOperationException or ArgumentException or NotSupportedException)
        {
            Console.Error.WriteLine($"エラー: {e.Message}");
            if (Environment.GetEnvironmentVariable("DVAULT_DEBUG") == "1") Console.Error.WriteLine(e);
            return 2;
        }
    }

    static void Usage() => Console.WriteLine("""
        DiskVault イメージバックアップ CLI

          dvault backup <repo> <source>... [--full] [--job 名前] [--machine PC名] [--keep N] [--password P] [--comment 文]
                              [--crash-consistent]（VSS ライターと連携しない） [--vss-full]（VSS 完全バックアップ＝ログ切り捨て）
                              [--offline]（スナップショットを使わず直接読む。WinPE では自動）
              source: C: / \\?\Volume{...}\ （VSS スナップショットから。管理者権限が必要）
                      system                  （Windows の入ったディスク全体：ベアメタル復元用）
                      physical:N               （物理ディスク N の全体：ベアメタル復元用）
                      image.img                （ボリュームのイメージファイル）
                      image.img@offset:length  （イメージの一部）
                      disk:disk.img            （ディスクイメージ。全パーティション＋構成）
          dvault list <repo>
          dvault info <repo> <point>
          dvault verify <repo> [point|all]
          dvault restore-volume <repo> <point> <vol> <out.img|\\.\X:> [--sparse]
          dvault restore-disk <repo> <point> <out.img|\\.\PhysicalDriveN> [--disk N] [--size 容量] [--yes]
                              [--resize auto | 3=max | 3=80GB,4=1GB]   （パーティションのサイズ変更。NTFS のみ）
                              [--gap 2=300MB,...]（パーティションの前に空きを入れて開始位置をずらす）
          dvault ls <repo> <point> <vol> [パス]
          dvault extract <repo> <point> <vol> <パス> <出力先>
          dvault prune <repo> --keep N
          dvault volumes
          dvault media --iso out.iso | --usb N  [--app 発行フォルダ] [--no-drivers] [--drivers フォルダ]
          dvault media --uninstall-adk           （Windows ADK と WinPE アドオンを削除）
          dvault recover                         （回復環境用の対話式ベアメタル復元）
          dvault selftest [作業フォルダ]

          point は復元ポイント名（先頭一致可）または latest。パスワードは環境変数 DVAULT_PASSWORD でも指定可。
        """);

    static int Fail(string msg)
    {
        Console.Error.WriteLine(msg);
        return 1;
    }

    static string? Password(Args a) => a.Opt("password") ?? Environment.GetEnvironmentVariable("DVAULT_PASSWORD");

    static Repository OpenRepo(Args a, int pos = 0) => Repository.Open(a.Pos(pos) ?? throw new ArgumentException("リポジトリを指定してください。"), Password(a));

    // ---- backup ----

    static int Backup(Args a)
    {
        var root = a.Pos(0) ?? throw new ArgumentException("リポジトリを指定してください。");
        var sources = a.Positional.Skip(1).ToList();
        if (sources.Count == 0) throw new ArgumentException("バックアップ元を指定してください。");
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
        using var plan = SourcePlanner.Build(sources, Console.WriteLine, useWriters: !a.Flag("crash-consistent"), vssFullBackup: a.Flag("vss-full"), offline: a.Flag("offline") ? true : null);
        o.Disks = plan.Disks;
        o.Warnings = [.. plan.Warnings];
        o.Consistency = plan.Consistency;
        var progress = new ConsoleProgress();
        var p = BackupEngine.Run(repo, plan.Volumes, o, progress, Console.WriteLine);
        progress.End();
        plan.Complete();
        foreach (var (wn, st, err) in plan.Writers) Console.WriteLine($"  VSS ライター: {wn}  {st}{(err != 0 ? $"  0x{err:X8}" : "")}");
        foreach (var w in p.Warnings) Console.WriteLine($"警告: {w}");
        if (a.Opt("keep") is { } keep)
            Maintenance.ApplyRetention(repo, int.Parse(keep), Console.WriteLine);
        Console.WriteLine($"復元ポイント: {p.Id}");
        return 0;
    }

    // ---- list / info ----

    static int List(Args a)
    {
        var repo = OpenRepo(a);
        var pts = repo.ListPoints();
        Console.WriteLine($"{repo.Root}  （ブロック {Fmt.Size(repo.Config.BlockSize)}、{(repo.IsEncrypted ? "暗号化あり" : "暗号化なし")}、使用量 {Fmt.Size(Maintenance.DirSize(repo.Root))}）");
        Console.WriteLine($"{"復元ポイント",-20} {"種別",-4} {"ジョブ",-12} {"使用中",10} {"新規",10} {"保存",10}  ボリューム");
        foreach (var p in pts)
            Console.WriteLine($"{p.Id,-20} {(p.Kind == PointKind.Full ? "フル" : "増分"),-4} {p.Job,-12} {Fmt.Size(p.Stats.UsedBytes),10} {Fmt.Size(p.Stats.NewBytes),10} {Fmt.Size(p.Stats.StoredBytes),10}  " +
                              string.Join(", ", p.Volumes.Select(v => v.Display.Trim())) + (p.BareMetalCapable ? "  [BMR]" : ""));
        Console.WriteLine($"{pts.Count} 個");
        return 0;
    }

    static int Info(Args a)
    {
        var repo = OpenRepo(a);
        var p = repo.GetPoint(a.Pos(1) ?? "latest");
        Console.WriteLine($"復元ポイント {p.Id}  {p.Time.ToLocalTime():yyyy-MM-dd HH:mm:ss}  {(p.Kind == PointKind.Full ? "フル" : "増分")}  チェーン {p.Chain}");
        Console.WriteLine($"  ジョブ {p.Job}  PC {p.Machine}  OS {p.OsVersion}");
        if (p.Comment != null) Console.WriteLine($"  コメント: {p.Comment}");
        Console.WriteLine($"  所要 {Fmt.Duration(TimeSpan.FromSeconds(p.Stats.Seconds))}、読み取り {Fmt.Size(p.Stats.ReadBytes)}、新規 {Fmt.Size(p.Stats.NewBytes)}、保存 {Fmt.Size(p.Stats.StoredBytes)}");
        foreach (var v in p.Volumes)
            Console.WriteLine($"  vol{v.Index}: {v.Display.Trim()}  {Fmt.Size(v.Size)}（使用中 {Fmt.Size(v.UsedBytes)}）{(v.IsSystem ? " [システム]" : "")}{(v.IsBoot ? " [ブート]" : "")}");
        foreach (var d in p.Disks)
        {
            Console.WriteLine($"  ディスク {d.Index}（{d.Model ?? "?"}、{Fmt.Size(d.Size)}、{d.Style}）");
            foreach (var part in d.Partitions)
                Console.WriteLine($"    #{part.Number} {part.Kind,-10} {Fmt.Size(part.Offset),10} 〜 {Fmt.Size(part.Length),10}  {(part.Volume is int vi ? $"vol{vi}" : "(中身なし)")}");
        }
        foreach (var w in p.Warnings) Console.WriteLine($"  警告: {w}");
        return 0;
    }

    // ---- verify ----

    static int Verify(Args a)
    {
        var repo = OpenRepo(a);
        var which = a.Pos(1) ?? "all";
        var pts = which == "all" ? repo.ListPoints() : [repo.GetPoint(which)];
        var r = Maintenance.Verify(repo, pts, new Progress<(long Done, long Total)>(x => Console.Write($"\r検証中 {x.Done}/{x.Total}   ")));
        Console.WriteLine();
        foreach (var e in r.Errors.Take(20)) Console.WriteLine($"  NG: {e}");
        Console.WriteLine(r.Bad == 0 ? $"OK: {r.Points} ポイント、{r.Blocks} ブロックを検証しました。" : $"NG: {r.Bad} 件のエラー");
        return r.Bad == 0 ? 0 : 3;
    }

    // ---- restore ----

    static int RestoreVolume(Args a)
    {
        var repo = OpenRepo(a);
        var p = repo.GetPoint(a.Pos(1) ?? throw new ArgumentException("復元ポイントを指定してください。"));
        int vol = int.Parse(a.Pos(2) ?? throw new ArgumentException("ボリューム番号を指定してください。"));
        var outPath = a.Pos(3) ?? throw new ArgumentException("復元先を指定してください。");
        var v = p.Volumes[vol];
        long newSize = a.Opt("size") is { } sz ? ParseSize(sz) / LayoutPlanner.Align * LayoutPlanner.Align : v.Size;
        bool device = Targets.IsDevice(outPath);
        using (var target = Targets.Open(outPath, newSize, a.Flag("yes")))
        {
            var progress = new ConsoleProgress();
            var prog = new Progress<RestoreProgress>(x => progress.Report(x.Phase, x.Done, x.Total, x.BytesPerSecond));
            if (newSize < v.Size)
            {
                if (v.FileSystem != "NTFS") throw new InvalidOperationException("縮小できるのは NTFS だけです。");
                using var src = new PointVolumeSource(repo, p, vol);
                long min = NtfsResize.MinSize(src);
                if (newSize < min) throw new InvalidOperationException($"{Fmt.Size(min)} より小さくできません。");
                RestoreEngine.RestoreVolume(repo, p, vol, target, writeUnused: !device && !a.Flag("sparse"), prog, limit: newSize);
                NtfsResize.ShrinkOffline(src, target, newSize, Console.WriteLine);
            }
            else RestoreEngine.RestoreVolume(repo, p, vol, target, writeUnused: !device && !a.Flag("sparse"), prog);
            progress.End();
        }
        if (device && v.FileSystem == "NTFS" && newSize >= v.Size && a.Flag("extend"))
            Console.WriteLine(NtfsResize.Extend(outPath));
        Console.WriteLine($"{v.Display.Trim()} を {outPath} に復元しました。");
        return 0;
    }

    static int RestoreDisk(Args a)
    {
        var repo = OpenRepo(a);
        var p = repo.GetPoint(a.Pos(1) ?? throw new ArgumentException("復元ポイントを指定してください。"));
        var outPath = a.Pos(2) ?? throw new ArgumentException("復元先を指定してください。");
        int di = int.Parse(a.Opt("disk") ?? "0");
        if (di >= p.Disks.Count) throw new ArgumentException("この復元ポイントにはディスク構成がありません（ベアメタル復元に非対応）。");
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
                        var pp = plan.FirstOrDefault(x => x.Source.Number == int.Parse(kv[0])) ?? throw new ArgumentException($"パーティション {kv[0]} はありません。");
                        if (kv[1] == "max") pp.Fill = true; else pp.NewLength = ParseSize(kv[1]) / LayoutPlanner.Align * LayoutPlanner.Align;
                    }
                // --gap 2=100MB,3=0 … パーティションの前に空きを入れて開始位置を後ろへずらす
                foreach (var item in (a.Opt("gap") ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                {
                    var kv = item.Split('=', 2);
                    var pp = plan.FirstOrDefault(x => x.Source.Number == int.Parse(kv[0])) ?? throw new ArgumentException($"パーティション {kv[0]} はありません。");
                    pp.GapBefore = (ParseSize(kv[1]) + LayoutPlanner.Align - 1) / LayoutPlanner.Align * LayoutPlanner.Align;
                }
                layout = LayoutPlanner.Build(disk, plan, target.Length);
                foreach (var pp in plan)
                    Console.WriteLine($"  #{pp.Source.Number} {pp.Source.Kind,-10} {Fmt.Size(pp.Source.Length),10} → {Fmt.Size(layout.Partitions.First(x => x.Number == pp.Source.Number).Length),10}  開始 {layout.Partitions.First(x => x.Number == pp.Source.Number).Offset / (1 << 20)} MiB" +
                                      (pp.Resizable ? $"（最小 {Fmt.Size(pp.MinLength)}）" : "（変更不可）"));
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
            else Console.WriteLine("  イメージファイルのため NTFS の拡張は行いません（使うときに「ディスクの管理」でボリュームを拡張してください）。");
        }
        Console.WriteLine($"ディスク {di} を {outPath} に復元しました。");
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
        var e = ntfs.Resolve(path) ?? throw new KeyNotFoundException($"{path} が見つかりません。");
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
        var path = a.Pos(3) ?? throw new ArgumentException("取り出すパスを指定してください。");
        var outDir = a.Pos(4) ?? throw new ArgumentException("出力先を指定してください。");
        using var src = new PointVolumeSource(repo, p, vol);
        var ntfs = NtfsVolume.Open(src);
        var e = ntfs.Resolve(path) ?? throw new KeyNotFoundException($"{path} が見つかりません。");
        var r = FileExtractor.Extract(ntfs, e, outDir, Console.WriteLine);
        Console.WriteLine($"{r.Files} ファイル（{Fmt.Size(r.Bytes)}）を取り出しました。{(r.Errors > 0 ? $"エラー {r.Errors} 件" : "")}");
        return r.Errors == 0 ? 0 : 3;
    }

    static int Prune(Args a)
    {
        var repo = OpenRepo(a);
        int keep = int.Parse(a.Opt("keep") ?? throw new ArgumentException("--keep N を指定してください。"));
        var r = Maintenance.ApplyRetention(repo, keep, Console.WriteLine);
        Console.WriteLine($"削除 {r.DeletedPoints.Count} ポイント、{Fmt.Size(r.FreedBytes)} 解放");
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
            foreach (var (w, st, err) in set.Writers) Console.WriteLine($"  ライター: {w,-40} {st}{(err != 0 ? $" 0x{err:X8}" : "")}");
            foreach (var w in set.Warnings) Console.WriteLine($"  警告: {w}");
            set.BackupComplete();
        }
        int after = CountShadows();
        Console.WriteLine($"シャドウコピー数: 前 {before} / 後 {after}");
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
                Console.WriteLine($"disk{d.Index} #{pp.Source.Number} {pp.Source.Kind,-10} {pp.Source.Length,15} 最小 {pp.MinLength,15}{(pp.Resizable ? "" : " 変更不可")}");
        for (int i = 0; i < p.Volumes.Count; i++)
        {
            if (p.Volumes[i].FileSystem != "NTFS") continue;
            using var src = new PointVolumeSource(repo, p, i);
            Console.WriteLine($"vol{i} {p.Volumes[i].Display.Trim()} {p.Volumes[i].Size} 最小 {NtfsResize.MinSize(src)}");
        }
        return 0;
    }

    static int Media(Args a)
    {
        if (a.Flag("uninstall-adk"))
        {
            var list = MediaBuilder.InstalledAdk();
            if (list.Count == 0) { Console.WriteLine("アンインストールできる Windows ADK はありません。"); return 0; }
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
        Console.WriteLine($"完了（{Fmt.Duration(sw.Elapsed)}）");
        return 0;
    }

    static int Volumes(Args a)
    {
        foreach (var v in VolumeInfo.Enumerate())
            Console.WriteLine($"{v.Letter ?? "  ",-3} {v.FileSystem ?? "-",-6} {Fmt.Size(v.Size),10} {(v.Disk is int d ? $"disk{d}@{Fmt.Size(v.DiskOffset ?? 0)}" : ""),-18} {v.Label}  {v.GuidPath}" +
                              (v.IsSystem ? " [システム]" : ""));
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

    static bool IsFlag(string n) => n is "full" or "sparse" or "yes" or "no-exclude" or "keep-work" or "no-drivers" or "english" or "extend" or "crash-consistent" or "vss-full" or "offline" or "uninstall-adk";

    public string? Pos(int i) => i < Positional.Count ? Positional[i] : null;
    public string? Opt(string n) => _opts.TryGetValue(n, out var v) ? v : null;
    public bool Flag(string n) => _opts.ContainsKey(n);
}

sealed class ConsoleProgress : IProgress<BackupProgress>
{
    bool _dirty;
    public void Report(BackupProgress p) =>
        Report($"{p.Phase} {p.Volume + 1}/{p.VolumeCount}", p.Done, p.Total, p.BytesPerSecond, $" 新規 {Fmt.Size(p.NewBytes)}");

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



