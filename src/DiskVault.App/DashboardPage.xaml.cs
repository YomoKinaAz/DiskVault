using System.ServiceProcess;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using DiskVault.Jobs;
using DiskVault.Platform;
using DiskVault.Storage;

namespace DiskVault.App;

public partial class DashboardPage : UserControl, IPage
{
    readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(1) };
    readonly Dictionary<string, JobCard> _cards = [];
    bool _busy;

    public DashboardPage()
    {
        InitializeComponent();
        _timer.Tick += async (_, _) => await Refresh();
        Loaded += (_, _) => _timer.Start();
        Unloaded += (_, _) => _timer.Stop();
    }

    public void OnShown() => _ = Refresh();

    async Task Refresh()
    {
        if (_busy) return;
        _busy = true;
        try
        {
            var (resp, svc, status) = await Task.Run(() =>
            {
                var r = Ipc.Call(new IpcRequest { Cmd = "status" }, 800);
                return (r, r?.Ok == true ? $"実行中（v{r.Version}）" : ServiceCtl.Describe(), ServiceCtl.Status());
            });
            SvcText.Text = svc;
            List<JobState> jobs;
            bool online = resp?.Ok == true && resp.Jobs != null;
            if (online)
            {
                jobs = resp!.Jobs!;
                SvcBanner.Visibility = Visibility.Collapsed;
                SvcButton.Visibility = Visibility.Collapsed;
                SvcText.Foreground = (Brush)FindResource("Ok");
            }
            else
            {
                jobs = await Task.Run(OfflineStatus);
                SvcText.Foreground = (Brush)FindResource("Bad");
                SvcBanner.Visibility = Visibility.Visible;
                SvcButton.Visibility = Sys.IsAdmin ? Visibility.Visible : Visibility.Collapsed;
                if (status == null && Sys.IsPackaged)
                {
                    SvcBannerText.Text = "バックアップ サービスが見つかりません。Microsoft Store から DiskVault を修復（または再インストール）してください。";
                    SvcButton.Visibility = Visibility.Collapsed;
                }
                else if (status == null)
                {
                    SvcBannerText.Text = "バックアップ サービスが登録されていません。スケジュール実行と「今すぐ実行」にはサービスが必要です。";
                    SvcButton.Content = "サービスを登録";
                }
                else
                {
                    SvcBannerText.Text = "バックアップ サービスが停止しています。";
                    SvcButton.Content = "サービスを開始";
                }
            }
            Empty.Visibility = jobs.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            if (!jobs.Select(j => j.Id).SequenceEqual(_cards.Keys))
            {
                Cards.Children.Clear();
                _cards.Clear();
                foreach (var j in jobs)
                {
                    var c = new JobCard(j.Id);
                    _cards[j.Id] = c;
                    Cards.Children.Add(c.Root);
                }
            }
            foreach (var j in jobs) _cards[j.Id].Update(j, online);
        }
        finally { _busy = false; }
    }

    static List<JobState> OfflineStatus()
    {
        var cfg = AppConfig.Load();
        var hist = History.Load(max: 300);
        return cfg.Jobs.Select(j =>
        {
            var next = j.Enabled ? j.Schedule.Next(DateTime.Now) : null;
            return new JobState
            {
                Id = j.Id, Name = j.Name, Enabled = j.Enabled, Schedule = j.Schedule.Describe(),
                Last = hist.FirstOrDefault(h => h.JobId == j.Id), Next = next?.When, NextKind = next?.Kind,
            };
        }).ToList();
    }

    async void Svc_Click(object sender, RoutedEventArgs e)
    {
        SvcButton.IsEnabled = false;
        try
        {
            if (ServiceCtl.Status() == null)
            {
                var (code, output) = await Task.Run(() => ServiceCtl.RunServiceExe("install"));
                if (code != 0) MessageBox.Show(output, "サービスの登録", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
            else await Task.Run(ServiceCtl.Start);
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ServiceProcess.TimeoutException or System.ComponentModel.Win32Exception)
        {
            MessageBox.Show(ex.Message, "サービス", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally { SvcButton.IsEnabled = true; }
        await Refresh();
    }

    void NewJob_Click(object sender, RoutedEventArgs e)
    {
        MainWindow.Current?.Show("jobs");
        MainWindow.Current?.Page<JobsPage>("jobs").NewJob();
    }

    /// <summary>ジョブ 1 件分のカード。</summary>
    sealed class JobCard
    {
        public Border Root { get; }
        readonly string _id;
        readonly TextBlock _name = new() { FontSize = 16, FontWeight = FontWeights.SemiBold };
        readonly TextBlock _sched = new() { Foreground = Brushes.DimGray, Margin = new Thickness(0, 2, 0, 8), TextWrapping = TextWrapping.Wrap };
        readonly TextBlock _lastStatus = new() { FontWeight = FontWeights.SemiBold, FontSize = 14 };
        readonly TextBlock _lastDetail = new() { Foreground = Brushes.DimGray, TextWrapping = TextWrapping.Wrap };
        readonly TextBlock _next = new() { FontSize = 14 };
        readonly ProgressBar _bar = new() { Height = 8, Margin = new Thickness(0, 8, 0, 4), Maximum = 1000 };
        readonly TextBlock _prog = new() { Foreground = Brushes.DimGray, TextTrimming = TextTrimming.CharacterEllipsis };
        readonly StackPanel _progPanel = new() { Visibility = Visibility.Collapsed };
        readonly Button _inc = new() { Content = "今すぐ増分" };
        readonly Button _full = new() { Content = "今すぐフル" };
        readonly Button _cancel = new() { Content = "中止", Visibility = Visibility.Collapsed };

        public JobCard(string id)
        {
            _id = id;
            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(220) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var left = new StackPanel();
            left.Children.Add(_name);
            left.Children.Add(_sched);
            left.Children.Add(Caption("前回"));
            left.Children.Add(_lastStatus);
            left.Children.Add(_lastDetail);
            grid.Children.Add(left);
            var mid = new StackPanel { Margin = new Thickness(16, 0, 16, 0) };
            mid.Children.Add(Caption("次回の予定"));
            mid.Children.Add(_next);
            Grid.SetColumn(mid, 1);
            grid.Children.Add(mid);
            var buttons = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Top };
            buttons.Children.Add(_inc);
            buttons.Children.Add(_full);
            buttons.Children.Add(_cancel);
            Grid.SetColumn(buttons, 2);
            grid.Children.Add(buttons);
            _progPanel.Children.Add(_bar);
            _progPanel.Children.Add(_prog);
            var outer = new StackPanel();
            outer.Children.Add(grid);
            outer.Children.Add(_progPanel);
            Root = new Border { Style = (Style)Application.Current.FindResource("Card"), Child = outer };
            _inc.Click += (_, _) => Run(PointKind.Incremental);
            _full.Click += (_, _) => Run(PointKind.Full);
            _cancel.Click += (_, _) =>
            {
                if (MessageBox.Show("実行中のバックアップを中止しますか？", "中止", MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes)
                    Ipc.Call(new IpcRequest { Cmd = "cancel", JobId = _id });
            };
        }

        static TextBlock Caption(string s) => new() { Text = s, Foreground = Brushes.Gray, FontSize = 11, Margin = new Thickness(0, 4, 0, 0) };

        void Run(PointKind k)
        {
            var r = Ipc.Call(new IpcRequest { Cmd = "run", JobId = _id, Kind = k });
            if (r == null) MessageBox.Show("サービスに接続できません。", "実行", MessageBoxButton.OK, MessageBoxImage.Warning);
            else if (!r.Ok) MessageBox.Show(r.Error, "実行", MessageBoxButton.OK, MessageBoxImage.Warning);
        }

        public void Update(JobState j, bool online)
        {
            _name.Text = j.Name + (j.Enabled ? "" : "（無効）");
            _sched.Text = j.Schedule;
            if (j.Last is { } l)
            {
                _lastStatus.Text = $"{l.StatusText}  {l.End:yyyy/MM/dd HH:mm}（{(l.Kind == PointKind.Full ? "フル" : "増分")}）";
                _lastStatus.Foreground = (Brush)Application.Current.FindResource(l.Status switch
                {
                    JobStatus.Success => "Ok", JobStatus.Warning => "Warn", _ => "Bad",
                });
                _lastDetail.Text = l.Error ?? (l.ReadBytes > 0
                    ? $"読み取り {Fmt.Size(l.ReadBytes)} ・ 新規 {Fmt.Size(l.NewBytes)} ・ 保存 {Fmt.Size(l.StoredBytes)} ・ {Fmt.Duration(l.End - l.Start)} ・ ポイント {l.Points} 個"
                    : "") + (l.Warnings.Count > 0 ? "\n" + string.Join("\n", l.Warnings) : "");
            }
            else
            {
                _lastStatus.Text = "まだ実行していません";
                _lastStatus.Foreground = Brushes.Gray;
                _lastDetail.Text = "";
            }
            _next.Text = j.Next is { } n ? $"{n:MM/dd (ddd) HH:mm}\n{(j.NextKind == PointKind.Full ? "フル" : "増分")}" : "なし";
            bool running = j.Running || j.Queued;
            _inc.IsEnabled = _full.IsEnabled = online && !running;
            _cancel.Visibility = running ? Visibility.Visible : Visibility.Collapsed;
            _progPanel.Visibility = running ? Visibility.Visible : Visibility.Collapsed;
            if (j.Queued && !j.Running)
            {
                _bar.IsIndeterminate = true;
                _prog.Text = "待機中（ほかのジョブの終了後に開始）";
            }
            else if (j.Progress is { } p)
            {
                _bar.IsIndeterminate = p.Total == 0;
                _bar.Value = p.Total > 0 ? p.Done * 1000.0 / p.Total : 0;
                var pct = p.Total > 0 ? $"{p.Done * 100 / p.Total}%  " : "";
                var vol = p.VolumeCount > 0 ? $"ボリューム {p.Volume + 1}/{p.VolumeCount}  " : "";
                var rate = p.Rate > 0 ? $"  {Fmt.Rate(p.Rate)}" : "";
                var eta = p.Rate > 0 && p.Total > p.Done ? $"  残り約 {Fmt.Duration(TimeSpan.FromSeconds((p.Total - p.Done) / p.Rate))}" : "";
                _prog.Text = $"{p.Phase}  {vol}{pct}{Fmt.Size(p.Done)} / {Fmt.Size(p.Total)}{rate}{eta}  新規 {Fmt.Size(p.NewBytes)}   {p.LastLog}";
            }
        }
    }
}
