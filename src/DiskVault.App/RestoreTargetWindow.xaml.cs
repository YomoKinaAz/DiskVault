using System.Windows;
using System.Windows.Controls;
using DiskVault.Engine;
using DiskVault.Platform;
using DiskVault.Storage;

namespace DiskVault.App;

/// <summary>ボリュームまたはディスク全体の復元先を選んで復元する。</summary>
public partial class RestoreTargetWindow : Window
{
    readonly Repository _repo;
    readonly RecoveryPoint _p;
    readonly VolumeRecord? _vol;
    readonly DiskRecord? _disk;
    CancellationTokenSource? _cts;
    bool _running;

    sealed record Target(string Path, long Size, string Text, bool Allowed, int? Disk);

    /// <summary>パーティション配置の表の 1 行。</summary>
    public sealed class LayoutRow(PartitionPlan plan) : System.ComponentModel.INotifyPropertyChanged
    {
        public PartitionPlan Plan { get; } = plan;
        public int Number => Plan.Source.Number;
        public string Kind => Plan.Source.Kind + (Plan.Resizable ? "" : T("（変更不可）"));
        public string OrigText => Fmt.Size(Plan.Source.Length);
        public string MinText => Plan.Resizable ? Fmt.Size(Plan.MinLength) : "";
        public string NewGB
        {
            get => (Plan.NewLength / (double)(1L << 30)).ToString("0.##");
            set
            {
                if (!Plan.Resizable) return;
                if (double.TryParse(value, out var gb) && gb > 0)
                {
                    Plan.NewLength = (long)(gb * (1L << 30)) / LayoutPlanner.Align * LayoutPlanner.Align;
                    Plan.Fill = false;
                }
                Changed();
            }
        }
        public string GapGB
        {
            get => Plan.GapBefore == 0 ? "0" : (Plan.GapBefore / (double)(1L << 30)).ToString("0.###");
            set
            {
                if (double.TryParse(value, out var gb) && gb >= 0)
                    Plan.GapBefore = (long)Math.Round(gb * (1L << 30) / LayoutPlanner.Align) * LayoutPlanner.Align;
                Changed();
            }
        }
        public bool Fill
        {
            get => Plan.Fill;
            set { if (Plan.Resizable) Plan.Fill = value; Changed(); }
        }
        public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;
        public void Changed()
        {
            PropertyChanged?.Invoke(this, new(nameof(NewGB)));
            PropertyChanged?.Invoke(this, new(nameof(Fill)));
            PropertyChanged?.Invoke(this, new(nameof(GapGB)));
        }
    }

    List<PartitionPlan>? _plan;
    List<LayoutRow> _rows = [];
    DiskRecord? _layout;

