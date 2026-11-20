using System.Diagnostics;
using System.ServiceProcess;
using System.Text;
using DiskVault.Jobs;

namespace DiskVault.Service;

/// <summary>
/// DiskVault のバックグラウンドサービス（LocalSystem）。スケジュール実行と GUI からの指示を受ける。
///   DiskVaultService.exe            … サービスとして起動（SCM から）
///   DiskVaultService.exe --console  … コンソールで動かす（テスト用）
///   DiskVaultService.exe install / uninstall
/// </summary>
static class Program
{
    public const string ServiceName = "DiskVault";
    public static string Version => typeof(Program).Assembly.GetName().Version?.ToString(3) ?? "0";

    /// <summary>起動に失敗したときの記録先（AppConfig を通さず、ここだけは確実に書ける場所に）。</summary>
    public static string CrashLog => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "DiskVault", "logs", "service-error.log");

    public static void WriteCrash(string what, Exception? e)
    {
        var text = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {what} {Version}\r\n{e}\r\n";
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(CrashLog)!);
            File.AppendAllText(CrashLog, text, Encoding.UTF8);
        }
        catch (Exception) { }
        // イベントログ（Application、ソース "Application"）にも残す。ソース登録は不要
        try { EventLog.WriteEntry("Application", T("DiskVault サービス: {0}", text), EventLogEntryType.Error, 1000); }
        catch (Exception) { }
    }

    static int Main(string[] args)
    {
        AppDomain.CurrentDomain.UnhandledException += (_, e) => WriteCrash(T("サービスが異常終了しました"), e.ExceptionObject as Exception);
        // --home <フォルダ>: データフォルダを変える（MSIX のテスト用パッケージで本番の設定・ジョブを使わないため）
        if (args.Length >= 2 && args[0] == "--home")
        {
            Environment.SetEnvironmentVariable("DISKVAULT_HOME", args[1]);
            args = args[2..];
        }
        var cmd = args.FirstOrDefault() ?? "";
        if (cmd.Length == 0)
        {
            // SCM には 30 秒以内に応答しないと「開始できない」（MSI ではエラー 1920）になるため、
            // 余計なことをせずにまず ServiceBase.Run に入る
            DiskVault.Platform.Sys.InitComSecurity();
            AppConfig.ApplyLanguage();
            ServiceBase.Run(new Svc());
            return 0;
        }
        Console.OutputEncoding = Encoding.UTF8;
        DiskVault.Platform.Sys.InitComSecurity();
        AppConfig.ApplyLanguage();
        switch (cmd)
        {
            case "--console":
                return RunConsole();
            case "install":
                return Install();
            case "uninstall":
                return Uninstall();
            default:
                Console.WriteLine(T("不明な引数です: {0}（--console / install / uninstall）", cmd));
                return 2;
        }
    }

    static int RunConsole()
    {
        using var host = new Host(Console.WriteLine);
        host.Start();
        Console.WriteLine(T("DiskVault サービス {0} をコンソールで実行中（パイプ {1}、データ {2}）。Ctrl+C で終了。", Version, Ipc.PipeName, AppConfig.Dir));
        var done = new ManualResetEventSlim();
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; done.Set(); };
        // 標準入力が閉じられたら終了（テストから止めやすくする）
        if (Console.IsInputRedirected) _ = Task.Run(() => { Console.In.ReadToEnd(); done.Set(); });
        done.Wait();
        host.Stop();
        return 0;
    }

    static int Sc(string args)
    {
        var p = Process.Start(new ProcessStartInfo("sc.exe", args) { UseShellExecute = false, RedirectStandardOutput = true })!;
        var o = p.StandardOutput.ReadToEnd();
        p.WaitForExit();
        if (p.ExitCode != 0) Console.WriteLine(o.Trim());
        return p.ExitCode;
    }

    static int Install()
    {
        if (DiskVault.Platform.Sys.IsPackaged) { Console.WriteLine(T("Microsoft Store 版では、サービスはアプリと一緒に自動で登録されます。")); return 0; }
        var exe = Environment.ProcessPath!;
        int rc = Sc($"create {ServiceName} binPath= \"\\\"{exe}\\\"\" start= delayed-auto obj= LocalSystem DisplayName= \"{T("DiskVault イメージバックアップ")}\"");
        if (rc != 0 && rc != 1073) return rc; // 1073 = 既に存在
        if (rc == 1073) Sc($"config {ServiceName} binPath= \"\\\"{exe}\\\"\"");
        Sc($"description {ServiceName} \"{T("ディスクイメージのバックアップをスケジュール実行します。")}\"");
        Sc($"failure {ServiceName} reset= 86400 actions= restart/60000/restart/60000/restart/300000");
        rc = Sc($"start {ServiceName}");
        Console.WriteLine(rc == 0 || rc == 1056 ? T("サービスを登録して開始しました。") : T("サービスを登録しましたが開始できませんでした（{0}）。", rc));
        return 0;
    }

    static int Uninstall()
    {
        if (DiskVault.Platform.Sys.IsPackaged) { Console.WriteLine(T("Microsoft Store 版では、アプリをアンインストールするとサービスも削除されます。")); return 0; }
        Sc($"stop {ServiceName}");
        Thread.Sleep(2000);
        return Sc($"delete {ServiceName}");
    }
}

