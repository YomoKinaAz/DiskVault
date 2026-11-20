using System.Diagnostics;
using System.Windows;
using DiskVault.Platform;

namespace DiskVault.App;

public partial class App : Application
{
    public static bool NoElevate { get; private set; }
    public static string? StartPage { get; private set; }
    /// <summary>回復環境（WinPE）用の画面：復元とツールだけを出す。</summary>
    public static bool Recovery { get; private set; }

    void OnStartup(object sender, StartupEventArgs e)
    {
        var args = e.Args.ToList();
        NoElevate = args.Contains("--no-elevate");
        Recovery = args.Contains("--recovery") || Sys.IsWinPE;
        int pi = args.IndexOf("--page");
        if (pi >= 0 && pi + 1 < args.Count) StartPage = args[pi + 1];
        if (!Sys.IsAdmin && !NoElevate)
        {
            try
            {
                Process.Start(new ProcessStartInfo(Environment.ProcessPath!, string.Join(" ", e.Args.Select(a => $"\"{a}\""))) { UseShellExecute = true, Verb = "runas" });
                Shutdown();
                return;
            }
            catch (System.ComponentModel.Win32Exception)
            {
                // UAC を断られた：権限なしで続ける（バナーで案内）
            }
        }
        if (Sys.IsAdmin) Jobs.AppConfig.SecureDir();
        DispatcherUnhandledException += (_, ex) =>
        {
            MessageBox.Show(ex.Exception.Message, "DiskVault - エラー", MessageBoxButton.OK, MessageBoxImage.Error);
            ex.Handled = true;
        };
        new MainWindow().Show();
    }
}

