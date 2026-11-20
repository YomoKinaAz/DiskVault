using System.Buffers.Binary;
using System.Runtime.InteropServices;

namespace DiskVault.Storage;

/// <summary>ブロックの内容から求めた 32 バイトの ID（暗号化なし: SHA-256、暗号化あり: HMAC-SHA256）。</summary>
[StructLayout(LayoutKind.Sequential)]
public readonly struct BlockId : IEquatable<BlockId>
{
    public const int Size = 32;
    readonly ulong _a, _b, _c, _d;

    public BlockId(ReadOnlySpan<byte> s)
    {
        _a = BinaryPrimitives.ReadUInt64LittleEndian(s);
        _b = BinaryPrimitives.ReadUInt64LittleEndian(s[8..]);
        _c = BinaryPrimitives.ReadUInt64LittleEndian(s[16..]);
        _d = BinaryPrimitives.ReadUInt64LittleEndian(s[24..]);
    }

    public void Write(Span<byte> d)
    {
        BinaryPrimitives.WriteUInt64LittleEndian(d, _a);
        BinaryPrimitives.WriteUInt64LittleEndian(d[8..], _b);
        BinaryPrimitives.WriteUInt64LittleEndian(d[16..], _c);
        BinaryPrimitives.WriteUInt64LittleEndian(d[24..], _d);
    }

    public byte[] ToArray() { var b = new byte[Size]; Write(b); return b; }

    public bool Equals(BlockId o) => _a == o._a && _b == o._b && _c == o._c && _d == o._d;
    public override bool Equals(object? obj) => obj is BlockId o && Equals(o);
    public override int GetHashCode() => (int)_a ^ (int)(_a >> 32);
    public static bool operator ==(BlockId x, BlockId y) => x.Equals(y);
    public static bool operator !=(BlockId x, BlockId y) => !x.Equals(y);
    public override string ToString() => Convert.ToHexStringLower(ToArray());
}
