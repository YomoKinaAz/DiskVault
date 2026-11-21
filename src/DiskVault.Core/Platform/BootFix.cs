using System.Diagnostics;
using System.Runtime.InteropServices;
using DiskVault.Storage;

namespace DiskVault.Platform;

/// <summary>
/// ベアメタル復元の後にブート構成を作り直す（bcdboot）。パーティション GUID ごと復元しているので通常は不要だが、
/// 別のディスクへ復元したときや BCD が壊れていたときの保険として WinPE から実行する。
/// </summary>
public static partial class BootFix
{
    public static List<string> Run(int diskNumber, DiskRecord disk)
    {
        var log = new List<string>();
        var (win, winTemp, vols) = WaitWindows(diskNumber);
        if (win == null)
        {
            log.Add(T("ブート構成: 復元したディスクに Windows フォルダが見つからないため、bcdboot は実行しませんでした。"));
            return log;
        }
        try { RunBcdboot(win, vols, disk, log); }
        finally { ReleaseLetter(winTemp); }
        return log;
    }

    static void RunBcdboot(string win, List<VolumeInfo> vols, DiskRecord disk, List<string> log)
    {
        bool uefi = disk.Style == "GPT";
        var bootPart = uefi
            ? disk.Partitions.FirstOrDefault(p => string.Equals(p.TypeGuid, PartitionRecord.EspType, StringComparison.OrdinalIgnoreCase))
            : disk.Partitions.FirstOrDefault(p => p.Active);
        var bootVol = bootPart == null ? null : vols.FirstOrDefault(v => v.DiskOffset == bootPart.Offset);
        if (bootVol == null)
        {
            log.Add(T("ブート構成: ブートパーティションが見つからないため、bcdboot は実行しませんでした。"));
            return;
        }
        string? tempLetter = null;
        var sysLetter = bootVol.Letter;
        if (sysLetter == null)
        {
            tempLetter = FreeLetter() ?? throw new IOException(T("空いているドライブ文字がありません。"));
            if (!SetVolumeMountPointW(tempLetter + "\\", bootVol.GuidPath))
                throw new IOException(T("ブートパーティションにドライブ文字を割り当てられません（{0}）。", Marshal.GetLastPInvokeError()));
            sysLetter = tempLetter;
        }
        try
        {
            var args = $"{win}\\Windows /s {sysLetter} /f {(uefi ? "UEFI" : "BIOS")}";
            var psi = new ProcessStartInfo("bcdboot.exe", args) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
            using var p = Process.Start(psi)!;
            var o = p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd();
            p.WaitForExit();
            log.Add(p.ExitCode == 0 ? T("ブート構成を作り直しました（bcdboot {0}）。", args) : T("bcdboot が失敗しました（{0}）: {1}", p.ExitCode, o.Trim()));
        }
        finally
        {
            if (tempLetter != null) DeleteVolumeMountPointW(tempLetter + "\\");
        }
    }

    /// <summary>
    /// 復元したディスクの Windows のドライブ。新しいパーティションが現れるまで最大 20 秒待つ。
    /// ドライブ文字がなければ一時的に割り当てる（Temp。使い終わったら ReleaseLetter で外す）。
    /// </summary>
    static (string? Letter, string? Temp, List<VolumeInfo> Vols) WaitWindows(int diskNumber)
    {
        List<VolumeInfo> vols = [];
        for (int i = 0; i < 20; i++)
        {
            vols = VolumeInfo.Enumerate().Where(v => v.Disk == diskNumber).ToList();
            var win = vols.FirstOrDefault(v => v.Letter != null && Directory.Exists(Path.Combine(v.Letter + "\\", "Windows", "System32")));
            if (win != null) return (win.Letter, null, vols);
            if (i >= 3)
            {
                var g = vols.FirstOrDefault(v => v.Letter == null && !string.IsNullOrEmpty(v.GuidPath) && Directory.Exists(Path.Combine(v.GuidPath, "Windows", "System32")));
                if (g != null && FreeLetter() is { } t && SetVolumeMountPointW(t + "\\", g.GuidPath)) return (t, t, vols);
            }
            Thread.Sleep(1000);
        }
        return (null, null, vols);
    }

    /// <summary>復元したディスクの Windows のドライブ（"E:"）。Temp が null でなければ一時的に割り当てた文字（ReleaseLetter で外す）。</summary>
    public static (string? Letter, string? Temp) FindWindows(int diskNumber)
    {
        var (l, t, _) = WaitWindows(diskNumber);
        return (l, t);
    }

    public static void ReleaseLetter(string? temp)
    {
        if (temp != null) DeleteVolumeMountPointW(temp + "\\");
    }

    static string? FreeLetter()
    {
        var used = DriveInfo.GetDrives().Select(d => char.ToUpperInvariant(d.Name[0])).ToHashSet();
        for (char c = 'S'; c <= 'Z'; c++) if (!used.Contains(c)) return c + ":";
        return null;
    }

    [LibraryImport("kernel32.dll", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetVolumeMountPointW(string mountPoint, string volumeName);

    [LibraryImport("kernel32.dll", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool DeleteVolumeMountPointW(string mountPoint);
}
