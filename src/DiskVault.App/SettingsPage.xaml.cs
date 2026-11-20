using System.Windows;
using System.Windows.Controls;
using DiskVault.Jobs;

namespace DiskVault.App;

public partial class SettingsPage : UserControl, IPage
{
    public SettingsPage() => InitializeComponent();

    public void OnShown()
    {
        var c = AppConfig.Load();
        var m = c.Mail;
        MailOn.IsChecked = m.Enabled;
        Host.Text = m.Host;
        Port.Text = m.Port.ToString();
        Security.SelectedItem = Security.Items.Cast<ComboBoxItem>().FirstOrDefault(i => (string)i.Tag == m.Security) ?? Security.Items[0];
        User.Text = m.User ?? "";
        try { Pass.Password = Secret.Unprotect(m.PasswordEnc) ?? ""; } catch (UnauthorizedAccessException) { Pass.Password = ""; }
        From.Text = m.From;
        To.Text = string.Join(", ", m.To);
        When.SelectedItem = When.Items.Cast<ComboBoxItem>().FirstOrDefault(i => (string)i.Tag == m.When) ?? When.Items[0];
        Prefix.Text = m.SubjectPrefix;
        KeepDays.Text = c.HistoryKeepDays.ToString();
        DataDir.Text = $"データフォルダ: {AppConfig.Dir}";
        SvcState.Text = ServiceCtl.Describe();
        // Store（MSIX）版ではサービスの登録・解除はパッケージが行う
        if (DiskVault.Platform.Sys.IsPackaged) SvcInstallButton.Visibility = SvcUninstallButton.Visibility = Visibility.Collapsed;
    }

    MailConfig Collect() => new()
    {
        Enabled = MailOn.IsChecked == true,
        Host = Host.Text.Trim(),
        Port = int.TryParse(Port.Text, out var p) && p is > 0 and < 65536 ? p : throw new FormatException("ポート番号が不正です。"),
        Security = (string)((ComboBoxItem)Security.SelectedItem).Tag,
        User = User.Text.Trim().Length > 0 ? User.Text.Trim() : null,
        PasswordEnc = Secret.Protect(Pass.Password),
        From = From.Text.Trim(),
        To = To.Text.Split([',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList(),
        When = (string)((ComboBoxItem)When.SelectedItem).Tag,
        SubjectPrefix = Prefix.Text.Trim(),
    };

    void Save_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var c = AppConfig.Load();
            c.Mail = Collect();
            c.HistoryKeepDays = int.TryParse(KeepDays.Text, out var d) && d is >= 7 and <= 3650 ? d : throw new FormatException("履歴を残す日数は 7〜3650 にしてください。");
            c.Save();
            ServiceCtl.Reload();
            MailStatus.Text = $"保存しました（{DateTime.Now:HH:mm:ss}）";
        }
        catch (FormatException ex) { MessageBox.Show(ex.Message, "入力の確認", MessageBoxButton.OK, MessageBoxImage.Warning); }
        catch (UnauthorizedAccessException) { MessageBox.Show("保存できません。管理者として実行してください。", "保存", MessageBoxButton.OK, MessageBoxImage.Warning); }
    }

    async void Test_Click(object sender, RoutedEventArgs e)
    {
        MailConfig m;
        try { m = Collect(); }
        catch (FormatException ex) { MessageBox.Show(ex.Message); return; }
        TestButton.IsEnabled = false;
        MailStatus.Text = "送信中…";
        var err = await Task.Run(() =>
        {
            try
            {
                Mailer.Send(m, $"テストメール（{Environment.MachineName}）", $"DiskVault からのテストメールです。\n送信日時: {DateTime.Now:yyyy-MM-dd HH:mm:ss}\nPC: {Environment.MachineName}");
                return null;
            }
            catch (Exception ex) { return ex.Message; }
        });
        TestButton.IsEnabled = true;
        MailStatus.Text = err == null ? "送信しました。受信を確認してください。" : "送信できません: " + err;
    }

    async void SvcInstall_Click(object sender, RoutedEventArgs e)
    {
        var (code, output) = await Task.Run(() => ServiceCtl.RunServiceExe("install"));
        MessageBox.Show(output.Length > 0 ? output : $"終了コード {code}", "サービス");
        SvcState.Text = ServiceCtl.Describe();
    }

    async void SvcUninstall_Click(object sender, RoutedEventArgs e)
    {
        if (MessageBox.Show("サービスの登録を解除しますか？スケジュール実行が止まります。", "サービス", MessageBoxButton.YesNo) != MessageBoxResult.Yes) return;
        var (code, output) = await Task.Run(() => ServiceCtl.RunServiceExe("uninstall"));
        MessageBox.Show(output.Length > 0 ? output : $"終了コード {code}", "サービス");
        SvcState.Text = ServiceCtl.Describe();
    }
}
