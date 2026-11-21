using System.Diagnostics;
using Microsoft.Win32;

namespace DiskVault.Platform;

/// <summary>
/// 別の PC（ハードウェアの違う PC）へ復元した Windows を起動できるようにする。復元後、その Windows が止まっている間に行う。
/// 1) 標準のストレージ用ドライバ（AHCI・NVMe・RAID など）を起動時に読み込むようにする（INACCESSIBLE_BOOT_DEVICE 対策）
/// 2) 指定したフォルダ・回復環境で読み込んだドライバを DISM で追加する（Intel VMD/RST など標準にないもの）
/// 3) 休止状態のファイルを消す（前の PC の状態から再開しようとしないように）
/// ブート構成（bcdboot）は BootFix が行う。
/// </summary>
public static class HardwareAdapt
{
    /// <summary>
    /// 起動時に読み込むようにする Windows 標準のストレージ用ドライバ（サービス名）。ハードウェアがなければ読み込まれても何もしない。
    /// Windows にないもの（そのバージョンに含まれないもの）は飛ばす。
    /// </summary>
    public static readonly string[] BootStorageDrivers =
    [
        "storahci", "stornvme", "storufs", "msahci", "pciide", "intelide", "atapi",
        "iaStorAVC", "iaStorV", "iaStorAV", "amdsata", "amdsbs", "amdxata", "nvraid", "nvstor",
        "LSI_SAS", "LSI_SAS2i", "LSI_SAS3i", "LSI_SSS", "megasas", "megasas2i", "megasas35i", "megasr", "MegaSR",
        "percsas2i", "percsas3i", "SmartSAMD", "arcsas", "ItSas35i", "ADP80XX", "HpSAMD", "mvumis", "3ware", "VSTXRAID",
        "storvsc", "vmbus", "stexstor", "SiSRaid2", "SiSRaid4", "aliide", "cmdide", "viaide",
    ];

    /// <summary>復元したディスク（物理ディスク番号）の Windows を調整する。</summary>
    public static List<string> RunOnDisk(int diskNumber, IEnumerable<string>? driverFolders = null, CancellationToken ct = default) =>
        BootFix.FindWindows(diskNumber) is { } w
            ? Run(w, driverFolders, ct)
            : [T("別の PC 向けの調整: 復元したディスクに Windows が見つからないため行いませんでした。")];

