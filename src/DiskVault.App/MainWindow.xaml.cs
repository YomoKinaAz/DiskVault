using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using DiskVault.Platform;

namespace DiskVault.App;

public partial class MainWindow : Window
{
    readonly Dictionary<string, UserControl> _pages = [];
    public static MainWindow? Current { get; private set; }

    public MainWindow()
    {
        InitializeComponent();
        Current = this;
        VersionText.Text = $"v{typeof(MainWindow).Assembly.GetName().Version?.ToString(3)}" + (Sys.IsWinPE ? "  (WinPE)" : "");
        // DISKVAULT_DEMO=1: ストア掲載用のスクリーンショット（tests\store_shots.ps1）では管理者でなくても帯を出さない
        AdminBanner.Visibility = Sys.IsAdmin || Environment.GetEnvironmentVariable("DISKVAULT_DEMO") == "1" ? Visibility.Collapsed : Visibility.Visible;
        foreach (ListBoxItem i in Nav.Items)
        {
            var tag = (string)i.Tag;
            bool show = App.Recovery ? tag is "restore" or "pebackup" or "tools" : tag is not ("tools" or "pebackup");
            i.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        }
        if (App.Recovery) Title = "DiskVault 回復環境";
        var start = App.StartPage ?? (App.Recovery ? "restore" : "dash");
        Nav.SelectedItem = Nav.Items.Cast<ListBoxItem>().FirstOrDefault(i => (string)i.Tag == start) ?? Nav.Items[0];
    }

    public void Show(string page)
    {
        Nav.SelectedItem = Nav.Items.Cast<ListBoxItem>().First(i => (string)i.Tag == page);
    }

    public T Page<T>(string tag) where T : UserControl => (T)GetPage(tag);

    UserControl GetPage(string tag)
    {
        if (_pages.TryGetValue(tag, out var p)) return p;
        p = tag switch
        {
            "dash" => new DashboardPage(),
            "jobs" => new JobsPage(),
            "restore" => new RestorePage(),
            "history" => new HistoryPage(),
            "media" => new MediaPage(),
            "settings" => new SettingsPage(),
            "tools" => new ToolsPage(),
            "pebackup" => new PeBackupPage(),
            _ => throw new ArgumentException(tag),
        };
        _pages[tag] = p;
        return p;
    }

    void Nav_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (Nav.SelectedItem is not ListBoxItem item) return;
        var page = GetPage((string)item.Tag);
        PageHost.Content = page;
        if (page is IPage ip) ip.OnShown();
    }

    void RestartAdmin_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Process.Start(new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = true, Verb = "runas" });
            Close();
        }
        catch (System.ComponentModel.Win32Exception) { }
    }
}

public interface IPage
{
    void OnShown();
}

