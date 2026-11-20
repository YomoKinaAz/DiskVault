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
        LanguageBox.SelectedItem = LanguageBox.Items.Cast<ComboBoxItem>().FirstOrDefault(i => (string)i.Tag == c.Language) ?? LanguageBox.Items[0];
        DataDir.Text = T("データフォルダ: {0}", AppConfig.Dir);
        SvcState.Text = ServiceCtl.Describe();
        // Store（MSIX）版ではサービスの登録・解除はパッケージが行う
        if (DiskVault.Platform.Sys.IsPackaged) SvcInstallButton.Visibility = SvcUninstallButton.Visibility = Visibility.Collapsed;
    }

    MailConfig Collect() => new()
    {
        Enabled = MailOn.IsChecked == true,
        Host = Host.Text.Trim(),
        Port = int.TryParse(Port.Text, out var p) && p is > 0 and < 65536 ? p : throw new FormatException(T("ポート番号が不正です。")),
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
            c.HistoryKeepDays = int.TryParse(KeepDays.Text, out var d) && d is >= 7 and <= 3650 ? d : throw new FormatException(T("履歴を残す日数は 7〜3650 にしてください。"));
            c.Language = (string)((ComboBoxItem)LanguageBox.SelectedItem).Tag;
            c.Save();
            ServiceCtl.Reload();
            MailStatus.Text = LangStatus.Text = T("保存しました（{0:HH:mm:ss}）", DateTime.Now);
        }
        catch (FormatException ex) { MessageBox.Show(ex.Message, T("入力の確認"), MessageBoxButton.OK, MessageBoxImage.Warning); }
        catch (UnauthorizedAccessException) { MessageBox.Show(T("保存できません。管理者として実行してください。"), T("保存"), MessageBoxButton.OK, MessageBoxImage.Warning); }
    }

    async void Test_Click(object sender, RoutedEventArgs e)
    {
        MailConfig m;
        try { m = Collect(); }
        catch (FormatException ex) { MessageBox.Show(ex.Message); return; }
        TestButton.IsEnabled = false;
        MailStatus.Text = T("送信中…");
        var err = await Task.Run(() =>
        {
            try
            {
                Mailer.Send(m, T("テストメール（{0}）", Environment.MachineName), T("DiskVault からのテストメールです。\n送信日時: {0:yyyy-MM-dd HH:mm:ss}\nPC: {1}", DateTime.Now, Environment.MachineName));
                return null;
            }
            catch (Exception ex) { return ex.Message; }
        });
        TestButton.IsEnabled = true;
        MailStatus.Text = err == null ? T("送信しました。受信を確認してください。") : T("送信できません: ") + err;
    }

    async void SvcInstall_Click(object sender, RoutedEventArgs e)
    {
        var (code, output) = await Task.Run(() => ServiceCtl.RunServiceExe("install"));
        MessageBox.Show(output.Length > 0 ? output : T("終了コード {0}", code), T("サービス"));
        SvcState.Text = ServiceCtl.Describe();
    }

    async void SvcUninstall_Click(object sender, RoutedEventArgs e)
    {
        if (MessageBox.Show(T("サービスの登録を解除しますか？スケジュール実行が止まります。"), T("サービス"), MessageBoxButton.YesNo) != MessageBoxResult.Yes) return;
        var (code, output) = await Task.Run(() => ServiceCtl.RunServiceExe("uninstall"));
        MessageBox.Show(output.Length > 0 ? output : T("終了コード {0}", code), T("サービス"));
        SvcState.Text = ServiceCtl.Describe();
    }
}
