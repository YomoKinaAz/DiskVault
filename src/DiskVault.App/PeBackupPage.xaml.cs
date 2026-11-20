using System.Windows;
using System.Windows.Controls;
using DiskVault.Engine;
using DiskVault.Jobs;
using DiskVault.Platform;
using DiskVault.Storage;

namespace DiskVault.App;

/// <summary>回復環境（WinPE）からのフルバックアップ。スケジュールや世代整理はなく、その場で 1 回だけ取る。</summary>
public partial class PeBackupPage : UserControl, IPage
{
    sealed record DiskItem(int Number, long Size, string Text, bool HasSystem);
    CancellationTokenSource? _cts;
    bool _loaded;

    public PeBackupPage() => InitializeComponent();

    public void OnShown()
    {
        if (_loaded) return;
        _loaded = true;
        _ = Reload();
    }

    async Task Reload()
    {
        DiskList.Items.Clear();
        DiskList.Items.Add(new ListBoxItem { Content = "ディスクを調べています…", IsEnabled = false });
        var (disks, pc) = await Task.Run(() =>
        {
            var vols = VolumeInfo.Enumerate();
            var list = DiskLayout.ListDisks()
                .Where(d => d.Style != "未初期化" && d.Partitions > 0)
                .Select(d => new DiskItem(d.Number, d.Size,
                    $"ディスク {d.Number}  {d.Model}  {Fmt.Size(d.Size)}  {d.Style}  {(d.Volumes.Length > 0 ? "[" + d.Volumes + "]" : "")}{(d.HasSystem ? "  ← Windows" : "")}", d.HasSystem))
                .ToList();
            var sys = vols.FirstOrDefault(v => v.IsSystem);
            return (list, sys?.Letter is { } l ? OfflineWindows.ComputerName(l) : null);
        });
        DiskList.Items.Clear();
        foreach (var d in disks)
            DiskList.Items.Add(new CheckBox { Content = d.Text, Tag = d, IsChecked = d.HasSystem, Margin = new Thickness(4, 4, 4, 4) });
        if (disks.Count == 0) DiskList.Items.Add(new ListBoxItem { Content = "バックアップできるディスクがありません（ストレージのドライバを「ツール」から読み込んでください）。", IsEnabled = false });
        if (string.IsNullOrEmpty(PcBox.Text)) PcBox.Text = pc ?? "PC";
        UpdateRepoText();
    }

    void Reload_Click(object sender, RoutedEventArgs e) => _ = Reload();

    void Browse_Click(object sender, RoutedEventArgs e)
    {
        if (Dialogs.PickFolder(Window.GetWindow(this), "バックアップの保存先フォルダ", DestBox.Text) is { } p) DestBox.Text = p;
    }

    void Dest_Changed(object sender, TextChangedEventArgs e)
    {
        if (CredPanel == null) return;
        CredPanel.Visibility = DestBox.Text.Trim().StartsWith(@"\\") ? Visibility.Visible : Visibility.Collapsed;
        UpdateRepoText();
    }

    string RepoPath => Path.Combine(DestBox.Text.Trim(), San(PcBox.Text), San(NameBox.Text));

    static string San(string s)
    {
        s = s.Trim();
        foreach (var c in Path.GetInvalidFileNameChars()) s = s.Replace(c, '_');
        return s.Length == 0 ? "_" : s;
    }

    void UpdateRepoText()
    {
        if (RepoText == null) return;
        RepoText.Text = DestBox.Text.Trim().Length == 0 ? "保存先を入力してください。" : $"保存場所: {RepoPath}（既にバックアップがあれば、そこに新しいフルの復元ポイントを追加します）";
    }

    List<DiskItem> Selected => DiskList.Items.OfType<CheckBox>().Where(c => c.IsChecked == true).Select(c => (DiskItem)c.Tag).ToList();

    void Log(string m)
    {
        LogBox.AppendText($"{DateTime.Now:HH:mm:ss} {m}\n");
        LogBox.ScrollToEnd();
    }

