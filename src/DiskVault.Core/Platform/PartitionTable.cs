using System.Buffers.Binary;
using System.Text;
using DiskVault.Engine;
using DiskVault.Source;
using DiskVault.Storage;

namespace DiskVault.Platform;

/// <summary>GPT / MBR パーティションテーブルの読み書き。</summary>
public static class PartitionTable
{
    static readonly uint[] CrcTable = MakeCrc();

    static uint[] MakeCrc()
    {
        var t = new uint[256];
        for (uint i = 0; i < 256; i++)
        {
            uint c = i;
            for (int k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320 ^ (c >> 1) : c >> 1;
            t[i] = c;
        }
        return t;
    }

    public static uint Crc32(ReadOnlySpan<byte> d)
    {
        uint c = 0xFFFFFFFF;
        foreach (var b in d) c = CrcTable[(c ^ b) & 0xFF] ^ (c >> 8);
        return c ^ 0xFFFFFFFF;
    }

    /// <summary>ディスク（またはディスクイメージ）先頭からパーティションテーブルを読む。</summary>
    public static DiskRecord Read(IVolumeSource disk, int sectorSize = 512)
    {
        var mbr = new byte[sectorSize];
        disk.Read(0, mbr);
        if (mbr[510] != 0x55 || mbr[511] != 0xAA) throw new InvalidDataException(T("{0} にパーティションテーブルがありません。", disk.Name));
        var rec = new DiskRecord { Size = disk.Length, SectorSize = sectorSize };
        bool protective = false;
        for (int i = 0; i < 4; i++) if (mbr[446 + i * 16 + 4] == 0xEE) protective = true;
        if (protective)
        {
            var hdr = new byte[sectorSize];
            disk.Read(sectorSize, hdr);
            if (!hdr.AsSpan(0, 8).SequenceEqual("EFI PART"u8)) throw new InvalidDataException(T("GPT ヘッダがありません。"));
            rec.Style = "GPT";
            rec.DiskGuid = new Guid(hdr.AsSpan(56, 16)).ToString();
            long entLba = BinaryPrimitives.ReadInt64LittleEndian(hdr.AsSpan(72));
            int count = (int)BinaryPrimitives.ReadUInt32LittleEndian(hdr.AsSpan(80));
            int esize = (int)BinaryPrimitives.ReadUInt32LittleEndian(hdr.AsSpan(84));
            if (count > 1024 || esize < 128 || esize > 1024) throw new InvalidDataException(T("GPT ヘッダが不正です。"));
            int bytes = (count * esize + sectorSize - 1) / sectorSize * sectorSize;
            var ents = new byte[bytes];
            disk.Read(entLba * sectorSize, ents);
            for (int i = 0; i < count; i++)
            {
                var e = ents.AsSpan(i * esize, esize);
                var type = new Guid(e[..16]);
                if (type == Guid.Empty) continue;
                long first = BinaryPrimitives.ReadInt64LittleEndian(e[32..]);
                long last = BinaryPrimitives.ReadInt64LittleEndian(e[40..]);
                rec.Partitions.Add(new PartitionRecord
                {
                    Number = i + 1,
                    TypeGuid = type.ToString(),
                    PartitionGuid = new Guid(e.Slice(16, 16)).ToString(),
                    Offset = first * sectorSize,
                    Length = (last - first + 1) * sectorSize,
                    Attributes = BinaryPrimitives.ReadUInt64LittleEndian(e[48..]),
                    Name = Encoding.Unicode.GetString(e.Slice(56, 72)).TrimEnd('\0'),
                });
            }
        }
        else
        {
            rec.Style = "MBR";
            rec.MbrSignature = BinaryPrimitives.ReadUInt32LittleEndian(mbr.AsSpan(440));
            for (int i = 0; i < 4; i++)
            {
                var e = mbr.AsSpan(446 + i * 16, 16);
                byte type = e[4];
                if (type == 0) continue;
                if (type is 0x05 or 0x0F) continue; // 拡張パーティション（論理ドライブ）は非対応
                rec.Partitions.Add(new PartitionRecord
                {
                    Number = i + 1,
                    MbrType = type,
                    Active = e[0] == 0x80,
                    Offset = BinaryPrimitives.ReadUInt32LittleEndian(e[8..]) * (long)sectorSize,
                    Length = BinaryPrimitives.ReadUInt32LittleEndian(e[12..]) * (long)sectorSize,
                });
            }
        }
        return rec;
    }

    /// <summary>
    /// パーティションテーブルを target（ディスク全体）に書く。GPT はプライマリとバックアップの両方を、ディスクの実サイズに合わせて書く。
    /// </summary>
    public static void Write(DiskRecord d, IVolumeTarget target, int sectorSize = 512)
    {
        long sectors = target.Length / sectorSize;
        var head = new byte[34 * sectorSize];
        if (d.Style == "MBR")
        {
            var mbr = head.AsSpan(0, 512);
            BinaryPrimitives.WriteUInt32LittleEndian(mbr[440..], d.MbrSignature ?? (uint)Random.Shared.Next());
            foreach (var p in d.Partitions.Where(p => p.Number is >= 1 and <= 4))
            {
                var e = mbr.Slice(446 + (p.Number - 1) * 16, 16);
                e[0] = p.Active ? (byte)0x80 : (byte)0;
                e[1] = 0xFE; e[2] = 0xFF; e[3] = 0xFF; // CHS は LBA 使用を示す値
                e[4] = p.MbrType;
                e[5] = 0xFE; e[6] = 0xFF; e[7] = 0xFF;
                BinaryPrimitives.WriteUInt32LittleEndian(e[8..], (uint)(p.Offset / sectorSize));
                BinaryPrimitives.WriteUInt32LittleEndian(e[12..], (uint)(p.Length / sectorSize));
            }
            mbr[510] = 0x55; mbr[511] = 0xAA;
            target.Write(0, head.AsSpan(0, sectorSize));
            return;
        }

        // 保護 MBR
        var pm = head.AsSpan(0, 512);
        pm[446 + 4] = 0xEE;
        pm[446 + 1] = 0x00; pm[446 + 2] = 0x02; pm[446 + 3] = 0x00;
        pm[446 + 5] = 0xFF; pm[446 + 6] = 0xFF; pm[446 + 7] = 0xFF;
        BinaryPrimitives.WriteUInt32LittleEndian(pm[(446 + 8)..], 1);
        BinaryPrimitives.WriteUInt32LittleEndian(pm[(446 + 12)..], (uint)Math.Min(sectors - 1, uint.MaxValue));
        pm[510] = 0x55; pm[511] = 0xAA;

        const int count = 128, esize = 128;
        var ents = new byte[count * esize];
        foreach (var p in d.Partitions)
        {
            if (p.Number < 1 || p.Number > count) continue;
            var e = ents.AsSpan((p.Number - 1) * esize, esize);
            Guid.Parse(p.TypeGuid!).TryWriteBytes(e);
            (p.PartitionGuid != null ? Guid.Parse(p.PartitionGuid) : Guid.NewGuid()).TryWriteBytes(e[16..]);
            BinaryPrimitives.WriteInt64LittleEndian(e[32..], p.Offset / sectorSize);
            BinaryPrimitives.WriteInt64LittleEndian(e[40..], (p.Offset + p.Length) / sectorSize - 1);
            BinaryPrimitives.WriteUInt64LittleEndian(e[48..], p.Attributes);
            var name = Encoding.Unicode.GetBytes(p.Name ?? "");
            name.AsSpan(0, Math.Min(name.Length, 72)).CopyTo(e[56..]);
        }
        uint entCrc = Crc32(ents);
        int entSectors = count * esize / sectorSize;
        long lastUsable = sectors - 1 - entSectors - 1;
        long maxEnd = d.Partitions.Count == 0 ? 0 : d.Partitions.Max(p => (p.Offset + p.Length) / sectorSize - 1);
        if (maxEnd > lastUsable) throw new IOException(T("復元先ディスク（{0}）が小さすぎます。", Fmt.Size(target.Length)));
        var guid = d.DiskGuid != null ? Guid.Parse(d.DiskGuid) : Guid.NewGuid();

        byte[] Header(long myLba, long altLba, long entLba)
        {
            var h = new byte[sectorSize];
            "EFI PART"u8.CopyTo(h);
            BinaryPrimitives.WriteUInt32LittleEndian(h.AsSpan(8), 0x00010000);
            BinaryPrimitives.WriteUInt32LittleEndian(h.AsSpan(12), 92);
            BinaryPrimitives.WriteInt64LittleEndian(h.AsSpan(24), myLba);
            BinaryPrimitives.WriteInt64LittleEndian(h.AsSpan(32), altLba);
            BinaryPrimitives.WriteInt64LittleEndian(h.AsSpan(40), 2 + entSectors);
            BinaryPrimitives.WriteInt64LittleEndian(h.AsSpan(48), lastUsable);
            guid.TryWriteBytes(h.AsSpan(56));
            BinaryPrimitives.WriteInt64LittleEndian(h.AsSpan(72), entLba);
            BinaryPrimitives.WriteUInt32LittleEndian(h.AsSpan(80), count);
            BinaryPrimitives.WriteUInt32LittleEndian(h.AsSpan(84), esize);
            BinaryPrimitives.WriteUInt32LittleEndian(h.AsSpan(88), entCrc);
            BinaryPrimitives.WriteUInt32LittleEndian(h.AsSpan(16), Crc32(h.AsSpan(0, 92)));
            return h;
        }

        Header(1, sectors - 1, 2).CopyTo(head.AsSpan(sectorSize));
        ents.CopyTo(head.AsSpan(2 * sectorSize));
        target.Write(0, head.AsSpan(0, (2 + entSectors) * sectorSize));
        var tail = new byte[(entSectors + 1) * sectorSize];
        ents.CopyTo(tail.AsSpan());
        Header(sectors - 1, 1, sectors - 1 - entSectors).CopyTo(tail.AsSpan(entSectors * sectorSize));
        target.Write((sectors - 1 - entSectors) * sectorSize, tail);
    }
}
