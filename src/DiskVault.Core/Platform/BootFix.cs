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
        // 新しいパーティションのボリュームが現れるのを待つ
        List<VolumeInfo> vols = [];
        for (int i = 0; i < 20; i++)
        {
            vols = VolumeInfo.Enumerate().Where(v => v.Disk == diskNumber).ToList();
            if (vols.Any(v => v.Letter != null && Directory.Exists(Path.Combine(v.Letter + "\\", "Windows", "System32")))) break;
            Thread.Sleep(1000);
        }
        var win = vols.FirstOrDefault(v => v.Letter != null && Directory.Exists(Path.Combine(v.Letter + "\\", "Windows", "System32")));
        if (win == null)
        {
            log.Add("ブート構成: 復元したディスクに Windows フォルダが見つからないため、bcdboot は実行しませんでした。");
            return log;
        }
        bool uefi = disk.Style == "GPT";
        var bootPart = uefi
            ? disk.Partitions.FirstOrDefault(p => string.Equals(p.TypeGuid, PartitionRecord.EspType, StringComparison.OrdinalIgnoreCase))
            : disk.Partitions.FirstOrDefault(p => p.Active);
        var bootVol = bootPart == null ? null : vols.FirstOrDefault(v => v.DiskOffset == bootPart.Offset);
        if (bootVol == null)
        {
            log.Add("ブート構成: ブートパーティションが見つからないため、bcdboot は実行しませんでした。");
            return log;
        }
        string? tempLetter = null;
        var sysLetter = bootVol.Letter;
        if (sysLetter == null)
        {
            tempLetter = FreeLetter() ?? throw new IOException("空いているドライブ文字がありません。");
            if (!SetVolumeMountPointW(tempLetter + "\\", bootVol.GuidPath))
                throw new IOException($"ブートパーティションにドライブ文字を割り当てられません（{Marshal.GetLastPInvokeError()}）。");
            sysLetter = tempLetter;
        }
        try
        {
            var args = $"{win.Letter}\\Windows /s {sysLetter} /f {(uefi ? "UEFI" : "BIOS")}";
            var psi = new ProcessStartInfo("bcdboot.exe", args) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
            using var p = Process.Start(psi)!;
            var o = p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd();
            p.WaitForExit();
            log.Add(p.ExitCode == 0 ? $"ブート構成を作り直しました（bcdboot {args}）。" : $"bcdboot が失敗しました（{p.ExitCode}）: {o.Trim()}");
        }
        finally
        {
            if (tempLetter != null) DeleteVolumeMountPointW(tempLetter + "\\");
        }
        return log;
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
