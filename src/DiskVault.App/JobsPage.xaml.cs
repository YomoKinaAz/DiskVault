using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using DiskVault.Jobs;
using DiskVault.Platform;

namespace DiskVault.App;

public partial class JobsPage : UserControl, IPage
{
    AppConfig _cfg = new();
    JobConfig? _job;
    bool _isNew;
    readonly List<(CheckBox Box, string Spec)> _volBoxes = [];
    readonly CheckBox[] _days = new CheckBox[7];
    static readonly string[] DayNames = ["日曜", "月曜", "火曜", "水曜", "木曜", "金曜", "土曜"];
    string? _encSet; // 既存の暗号化パスワード（変更しないとき用）

    public JobsPage()
    {
        InitializeComponent();
        foreach (var d in new[] { 1, 2, 3, 4, 5, 6, 0 })
        {
            var cb = new CheckBox { Content = T(DayNames[d]) };
            _days[d] = cb;
            DaysPanel.Children.Add(cb);
        }
        foreach (var d in new[] { 1, 2, 3, 4, 5, 6, 0 }) FullDay.Items.Add(new ComboBoxItem { Content = T(DayNames[d]), Tag = d });
        LoadVolumes();
    }

    public void OnShown()
    {
        _cfg = AppConfig.Load();
        var sel = _job?.Id;
        JobList.ItemsSource = null;
        JobList.ItemsSource = _cfg.Jobs;
        if (_isNew && _job != null) return;
        JobList.SelectedItem = _cfg.Jobs.FirstOrDefault(j => j.Id == sel) ?? _cfg.Jobs.FirstOrDefault();
        if (_cfg.Jobs.Count == 0) NewJob();
    }

    void LoadVolumes()
    {
        try
        {
            var vols = VolumeInfo.Enumerate();
            var sys = vols.FirstOrDefault(v => v.IsSystem);
            if (sys?.Disk is int dn)
            {
                var others = vols.Where(v => v.Disk == dn && !v.IsSystem).Select(v => v.FileSystem == "FAT32" ? T("EFI（{0}）", Fmt.Size(v.Size)) : T("{0}（{1}）", v.Letter ?? (string.IsNullOrEmpty(v.Label) ? T("回復") : v.Label), Fmt.Size(v.Size)));
                SystemDetail.Text = T("ディスク {0}: {1} {2}", dn, sys.Letter, Fmt.Size(sys.Size)) + (others.Any() ? T(" ＋ ") + string.Join(T("、"), others) : "");
            }
            foreach (var v in vols.Where(v => v.Letter != null && !v.IsSystem && v.FileSystem is "NTFS" or "ReFS" or "FAT32" or "exFAT"))
            {
                var cb = new CheckBox { Content = $"{v.Letter}  {v.Label}  {v.FileSystem}  {Fmt.Size(v.Size)}" + T("（空き {0}）", Fmt.Size(v.Free)) };
                _volBoxes.Add((cb, v.Letter!));
                VolumeBoxes.Children.Add(cb);
            }
            if (_volBoxes.Count == 0) VolumeBoxes.Children.Add(new TextBlock { Text = T("（なし）"), Foreground = System.Windows.Media.Brushes.Gray });
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            SystemDetail.Text = e.Message;
        }
    }

    public void NewJob()
    {
        _cfg = AppConfig.Load();
        _isNew = true;
        _job = new JobConfig { Name = _cfg.Jobs.Count == 0 ? T("システム") : T("ジョブ {0}", _cfg.Jobs.Count + 1) };
        JobList.SelectedItem = null;
        Show(_job);
        NameBox.Focus();
        NameBox.SelectAll();
    }

