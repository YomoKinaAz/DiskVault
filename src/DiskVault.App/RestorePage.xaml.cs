using System.Windows;
using System.Windows.Controls;
using DiskVault.Engine;
using DiskVault.Jobs;
using DiskVault.Storage;

namespace DiskVault.App;

public sealed class PointRow(RecoveryPoint p)
{
    public RecoveryPoint Point { get; } = p;
    public string TimeText => Point.Time.ToLocalTime().ToString("yyyy/MM/dd HH:mm:ss");
    public string KindText => Point.Kind == PointKind.Full ? "フル" : "増分";
    public string Job => Point.Job;
    public string VolumesText => string.Join("、", Point.Volumes.Select(v => v.Letter ?? (v.IsBoot ? "EFI" : v.Label ?? "?")));
    public string UsedText => Fmt.Size(Point.Stats.UsedBytes);
    public string NewText => Fmt.Size(Point.Stats.NewBytes);
    public string BmrText => Point.BareMetalCapable ? "可" : "";
    public string ConsistencyText => Point.Consistency switch { "writers" => "アプリ整合", "crash" => "クラッシュ整合", "offline" => "オフライン", _ => "" };
}

public sealed class VolumeRow(VolumeRecord v)
{
    public VolumeRecord Vol { get; } = v;
    public int Index => Vol.Index;
    public string Display => Vol.Display.Trim();
    public string SizeText => Fmt.Size(Vol.Size);
    public string UsedText => Fmt.Size(Vol.UsedBytes);
    public string Role => Vol.IsSystem ? "Windows" : Vol.IsBoot ? "ブート（EFI）" : Vol.Disk != null && Vol.Letter == null ? "回復など" : "データ";
}

public partial class RestorePage : UserControl, IPage
{
    Repository? _repo;
    const string OtherFolder = "（フォルダを指定する）";

    public RestorePage()
    {
        InitializeComponent();
        UpdateButtons();
    }

    public void OnShown()
    {
        var sel = (SelectedJob)?.Id;
        SourceBox.Items.Clear();
        foreach (var j in AppConfig.Load().Jobs) SourceBox.Items.Add(new ComboBoxItem { Content = $"{j.Name}  （{j.RepoPath}）", Tag = j });
        SourceBox.Items.Add(new ComboBoxItem { Content = OtherFolder });
        SourceBox.SelectedItem = SourceBox.Items.Cast<ComboBoxItem>().FirstOrDefault(i => sel != null && (i.Tag as JobConfig)?.Id == sel) ?? SourceBox.Items[0];
    }

    JobConfig? SelectedJob => (SourceBox.SelectedItem as ComboBoxItem)?.Tag as JobConfig;

