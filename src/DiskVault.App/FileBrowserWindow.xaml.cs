using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using DiskVault.Engine;
using DiskVault.Ntfs;
using DiskVault.Storage;

namespace DiskVault.App;

public sealed class FileRow(NtfsEntry e, long size) : INotifyPropertyChanged
{
    bool _sel;
    public NtfsEntry Entry { get; } = e;
    public string Name => (Entry.IsDirectory ? "📁 " : "") + Entry.Name;
    public string SizeText => Entry.IsDirectory ? "" : Fmt.Size(size);
    public string ModifiedText => Entry.Modified > DateTime.MinValue ? Entry.Modified.ToLocalTime().ToString("yyyy/MM/dd HH:mm") : "";
    public bool Selected
    {
        get => _sel;
        set { _sel = value; PropertyChanged?.Invoke(this, new(nameof(Selected))); }
    }
    public event PropertyChangedEventHandler? PropertyChanged;
}

public partial class FileBrowserWindow : Window
{
    readonly PointVolumeSource _src;
    readonly NtfsVolume? _ntfs;
    readonly object _io = new();
    NtfsEntry _cur = new() { Ref = NtfsVolume.RootDir, Name = "", IsDirectory = true };
    readonly Stack<NtfsEntry> _path = new();
    CancellationTokenSource? _cts;

    public FileBrowserWindow(Repository repo, RecoveryPoint p, VolumeRecord v)
    {
        InitializeComponent();
        Header.Text = T("{0:yyyy/MM/dd HH:mm} の {1}", p.Time.ToLocalTime(), v.Display.Trim());
        _src = new PointVolumeSource(repo, p, v.Index);
        try { _ntfs = NtfsVolume.Open(_src); }
        catch (InvalidDataException e)
        {
            MessageBox.Show(e.Message, T("開けません"));
            Loaded += (_, _) => Close();
            return;
        }
        var root = MakeNode(_cur, v.Letter ?? "\\");
        Tree.Items.Add(root);
        root.IsExpanded = true;
        root.IsSelected = true;
        Closed += (_, _) => { _cts?.Cancel(); _src.Dispose(); };
    }

    List<NtfsEntry> List(NtfsEntry dir)
    {
        lock (_io) return _ntfs!.ListDirectory(dir.Record);
    }

    TreeViewItem MakeNode(NtfsEntry e, string? header = null)
    {
        var item = new TreeViewItem { Header = header ?? e.Name, Tag = e };
        item.Items.Add("…");
        item.Expanded += async (_, args) =>
        {
            if (args.OriginalSource != item || item.Items.Count != 1 || item.Items[0] is not string) return;
            List<NtfsEntry> dirs;
            try { dirs = (await Task.Run(() => List(e))).Where(c => c.IsDirectory).ToList(); }
            catch (Exception ex) when (ex is InvalidDataException or IOException) { item.Items.Clear(); Status.Text = ex.Message; return; }
            item.Items.Clear();
            foreach (var d in dirs) item.Items.Add(MakeNode(d));
        };
        return item;
    }

    async void Tree_SelectedItemChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
    {
        if (Tree.SelectedItem is TreeViewItem { Tag: NtfsEntry ent })
        {
            // ツリーの位置からパスを組み立てる
            var parts = new List<string>();
            for (var it = (TreeViewItem?)Tree.SelectedItem; it != null; it = ItemsControl.ItemsControlFromItemContainer(it) as TreeViewItem)
                if (it.Tag is NtfsEntry pe && pe.Record != NtfsVolume.RootDir) parts.Insert(0, pe.Name);
            await Open(ent, "\\" + string.Join("\\", parts));
        }
    }

    string _curPath = "\\";

    async Task Open(NtfsEntry dir, string path)
    {
        _cur = dir;
        _curPath = path;
        PathBox.Text = path;
        Status.Text = T("読み込み中…");
        try
        {
            var rows = await Task.Run(() =>
            {
                var list = List(dir);
                lock (_io) return list.Select(c => new FileRow(c, c.IsDirectory ? 0 : _ntfs!.FileInfo(c.Record).Size)).ToList();
            });
            Files.ItemsSource = rows;
            Status.Text = T("{0} フォルダ、{1} ファイル", rows.Count(r => r.Entry.IsDirectory), rows.Count(r => !r.Entry.IsDirectory));
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException)
        {
            Status.Text = ex.Message;
        }
    }

    async void Files_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (Files.SelectedItem is FileRow { Entry.IsDirectory: true } r)
        {
            _path.Push(_cur);
            await Open(r.Entry, _curPath.TrimEnd('\\') + "\\" + r.Entry.Name);
        }
    }

    async void Up_Click(object sender, RoutedEventArgs e)
    {
        if (_curPath == "\\") return;
        var parentPath = _curPath[.._curPath.LastIndexOf('\\')];
        if (parentPath.Length == 0) parentPath = "\\";
        NtfsEntry? parent;
        lock (_io) parent = _ntfs!.Resolve(parentPath);
        if (parent != null) await Open(parent, parentPath);
    }

    async void Restore_Click(object sender, RoutedEventArgs e)
    {
        var rows = (Files.ItemsSource as List<FileRow>) ?? [];
        var targets = rows.Where(r => r.Selected).Select(r => r.Entry).ToList();
        if (targets.Count == 0)
        {
            var sel = Files.SelectedItems.OfType<FileRow>().Select(r => r.Entry).ToList();
            targets = sel.Count > 0 ? sel : [_cur];
        }
        var outDir = Dialogs.PickFolder(this, T("復元先のフォルダ"));
        if (outDir == null) return;
        RestoreButton.IsEnabled = false;
        _cts = new CancellationTokenSource();
        var errors = new List<string>();
        var prog = new Progress<(int Files, long Bytes, string Current)>(x => Status.Text = T("{0} ファイル（{1}）", x.Files, Fmt.Size(x.Bytes)) + "  " + x.Current);
        int files = 0;
        long bytes = 0;
        try
        {
            await Task.Run(() =>
            {
                foreach (var t in targets)
                {
                    ExtractResult r;
                    lock (_io) r = FileExtractor.Extract(_ntfs!, t, outDir, m => { lock (errors) errors.Add(m); }, prog, _cts.Token);
                    files += r.Files;
                    bytes += r.Bytes;
                }
            });
            Status.Text = T("{0} ファイル（{1}）を {2} に復元しました。", files, Fmt.Size(bytes), outDir);
            var msg = T("{0} ファイル（{1}）を復元しました。", files, Fmt.Size(bytes)) + "\n" + outDir;
            if (errors.Count > 0) msg += "\n\n" + T("{0} 件のエラー:", errors.Count) + "\n" + string.Join("\n", errors.Take(10));
            MessageBox.Show(msg, T("復元"), MessageBoxButton.OK, errors.Count > 0 ? MessageBoxImage.Warning : MessageBoxImage.Information);
        }
        catch (OperationCanceledException) { Status.Text = T("中止しました。"); }
        finally { RestoreButton.IsEnabled = true; }
    }

    void Close_Click(object sender, RoutedEventArgs e) => Close();
}
