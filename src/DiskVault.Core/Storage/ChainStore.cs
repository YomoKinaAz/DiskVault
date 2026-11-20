using System.Buffers.Binary;
using System.Security.Cryptography;
using Microsoft.Win32.SafeHandles;

namespace DiskVault.Storage;

public readonly record struct BlockLoc(int Pack, long Offset, int PayloadLen, int RawLen, byte Codec);

/// <summary>
/// チェーン（フル 1 回 + その後の増分）のブロック置き場。ブロックは pack ファイルに追記し、
/// pack を閉じるときに索引（.idx）を書く。pack は .tmp に書いてから改名するので、途中で落ちても壊れた pack は残らない。
/// pack レコード: id(32) codec(1) rawLen(4) payloadLen(4) payload
/// </summary>
public sealed class ChainStore : IDisposable
{
    const uint PackMagic = 0x4B505644, IdxMagic = 0x58495644; // "DVPK" "DVIX"
    const int PackHeader = 8, RecHeader = 41, IdxEntry = 49;

    readonly Repository _repo;
    readonly bool _write;
    readonly List<string> _packs = [];          // pack 番号 → パス（.pack）
    readonly Dictionary<BlockId, BlockLoc> _index = [];
    readonly Dictionary<int, SafeFileHandle> _handles = [];
    readonly object _hlock = new();

    FileStream? _cur;
    int _curPack = -1;
    string? _curTmp;
    readonly List<(BlockId Id, BlockLoc Loc)> _curEntries = [];

    public string Chain { get; }
    public string Dir { get; }
    public long PackTargetSize { get; set; } = 64L << 20;
    public int BlockCount => _index.Count;
    public long WrittenBytes { get; private set; }

    internal ChainStore(Repository repo, string chain, bool forWrite)
    {
        _repo = repo;
        _write = forWrite;
        Chain = chain;
        Dir = repo.ChainDir(chain);
        if (forWrite) Directory.CreateDirectory(Path.Combine(Dir, "packs"));
        else if (!Directory.Exists(Dir)) throw new DirectoryNotFoundException($"チェーン {chain} のデータが見つかりません。");
        LoadIndex();
    }

