using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using DiskVault.Platform;

namespace DiskVault.App;

public partial class ToolsPage : UserControl, IPage
{
    public ToolsPage() => InitializeComponent();

    public void OnShown() { }

    async Task RunCapture(string exe, string args)
    {
        Output.Text = $"> {exe} {args}\n";
        var text = await Task.Run(() =>
        {
            try
            {
                var p = Process.Start(new ProcessStartInfo(exe, args) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true })!;
                var o = p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd();
                p.WaitForExit();
                return o;
            }
            catch (System.ComponentModel.Win32Exception e) { return e.Message; }
        });
        Output.AppendText(text);
    }

    async void Ip_Click(object sender, RoutedEventArgs e) => await RunCapture("ipconfig", "");

    async void Driver_Click(object sender, RoutedEventArgs e)
    {
        if (Dialogs.PickOpenFile(Window.GetWindow(this), T("ドライバ（.inf）"), ".inf") is { } f) { HardwareAdapt.RememberLoaded(f); await RunCapture("drvload", $"\"{f}\""); }
    }

    void Cmd_Click(object sender, RoutedEventArgs e) => Process.Start(new ProcessStartInfo("cmd.exe") { UseShellExecute = true });
    void Notepad_Click(object sender, RoutedEventArgs e) => Process.Start(new ProcessStartInfo("notepad.exe") { UseShellExecute = true });

    void Reboot_Click(object sender, RoutedEventArgs e)
    {
        if (MessageBox.Show(T("再起動しますか？"), T("再起動"), MessageBoxButton.YesNo) == MessageBoxResult.Yes)
            Process.Start(new ProcessStartInfo("wpeutil", "reboot") { UseShellExecute = false, CreateNoWindow = true });
    }

    void Shutdown_Click(object sender, RoutedEventArgs e)
    {
        if (MessageBox.Show(T("電源を切りますか？"), T("電源"), MessageBoxButton.YesNo) == MessageBoxResult.Yes)
            Process.Start(new ProcessStartInfo("wpeutil", "shutdown") { UseShellExecute = false, CreateNoWindow = true });
    }
}