    async void Start_Click(object sender, RoutedEventArgs e)
    {
        var disks = Selected;
        var dest = DestBox.Text.Trim();
        if (disks.Count == 0) { MessageBox.Show("バックアップするディスクを選んでください。", "バックアップ"); return; }
        if (dest.Length == 0) { MessageBox.Show("保存先を入力してください。", "バックアップ"); return; }
        if (!Sys.IsWinPE && disks.Any(d => d.HasSystem))
        {
            MessageBox.Show("実行中の Windows のディスクは、ここではバックアップできません（スナップショットなしで読むため）。回復メディアから起動して行うか、通常の「バックアップ ジョブ」を使ってください。", "バックアップ", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        if (EncBox.Password != Enc2Box.Password) { MessageBox.Show("暗号化パスワードと確認が一致しません。", "バックアップ"); return; }
        if (!dest.StartsWith(@"\\") && Path.GetPathRoot(dest) is { Length: >= 2 } root)
        {
            // 保存先がバックアップするディスク上にあると、書きながら読むことになる
            var v = VolumeInfo.Find(root[..2]);
            if (v?.Disk is int dn && disks.Any(d => d.Number == dn))
            {
                MessageBox.Show($"保存先 {root[..2]} はバックアップするディスク {dn} の上にあります。別のディスク（USB ディスクなど）か NAS を指定してください。", "バックアップ", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
        }
        string repoPath = RepoPath, user = UserBox.Text.Trim(), nasPw = NasPassBox.Password, name = NameBox.Text.Trim(), pc = PcBox.Text.Trim();
        string? enc = EncBox.Password.Length > 0 ? EncBox.Password : null;
        int level = int.Parse((string)((ComboBoxItem)LevelBox.SelectedItem).Tag);
        bool verify = VerifyBox.IsChecked == true, exclude = ExcludeBox.IsChecked == true;
        var specs = disks.Select(d => $"physical:{d.Number}").ToList();

        StartButton.IsEnabled = DiskList.IsEnabled = false;
        CancelButton.IsEnabled = true;
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;
        LogBox.Clear();
        var started = DateTime.Now;
        void L(string m) => Dispatcher.BeginInvoke(() => Log(m));
        var prog = new Progress<BackupProgress>(x =>
        {
            Bar.Value = x.Total > 0 ? x.Done * 1000.0 / x.Total : 0;
            ProgText.Text = $"{x.Phase}  ボリューム {x.Volume + 1}/{x.VolumeCount}  {Fmt.Size(x.Done)} / {Fmt.Size(x.Total)}  {Fmt.Rate(x.BytesPerSecond)}" +
                            (x.BytesPerSecond > 0 && x.Total > x.Done ? $"  残り約 {Fmt.Duration(TimeSpan.FromSeconds((x.Total - x.Done) / x.BytesPerSecond))}" : "");
        });
        try
        {
            var point = await Task.Run(() =>
            {
                using var share = NetShare.Connect(dest, user.Length > 0 ? user : null, nasPw);
                L($"保存先: {repoPath}");
                var repo = Repository.OpenOrCreate(repoPath, enc, level: level);
                if (repo.IsEncrypted != (enc != null))
                    throw new InvalidOperationException(repo.IsEncrypted ? "この保存先は暗号化されています。暗号化パスワードを入力してください。" : "この保存先は暗号化なしで作られています。暗号化パスワードを空にするか、別の名前にしてください。");
                RecoveryPoint pt;
                using (var plan = SourcePlanner.Build(specs, L, offline: true))
                {
                    pt = BackupEngine.Run(repo, plan.Volumes, new BackupOptions
                    {
                        Job = name.Length > 0 ? name : "回復環境",
                        Kind = PointKind.Full,
                        Disks = plan.Disks,
                        ExcludeSystemFiles = exclude,
                        Consistency = plan.Consistency,
                        Comment = "回復環境（WinPE）からのバックアップ",
                        Machine = pc.Length > 0 ? pc : null,
                    }, prog, L, ct);
                }
                if (verify)
                {
                    L("検証しています…");
                    var r = Maintenance.Verify(repo, [repo.GetPoint(pt.Id)], new Progress<(long Done, long Total)>(x =>
                        Dispatcher.BeginInvoke(() =>
                        {
                            Bar.Value = x.Total > 0 ? x.Done * 1000.0 / x.Total : 0;
                            ProgText.Text = $"検証中  {x.Done:N0} / {x.Total:N0} ブロック";
                        })), ct);
                    if (r.Bad > 0) throw new InvalidDataException($"検証で {r.Bad} 件のエラーがあります: {string.Join(" / ", r.Errors.Take(3))}");
                    L($"検証 OK（{r.Blocks:N0} ブロック）");
                }
                return pt;
            });
            Bar.Value = 1000;
            var took = DateTime.Now - started;
            ProgText.Text = $"完了しました（{Fmt.Duration(took)}）";
            Log($"完了: 復元ポイント {point.Id}  使用量 {Fmt.Size(point.Stats.UsedBytes)}  保存 {Fmt.Size(point.Stats.StoredBytes)}");
            MessageBox.Show($"フルバックアップが完了しました。\n\n保存場所: {repoPath}\n復元ポイント: {point.Id}\n使用量 {Fmt.Size(point.Stats.UsedBytes)} → 保存 {Fmt.Size(point.Stats.StoredBytes)}\n所要時間: {Fmt.Duration(took)}",
                "バックアップ", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (OperationCanceledException)
        {
            ProgText.Text = "中止しました。";
            Log("中止しました（途中のデータは次回のバックアップで再利用されるか、復元ポイントにならずに残ります）。");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or InvalidOperationException or ArgumentException or NotSupportedException)
        {
            ProgText.Text = "失敗しました: " + ex.Message;
            Log("失敗: " + ex.Message);
            MessageBox.Show(ex.Message, "バックアップに失敗しました", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            StartButton.IsEnabled = DiskList.IsEnabled = true;
            CancelButton.IsEnabled = false;
        }
    }

    void Cancel_Click(object sender, RoutedEventArgs e)
    {
        if (MessageBox.Show("バックアップを中止しますか？", "中止", MessageBoxButton.YesNo) == MessageBoxResult.Yes) _cts?.Cancel();
    }
}