    public RestoreTargetWindow(Repository repo, RecoveryPoint p, VolumeRecord? vol, DiskRecord? disk)
    {
        InitializeComponent();
        _repo = repo;
        _p = p;
        _vol = vol;
        _disk = disk;
        var when = p.Time.ToLocalTime().ToString("yyyy/MM/dd HH:mm");
        if (vol != null)
        {
            Header.Text = T("{0} の {1}（{2}）を復元", when, vol.Display.Trim(), Fmt.Size(vol.Size));
            SubHeader.Text = T("復元先のボリュームの内容はすべて上書きされます。元と同じかそれより大きいボリュームを選んでください。実行中の Windows のボリュームには復元できません（回復メディアから行ってください）。");
        }
        else
        {
            Header.Text = T("{0} のディスク全体（{1}、パーティション {2} 個、{3}）を復元", when, _disk!.Style, _disk.Partitions.Count, Fmt.Size(_disk.Size));
            SubHeader.Text = T("復元先ディスクの内容はすべて消去され、パーティション構成ごと書き戻されます（ベアメタル復元）。大きいディスクには広げて、小さいディスクには NTFS を縮めて復元できます（下の「パーティションの配置」）。");
            BootFixBox.Visibility = Sys.IsWinPE ? Visibility.Visible : Visibility.Collapsed;
            BootFixBox.IsChecked = Sys.IsWinPE;
            AdaptBox.Visibility = Visibility.Visible;
        }
        ImagePath.Text = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Desktop), vol != null ? $"restore-{p.Id}-vol{vol.Index}.img" : $"restore-{p.Id}-disk.img");
        Loaded += async (_, _) =>
        {
            if (_disk != null) await LoadPlan();
            await LoadTargets();
        };
        Closing += (_, e) =>
        {
            if (_running && MessageBox.Show(T("復元を中止しますか？復元先は使えない状態になります。"), T("中止"), MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
                e.Cancel = true;
            else _cts?.Cancel();
        };
    }

    /// <summary>NTFS を最小まで縮めたときに必要なディスク容量。</summary>
    long MinDiskNeed()
    {
        if (_plan == null) return Need;
        long sum = _plan.First().Source.Offset + LayoutPlanner.TailReserve;
        foreach (var x in _plan) sum += (x.Resizable ? x.MinLength : x.Source.Length) + LayoutPlanner.Align;
        return sum;
    }

    long Need => _vol?.Size ?? (_disk!.Partitions.Count == 0 ? 0 : _disk.Partitions.Max(x => x.Offset + x.Length) + (1 << 20));

    async Task LoadTargets()
    {
        var pe = Sys.IsWinPE;
        var list = await Task.Run(() =>
        {
            var t = new List<Target>();
            if (_vol != null)
            {
                foreach (var v in VolumeInfo.Enumerate())
                {
                    long size = v.ExtentLength ?? v.Size;
                    bool ok = size >= _vol.Size && (!v.IsSystem || pe);
                    var why = v.IsSystem && !pe ? T("（実行中の Windows）") : size < _vol.Size ? T("（小さすぎます）") : "";
                    t.Add(new Target(v.GuidPath, size, $"{v.Letter ?? T("(文字なし)")}  {v.Label}  {v.FileSystem}  {Fmt.Size(size)}  {(v.Disk is int d ? T("ディスク {0}", d) : "")} {why}", ok, v.Disk));
                }
            }
            else
            {
                foreach (var d in DiskLayout.ListDisks())
                {
                    long minNeed = MinDiskNeed();
                    bool ok = d.Size >= minNeed && (!d.HasSystem || pe);
                    var why = d.HasSystem && !pe ? T("（実行中の Windows があるディスク）") : d.Size < minNeed ? T("（小さすぎます）") : d.Size < Need ? T("（縮小して復元）") : "";
                    t.Add(new Target($@"\\.\PhysicalDrive{d.Number}", d.Size,
                        T("ディスク {0}", d.Number) + $"  {d.Model}  {Fmt.Size(d.Size)}  {d.Style}  {(d.Volumes.Length > 0 ? "[" + d.Volumes + "]" : "")} {why}", ok, d.Number));
                }
            }
            return t;
        });
        Targets.Items.Clear();
        foreach (var t in list)
            Targets.Items.Add(new ListBoxItem { Content = t.Text, Tag = t, IsEnabled = t.Allowed, Padding = new Thickness(8, 6, 8, 6) });
        var first = Targets.Items.Cast<ListBoxItem>().FirstOrDefault(i => i.IsEnabled);
        if (first != null) Targets.SelectedItem = first;
        else ImageRadio.IsChecked = true;
    }

    async Task LoadPlan()
    {
        LayoutPanel.Visibility = Visibility.Visible;
        LayoutStatus.Text = T("使用中の領域を調べています…");
        _plan = await Task.Run(() => LayoutPlanner.Analyze(_repo, _p, _disk!));
        _rows = _plan.Select(x => new LayoutRow(x)).ToList();
        LayoutGrid.ItemsSource = _rows;
        PartBar.LayoutEdited += RefreshLayout;
        PartBar.Validate = () =>
        {
            try { LayoutPlanner.Build(_disk!, _plan, TargetSize); return true; }
            catch (InvalidOperationException) { return false; }
        };
        RefreshLayout();
    }

    /// <summary>選んだ復元先の容量。イメージファイルなら元のディスクと同じ。</summary>
    long TargetSize => Targets.SelectedItem is ListBoxItem { Tag: Target t } ? t.Size : _disk?.Size ?? 0;

    void RefreshLayout()
    {
        if (_plan == null || _disk == null) return;
        // 何も変えていないとき、復元先が元と違う大きさなら自動（Windows を残り全体）にする
        if (!PartBar.Dragging && _plan.All(x => !x.Fill && x.GapBefore == 0 && x.NewLength == x.Source.Length) && Math.Abs(TargetSize - _disk.Size) > LayoutPlanner.Align * 64)
            LayoutPlanner.AutoFill(_plan, _p);
        try
        {
            _layout = LayoutPlanner.Build(_disk, _plan, TargetSize);
            foreach (var x in _plan) x.NewLength = _layout.Partitions.First(p => p.Number == x.Source.Number).Length;
            long used = _layout.Partitions.Max(x => x.Offset + x.Length);
            LayoutStatus.Foreground = (System.Windows.Media.Brush)FindResource("Muted");
            LayoutStatus.Text = T("復元先 {0} のうち {1} を使います。", Fmt.Size(TargetSize), Fmt.Size(used));
        }
        catch (InvalidOperationException ex)
        {
            _layout = null;
            LayoutStatus.Foreground = (System.Windows.Media.Brush)FindResource("Bad");
            LayoutStatus.Text = ex.Message;
        }
        foreach (var r in _rows) r.Changed();
        PartBar.Set(_plan, TargetSize, PartName);
    }

    /// <summary>バーに出すパーティション名（ドライブ文字・ラベルがあればそれ）。</summary>
    string PartName(PartitionPlan x)
    {
        var v = x.Source.Volume is int vi ? _p.Volumes[vi] : null;
        if (v?.IsSystem == true) return "Windows" + (v.Letter != null ? $" ({v.Letter})" : "");
        if (v?.Letter != null) return v.Letter + (string.IsNullOrEmpty(v.Label) ? "" : " " + v.Label);
        return x.Source.TypeGuid?.ToLowerInvariant() switch
        {
            PartitionRecord.EspType => "EFI",
            PartitionRecord.MsrType => "MSR",
            PartitionRecord.RecoveryType => T("回復"),
            _ => string.IsNullOrEmpty(v?.Label) ? x.Source.Kind : v!.Label!,
        };
    }

    void LayoutGrid_CellEditEnding(object? sender, DataGridCellEditEndingEventArgs e) =>
        Dispatcher.BeginInvoke(RefreshLayout, System.Windows.Threading.DispatcherPriority.Background);

    void AutoLayout_Click(object sender, RoutedEventArgs e)
    {
        if (_plan == null) return;
        LayoutPlanner.AutoFill(_plan, _p);
        RefreshLayout();
    }

    void OrigLayout_Click(object sender, RoutedEventArgs e)
    {
        if (_plan == null) return;
        foreach (var x in _plan) { x.Fill = false; x.NewLength = x.Source.Length; x.GapBefore = 0; }
        RefreshLayout();
    }

    void Targets_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (Targets.SelectedItem != null) ImageRadio.IsChecked = false;
        RefreshLayout();
    }

    void ImageRadio_Checked(object sender, RoutedEventArgs e) => Targets.SelectedItem = null;

    void BrowseImage_Click(object sender, RoutedEventArgs e)
    {
        if (Dialogs.PickSaveFile(this, T("イメージファイルの保存先"), ".img", ImagePath.Text) is { } p) ImagePath.Text = p;
        ImageRadio.IsChecked = true;
    }

    async void Start_Click(object sender, RoutedEventArgs e)
    {
        string path;
        long size;
        bool device;
        if (ImageRadio.IsChecked == true)
        {
            path = ImagePath.Text.Trim();
            size = _vol?.Size ?? (_layout?.Size ?? _disk!.Size);
            device = false;
            if (File.Exists(path) && MessageBox.Show(T("{0} を上書きしますか？", path), T("確認"), MessageBoxButton.YesNo) != MessageBoxResult.Yes) return;
        }
        else if (Targets.SelectedItem is ListBoxItem { Tag: Target t })
        {
            path = t.Path;
            size = _vol?.Size ?? t.Size;
            device = true;
            var what = _vol != null ? T("ボリューム「{0}」", t.Text.Trim()) : T("ディスク「{0}」", t.Text.Trim());
            if (MessageBox.Show(T("{0}の内容はすべて消去され、元に戻せません。\n\n復元を開始しますか？", what), T("復元の確認"),
                    MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) != MessageBoxResult.Yes) return;
            if (_disk != null && MessageBox.Show(T("本当によろしいですか？（最終確認）"), T("復元の確認"), MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) != MessageBoxResult.Yes) return;
        }
        else
        {
            MessageBox.Show(T("復元先を選んでください。"));
            return;
        }

        bool adapt = _disk != null && AdaptBox.IsChecked == true;
        var driverDir = DriverPath.Text.Trim();
        if (adapt && driverDir.Length > 0 && !Directory.Exists(driverDir))
        {
            MessageBox.Show(T("ドライバのフォルダが見つかりません: {0}", driverDir));
            return;
        }

        _running = true;
        SetInputs(false);
        ProgressPanel.Visibility = Visibility.Visible;
        _cts = new CancellationTokenSource();
        var log = new List<string>();
        var prog = new Progress<RestoreProgress>(x =>
        {
            Bar.Value = x.Total > 0 ? x.Done * 1000.0 / x.Total : 0;
            ProgText.Text = $"{x.Phase}  {Fmt.Size(x.Done)} / {Fmt.Size(x.Total)}  {Fmt.Rate(x.BytesPerSecond)}" +
                            (x.BytesPerSecond > 0 && x.Total > x.Done ? "  " + T("残り約 {0}", Fmt.Duration(TimeSpan.FromSeconds((x.Total - x.Done) / x.BytesPerSecond))) : "");
        });
        bool bootFix = BootFixBox.IsChecked == true;
        if (_disk != null && _layout == null)
        {
            MessageBox.Show(LayoutStatus.Text, T("パーティションの配置"), MessageBoxButton.OK, MessageBoxImage.Warning);
            ProgressPanel.Visibility = Visibility.Collapsed;
            _running = false;
            SetInputs(true);
            return;
        }
        var layout = _layout;
        try
        {
            await Task.Run(() =>
            {
                List<PartitionRecord> grow = [];
                using (var target = Targets_Open(path, size))
                {
                    if (_vol != null) RestoreEngine.RestoreVolume(_repo, _p, _vol.Index, target, writeUnused: !device, prog, _cts.Token);
                    else grow = RestoreEngine.RestoreDisk(_repo, _p, _disk!, target, writeUnused: !device, prog, m => { lock (log) log.Add(m); }, _cts.Token, layout);
                }
                if (grow.Count > 0)
                {
                    if (device && int.TryParse(path[@"\\.\PhysicalDrive".Length..], out var gdn)) lock (log) log.AddRange(NtfsResize.ExtendOnDisk(gdn, grow));
                    else lock (log) log.Add(T("イメージファイルのため NTFS の拡張は行いませんでした（使うときに「ディスクの管理」でボリュームを拡張してください）。"));
                }
                if (_vol != null && device && _vol.FileSystem == "NTFS")
                    try { lock (log) log.Add(NtfsResize.Extend(path).Trim()); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
                if (adapt && device && int.TryParse(path[@"\\.\PhysicalDrive".Length..], out var adn))
                    lock (log) log.AddRange(HardwareAdapt.RunOnDisk(adn, driverDir.Length > 0 ? [driverDir] : null));
                else if (adapt) lock (log) log.Add(T("イメージファイルのため、別の PC 向けの調整は行いませんでした。"));
                if (bootFix && device && _disk != null && int.TryParse(path[@"\\.\PhysicalDrive".Length..], out var dn))
                    lock (log) log.AddRange(BootFix.Run(dn, _disk));
            });
            _running = false;
            ProgText.Text = T("完了しました。");
            MessageBox.Show(T("復元が完了しました。") + (log.Count > 0 ? "\n\n" + string.Join("\n", log) : ""), T("復元"), MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (OperationCanceledException)
        {
            ProgText.Text = T("中止しました。");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or InvalidOperationException)
        {
            ProgText.Text = T("失敗しました: ") + ex.Message;
            MessageBox.Show(ex.Message, T("復元に失敗しました"), MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            _running = false;
            SetInputs(true);
        }
    }

    void SetInputs(bool on) =>
        StartButton.IsEnabled = Targets.IsEnabled = ImageRadio.IsEnabled = BootFixBox.IsEnabled = AdaptBox.IsEnabled = DriverPanel.IsEnabled = on;

    void AdaptBox_Changed(object sender, RoutedEventArgs e) =>
        DriverPanel.Visibility = AdaptBox.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;

    void BrowseDriver_Click(object sender, RoutedEventArgs e)
    {
        if (Dialogs.PickFolder(this, T("ドライバのフォルダ（.inf を含む。サブフォルダも探します）"), DriverPath.Text) is { } p) DriverPath.Text = p;
    }

    static IVolumeTarget Targets_Open(string path, long size) => Engine.Targets.Open(path, size, confirmed: true);

    void Close_Click(object sender, RoutedEventArgs e) => Close();
}




