using System.Diagnostics;
using System.ServiceProcess;
using DiskVault.Jobs;

namespace DiskVault.App;

/// <summary>サービスの登録状態と、パイプ経由の問い合わせ。</summary>
static class ServiceCtl
{
    public const string Name = "DiskVault";

    public static string ServiceExe => Path.Combine(AppContext.BaseDirectory, "DiskVaultService.exe");

    /// <summary>サービスが開始に失敗したときに書く記録（DiskVault.Service の Program.CrashLog と同じ場所）。</summary>
    public static string ErrorLog => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "DiskVault", "logs", "service-error.log");

    public static ServiceControllerStatus? Status()
    {
        try
        {
            using var sc = new ServiceController(Name);
            return sc.Status;
        }
        catch (InvalidOperationException) { return null; } // 未登録
    }

    public static string Describe()
    {
        var ping = Ipc.Call(new IpcRequest { Cmd = "ping" }, 500);
        if (ping?.Ok == true) return $"実行中（v{ping.Version}）";
        return Status() switch
        {
            null => "未登録",
            ServiceControllerStatus.Running => "起動中…",
            ServiceControllerStatus.Stopped => File.Exists(ErrorLog) ? $"停止（エラーの記録: {ErrorLog}）" : "停止",
            var s => s.ToString()!,
        };
    }

    public static bool Reachable => Ipc.Call(new IpcRequest { Cmd = "ping" }, 500)?.Ok == true;

    /// <summary>サービスの exe を install / uninstall で実行する（管理者権限が必要）。</summary>
    public static (int Code, string Output) RunServiceExe(string verb)
    {
        if (!File.Exists(ServiceExe)) return (-1, $"{ServiceExe} が見つかりません。");
        var p = Process.Start(new ProcessStartInfo(ServiceExe, verb) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, StandardOutputEncoding = System.Text.Encoding.UTF8 })!;
        var o = p.StandardOutput.ReadToEnd();
        p.WaitForExit();
        return (p.ExitCode, o.Trim());
    }

    public static void Start()
    {
        using var sc = new ServiceController(Name);
        if (sc.Status != ServiceControllerStatus.Running) sc.Start();
        sc.WaitForStatus(ServiceControllerStatus.Running, TimeSpan.FromSeconds(20));
    }

    /// <summary>設定を保存した後にサービスへ再読み込みを頼む（動いていなければ何もしない）。</summary>
    public static void Reload() => Ipc.Call(new IpcRequest { Cmd = "reload" }, 1000);
}
