using System.Diagnostics;
using System.Text;

namespace DiskVault.Platform;

public sealed class MediaOptions
{
    /// <summary>作業フォルダ（数 GB 使う。終わったら消す）。</summary>
    public string WorkDir { get; set; } = Path.Combine(Path.GetTempPath(), "DiskVaultPE");
    /// <summary>WinPE に入れる DiskVault 一式（自己完結で発行したフォルダ）。</summary>
    public string AppDir { get; set; } = AppContext.BaseDirectory;
    public string? IsoPath { get; set; }
    /// <summary>書き込む USB メモリのディスク番号（中身は消える）。</summary>
    public int? UsbDisk { get; set; }
    public bool Japanese { get; set; } = true;
    /// <summary>この PC のネットワーク・ストレージ用ドライバを WinPE に入れる。</summary>
    public bool DriversFromThisPC { get; set; } = true;
    /// <summary>追加のドライバフォルダ（.inf を含む）。</summary>
    public string? ExtraDrivers { get; set; }
}

/// <summary>
/// Windows ADK + WinPE アドオンを使って回復メディア（WinPE）を作る。copype 相当を自前で行い、
/// 日本語環境・WMI・ドライバ・DiskVault を boot.wim に入れて、ISO / USB に出力する。管理者権限が必要。
/// </summary>
public static class MediaBuilder
{
    public static string KitsRoot => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Windows Kits", "10", "Assessment and Deployment Kit");
    static string PeRoot => Path.Combine(KitsRoot, "Windows Preinstallation Environment", "amd64");
    static string DeployTools => Path.Combine(KitsRoot, "Deployment Tools", "amd64");
    static string Dism => Path.Combine(DeployTools, "DISM", "dism.exe");
    static string Oscdimg => Path.Combine(DeployTools, "Oscdimg", "oscdimg.exe");

    /// <summary>ADK と WinPE アドオンが入っているか。足りないものの説明（なければ null）。</summary>
    public static string? CheckAdk()
    {
        if (!File.Exists(Dism) || !File.Exists(Oscdimg)) return T("Windows ADK（展開ツール）がインストールされていません。");
        if (!File.Exists(Path.Combine(PeRoot, "en-us", "winpe.wim"))) return T("Windows ADK の WinPE アドオンがインストールされていません。");
        return null;
    }

    public sealed record AdkProduct(string Name, string Version, string Exe, bool IsWinPE);

    /// <summary>
    /// インストール済みの ADK / WinPE アドオン（アンインストール情報から。表示名は言語で変わるので
    /// Package Cache の adksetup.exe / adkwinpesetup.exe で見分ける）。アドオンを先に並べる。
    /// </summary>
    public static List<AdkProduct> InstalledAdk()
    {
        var list = new List<AdkProduct>();
        foreach (var view in new[] { Microsoft.Win32.RegistryView.Registry64, Microsoft.Win32.RegistryView.Registry32 })
        {
            using var hk = Microsoft.Win32.RegistryKey.OpenBaseKey(Microsoft.Win32.RegistryHive.LocalMachine, view);
            using var un = hk.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall");
            if (un == null) continue;
            foreach (var n in un.GetSubKeyNames())
            {
                using var k = un.OpenSubKey(n);
                var s = (k?.GetValue("QuietUninstallString") ?? k?.GetValue("UninstallString")) as string;
                if (s == null) continue;
                var exe = s.TrimStart().StartsWith('"') ? s.TrimStart()[1..].Split('"')[0] : s.Split(' ')[0];
                var file = Path.GetFileName(exe).ToLowerInvariant();
                if (file is not ("adksetup.exe" or "adkwinpesetup.exe")) continue;
                if (list.Any(p => string.Equals(p.Exe, exe, StringComparison.OrdinalIgnoreCase))) continue;
                list.Add(new AdkProduct(k!.GetValue("DisplayName") as string ?? file, k.GetValue("DisplayVersion") as string ?? "", exe, file == "adkwinpesetup.exe"));
            }
        }
        return list.OrderByDescending(p => p.IsWinPE).ToList();
    }

