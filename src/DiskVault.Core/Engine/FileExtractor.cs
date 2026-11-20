using DiskVault.Ntfs;

namespace DiskVault.Engine;

public sealed record ExtractResult(int Files, long Bytes, int Errors);

/// <summary>復元ポイント内の NTFS からファイル／フォルダを取り出す（ファイル単位の復元）。</summary>
public static class FileExtractor
{
    public static ExtractResult Extract(NtfsVolume ntfs, NtfsEntry entry, string outDir, Action<string>? log = null,
        IProgress<(int Files, long Bytes, string Current)>? progress = null, CancellationToken ct = default, bool overwrite = true)
    {
        log ??= _ => { };
        int files = 0, errors = 0;
        long bytes = 0;
        Directory.CreateDirectory(outDir);

        void One(NtfsEntry e, string dest, int depth)
        {
            ct.ThrowIfCancellationRequested();
            if (e.IsDirectory)
            {
                if (depth > 256) return;
                Directory.CreateDirectory(dest);
                List<NtfsEntry> children;
                try { children = ntfs.ListDirectory(e.Record); }
                catch (Exception ex) when (ex is InvalidDataException or IOException) { log($"読めないフォルダ: {dest}: {ex.Message}"); errors++; return; }
                foreach (var c in children) One(c, Path.Combine(dest, Safe(c.Name)), depth + 1);
                TrySetTimes(dest, e.Created, e.Modified, dir: true);
                return;
            }
            try
            {
                if (!overwrite && File.Exists(dest)) return;
                var info = ntfs.FileInfo(e.Record);
                using (var fs = new FileStream(dest, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16))
                    bytes += ntfs.CopyFile(e.Record, fs, ct);
                TrySetTimes(dest, info.Created, info.Modified, dir: false);
                // 読み取り専用・隠し属性などを戻す（アーカイブ・圧縮等の内部属性は除く）
                var attr = (FileAttributes)(info.Attributes & 0x7) ;
                if (attr != 0) File.SetAttributes(dest, attr);
                files++;
                progress?.Report((files, bytes, dest));
            }
            catch (Exception ex) when (ex is InvalidDataException or IOException or NotSupportedException or UnauthorizedAccessException)
            {
                log($"取り出せません: {dest}: {ex.Message}");
                errors++;
            }
        }

        One(entry, entry.Record == NtfsVolume.RootDir ? outDir : Path.Combine(outDir, Safe(entry.Name)), 0);
        return new ExtractResult(files, bytes, errors);
    }

    static string Safe(string name)
    {
        foreach (var c in Path.GetInvalidFileNameChars()) name = name.Replace(c, '_');
        return name;
    }

    static void TrySetTimes(string path, DateTime created, DateTime modified, bool dir)
    {
        try
        {
            if (dir)
            {
                if (created > DateTime.MinValue) Directory.SetCreationTimeUtc(path, created);
                if (modified > DateTime.MinValue) Directory.SetLastWriteTimeUtc(path, modified);
            }
            else
            {
                if (created > DateTime.MinValue) File.SetCreationTimeUtc(path, created);
                if (modified > DateTime.MinValue) File.SetLastWriteTimeUtc(path, modified);
            }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        catch (ArgumentOutOfRangeException) { }
    }
}
