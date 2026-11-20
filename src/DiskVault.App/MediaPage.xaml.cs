using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using DiskVault.Platform;

namespace DiskVault.App;

public partial class MediaPage : UserControl, IPage
{
    CancellationTokenSource? _cts;

    public MediaPage()
    {
        InitializeComponent();
        IsoPath.Text = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Desktop), "DiskVault-Recovery.iso");
    }

    public void OnShown()
    {
        var missing = MediaBuilder.CheckAdk();
        AdkState.Text = missing ?? "インストール済み";
        AdkState.Foreground = (System.Windows.Media.Brush)FindResource(missing == null ? "Ok" : "Bad");
        AdkButton.Visibility = missing == null ? Visibility.Collapsed : Visibility.Visible;
        BuildButton.IsEnabled = missing == null && _cts == null;
        var installed = MediaBuilder.InstalledAdk();
        if (installed.Count > 0 && missing != null) AdkState.Text += "（一部だけ入っています）";
        UninstallAdkButton.Visibility = installed.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        UninstallAdkButton.IsEnabled = _cts == null;
        _ = RefreshUsb();
    }

    async void UninstallAdk_Click(object sender, RoutedEventArgs e)
    {
        var list = MediaBuilder.InstalledAdk();
        if (list.Count == 0) { OnShown(); return; }
        var names = string.Join("\n", list.Select(p => $"・{p.Name} {p.Version}"));
        if (MessageBox.Show($"次をアンインストールします。\n\n{names}\n\n回復メディアを作り直すときは、もう一度インストールが必要です（数 GB のダウンロード）。作成済みの ISO / USB メモリはそのまま使えます。続けますか？",
                "Windows ADK のアンインストール", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        _cts = new CancellationTokenSource();
        BuildButton.IsEnabled = AdkButton.IsEnabled = UninstallAdkButton.IsEnabled = false;
        Log.Clear();
        BuildState.Text = "ADK をアンインストールしています…（数分かかります）";
        void L(string m) => Dispatcher.Invoke(() => { Log.AppendText($"{DateTime.Now:HH:mm:ss} {m}\n"); Log.ScrollToEnd(); });
        try
        {
            await Task.Run(() => MediaBuilder.UninstallAdk(L, _cts.Token));
            BuildState.Text = "ADK をアンインストールしました。";
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        {
            BuildState.Text = "アンインストールに失敗しました。";
            L("エラー: " + ex.Message);
            MessageBox.Show(ex.Message, "Windows ADK", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            _cts = null;
            AdkButton.IsEnabled = true;
            OnShown();
        }
    }

    async Task RefreshUsb()
    {
        var list = await Task.Run(() =>
        {
            try { return DiskLayout.ListDisks().Where(d => !d.HasSystem && d.Size is > 900_000_000 and < 300_000_000_000).ToList(); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return []; }
        });
        UsbBox.Items.Clear();
        foreach (var d in list)
            UsbBox.Items.Add(new ComboBoxItem { Content = $"ディスク {d.Number}  {d.Model}  {Fmt.Size(d.Size)}  {d.Volumes}", Tag = d.Number });
        if (UsbBox.Items.Count > 0) UsbBox.SelectedIndex = 0;
    }

    void RefreshUsb_Click(object sender, RoutedEventArgs e) => _ = RefreshUsb();

    void BrowseIso_Click(object sender, RoutedEventArgs e)
    {
        if (Dialogs.PickSaveFile(Window.GetWindow(this), "ISO の保存先", ".iso", IsoPath.Text) is { } p) IsoPath.Text = p;
        IsoRadio.IsChecked = true;
    }

    void InstallAdk_Click(object sender, RoutedEventArgs e)
    {
        if (MessageBox.Show("winget で Windows ADK（展開ツール）と WinPE アドオンをインストールします。数 GB のダウンロードがあります。続けますか？",
                "Windows ADK", MessageBoxButton.YesNo) != MessageBoxResult.Yes) return;
        const string ver = "10.1.26100.9457";
        var cmd = $"winget install --id Microsoft.WindowsADK --version {ver} --accept-package-agreements --accept-source-agreements --override \"/quiet /norestart /features OptionId.DeploymentTools\" && " +
                  $"winget install --id Microsoft.WindowsADK.WinPEAddon --version {ver} --accept-package-agreements --accept-source-agreements --override \"/quiet /norestart /features OptionId.WindowsPreinstallationEnvironment\" & pause";
        Process.Start(new ProcessStartInfo("cmd.exe", $"/c {cmd}") { UseShellExecute = true });
    }

    async void Build_Click(object sender, RoutedEventArgs e)
    {
        var o = new MediaOptions { DriversFromThisPC = DriversBox.IsChecked == true, Japanese = JapaneseBox.IsChecked == true };
        if (!File.Exists(Path.Combine(o.AppDir, "coreclr.dll")))
        {
            MessageBox.Show("この DiskVault は回復環境に入れられる形（自己完結）で配置されていません。配布用の dist フォルダから実行してください。", "回復メディア");
            return;
        }
        if (IsoRadio.IsChecked == true) o.IsoPath = IsoPath.Text.Trim();
        else if (UsbBox.SelectedItem is ComboBoxItem { Tag: int n })
        {
            if (MessageBox.Show($"{((ComboBoxItem)UsbBox.SelectedItem).Content} の内容はすべて消去されます。続けますか？", "USB メモリ",
                    MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
            o.UsbDisk = n;
        }
        else { MessageBox.Show("出力先を選んでください。"); return; }

        _cts = new CancellationTokenSource();
        BuildButton.IsEnabled = false;
        CancelButton.IsEnabled = true;
        Log.Clear();
        BuildState.Text = "作成中…（10〜20 分かかります）";
        void L(string m) => Dispatcher.Invoke(() => { Log.AppendText($"{DateTime.Now:HH:mm:ss} {m}\n"); Log.ScrollToEnd(); });
        try
        {
            await Task.Run(() => MediaBuilder.Build(o, L, _cts.Token));
            BuildState.Text = "完了しました。";
            MessageBox.Show(o.IsoPath != null ? $"回復メディアの ISO を作成しました。\n{o.IsoPath}" : "USB メモリに回復メディアを書き込みました。", "回復メディア",
                MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (OperationCanceledException) { BuildState.Text = "中止しました。"; }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or UnauthorizedAccessException or ArgumentException)
        {
            BuildState.Text = "失敗しました。";
            L("エラー: " + ex.Message);
            MessageBox.Show(ex.Message, "回復メディア", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            _cts = null;
            BuildButton.IsEnabled = true;
            CancelButton.IsEnabled = false;
        }
    }

    void Cancel_Click(object sender, RoutedEventArgs e) => _cts?.Cancel();
}
