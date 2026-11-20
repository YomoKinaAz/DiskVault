using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using DiskVault.Jobs;
using DiskVault.Storage;

namespace DiskVault.App;

public sealed class HistoryRow(JobResult r)
{
    public JobResult R { get; } = r;
    public string StartText => R.Start.ToString("yyyy/MM/dd HH:mm:ss");
    public string KindText => R.Kind == PointKind.Full ? "フル" : "増分";
    public string TriggerText => R.Trigger == "schedule" ? "スケジュール" : "手動";
    public string DurationText => Fmt.Duration(R.End - R.Start);
    public string NewText => R.ReadBytes > 0 ? Fmt.Size(R.NewBytes) : "";
    public string StoredText => R.ReadBytes > 0 ? Fmt.Size(R.StoredBytes) : "";
    public string Detail => R.Error ?? (R.Warnings.Count > 0 ? string.Join(" / ", R.Warnings) : R.PointId != null ? $"復元ポイント {R.PointId}" : "");
    public Brush StatusBrush => (Brush)Application.Current.FindResource(R.Status switch { JobStatus.Success => "Ok", JobStatus.Warning => "Warn", _ => "Bad" });
}

public partial class HistoryPage : UserControl, IPage
{
    public HistoryPage() => InitializeComponent();

    public void OnShown() => Load();

    void Load()
    {
        try { Grid.ItemsSource = History.Load(max: 1000).Select(r => new HistoryRow(r)).ToList(); }
        catch (UnauthorizedAccessException) { Grid.ItemsSource = null; LogView.Text = "履歴を読めません（管理者として実行してください）。"; }
    }

    void Refresh_Click(object sender, RoutedEventArgs e) => Load();

    void Grid_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (Grid.SelectedItem is not HistoryRow row) { LogView.Text = ""; return; }
        try
        {
            LogView.Text = row.R.LogFile != null && File.Exists(row.R.LogFile)
                ? File.ReadAllText(row.R.LogFile)
                : "（ログファイルがありません）";
            LogView.ScrollToEnd();
        }
        catch (IOException ex) { LogView.Text = ex.Message; }
    }

    void OpenLog_Click(object sender, RoutedEventArgs e)
    {
        if (Grid.SelectedItem is HistoryRow { R.LogFile: { } f } && File.Exists(f))
            Process.Start(new ProcessStartInfo("notepad.exe", $"\"{f}\"") { UseShellExecute = true });
    }
}

