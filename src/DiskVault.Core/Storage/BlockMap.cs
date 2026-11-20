using System.Buffers.Binary;
using ZstdSharp;

namespace DiskVault.Storage;

/// <summary>
/// 1 ボリューム分の「ブロック番号 → 内容」の対応表。
/// Kind: 0 = 未使用（保存していない。復元時は書かない／ゼロ）、1 = 使用中だが全部ゼロ、2 = データ（Ids に ID）。
/// </summary>
public sealed class BlockMap
{
    public const byte Unused = 0, ZeroBlock = 1, Data = 2;
    const uint Magic = 0x504D5644; // "DVMP"

    public int BlockSize { get; }
    public long VolumeSize { get; }
    public long Count { get; }
    public byte[] Kinds { get; }
    public BlockId[] Ids { get; }

    public BlockMap(int blockSize, long volumeSize)
    {
        BlockSize = blockSize;
        VolumeSize = volumeSize;
        Count = (volumeSize + blockSize - 1) / blockSize;
        Kinds = new byte[Count];
        Ids = new BlockId[Count];
    }

    /// <summary>ブロック i の実際の長さ（最後のブロックは短いことがある）。</summary>
    public int LengthOf(long i) => (int)Math.Min(BlockSize, VolumeSize - i * BlockSize);

    public long CountOf(byte kind)
    {
        long n = 0;
        foreach (var k in Kinds) if (k == kind) n++;
        return n;
    }

    public void Save(string path)
    {
        var tmp = path + ".tmp";
        using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16))
        {
            using (var z = new CompressionStream(fs, 3, leaveOpen: true))
            {
                Span<byte> h = stackalloc byte[32];
                BinaryPrimitives.WriteUInt32LittleEndian(h, Magic);
                BinaryPrimitives.WriteUInt32LittleEndian(h[4..], 1);
                BinaryPrimitives.WriteInt32LittleEndian(h[8..], BlockSize);
                BinaryPrimitives.WriteInt64LittleEndian(h[12..], VolumeSize);
                BinaryPrimitives.WriteInt64LittleEndian(h[20..], Count);
                z.Write(h);
                Span<byte> e = stackalloc byte[33];
                for (long i = 0; i < Count; i++)
                {
                    e[0] = Kinds[i];
                    if (Kinds[i] == Data)
                    {
                        Ids[i].Write(e[1..]);
                        z.Write(e);
                    }
                    else z.Write(e[..1]);
                }
            }
            fs.Flush(true);
        }
        File.Move(tmp, path, true);
    }

    public static BlockMap Load(string path)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16);
        using var z = new DecompressionStream(fs);
        var bs = new BufferedStream(z, 1 << 16);
        Span<byte> h = stackalloc byte[32];
        bs.ReadExactly(h);
        if (BinaryPrimitives.ReadUInt32LittleEndian(h) != Magic) throw new InvalidDataException($"{path} はブロックマップではありません。");
        int blockSize = BinaryPrimitives.ReadInt32LittleEndian(h[8..]);
        long volSize = BinaryPrimitives.ReadInt64LittleEndian(h[12..]);
        long count = BinaryPrimitives.ReadInt64LittleEndian(h[20..]);
        var m = new BlockMap(blockSize, volSize);
        if (m.Count != count) throw new InvalidDataException($"{path} のブロック数が不正です。");
        Span<byte> id = stackalloc byte[32];
        for (long i = 0; i < count; i++)
        {
            int k = bs.ReadByte();
            if (k < 0) throw new EndOfStreamException($"{path} が途中で切れています。");
            m.Kinds[i] = (byte)k;
            if (k == Data)
            {
                bs.ReadExactly(id);
                m.Ids[i] = new BlockId(id);
            }
            else if (k > Data) throw new InvalidDataException($"{path} に不正なエントリがあります。");
        }
        return m;
    }
}
