using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DiskVault.Storage;

public sealed class EncryptionInfo
{
    public string Kdf { get; set; } = "pbkdf2-sha256";
    public int Iterations { get; set; } = 600_000;
    public string Salt { get; set; } = "";
    /// <summary>パスワード由来の鍵で AES-GCM 包装した 64 バイトのマスター鍵（暗号鍵 32 + ID 鍵 32）。nonce+暗号文+tag。</summary>
    public string WrappedKey { get; set; } = "";
}

public sealed class RepoConfig
{
    public int Format { get; set; } = 1;
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public DateTime Created { get; set; } = DateTime.UtcNow;
    public int BlockSize { get; set; } = 256 * 1024;
    public int CompressionLevel { get; set; } = 3;
    public EncryptionInfo? Encryption { get; set; }
}

/// <summary>
/// バックアップ先フォルダ（リポジトリ）。
///   repo.json
///   chains\{chain}\packs\xx\{pack}.pack / .idx   … ブロック本体
///   points\{point}\point.json, vol{N}.map         … 復元ポイント（point.json が最後に書かれる＝確定）
///   lock                                          … 実行中ロック
/// </summary>
public sealed class Repository : IDisposable
{
    public string Root { get; }
    public RepoConfig Config { get; }
    public BlockCodec Codec { get; }
    FileStream? _lock;