/// <summary>スケジューラーとパイプサーバーをまとめたもの。</summary>
sealed class Host(Action<string> log) : IDisposable
{
    readonly Scheduler _s = new() { Log = m => log($"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {m}") };
    readonly CancellationTokenSource _cts = new();
    Task? _pipe;

    public void Start()
    {
        _s.Start();
        _pipe = Task.Run(() => Ipc.Serve(_s, _cts.Token, Program.Version));
    }

    public void Stop()
    {
        _cts.Cancel();
        _s.Stop();
        try { _pipe?.Wait(5000); } catch (AggregateException) { }
    }

    public void Dispose() => Stop();
}

sealed class Svc : ServiceBase
{
    Host? _host;
    StreamWriter? _log;

    public Svc()
    {
        ServiceName = Program.ServiceName;
        CanStop = true;
        CanShutdown = true;
    }

    Thread? _init;

    /// <summary>
    /// OnStart はすぐ返す（返すまで SCM は「開始中」のままで、時間がかかるとタイムアウトになる）。
    /// ACL 設定・ログ・前回のスナップショットの後始末などは別スレッドで行い、失敗はログに残して続ける。
    /// </summary>
    protected override void OnStart(string[] args)
    {
        _init = new Thread(Init) { IsBackground = true, Name = "DiskVault.Init" };
        _init.Start();
    }

    void Init()
    {
        try { AppConfig.SecureDir(); }
        catch (Exception e) { Program.WriteCrash(T("データフォルダのアクセス権を設定できませんでした（続行します）"), e); }
        try
        {
            var dir = Path.Combine(AppConfig.Dir, "logs");
            Directory.CreateDirectory(dir);
            // 前のプロセスがまだ掴んでいても開けるよう共有で開く
            var fs = new FileStream(Path.Combine(dir, "service.log"), FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
            _log = new StreamWriter(fs, Encoding.UTF8) { AutoFlush = true };
        }
        catch (Exception e) { Program.WriteCrash(T("service.log を開けませんでした（続行します）"), e); }
        void Log(string m) { var w = _log; if (w == null) return; lock (w) w.WriteLine(m); }
        Log($"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {T("サービス開始 {0}", Program.Version)}");
        try
        {
            var h = new Host(Log);
            h.Start();
            _host = h;
        }
        catch (Exception e)
        {
            Program.WriteCrash(T("サービスを開始できませんでした"), e);
            Log($"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {T("開始に失敗: {0}", e.Message)}");
            ExitCode = 1064; // ERROR_EXCEPTION_IN_SERVICE
            Stop();
        }
    }

    protected override void OnStop()
    {
        RequestAdditionalTime(60_000);
        _init?.Join(TimeSpan.FromSeconds(30));
        _host?.Stop();
        _log?.WriteLine($"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {T("サービス停止")}");
        _log?.Dispose();
    }

    protected override void OnShutdown() => OnStop();
}

