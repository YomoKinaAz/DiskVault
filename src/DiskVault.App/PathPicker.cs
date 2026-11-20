using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using DiskVault.Platform;

namespace DiskVault.App;

/// <summary>
/// フォルダ・ファイル選択。通常は Windows 標準のダイアログを使い、WinPE（標準ダイアログの COM が無い）では自前の画面を出す。
/// </summary>
static class Dialogs
{
    public static string? PickFolder(Window? owner, string title, string? initial = null)
    {
        if (!Sys.IsWinPE)
        {
            try
            {
                var d = new Microsoft.Win32.OpenFolderDialog { Title = title };
                if (!string.IsNullOrEmpty(initial) && Directory.Exists(initial)) d.InitialDirectory = initial;
                return d.ShowDialog(owner) == true ? d.FolderName : null;
            }
            catch (COMException) { }
        }
        return new PathPickerWindow(title, PickMode.Folder, null, initial) { Owner = owner }.Run();
    }

    public static string? PickOpenFile(Window? owner, string title, string ext)
    {
        if (!Sys.IsWinPE)
        {
            try
            {
                var d = new Microsoft.Win32.OpenFileDialog { Title = title, Filter = $"*{ext}|*{ext}" };
                return d.ShowDialog(owner) == true ? d.FileName : null;
            }
            catch (COMException) { }
        }
        return new PathPickerWindow(title, PickMode.OpenFile, ext, null) { Owner = owner }.Run();
    }

    public static string? PickSaveFile(Window? owner, string title, string ext, string initialPath)
    {
        if (!Sys.IsWinPE)
        {
            try
            {
                var d = new Microsoft.Win32.SaveFileDialog { Title = title, Filter = $"*{ext}|*{ext}", FileName = Path.GetFileName(initialPath) };
                var dir = Path.GetDirectoryName(initialPath);
                if (dir != null && Directory.Exists(dir)) d.InitialDirectory = dir;
                return d.ShowDialog(owner) == true ? d.FileName : null;
            }
            catch (COMException) { }
        }
        return new PathPickerWindow(title, PickMode.SaveFile, ext, initialPath) { Owner = owner }.Run();
    }
}

enum PickMode { Folder, OpenFile, SaveFile }

/// <summary>ドライブとフォルダのツリー＋パス入力欄だけの簡単な選択画面（WinPE 用）。</summary>
sealed class PathPickerWindow : Window
{
    readonly PickMode _mode;
    readonly string? _ext;
    readonly TextBox _path = new() { Margin = new Thickness(0, 0, 0, 8) };
    readonly TreeView _tree = new();
    readonly ListBox _files = new() { Margin = new Thickness(8, 0, 0, 0) };
    string? _result;

    public PathPickerWindow(string title, PickMode mode, string? ext, string? initial)
    {
        _mode = mode;
        _ext = ext;
        Title = title;
        Width = 760;
        Height = 520;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        FontFamily = new System.Windows.Media.FontFamily("Yu Gothic UI, Meiryo UI");
        FontSize = 13;
        _path.Text = initial ?? "";

        var ok = new Button { Content = "OK", IsDefault = true, MinWidth = 80, Margin = new Thickness(0, 0, 8, 0) };
        var cancel = new Button { Content = "キャンセル", IsCancel = true, MinWidth = 80 };
        ok.Click += (_, _) => Accept();
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 10, 0, 0) };
        buttons.Children.Add(ok);
        buttons.Children.Add(cancel);

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        if (mode != PickMode.Folder) grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.Children.Add(_tree);
        if (mode != PickMode.Folder)
        {
            Grid.SetColumn(_files, 1);
            grid.Children.Add(_files);
        }
        var root = new DockPanel { Margin = new Thickness(14) };
        var label = new TextBlock
        {
            Text = mode == PickMode.Folder ? "フォルダ（直接入力もできます）:" : "ファイル（直接入力もできます）:",
            Margin = new Thickness(0, 0, 0, 4),
        };
        DockPanel.SetDock(label, Dock.Top);
        DockPanel.SetDock(_path, Dock.Top);
        DockPanel.SetDock(buttons, Dock.Bottom);
        root.Children.Add(label);
        root.Children.Add(_path);
        root.Children.Add(buttons);
        root.Children.Add(grid);
        Content = root;

        foreach (var d in DriveInfo.GetDrives().Where(d => d.IsReady))
        {
            string label2;
            try { label2 = $"{d.Name.TrimEnd('\\')}  {d.VolumeLabel}  ({Fmt.Size(d.TotalSize)})"; }
            catch (IOException) { label2 = d.Name; }
            _tree.Items.Add(Node(d.RootDirectory.FullName, label2));
        }
        _tree.SelectedItemChanged += (_, _) =>
        {
            if (_tree.SelectedItem is TreeViewItem { Tag: string dir })
            {
                _path.Text = _mode == PickMode.SaveFile ? Path.Combine(dir, Path.GetFileName(_path.Text)) : dir;
                ListFiles(dir);
            }
        };
        _files.SelectionChanged += (_, _) =>
        {
            if (_files.SelectedItem is ListBoxItem { Tag: string f }) _path.Text = f;
        };
        _files.MouseDoubleClick += (_, _) => { if (_files.SelectedItem != null) Accept(); };
    }

    TreeViewItem Node(string dir, string? header = null)
    {
        var item = new TreeViewItem { Header = header ?? Path.GetFileName(dir.TrimEnd('\\')), Tag = dir };
        item.Items.Add("…");
        item.Expanded += (_, e) =>
        {
            if (e.OriginalSource != item || item.Items.Count != 1 || item.Items[0] is not string) return;
            item.Items.Clear();
            try
            {
                foreach (var sub in Directory.GetDirectories(dir).Order(StringComparer.OrdinalIgnoreCase))
                {
                    var attr = File.GetAttributes(sub);
                    if ((attr & (FileAttributes.Hidden | FileAttributes.System)) == 0) item.Items.Add(Node(sub));
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        };
        return item;
    }

    void ListFiles(string dir)
    {
        if (_mode == PickMode.Folder) return;
        _files.Items.Clear();
        try
        {
            foreach (var f in Directory.GetFiles(dir, "*" + _ext).Order(StringComparer.OrdinalIgnoreCase))
                _files.Items.Add(new ListBoxItem { Content = Path.GetFileName(f), Tag = f });
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    void Accept()
    {
        var p = _path.Text.Trim();
        if (p.Length == 0) return;
        bool ok = _mode switch
        {
            PickMode.Folder => Directory.Exists(p),
            PickMode.OpenFile => File.Exists(p),
            _ => Directory.Exists(Path.GetDirectoryName(Path.GetFullPath(p))),
        };
        if (!ok)
        {
            MessageBox.Show(this, $"{p} が見つかりません。", Title);
            return;
        }
        if (_mode == PickMode.SaveFile && _ext != null && !p.EndsWith(_ext, StringComparison.OrdinalIgnoreCase)) p += _ext;
        _result = p;
        DialogResult = true;
    }

    public string? Run() => ShowDialog() == true ? _result : null;
}
