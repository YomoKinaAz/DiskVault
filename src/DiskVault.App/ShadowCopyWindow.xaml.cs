using System.Windows;
using System.Windows.Controls;
using DiskVault.Jobs;
using DiskVault.Platform;
using DiskVault.Storage;

namespace DiskVault.App;

/// <summary>既存のシャドウコピーを選んで、その時点の内容でジョブを 1 回実行する。</summary>
public partial class ShadowCopyWindow : Window
{
    sealed record Row(ShadowCopyInfo Info)
    {
        public string When => $"{Info.Created:yyyy/MM/dd HH:mm:ss}";
        public string Volume => Info.VolumeText;
        public string RestorePoint => Info.RestorePoint ?? "";
        public string Id => Info.Id;
    }

    readonly string _jobId;

    /// <summary>選んだシャドウコピーの ID（ボリュームごとに 1 つ）。</summary>
    public List<string> Selected { get; } = [];
    public PointKind Kind => FullRadio.IsChecked == true ? PointKind.Full : PointKind.Incremental;

    public ShadowCopyWindow(string jobId, string jobName)
    {
        InitializeComponent();
        _jobId = jobId;
        Heading.Text = T("ジョブ「{0}」", jobName);
        Loaded += async (_, _) => await Load();
    }

    async Task Load()
    {
        Status.Text = T("シャドウコピーを調べています…");
        try
        {
            var job = AppConfig.Load().Job(_jobId) ?? throw new InvalidOperationException(T("ジョブ {0} がありません。", _jobId));
            var (rows, outOfScope) = await Task.Run(() =>
            {
                var all = ShadowCopies.List();
                var vols = VolumeInfo.Enumerate();
                var inScope = all.Where(s => InScope(job, vols.FirstOrDefault(v => Same(v.GuidPath, s.VolumeName)), vols)).ToList();
                return (inScope.Select(s => new Row(s)).ToList(), all.Count - inScope.Count);
            });
            Grid.ItemsSource = rows;
            Status.Text = rows.Count == 0
                ? T("このジョブの範囲のボリュームには、シャドウコピーがありません。システムの復元（システムの保護）を有効にすると、更新のたびに作られます。")
                : T("{0} 個のシャドウコピーがあります（ボリュームごとに 1 つ選べます。Ctrl キーで複数選択）。", rows.Count) +
                  (outOfScope > 0 ? " " + T("ほかに範囲外のボリュームのものが {0} 個あります。", outOfScope) : "");
        }
        catch (Exception e) when (e is UnauthorizedAccessException or IOException or InvalidOperationException)
        {
            Status.Text = e.Message;
        }
    }

    static bool Same(string a, string b) => string.Equals(a.TrimEnd('\\'), b.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase);

    /// <summary>そのボリュームがジョブのバックアップ範囲に入っているか。</summary>
    static bool InScope(JobConfig job, VolumeInfo? v, List<VolumeInfo> vols)
    {
        if (v == null) return false;
        foreach (var s in job.Sources)
        {
            if (s.Equals("system", StringComparison.OrdinalIgnoreCase))
            {
                if (vols.FirstOrDefault(x => x.IsSystem)?.Disk is int sd && v.Disk == sd) return true;
            }
            else if (s.StartsWith("physical:", StringComparison.OrdinalIgnoreCase))
            {
                if (int.TryParse(s[9..], out var n) && v.Disk == n) return true;
            }
            else if (v.Letter != null && string.Equals(s.TrimEnd('\\'), v.Letter, StringComparison.OrdinalIgnoreCase)) return true;
            else if (Same(s, v.GuidPath)) return true;
        }
        return false;
    }

    void Grid_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        var sel = Grid.SelectedItems.Cast<Row>().ToList();
        var dup = sel.GroupBy(r => r.Info.VolumeName, StringComparer.OrdinalIgnoreCase).FirstOrDefault(g => g.Count() > 1);
        RunButton.IsEnabled = sel.Count > 0 && dup == null;
        if (dup != null) Status.Text = T("同じボリューム（{0}）のシャドウコピーは 1 つだけ選んでください。", dup.First().Volume);
        else if (sel.Count > 0) Status.Text = T("{0} の時点の内容でバックアップします。", string.Join(T("、"), sel.Select(r => $"{r.Volume} {r.Info.Created:yyyy/MM/dd HH:mm}")));
    }

    void Grid_MouseDoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (RunButton.IsEnabled) Run_Click(sender, e);
    }

    void Run_Click(object sender, RoutedEventArgs e)
    {
        Selected.Clear();
        Selected.AddRange(Grid.SelectedItems.Cast<Row>().Select(r => r.Info.Id));
        if (Selected.Count == 0) return;
        DialogResult = true;
    }
}