    /// <summary>ADK と WinPE アドオンをアンインストールする（アドオン → 本体の順）。管理者権限が必要。</summary>
    public static void UninstallAdk(Action<string> log, CancellationToken ct = default)
    {
        if (!Native.IsAdmin()) throw new UnauthorizedAccessException(T("アンインストールには管理者権限が必要です。"));
        var list = InstalledAdk();
        if (list.Count == 0) throw new InvalidOperationException(T("アンインストールできる Windows ADK が見つかりません（Windows の「アプリ」から削除してください）。"));
        // 作りかけの回復メディアの WIM がマウントされたままだと ADK のファイルが消せないので、先に片付ける
        var work = new MediaOptions().WorkDir;
        CleanupMount(Path.Combine(work, "mount"), log);
        try { if (Directory.Exists(work)) Directory.Delete(work, true); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }

        foreach (var p in list)
        {
            ct.ThrowIfCancellationRequested();
            if (!File.Exists(p.Exe)) throw new FileNotFoundException(T("{0} のアンインストーラーがありません: {1}", p.Name, p.Exe));
            log(T("{0} {1} をアンインストールしています…", p.Name, p.Version));
            using var proc = Process.Start(new ProcessStartInfo(p.Exe, "/uninstall /quiet /norestart") { UseShellExecute = false, CreateNoWindow = true })
                             ?? throw new InvalidOperationException(T("{0} を起動できません。", p.Exe));
            proc.WaitForExit();
            // 3010 = 再起動が必要（成功）
            if (proc.ExitCode is not (0 or 3010))
                throw new InvalidOperationException(T("{0} のアンインストールに失敗しました（終了コード {1}）。", p.Name, proc.ExitCode));
            log(proc.ExitCode == 3010 ? T("完了しました（再起動が必要です）。") : T("完了しました。"));
        }
    }

