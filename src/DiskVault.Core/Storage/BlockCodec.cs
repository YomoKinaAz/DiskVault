using System.Security.Cryptography;
using ZstdSharp;

namespace DiskVault.Storage;

/// <summary>
/// ブロックの ID 計算・圧縮・暗号化。codec バイト: 下位 7 ビット = 圧縮方式（0 無圧縮 / 1 zstd）、0x80 = AES-256-GCM 暗号化。
/// 暗号化時のペイロードは nonce(12) + 暗号文 + tag(16)、AAD はブロック ID。
/// </summary>
public sealed class BlockCodec
{
    public const byte Raw = 0, Zstd = 1, EncryptedFlag = 0x80;

    readonly int _level;
    readonly byte[]? _encKey, _idKey;
    readonly ThreadLocal<Compressor> _comp;
    readonly ThreadLocal<Decompressor> _decomp = new(() => new Decompressor());

    public bool Encrypted => _encKey != null;

    public BlockCodec(int level, byte[]? encKey, byte[]? idKey)
    {
        _level = Math.Clamp(level, 1, 19);
        _encKey = encKey;
        _idKey = idKey;
        _comp = new(() => new Compressor(_level));
    }

    public BlockId ComputeId(ReadOnlySpan<byte> data)
    {
        Span<byte> h = stackalloc byte[32];
        if (_idKey != null) HMACSHA256.HashData(_idKey, data, h);
        else SHA256.HashData(data, h);
        return new BlockId(h);
    }

    /// <summary>保存用に符号化する。返り値は (codec, ペイロード)。</summary>
    public (byte Codec, byte[] Payload) Encode(BlockId id, ReadOnlySpan<byte> data)
    {
        byte codec;
        byte[] body;
        var bound = Compressor.GetCompressBound(data.Length);
        var tmp = new byte[bound];
        int n = _comp.Value!.Wrap(data, tmp);
        if (n < data.Length - data.Length / 32)
        {
            codec = Zstd;
            body = tmp.AsSpan(0, n).ToArray();
        }
        else
        {
            codec = Raw;
            body = data.ToArray();
        }
        if (_encKey == null) return (codec, body);

        var outp = new byte[12 + body.Length + 16];
        RandomNumberGenerator.Fill(outp.AsSpan(0, 12));
        using var gcm = new AesGcm(_encKey, 16);
        Span<byte> aad = stackalloc byte[32];
        id.Write(aad);
        gcm.Encrypt(outp.AsSpan(0, 12), body, outp.AsSpan(12, body.Length), outp.AsSpan(12 + body.Length, 16), aad);
        return ((byte)(codec | EncryptedFlag), outp);
    }

    /// <summary>復号・伸張して dest（rawLen バイト）に書く。ID が一致しなければ InvalidDataException。</summary>
    public void Decode(BlockId id, byte codec, ReadOnlySpan<byte> payload, Span<byte> dest)
    {
        ReadOnlySpan<byte> body = payload;
        byte[]? plain = null;
        if ((codec & EncryptedFlag) != 0)
        {
            if (_encKey == null) throw new InvalidDataException("暗号化されたブロックですが、パスワードが指定されていません。");
            if (payload.Length < 28) throw new InvalidDataException("暗号化ブロックが短すぎます。");
            plain = new byte[payload.Length - 28];
            using var gcm = new AesGcm(_encKey, 16);
            Span<byte> aad = stackalloc byte[32];
            id.Write(aad);
            try { gcm.Decrypt(payload[..12], payload.Slice(12, plain.Length), payload[^16..], plain, aad); }
            catch (CryptographicException) { throw new InvalidDataException($"ブロック {id} の復号に失敗しました（データ破損またはパスワード違い）。"); }
            body = plain;
        }
        switch (codec & 0x7F)
        {
            case Raw:
                if (body.Length != dest.Length) throw new InvalidDataException($"ブロック {id} のサイズが不正です。");
                body.CopyTo(dest);
                break;
            case Zstd:
                int n;
                try { n = _decomp.Value!.Unwrap(body, dest); }
                catch (ZstdException e) { throw new InvalidDataException($"ブロック {id} の伸張に失敗しました: {e.Message}"); }
                if (n != dest.Length) throw new InvalidDataException($"ブロック {id} の伸張後サイズが不正です。");
                break;
            default:
                throw new InvalidDataException($"未知の圧縮方式 {codec}。");
        }
        if (ComputeId(dest) != id) throw new InvalidDataException($"ブロック {id} の内容がハッシュと一致しません（データ破損）。");
    }
}