    public static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() },
    };

    Repository(string root, RepoConfig cfg, BlockCodec codec)
    {
        Root = root;
        Config = cfg;
        Codec = codec;
    }

    public string PointsDir => Path.Combine(Root, "points");
    public string ChainsDir => Path.Combine(Root, "chains");
    public string ChainDir(string chain) => Path.Combine(ChainsDir, chain);
    public string PointDir(string point) => Path.Combine(PointsDir, point);

    public static bool Exists(string root) => File.Exists(Path.Combine(root, "repo.json"));

    public static Repository Create(string root, string? password, int blockSize = 256 * 1024, int level = 3)
    {
        if (Exists(root)) throw new IOException(T("{0} には既にリポジトリがあります。", root));
        if (blockSize < 4096 || (blockSize & (blockSize - 1)) != 0 || blockSize > 16 << 20)
            throw new ArgumentException(T("ブロックサイズは 4KiB〜16MiB の 2 の累乗にしてください。"));
        Directory.CreateDirectory(root);
        var cfg = new RepoConfig { BlockSize = blockSize, CompressionLevel = level };
        byte[]? enc = null, idk = null;
        if (!string.IsNullOrEmpty(password))
        {
            var master = RandomNumberGenerator.GetBytes(64);
            var salt = RandomNumberGenerator.GetBytes(16);
            var info = new EncryptionInfo { Salt = Convert.ToBase64String(salt) };
            var kek = Rfc2898DeriveBytes.Pbkdf2(password, salt, info.Iterations, HashAlgorithmName.SHA256, 32);
            var wrapped = new byte[12 + 64 + 16];
            RandomNumberGenerator.Fill(wrapped.AsSpan(0, 12));
            using (var gcm = new AesGcm(kek, 16))
                gcm.Encrypt(wrapped.AsSpan(0, 12), master, wrapped.AsSpan(12, 64), wrapped.AsSpan(76, 16));
            info.WrappedKey = Convert.ToBase64String(wrapped);
            cfg.Encryption = info;
            enc = master[..32];
            idk = master[32..];
        }
        Directory.CreateDirectory(Path.Combine(root, "points"));
        Directory.CreateDirectory(Path.Combine(root, "chains"));
        AtomicWrite(Path.Combine(root, "repo.json"), JsonSerializer.SerializeToUtf8Bytes(cfg, Json));
        return new Repository(root, cfg, new BlockCodec(level, enc, idk));
    }

    public static Repository Open(string root, string? password)
    {
        var path = Path.Combine(root, "repo.json");
        if (!File.Exists(path)) throw new FileNotFoundException(T("{0} はバックアップ先（リポジトリ）ではありません。", root), path);
        var cfg = JsonSerializer.Deserialize<RepoConfig>(File.ReadAllBytes(path), Json)
                  ?? throw new InvalidDataException(T("repo.json を読めません。"));
        if (cfg.Format != 1) throw new InvalidDataException(T("未対応のリポジトリ形式 {0}。", cfg.Format));
        byte[]? enc = null, idk = null;
        if (cfg.Encryption is { } e)
        {
            if (string.IsNullOrEmpty(password)) throw new UnauthorizedAccessException(T("このバックアップは暗号化されています。パスワードを指定してください。"));
            var kek = Rfc2898DeriveBytes.Pbkdf2(password, Convert.FromBase64String(e.Salt), e.Iterations, HashAlgorithmName.SHA256, 32);
            var w = Convert.FromBase64String(e.WrappedKey);
            var master = new byte[64];
            try
            {
                using var gcm = new AesGcm(kek, 16);
                gcm.Decrypt(w.AsSpan(0, 12), w.AsSpan(12, 64), w.AsSpan(76, 16), master);
            }
            catch (CryptographicException) { throw new UnauthorizedAccessException(T("パスワードが違います。")); }
            enc = master[..32];
            idk = master[32..];
        }
        return new Repository(root, cfg, new BlockCodec(cfg.CompressionLevel, enc, idk));
    }

    public static Repository OpenOrCreate(string root, string? password, int blockSize = 256 * 1024, int level = 3) =>
        Exists(root) ? Open(root, password) : Create(root, password, blockSize, level);

    public bool IsEncrypted => Config.Encryption != null;

    /// <summary>排他ロックを取る（別プロセスが実行中なら IOException）。</summary>
    public void Lock()
    {
        if (_lock != null) return;
        var p = Path.Combine(Root, "lock");
        try
        {
            _lock = new FileStream(p, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.Read, 1, FileOptions.DeleteOnClose);
        }
        catch (IOException)
        {
            string who = "";
            try { who = File.ReadAllText(p); } catch { }
            throw new IOException(T("このバックアップ先は別の処理が使用中です。{0}", who).Trim());
        }
        var info = System.Text.Encoding.UTF8.GetBytes($"({Environment.MachineName} pid {Environment.ProcessId} {DateTime.Now:yyyy-MM-dd HH:mm})");
        _lock.SetLength(0);
        _lock.Write(info);
        _lock.Flush();
    }

    public void Unlock()
    {
        _lock?.Dispose();
        _lock = null;
    }

    public void Dispose() => Unlock();

    // ---- 復元ポイント ----

    /// <summary>確定済みの復元ポイント（古い順）。</summary>
    public List<RecoveryPoint> ListPoints()
    {
        var list = new List<RecoveryPoint>();
        if (!Directory.Exists(PointsDir)) return list;
        foreach (var d in Directory.EnumerateDirectories(PointsDir))
        {
            var f = Path.Combine(d, "point.json");
            if (!File.Exists(f)) continue;
            try
            {
                var p = JsonSerializer.Deserialize<RecoveryPoint>(File.ReadAllBytes(f), Json);
                if (p != null) { p.Id = Path.GetFileName(d); list.Add(p); }
            }
            catch (JsonException) { Trace.WriteLine($"壊れた point.json: {f}"); }
        }
        list.Sort((a, b) => a.Time.CompareTo(b.Time));
        return list;
    }

    public RecoveryPoint GetPoint(string id)
    {
        var all = ListPoints();
        if (id == "latest") return all.LastOrDefault() ?? throw new InvalidOperationException(T("復元ポイントがありません。"));
        return all.FirstOrDefault(p => p.Id == id)
               ?? all.FirstOrDefault(p => p.Id.StartsWith(id, StringComparison.Ordinal))
               ?? throw new KeyNotFoundException(T("復元ポイント {0} が見つかりません。", id));
    }

    public void SavePoint(RecoveryPoint p)
    {
        Directory.CreateDirectory(PointDir(p.Id));
        AtomicWrite(Path.Combine(PointDir(p.Id), "point.json"), JsonSerializer.SerializeToUtf8Bytes(p, Json));
    }

    public string MapPath(string point, int volume) => Path.Combine(PointDir(point), $"vol{volume}.map");

    public BlockMap LoadMap(RecoveryPoint p, int volume) => BlockMap.Load(MapPath(p.Id, volume));

    public ChainStore OpenChain(string chain, bool forWrite) => new(this, chain, forWrite);

    public static void AtomicWrite(string path, byte[] data)
    {
        var tmp = path + ".tmp";
        using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            fs.Write(data);
            fs.Flush(true);
        }
        File.Move(tmp, path, true);
    }
}