    public static List<string> Run(string windowsDrive, IEnumerable<string>? driverFolders = null, CancellationToken ct = default)
    {
        var root = windowsDrive.TrimEnd('\\') + "\\";
        if (string.Equals(Path.GetPathRoot(Environment.SystemDirectory), root, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException(T("実行中の Windows（{0}）は調整できません。復元したディスクの Windows を指定してください。", root));
        var log = new List<string>();
        if (!File.Exists(Path.Combine(root, @"Windows\System32\config\SYSTEM")))
        {
            log.Add(T("別の PC 向けの調整: {0} に Windows が見つからないため行いませんでした。", root));
            return log;
        }

        // 1) ストレージ用ドライバ
        var changed = OfflineWindows.WithSystemHive(root, sys =>
            FixServices(sys, name => File.Exists(Path.Combine(root, @"Windows\System32\drivers", name + ".sys"))));
        if (changed == null) log.Add(T("別の PC 向けの調整: レジストリを読み込めませんでした（管理者権限が必要です）。"));
        else if (changed.Count == 0) log.Add(T("ストレージ用ドライバ: 変更は必要ありませんでした。"));
        else log.Add(T("ストレージ用ドライバを起動時に読み込むようにしました: {0}", string.Join(", ", changed)));

        // 2) ドライバの追加
        var folders = (driverFolders ?? []).Concat(DefaultDriverFolders()).Where(Directory.Exists)
            .Select(Path.GetFullPath).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        foreach (var f in folders)
        {
            ct.ThrowIfCancellationRequested();
            log.Add(AddDrivers(root, f));
        }

        // 3) 休止状態（高速スタートアップを含む）のファイル
        var hib = Path.Combine(root, "hiberfil.sys");
        try
        {
            if (File.Exists(hib))
            {
                File.SetAttributes(hib, FileAttributes.Normal);
                File.Delete(hib);
                log.Add(T("休止状態のファイル（hiberfil.sys）を削除しました。"));
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { log.Add(T("hiberfil.sys を削除できません: {0}", e.Message)); }
        return log;
    }

    /// <summary>
    /// SYSTEM ハイブ（の根）の全コントロールセットで、ストレージ用ドライバの Start を 0（ブート）にし、
    /// 無効化の上書き設定（StartOverride）を消す。変更したサービス名を返す。selftest からは HKCU の作り物で呼ぶ。
    /// </summary>
    public static List<string> FixServices(RegistryKey system, Func<string, bool> driverExists)
    {
        var changed = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var cs in system.GetSubKeyNames().Where(n => n.StartsWith("ControlSet", StringComparison.OrdinalIgnoreCase)))
        {
            using var services = system.OpenSubKey($@"{cs}\Services", writable: true);
            if (services == null) continue;
            foreach (var name in BootStorageDrivers)
            {
                using var k = services.OpenSubKey(name, writable: true);
                if (k == null || !driverExists(name)) continue;
                if (k.GetValue("Type") is int type && type != 1) continue; // カーネルドライバだけ
                bool did = false;
                if (k.GetValue("Start") is not int start || start != 0) { k.SetValue("Start", 0, RegistryValueKind.DWord); did = true; }
                if (k.OpenSubKey("StartOverride") is { } so) { so.Dispose(); k.DeleteSubKeyTree("StartOverride"); did = true; }
                if (did) changed.Add(name);
            }
        }
        return changed.ToList();
    }

    /// <summary>回復メディアに入れたドライバ（\DiskVault\drivers）と、回復環境で読み込んだドライバのフォルダ。</summary>
    public static IEnumerable<string> DefaultDriverFolders()
    {
        var media = Path.Combine(AppContext.BaseDirectory, "drivers");
        if (Directory.Exists(media)) yield return media;
        if (File.Exists(LoadedList))
            foreach (var l in File.ReadAllLines(LoadedList).Where(l => l.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase))
                yield return l;
    }

    static string LoadedList => Path.Combine(Path.GetTempPath(), "diskvault-drvload.txt");

    /// <summary>回復環境で drvload した .inf のフォルダを覚えておく（復元した Windows にも同じドライバを入れるため）。</summary>
    public static void RememberLoaded(string inf)
    {
        try
        {
            if (Path.GetDirectoryName(Path.GetFullPath(inf)) is { } d) File.AppendAllLines(LoadedList, [d]);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }

    static string AddDrivers(string root, string folder)
    {
        var dism = Path.Combine(Environment.SystemDirectory, "dism.exe");
        var scratch = Path.Combine(root, @"Windows\Temp\DiskVault-dism");
        try
        {
            Directory.CreateDirectory(scratch);
            var psi = new ProcessStartInfo(dism, $"/Image:\"{root.TrimEnd('\\')}\\\" /ScratchDir:\"{scratch}\" /Add-Driver /Driver:\"{folder}\" /Recurse /English")
            { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
            using var p = Process.Start(psi)!;
            var err = p.StandardError.ReadToEndAsync();
            var o = p.StandardOutput.ReadToEnd();
            p.WaitForExit();
            return p.ExitCode == 0
                ? T("ドライバを追加しました: {0}", folder)
                : T("ドライバの追加に失敗しました（{0}、終了コード {1}）: {2}", folder, p.ExitCode, LastLines(o + err.Result));
        }
        catch (System.ComponentModel.Win32Exception e) { return T("ドライバの追加に失敗しました（{0}、終了コード {1}）: {2}", folder, -1, e.Message); }
        finally
        {
            try { Directory.Delete(scratch, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    static string LastLines(string s) => string.Join(" ", s.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0).TakeLast(2));
}