    public static void Build(MediaOptions o, Action<string> log, CancellationToken ct = default)
    {
        if (CheckAdk() is { } missing) throw new InvalidOperationException(missing);
        if (!Native.IsAdmin()) throw new UnauthorizedAccessException(T("回復メディアの作成には管理者権限が必要です。"));
        if (o.IsoPath == null && o.UsbDisk == null) throw new ArgumentException(T("ISO の保存先か USB メモリを指定してください。"));
        if (!File.Exists(Path.Combine(o.AppDir, "DiskVault.exe")) || !File.Exists(Path.Combine(o.AppDir, "dvault.exe")))
            throw new FileNotFoundException(T("{0} に DiskVault.exe / dvault.exe がありません。", o.AppDir));

        var work = o.WorkDir;
        var media = Path.Combine(work, "media");
        var mount = Path.Combine(work, "mount");
        CleanupMount(mount, log);
        if (Directory.Exists(work)) Directory.Delete(work, true);
        Directory.CreateDirectory(mount);

        log(T("WinPE をコピーしています…"));
        CopyDir(Path.Combine(PeRoot, "Media"), media);
        Directory.CreateDirectory(Path.Combine(media, "sources"));
        var wim = Path.Combine(media, "sources", "boot.wim");
        File.Copy(Path.Combine(PeRoot, "en-us", "winpe.wim"), wim);
        File.SetAttributes(wim, FileAttributes.Normal);

        log(T("boot.wim をマウントしています…"));
        Run(Dism, $"/Mount-Image /ImageFile:\"{wim}\" /Index:1 /MountDir:\"{mount}\"", log, ct);
        bool committed = false;
        try
        {
            var ocs = Path.Combine(PeRoot, "WinPE_OCs");
            var packages = new List<string> { "WinPE-WMI", "WinPE-StorageWMI", "WinPE-EnhancedStorage", "WinPE-SecureStartup" };
            foreach (var p in packages)
            {
                ct.ThrowIfCancellationRequested();
                log(T("コンポーネント追加: {0}", p));
                AddPackage(mount, Path.Combine(ocs, p + ".cab"), log, ct);
                AddPackage(mount, Path.Combine(ocs, "en-us", p + "_en-us.cab"), log, ct);
            }
            if (o.Japanese)
            {
                log(T("日本語環境を追加しています…"));
                AddPackage(mount, Path.Combine(ocs, "WinPE-FontSupport-JA-JP.cab"), log, ct);
                AddPackage(mount, Path.Combine(ocs, "ja-jp", "lp.cab"), log, ct);
                foreach (var p in packages) AddPackage(mount, Path.Combine(ocs, "ja-jp", p + "_ja-jp.cab"), log, ct);
                Run(Dism, $"/Image:\"{mount}\" /Set-AllIntl:ja-JP", log, ct);
                Run(Dism, $"/Image:\"{mount}\" /Set-InputLocale:0411:{{03B5835F-F03C-411B-9CE2-AA23E1171E36}}{{A76C93D9-5523-4E90-AAFA-4DB112F9AC76}}", log, ct, allowFail: true);
                Run(Dism, $"/Image:\"{mount}\" /Set-TimeZone:\"Tokyo Standard Time\"", log, ct, allowFail: true);
            }
            Run(Dism, $"/Image:\"{mount}\" /Set-ScratchSpace:512", log, ct, allowFail: true);

            var drivers = new List<string>();
            if (o.DriversFromThisPC)
            {
                log(T("この PC のドライバを書き出しています…"));
                var exp = Path.Combine(work, "drivers-all");
                Directory.CreateDirectory(exp);
                Run(Dism, $"/Online /Export-Driver /Destination:\"{exp}\"", log, ct, allowFail: true);
                drivers.AddRange(Directory.EnumerateDirectories(exp).Where(d => Directory.EnumerateFiles(d, "*.inf").Any(IsBootCriticalInf)));
                log(T("  ネットワーク・ストレージ関係のドライバ {0} 個を追加します。", drivers.Count));
            }
            if (!string.IsNullOrEmpty(o.ExtraDrivers) && Directory.Exists(o.ExtraDrivers)) drivers.Add(o.ExtraDrivers);
            foreach (var d in drivers)
            {
                ct.ThrowIfCancellationRequested();
                Run(Dism, $"/Image:\"{mount}\" /Add-Driver /Driver:\"{d}\" /Recurse", log, ct, allowFail: true);
            }

            log(T("DiskVault を組み込んでいます…"));
            var appDst = Path.Combine(mount, "DiskVault");
            CopyDir(o.AppDir, appDst, skip: f => f.EndsWith("DiskVaultService.exe", StringComparison.OrdinalIgnoreCase)
                                                 || f.EndsWith(".pdb", StringComparison.OrdinalIgnoreCase));
            // 追加のドライバは、復元した Windows にも入れられるようにメディアにも置く（HardwareAdapt.DefaultDriverFolders）
            if (!string.IsNullOrEmpty(o.ExtraDrivers) && Directory.Exists(o.ExtraDrivers))
                CopyDir(o.ExtraDrivers, Path.Combine(appDst, "drivers"), skip: _ => false);
            File.WriteAllText(Path.Combine(mount, "Windows", "System32", "startnet.cmd"), StartNet(o.Japanese).ReplaceLineEndings("\r\n"), new UTF8Encoding(false));

            log(T("変更を保存しています（数分かかります）…"));
            Run(Dism, $"/Unmount-Image /MountDir:\"{mount}\" /Commit", log, ct);
            committed = true;
        }
        finally
        {
            if (!committed) Run(Dism, $"/Unmount-Image /MountDir:\"{mount}\" /Discard", log, CancellationToken.None, allowFail: true);
        }
        // boot.wim を詰め直して小さくする
        log(T("イメージを最適化しています…"));
        var wim2 = wim + ".new";
        Run(Dism, $"/Export-Image /SourceImageFile:\"{wim}\" /SourceIndex:1 /DestinationImageFile:\"{wim2}\" /Compress:max", log, ct, allowFail: true);
        if (File.Exists(wim2)) { File.Delete(wim); File.Move(wim2, wim); }
        log($"boot.wim: {Fmt.Size(new FileInfo(wim).Length)}");

        if (o.IsoPath != null)
        {
            log(T("ISO を作成しています: {0}", o.IsoPath));
            var fw = Path.Combine(DeployTools, "Oscdimg");
            var efi = File.Exists(Path.Combine(fw, "efisys_noprompt.bin")) ? "efisys_noprompt.bin" : "efisys.bin";
            var bootdata = $"2#p0,e,b\"{Path.Combine(fw, "etfsboot.com")}\"#pEF,e,b\"{Path.Combine(fw, efi)}\"";
            if (File.Exists(o.IsoPath)) File.Delete(o.IsoPath);
            Run(Oscdimg, $"-m -o -u2 -udfver102 -bootdata:{bootdata} \"{media}\" \"{o.IsoPath}\"", log, ct);
            log($"ISO: {Fmt.Size(new FileInfo(o.IsoPath).Length)}");
        }
        if (o.UsbDisk is int disk) WriteUsb(disk, media, log, ct);
        log(T("回復メディアを作成しました。"));
        try { Directory.Delete(work, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    /// <summary>起動時に DiskVault の回復画面を開く。GUI が起動できなければ文字画面版を開く。</summary>
    /// 日本語フォントを入れなかったメディアでは英語で表示する（DISKVAULT_LANG）。
    static string StartNet(bool japanese) => japanese
        ? """
          @echo off
          wpeinit
          wpeutil DisableFirewall >nul 2>&1
          chcp 65001 >nul
          set DISKVAULT_LANG=ja
          title DiskVault 回復環境
          cd /d X:\DiskVault
          start "" /wait X:\DiskVault\DiskVault.exe --recovery
          if errorlevel 1 X:\DiskVault\dvault.exe recover
          echo.
          echo DiskVault: X:\DiskVault\DiskVault.exe --recovery （画面版） / X:\DiskVault\dvault.exe recover （文字版）
          echo 再起動: wpeutil reboot   電源を切る: wpeutil shutdown
          """
        : """
          @echo off
          wpeinit
          wpeutil DisableFirewall >nul 2>&1
          chcp 65001 >nul
          set DISKVAULT_LANG=en
          title DiskVault recovery environment
          cd /d X:\DiskVault
          start "" /wait X:\DiskVault\DiskVault.exe --recovery
          if errorlevel 1 X:\DiskVault\dvault.exe recover
          echo.
          echo DiskVault: X:\DiskVault\DiskVault.exe --recovery (GUI) / X:\DiskVault\dvault.exe recover (text mode)
          echo Restart: wpeutil reboot   Shut down: wpeutil shutdown
          """;

    static bool IsBootCriticalInf(string inf)
    {
        try
        {
            foreach (var line in File.ReadLines(inf).Take(80))
            {
                var l = line.Trim();
                if (!l.StartsWith("Class", StringComparison.OrdinalIgnoreCase) || l.StartsWith("ClassGuid", StringComparison.OrdinalIgnoreCase)) continue;
                var v = l.Split('=', 2).ElementAtOrDefault(1)?.Split(';')[0].Trim().Trim('"') ?? "";
                return v.Equals("Net", StringComparison.OrdinalIgnoreCase) || v.Equals("SCSIAdapter", StringComparison.OrdinalIgnoreCase)
                       || v.Equals("HDC", StringComparison.OrdinalIgnoreCase) || v.Equals("USB", StringComparison.OrdinalIgnoreCase);
            }
        }
        catch (IOException) { }
        return false;
    }

    static void AddPackage(string mount, string cab, Action<string> log, CancellationToken ct)
    {
        if (!File.Exists(cab)) { log(T("  （{0} がないので省略）", Path.GetFileName(cab))); return; }
        Run(Dism, $"/Image:\"{mount}\" /Add-Package /PackagePath:\"{cab}\"", log, ct);
    }

    static void CleanupMount(string mount, Action<string> log)
    {
        if (!File.Exists(Dism)) return;
        if (Directory.Exists(mount) && Directory.EnumerateFileSystemEntries(mount).Any())
            Run(Dism, $"/Unmount-Image /MountDir:\"{mount}\" /Discard", log, CancellationToken.None, allowFail: true);
        Run(Dism, "/Cleanup-Mountpoints", log, CancellationToken.None, allowFail: true);
    }

    static void WriteUsb(int disk, string media, Action<string> log, CancellationToken ct)
    {
        var vols = VolumeInfo.Enumerate();
        if (vols.Any(v => v.Disk == disk && v.IsSystem)) throw new InvalidOperationException(T("システムディスクには書き込めません。"));
        log(T("USB メモリ（ディスク {0}）を初期化しています…", disk));
        // 32GB を超える USB でも FAT32 で作れるよう、パーティションは 32GB までにする
        var ps = $$"""
            $ErrorActionPreference = 'Stop'
            $d = Get-Disk -Number {{disk}}
            if ($d.BusType -ne 'USB') { throw 'USB ディスクではありません。' }
            if ($d.IsOffline) { Set-Disk -Number {{disk}} -IsOffline $false }
            Clear-Disk -Number {{disk}} -RemoveData -RemoveOEM -Confirm:$false -ErrorAction SilentlyContinue
            Initialize-Disk -Number {{disk}} -PartitionStyle MBR -ErrorAction SilentlyContinue
            $size = [Math]::Min($d.Size - 2MB, 32GB)
            $p = New-Partition -DiskNumber {{disk}} -Size $size -IsActive -AssignDriveLetter
            Format-Volume -Partition $p -FileSystem FAT32 -NewFileSystemLabel 'DISKVAULT' -Confirm:$false | Out-Null
            (Get-Partition -DiskNumber {{disk}} -PartitionNumber $p.PartitionNumber).DriveLetter
            """;
        var letter = RunPowerShell(ps, log, ct).Trim().Split('\n').Last().Trim();
        if (letter.Length != 1) throw new IOException(T("USB メモリのドライブ文字を取得できません（{0}）。", letter));
        log(T("ファイルをコピーしています（{0}:）…", letter));
        CopyDir(media, letter + ":\\");
        log(T("USB メモリ（{0}:）に書き込みました。", letter));
    }

    static string RunPowerShell(string script, Action<string> log, CancellationToken ct)
    {
        var f = Path.Combine(Path.GetTempPath(), $"dvusb-{Environment.ProcessId}.ps1");
        File.WriteAllText(f, script, new UTF8Encoding(true));
        try { return Run("powershell.exe", $"-NoProfile -ExecutionPolicy Bypass -File \"{f}\"", log, ct, capture: true); }
        finally { File.Delete(f); }
    }

    static string Run(string exe, string args, Action<string> log, CancellationToken ct, bool allowFail = false, bool capture = false)
    {
        var psi = new ProcessStartInfo(exe, args)
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8,
        };
        psi.Environment["DOTNET_CLI_UI_LANGUAGE"] = "ja-JP";
        using var p = Process.Start(psi)!;
        var sb = new StringBuilder();
        p.OutputDataReceived += (_, e) => { if (e.Data != null) lock (sb) sb.AppendLine(e.Data); };
        p.ErrorDataReceived += (_, e) => { if (e.Data != null) lock (sb) sb.AppendLine(e.Data); };
        p.BeginOutputReadLine();
        p.BeginErrorReadLine();
        while (!p.WaitForExit(500))
        {
            if (ct.IsCancellationRequested)
            {
                try { p.Kill(true); } catch (InvalidOperationException) { }
                ct.ThrowIfCancellationRequested();
            }
        }
        p.WaitForExit();
        var output = sb.ToString();
        if (p.ExitCode != 0 && !allowFail)
        {
            var tail = string.Join("\n", output.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0).TakeLast(8));
            throw new IOException(T("{0} が失敗しました（{1}）: {2}", Path.GetFileName(exe), p.ExitCode, tail));
        }
        if (p.ExitCode != 0) log(T("  （{0} 終了コード {1}、続行）", Path.GetFileName(exe), p.ExitCode));
        return output;
    }

    static void CopyDir(string src, string dst, Func<string, bool>? skip = null)
    {
        Directory.CreateDirectory(dst);
        foreach (var d in Directory.EnumerateDirectories(src, "*", SearchOption.AllDirectories))
            Directory.CreateDirectory(Path.Combine(dst, Path.GetRelativePath(src, d)));
        foreach (var f in Directory.EnumerateFiles(src, "*", SearchOption.AllDirectories))
        {
            if (skip?.Invoke(f) == true) continue;
            File.Copy(f, Path.Combine(dst, Path.GetRelativePath(src, f)), true);
        }
    }
}