    void JobList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (JobList.SelectedItem is JobConfig j)
        {
            _isNew = false;
            _job = j;
            Show(j);
        }
    }

    static void Select(ComboBox c, string tag)
    {
        c.SelectedItem = c.Items.Cast<ComboBoxItem>().FirstOrDefault(i => i.Tag?.ToString() == tag) ?? c.Items[0];
    }

    static string TagOf(ComboBox c) => (c.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "";

    void Show(JobConfig j)
    {
        NameBox.Text = j.Name;
        EnabledBox.IsChecked = j.Enabled;
        SystemBox.IsChecked = j.Sources.Contains("system", StringComparer.OrdinalIgnoreCase);
        foreach (var (box, spec) in _volBoxes) box.IsChecked = j.Sources.Contains(spec, StringComparer.OrdinalIgnoreCase);
        DestBox.Text = j.Destination.Path;
        UserBox.Text = j.Destination.User ?? "";
        PassBox.Password = Secret.Unprotect(j.Destination.PasswordEnc) ?? "";
        var s = j.Schedule;
        SchedBox.IsChecked = s.Enabled;
        Select(IncMode, s.Incremental);
        TimesBox.Text = string.Join(", ", s.Times);
        StartBox.Text = s.HourlyStart;
        IntervalBox.Text = s.IntervalHours.ToString();
        for (int d = 0; d < 7; d++) _days[d].IsChecked = s.Days.Contains((DayOfWeek)d);
        Select(FullMode, s.Full);
        FullDay.SelectedItem = FullDay.Items.Cast<ComboBoxItem>().First(i => (int)i.Tag == (int)s.FullDay);
        FullDom.Text = s.FullDayOfMonth.ToString();
        FullTimeBox.Text = s.FullTime;
        MissedBox.IsChecked = s.RunMissed;
        KeepBox.Text = j.Keep.ToString();
        VerifyBox.IsChecked = j.VerifyAfterBackup;
        Select(LevelBox, j.CompressionLevel.ToString());
        ExcludeBox.IsChecked = j.ExcludeSystemFiles;
        WritersBox.IsChecked = j.VssWriters;
        VssFullBox.IsChecked = j.VssFullBackup;
        _encSet = j.EncryptionPasswordEnc;
        EncryptBox.IsChecked = j.EncryptionPasswordEnc != null;
        EncPass1.Password = EncPass2.Password = _encSet != null ? "********" : "";
        DeleteButton.IsEnabled = !_isNew;
        SaveStatus.Text = _isNew ? T("新しいジョブ（まだ保存していません）") : "";
        UpdateVisibility();
    }

    void UpdateVisibility()
    {
        CredPanel.Visibility = DestBox.Text.StartsWith(@"\\") ? Visibility.Visible : Visibility.Collapsed;
        var inc = TagOf(IncMode);
        DailyPanel.Visibility = DaysPanel.Visibility = inc == "daily" ? Visibility.Visible : Visibility.Collapsed;
        HourlyPanel.Visibility = inc == "hourly" ? Visibility.Visible : Visibility.Collapsed;
        var full = TagOf(FullMode);
        FullDay.Visibility = full == "weekly" ? Visibility.Visible : Visibility.Collapsed;
        MonthPanel.Visibility = full == "monthly" ? Visibility.Visible : Visibility.Collapsed;
        FullTimeBox.IsEnabled = full != "none";
        EncPanel.Visibility = EncNote.Visibility = EncryptBox.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
    }

    void IncMode_SelectionChanged(object sender, SelectionChangedEventArgs e) { if (IsLoaded) UpdateVisibility(); }
    void FullMode_SelectionChanged(object sender, SelectionChangedEventArgs e) { if (IsLoaded) UpdateVisibility(); }
    void EncryptBox_Changed(object sender, RoutedEventArgs e) => UpdateVisibility();
    void DestBox_TextChanged(object sender, TextChangedEventArgs e) => UpdateVisibility();

    static string NormTime(string s)
    {
        if (!TimeSpan.TryParseExact(s.Trim(), [@"h\:mm", @"hh\:mm", "%h"], CultureInfo.InvariantCulture, out var t) || t >= TimeSpan.FromDays(1))
            throw new FormatException(T("時刻「{0}」は HH:mm で入力してください。", s));
        return $"{t.Hours:00}:{t.Minutes:00}";
    }

    /// <summary>画面の内容から JobConfig を作る。問題があれば FormatException。</summary>
    JobConfig Collect()
    {
        var j = _job ?? new JobConfig();
        var name = NameBox.Text.Trim();
        if (name.Length == 0) throw new FormatException(T("ジョブ名を入力してください。"));
        if (_cfg.Jobs.Any(o => o.Id != j.Id && o.Name == name)) throw new FormatException(T("同じ名前のジョブがあります。"));
        var sources = new List<string>();
        if (SystemBox.IsChecked == true) sources.Add("system");
        sources.AddRange(_volBoxes.Where(v => v.Box.IsChecked == true).Select(v => v.Spec));
        if (sources.Count == 0) throw new FormatException(T("バックアップする範囲を選んでください。"));
        var dest = DestBox.Text.Trim().TrimEnd('\\');
        if (dest.Length < 3) throw new FormatException(T("保存先を入力してください。"));
        if (sources.Contains("system") || sources.Any(s => dest.StartsWith(s, StringComparison.OrdinalIgnoreCase)))
        {
            var sysDrive = Environment.GetEnvironmentVariable("SystemDrive") ?? "C:";
            if ((sources.Contains("system") && dest.StartsWith(sysDrive, StringComparison.OrdinalIgnoreCase))
                || sources.Any(s => s.Length == 2 && dest.StartsWith(s, StringComparison.OrdinalIgnoreCase)))
                throw new FormatException(T("バックアップするドライブの中には保存できません。別のディスクか NAS を指定してください。"));
        }
        var s = new ScheduleConfig
        {
            Enabled = SchedBox.IsChecked == true,
            Incremental = TagOf(IncMode),
            Times = TimesBox.Text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(NormTime).Distinct().Order().ToList(),
            HourlyStart = NormTime(StartBox.Text),
            IntervalHours = int.TryParse(IntervalBox.Text, out var ih) && ih is >= 1 and <= 24 ? ih : throw new FormatException(T("間隔は 1〜24 時間にしてください。")),
            Days = Enumerable.Range(0, 7).Where(d => _days[d].IsChecked == true).Select(d => (DayOfWeek)d).ToList(),
            Full = TagOf(FullMode),
            FullDay = (DayOfWeek)(int)((ComboBoxItem)FullDay.SelectedItem).Tag,
            FullDayOfMonth = int.TryParse(FullDom.Text, out var dom) && dom is >= 1 and <= 31 ? dom : throw new FormatException(T("フルの日付は 1〜31 にしてください。")),
            FullTime = NormTime(FullTimeBox.Text),
            RunMissed = MissedBox.IsChecked == true,
        };
        if (s.Incremental == "daily" && (s.Times.Count == 0 || s.Days.Count == 0)) throw new FormatException(T("増分の時刻と曜日を指定してください。"));
        string? enc = null;
        if (EncryptBox.IsChecked == true)
        {
            if (_encSet != null && EncPass1.Password == "********" && EncPass2.Password == "********") enc = _encSet;
            else
            {
                if (EncPass1.Password.Length < 8) throw new FormatException(T("暗号化パスワードは 8 文字以上にしてください。"));
                if (EncPass1.Password != EncPass2.Password) throw new FormatException(T("暗号化パスワードが一致しません。"));
                enc = Secret.Protect(EncPass1.Password);
            }
        }
        j.Name = name;
        j.Enabled = EnabledBox.IsChecked == true;
        j.Sources = sources;
        j.Destination = new DestinationConfig
        {
            Path = dest,
            User = dest.StartsWith(@"\\") && UserBox.Text.Trim().Length > 0 ? UserBox.Text.Trim() : null,
            PasswordEnc = dest.StartsWith(@"\\") && PassBox.Password.Length > 0 ? Secret.Protect(PassBox.Password) : null,
        };
        if (string.IsNullOrEmpty(j.RepoFolder)) j.RepoFolder = JobConfig.SafeFolder(name);
        j.EncryptionPasswordEnc = enc;
        j.Schedule = s;
        j.Keep = int.TryParse(KeepBox.Text, out var k) && k is >= 1 and <= 1000 ? k : throw new FormatException(T("残す数は 1〜1000 にしてください。"));
        j.VerifyAfterBackup = VerifyBox.IsChecked == true;
        j.ExcludeSystemFiles = ExcludeBox.IsChecked == true;
        j.VssWriters = WritersBox.IsChecked == true;
        j.VssFullBackup = j.VssWriters && VssFullBox.IsChecked == true;
        j.CompressionLevel = int.Parse(TagOf(LevelBox));
        return j;
    }

    void Save_Click(object sender, RoutedEventArgs e)
    {
        JobConfig j;
        var before = _job == null ? null : System.Text.Json.JsonSerializer.Serialize(_job, Storage.Repository.Json);
        try { j = Collect(); }
        catch (FormatException ex)
        {
            MessageBox.Show(ex.Message, T("入力の確認"), MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        var cfg = AppConfig.Load();
        var idx = cfg.Jobs.FindIndex(o => o.Id == j.Id);
        if (idx >= 0)
        {
            var old = cfg.Jobs[idx];
            if (old.EncryptionPasswordEnc != j.EncryptionPasswordEnc && Storage.Repository.Exists(old.RepoPath))
            {
                var ans = MessageBox.Show(T("既にバックアップがある保存先の暗号化設定（パスワード）は変えられません。\n新しい設定は、保存先フォルダを変えるか古いバックアップを消したときから有効になります。\nこのまま保存しますか？"),
                    T("暗号化"), MessageBoxButton.YesNo, MessageBoxImage.Warning);
                if (ans != MessageBoxResult.Yes) return;
            }
            cfg.Jobs[idx] = j;
        }
        else cfg.Jobs.Add(j);
        try { cfg.Save(); }
        catch (UnauthorizedAccessException)
        {
            MessageBox.Show(T("設定を保存できません。管理者として実行してください。"), T("保存"), MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        ServiceCtl.Reload();
        _isNew = false;
        _job = j;
        _cfg = cfg;
        JobList.ItemsSource = null;
        JobList.ItemsSource = _cfg.Jobs;
        JobList.SelectedItem = _cfg.Jobs.First(o => o.Id == j.Id);
        SaveStatus.Text = T("保存しました（{0:HH:mm:ss}）", DateTime.Now);
    }

    void Revert_Click(object sender, RoutedEventArgs e)
    {
        if (_isNew) { OnShown(); return; }
        _cfg = AppConfig.Load();
        _job = _cfg.Job(_job?.Id ?? "");
        if (_job != null) Show(_job);
    }

    void New_Click(object sender, RoutedEventArgs e) => NewJob();

    void Delete_Click(object sender, RoutedEventArgs e)
    {
        if (_job == null || _isNew) return;
        if (MessageBox.Show(T("ジョブ「{0}」を削除しますか？\n保存先のバックアップ（{1}）は消しません。", _job.Name, _job.RepoPath), T("削除"),
                MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        var cfg = AppConfig.Load();
        cfg.Jobs.RemoveAll(o => o.Id == _job.Id);
        cfg.Save();
        ServiceCtl.Reload();
        _job = null;
        OnShown();
    }

    void Browse_Click(object sender, RoutedEventArgs e)
    {
        if (Dialogs.PickFolder(Window.GetWindow(this), T("保存先フォルダ"), DestBox.Text) is { } p) DestBox.Text = p;
    }

    async void TestDest_Click(object sender, RoutedEventArgs e)
    {
        var dest = new DestinationConfig
        {
            Path = DestBox.Text.Trim(),
            User = UserBox.Text.Trim().Length > 0 ? UserBox.Text.Trim() : null,
            PasswordEnc = Secret.Protect(PassBox.Password),
        };
        var msg = await Task.Run(() => TestDestination(dest));
        MessageBox.Show(msg, T("接続テスト"), MessageBoxButton.OK, msg.StartsWith("OK") ? MessageBoxImage.Information : MessageBoxImage.Warning);
    }

    internal static string TestDestination(DestinationConfig dest)
    {
        try
        {
            using var share = NetShare.Connect(dest);
            Directory.CreateDirectory(dest.Path);
            var probe = Path.Combine(dest.Path, $".diskvault-test-{Environment.ProcessId}");
            File.WriteAllText(probe, "test");
            File.Delete(probe);
            var root = Path.GetPathRoot(Path.GetFullPath(dest.Path));
            string free = "";
            try
            {
                var di = new DriveInfo(root!);
                free = T("（空き {0}）", Fmt.Size(di.AvailableFreeSpace));
            }
            catch (ArgumentException) { }
            catch (IOException) { }
            return T("OK: 書き込みできました。") + free;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return T("接続できません: {0}", ex.Message);
        }
    }
}



