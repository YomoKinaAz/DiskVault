using System.Diagnostics;
using Microsoft.Win32;

namespace DiskVault.Platform;

/// <summary>回復環境から見た、インストール済み（停止中）の Windows の情報。</summary>
public static class OfflineWindows
{
    /// <summary>
    /// オフラインの SYSTEM ハイブを一時的に読み込んでコンピューター名を返す（失敗したら null）。
    /// 回復環境でのバックアップを、通常のジョブと同じ「保存先\PC 名\…」に置くために使う。
    /// </summary>
    public static string? ComputerName(string letter)
    {
        var hive = $@"{letter.TrimEnd('\\')}\Windows\System32\config\SYSTEM";
        if (!File.Exists(hive)) return null;
        const string key = "DV_OFFLINE_SYS";
        if (!Reg($@"load HKLM\{key} ""{hive}""")) return null;
        try
        {
            using var sel = Registry.LocalMachine.OpenSubKey($@"{key}\Select");
            int cs = sel?.GetValue("Current") is int c ? c : 1;
            using var k = Registry.LocalMachine.OpenSubKey($@"{key}\ControlSet{cs:000}\Control\ComputerName\ComputerName");
            return k?.GetValue("ComputerName") as string;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Security.SecurityException) { return null; }
        finally
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            Reg($@"unload HKLM\{key}");
        }
    }

    static bool Reg(string args)
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo("reg.exe", args) { CreateNoWindow = true, UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true });
            if (p == null) return false;
            p.StandardOutput.ReadToEnd();
            p.StandardError.ReadToEnd();
            p.WaitForExit(15000);
            return p.ExitCode == 0;
        }
        catch (System.ComponentModel.Win32Exception) { return false; }
    }
}