    void SourceBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        bool other = SelectedJob == null;
        PathBox.Visibility = BrowseButton.Visibility = PathLabel.Visibility = CredPanel.Visibility = other ? Visibility.Visible : Visibility.Collapsed;
        if (SelectedJob is { } j) PathBox.Text = j.RepoPath;
    }

    async void Open_Click(object sender, RoutedEventArgs e)
    {
        string path;
        DestinationConfig dest;
        string? pw;
        if (SelectedJob is { } j)
        {
            path = j.RepoPath;
            dest = j.Destination;
            try { pw = Secret.Unprotect(j.EncryptionPasswordEnc); }
            catch (UnauthorizedAccessException ex) { MessageBox.Show(ex.Message); return; }
        }
        else
        {
            path = PathBox.Text.Trim();
            dest = new DestinationConfig { Path = path, User = UserBox.Text.Trim().Length > 0 ? UserBox.Text.Trim() : null, PasswordEnc = Secret.Protect(NasPassBox.Password) };
            pw = EncPassBox.Password.Length > 0 ? EncPassBox.Password : null;
        }
        RepoInfo.Text = "開いています…";
        try
        {
            var (repo, points) = await Task.Run(() =>
            {
                NetShare.Connect(dest); // 復元中は接続したままにする
                var r = Repository.Open(path, pw);
                return (r, r.ListPoints());
            });
            _repo = repo;
            Points.ItemsSource = points.AsEnumerable().Reverse().Select(p => new PointRow(p)).ToList();
            if (points.Count > 0) Points.SelectedIndex = 0;
            RepoInfo.Text = $"{path}  ・  復元ポイント {points.Count} 個  ・  {(repo.IsEncrypted ? "暗号化あり" : "暗号化なし")}";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or ArgumentException)
        {
            _repo = null;
            Points.ItemsSource = null;
            RepoInfo.Text = "";
            MessageBox.Show(ex.Message, "開けません", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        UpdateButtons();
    }

    void Browse_Click(object sender, RoutedEventArgs e)
    {
        if (Dialogs.PickFolder(Window.GetWindow(this), "バックアップのフォルダ（repo.json のある場所）", PathBox.Text) is { } p) PathBox.Text = p;
    }

    RecoveryPoint? Selected => (Points.SelectedItem as PointRow)?.Point;
    VolumeRecord? SelectedVolume => (Volumes.SelectedItem as VolumeRow)?.Vol;

    void Points_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        var p = Selected;
        Volumes.ItemsSource = p?.Volumes.Select(v => new VolumeRow(v)).ToList();
        if (p != null)
        {
            var main = p.Volumes.FirstOrDefault(v => v.IsSystem) ?? p.Volumes.FirstOrDefault(v => v.FileSystem == "NTFS") ?? p.Volumes.FirstOrDefault();
            Volumes.SelectedItem = (Volumes.ItemsSource as List<VolumeRow>)?.FirstOrDefault(r => r.Vol == main);
        }
        UpdateButtons();
    }

    void UpdateButtons()
    {
        var p = Selected;
        FilesButton.IsEnabled = VolumeButton.IsEnabled = VerifyButton.IsEnabled = p != null;
        DiskButton.IsEnabled = p?.Disks.Count > 0;
    }

    void Files_Click(object sender, RoutedEventArgs e)
    {
        if (_repo == null || Selected is not { } p) return;
        var v = SelectedVolume ?? p.Volumes.FirstOrDefault(x => x.FileSystem == "NTFS");
        if (v == null || v.FileSystem != "NTFS")
        {
            MessageBox.Show("ファイル単位の復元は NTFS ボリュームだけに対応しています。下の一覧で NTFS のボリュームを選んでください。", "ファイル単位で復元");
            return;
        }
        new FileBrowserWindow(_repo, p, v) { Owner = Window.GetWindow(this) }.Show();
    }

    void Volume_Click(object sender, RoutedEventArgs e)
    {
        if (_repo == null || Selected is not { } p) return;
        var v = SelectedVolume ?? p.Volumes[0];
        new RestoreTargetWindow(_repo, p, v, null) { Owner = Window.GetWindow(this) }.ShowDialog();
    }

    void Disk_Click(object sender, RoutedEventArgs e)
    {
        if (_repo == null || Selected is not { } p || p.Disks.Count == 0) return;
        new RestoreTargetWindow(_repo, p, null, p.Disks[0]) { Owner = Window.GetWindow(this) }.ShowDialog();
    }

    async void Verify_Click(object sender, RoutedEventArgs e)
    {
        if (_repo == null || Selected is not { } p) return;
        VerifyButton.IsEnabled = false;
        var repo = _repo;
        var prog = new Progress<(long Done, long Total)>(x => RepoInfo.Text = $"検証中… {x.Done:N0} / {x.Total:N0} ブロック");
        var r = await Task.Run(() => Maintenance.Verify(repo, [p], prog));
        RepoInfo.Text = "";
        VerifyButton.IsEnabled = true;
        if (r.Bad == 0) MessageBox.Show($"復元ポイント {p.Id} は正常です（{r.Blocks:N0} ブロックを検証）。", "検証", MessageBoxButton.OK, MessageBoxImage.Information);
        else MessageBox.Show($"{r.Bad} 件のエラーがあります。\n\n" + string.Join("\n", r.Errors.Take(10)), "検証", MessageBoxButton.OK, MessageBoxImage.Error);
    }
}