    static string NewPackId() => DateTime.UtcNow.ToString("yyyyMMddHHmmss") + "-" + Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(6));

    string PackPath(string id) => Path.Combine(Dir, "packs", id[^2..], id + ".pack");

    void LoadIndex()
    {
        var packsDir = Path.Combine(Dir, "packs");
        if (!Directory.Exists(packsDir)) return;
        foreach (var sub in Directory.EnumerateDirectories(packsDir))
        {
            foreach (var f in Directory.EnumerateFiles(sub))
            {
                if (f.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase))
                {
                    // 前回の中断で残った書きかけ
                    if (_write) TryDelete(f);
                    continue;
                }
                if (!f.EndsWith(".pack", StringComparison.OrdinalIgnoreCase)) continue;
                int no = _packs.Count;
                _packs.Add(f);
                var idx = Path.ChangeExtension(f, ".idx");
                List<(BlockId, BlockLoc)> entries;
                try { entries = File.Exists(idx) ? ReadIdx(idx, no) : ScanPack(f, no, writeIdx: _write); }
                catch (InvalidDataException) { entries = ScanPack(f, no, writeIdx: _write); }
                foreach (var (id, loc) in entries) _index.TryAdd(id, loc);
            }
        }
    }

    static List<(BlockId, BlockLoc)> ReadIdx(string path, int pack)
    {
        var b = File.ReadAllBytes(path);
        if (b.Length < 12 + 32 || BinaryPrimitives.ReadUInt32LittleEndian(b) != IdxMagic) throw new InvalidDataException(path);
        int count = BinaryPrimitives.ReadInt32LittleEndian(b.AsSpan(8));
        if (12L + (long)count * IdxEntry + 32 != b.Length) throw new InvalidDataException(path);
        if (!SHA256.HashData(b.AsSpan(0, b.Length - 32)).AsSpan().SequenceEqual(b.AsSpan(b.Length - 32))) throw new InvalidDataException(path);
        var list = new List<(BlockId, BlockLoc)>(count);
        for (int i = 0; i < count; i++)
        {
            var e = b.AsSpan(12 + i * IdxEntry, IdxEntry);
            list.Add((new BlockId(e), new BlockLoc(pack, BinaryPrimitives.ReadInt64LittleEndian(e[32..]),
                BinaryPrimitives.ReadInt32LittleEndian(e[40..]), BinaryPrimitives.ReadInt32LittleEndian(e[44..]), e[48])));
        }
        return list;
    }

    static void WriteIdx(string path, List<(BlockId Id, BlockLoc Loc)> entries)
    {
        var b = new byte[12 + entries.Count * IdxEntry + 32];
        BinaryPrimitives.WriteUInt32LittleEndian(b, IdxMagic);
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(4), 1);
        BinaryPrimitives.WriteInt32LittleEndian(b.AsSpan(8), entries.Count);
        for (int i = 0; i < entries.Count; i++)
        {
            var e = b.AsSpan(12 + i * IdxEntry, IdxEntry);
            var (id, loc) = entries[i];
            id.Write(e);
            BinaryPrimitives.WriteInt64LittleEndian(e[32..], loc.Offset);
            BinaryPrimitives.WriteInt32LittleEndian(e[40..], loc.PayloadLen);
            BinaryPrimitives.WriteInt32LittleEndian(e[44..], loc.RawLen);
            e[48] = loc.Codec;
        }
        SHA256.HashData(b.AsSpan(0, b.Length - 32), b.AsSpan(b.Length - 32));
        Repository.AtomicWrite(path, b);
    }

    /// <summary>索引が無い／壊れた pack を先頭から読んで索引を作り直す。</summary>
    static List<(BlockId, BlockLoc)> ScanPack(string path, int pack, bool writeIdx)
    {
        var list = new List<(BlockId, BlockLoc)>();
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16);
        Span<byte> h = stackalloc byte[RecHeader];
        if (fs.Read(h[..PackHeader]) != PackHeader || BinaryPrimitives.ReadUInt32LittleEndian(h) != PackMagic) return list;
        long pos = PackHeader;
        while (pos + RecHeader <= fs.Length)
        {
            fs.Position = pos;
            fs.ReadExactly(h);
            int raw = BinaryPrimitives.ReadInt32LittleEndian(h[33..]);
            int plen = BinaryPrimitives.ReadInt32LittleEndian(h[37..]);
            if (plen <= 0 || raw <= 0 || pos + RecHeader + plen > fs.Length) break;
            list.Add((new BlockId(h), new BlockLoc(pack, pos, plen, raw, h[32])));
            pos += RecHeader + plen;
        }
        if (writeIdx) WriteIdx(Path.ChangeExtension(path, ".idx"), list);
        return list;
    }

    public bool Contains(BlockId id) => _index.ContainsKey(id);

    /// <summary>ブロックを追加する（既にあれば何もしない）。書いたバイト数を返す。</summary>
    public long Add(BlockId id, byte codec, int rawLen, ReadOnlySpan<byte> payload)
    {
        if (!_write) throw new InvalidOperationException("読み取り専用で開いています。");
        if (_index.ContainsKey(id)) return 0;
        if (_cur == null) StartPack();
        long off = _cur!.Position;
        Span<byte> h = stackalloc byte[RecHeader];
        id.Write(h);
        h[32] = codec;
        BinaryPrimitives.WriteInt32LittleEndian(h[33..], rawLen);
        BinaryPrimitives.WriteInt32LittleEndian(h[37..], payload.Length);
        _cur.Write(h);
        _cur.Write(payload);
        var loc = new BlockLoc(_curPack, off, payload.Length, rawLen, codec);
        _index[id] = loc;
        _curEntries.Add((id, loc));
        long n = RecHeader + payload.Length;
        WrittenBytes += n;
        if (_cur.Position >= PackTargetSize) SealPack();
        return n;
    }

    void StartPack()
    {
        var id = NewPackId();
        var path = PackPath(id);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        _curTmp = path + ".tmp";
        _cur = new FileStream(_curTmp, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.Read, 1 << 20);
        Span<byte> h = stackalloc byte[PackHeader];
        BinaryPrimitives.WriteUInt32LittleEndian(h, PackMagic);
        BinaryPrimitives.WriteUInt32LittleEndian(h[4..], 1);
        _cur.Write(h);
        _curPack = _packs.Count;
        _packs.Add(path);
        _curEntries.Clear();
    }

    void SealPack()
    {
        if (_cur == null) return;
        var t = System.Diagnostics.Stopwatch.StartNew();
        bool trace = Environment.GetEnvironmentVariable("DVAULT_TRACE") == "1";
        _cur.Flush(true);
        if (trace) Console.Error.WriteLine($"[trace] fsync {t.ElapsedMilliseconds} ms");
        _cur.Dispose();
        if (trace) Console.Error.WriteLine($"[trace] close {t.ElapsedMilliseconds} ms");
        _cur = null;
        var path = _packs[_curPack];
        lock (_hlock)
        {
            if (_handles.Remove(_curPack, out var h)) h.Dispose();
        }
        File.Move(_curTmp!, path);
        if (trace) Console.Error.WriteLine($"[trace] move {t.ElapsedMilliseconds} ms");
        WriteIdx(Path.ChangeExtension(path, ".idx"), _curEntries);
        if (trace) Console.Error.WriteLine($"[trace] idx {t.ElapsedMilliseconds} ms");
        _curEntries.Clear();
        _curTmp = null;
    }

    /// <summary>書きかけの pack を閉じて確定する。復元ポイントを保存する前に必ず呼ぶ。</summary>
    public void Flush() => SealPack();

    /// <summary>ブロックを読み、復号・伸張・ハッシュ検証して dest に書く。</summary>
    public void Read(BlockId id, Span<byte> dest)
    {
        if (!_index.TryGetValue(id, out var loc)) throw new InvalidDataException($"ブロック {id} がチェーン {Chain} にありません（データ欠落）。");
        if (loc.RawLen != dest.Length) throw new InvalidDataException($"ブロック {id} の長さが一致しません。");
        var buf = System.Buffers.ArrayPool<byte>.Shared.Rent(RecHeader + loc.PayloadLen);
        try
        {
            var span = buf.AsSpan(0, RecHeader + loc.PayloadLen);
            ReadExact(loc.Pack, loc.Offset, span);
            if (!new BlockId(span).Equals(id)) throw new InvalidDataException($"pack 内のブロック {id} のヘッダが不正です。");
            _repo.Codec.Decode(id, loc.Codec, span[RecHeader..], dest);
        }
        finally { System.Buffers.ArrayPool<byte>.Shared.Return(buf); }
    }

    void ReadExact(int pack, long off, Span<byte> dest)
    {
        if (pack == _curPack && _cur != null) _cur.Flush();
        var h = Handle(pack);
        int got = 0;
        while (got < dest.Length)
        {
            int n = RandomAccess.Read(h, dest[got..], off + got);
            if (n <= 0) throw new InvalidDataException($"{_packs[pack]} が途中で切れています。");
            got += n;
        }
    }

    SafeFileHandle Handle(int pack)
    {
        lock (_hlock)
        {
            if (_handles.TryGetValue(pack, out var h)) return h;
            if (_handles.Count >= 64)
            {
                foreach (var kv in _handles) kv.Value.Dispose();
                _handles.Clear();
            }
            var path = pack == _curPack && _curTmp != null ? _curTmp : _packs[pack];
            h = File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, FileOptions.RandomAccess);
            _handles[pack] = h;
            return h;
        }
    }

    // ---- 不要データの回収 ----

    public sealed record CompactResult(int DeletedPacks, int RewrittenPacks, long FreedBytes);

    /// <summary>live に含まれないブロックを消す。空の pack は削除、生きているデータが threshold 未満の pack は詰め直す。</summary>
    public CompactResult Compact(HashSet<BlockId> live, double threshold = 0.5)
    {
        if (!_write) throw new InvalidOperationException("読み取り専用で開いています。");
        Flush();
        var byPack = new Dictionary<int, List<(BlockId Id, BlockLoc Loc)>>();
        foreach (var (id, loc) in _index)
        {
            if (!byPack.TryGetValue(loc.Pack, out var l)) byPack[loc.Pack] = l = [];
            l.Add((id, loc));
        }
        int deleted = 0, rewritten = 0;
        long freed = 0;
        var toRewrite = new List<int>();
        for (int p = 0; p < _packs.Count; p++)
        {
            if (!File.Exists(_packs[p])) continue;
            long size = new FileInfo(_packs[p]).Length;
            var entries = byPack.GetValueOrDefault(p) ?? [];
            long liveBytes = entries.Where(e => live.Contains(e.Id)).Sum(e => (long)RecHeader + e.Loc.PayloadLen);
            if (liveBytes == 0)
            {
                DeletePack(p, entries);
                freed += size;
                deleted++;
            }
            else if (liveBytes < size * threshold) toRewrite.Add(p);
        }
        foreach (var p in toRewrite)
        {
            long size = new FileInfo(_packs[p]).Length;
            var entries = byPack[p];
            var buf = new byte[1 << 20];
            foreach (var (id, loc) in entries.Where(e => live.Contains(e.Id)).OrderBy(e => e.Loc.Offset))
            {
                int total = RecHeader + loc.PayloadLen;
                if (buf.Length < total) buf = new byte[total];
                ReadExact(p, loc.Offset, buf.AsSpan(0, total));
                _index.Remove(id);
                Add(id, loc.Codec, loc.RawLen, buf.AsSpan(RecHeader, loc.PayloadLen));
            }
            Flush(); // 新しい pack を確定してから古い pack を消す
            DeletePack(p, entries.Where(e => !live.Contains(e.Id)).ToList());
            freed += size;
            rewritten++;
        }
        return new CompactResult(deleted, rewritten, freed);
    }

    void DeletePack(int p, List<(BlockId Id, BlockLoc Loc)> entries)
    {
        lock (_hlock)
        {
            if (_handles.Remove(p, out var h)) h.Dispose();
        }
        foreach (var (id, loc) in entries)
            if (_index.TryGetValue(id, out var cur) && cur.Pack == p) _index.Remove(id);
        TryDelete(Path.ChangeExtension(_packs[p], ".idx"));
        TryDelete(_packs[p]);
    }

    static void TryDelete(string f)
    {
        try { File.Delete(f); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    /// <summary>全ブロック ID（検証用）。</summary>
    public IEnumerable<BlockId> AllIds => _index.Keys;

    public void Dispose()
    {
        if (_write) Flush();
        lock (_hlock)
        {
            foreach (var h in _handles.Values) h.Dispose();
            _handles.Clear();
        }
    }
}
