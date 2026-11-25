using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;

namespace Mpm.Core.Mpq;

/// <summary>
/// Writes a format-0 MPQ the 3.3.5a client loads as a patch: 4 KiB sectors, zlib per sector when it
/// saves space, a generated (listfile), no (attributes), no encryption of file data.
/// </summary>
public static class MpqWriter
{
    private const int SectorShift = 3;
    private const int SectorSize = 512 << SectorShift;
    private const int HeaderSize = 32;

    public static void Write(string path, IReadOnlyDictionary<string, byte[]> files)
    {
        var all = new Dictionary<string, byte[]>(MpqPath.Comparer);
        foreach (var (name, data) in files)
        {
            var normalized = MpqPath.Normalize(name);
            if (normalized.StartsWith('('))
            {
                continue; // internal files are generated here
            }
            if (!all.TryAdd(normalized, data))
            {
                throw new ArgumentException($"Duplicate archive path (case-insensitive): {normalized}", nameof(files));
            }
        }
        var names = all.Keys.OrderBy(n => n, MpqPath.Comparer).ToList();
        all["(listfile)"] = Encoding.Latin1.GetBytes(string.Concat(names.Select(n => n + "\r\n")));
        names.Add("(listfile)");

        var hashSize = 16;
        while (hashSize < names.Count * 2)
        {
            hashSize *= 2;
        }

        var dir = Path.GetDirectoryName(Path.GetFullPath(path));
        if (dir is not null)
        {
            Directory.CreateDirectory(dir);
        }

        var tmp = path + ".tmp";
        using (var output = new FileStream(tmp, FileMode.Create, FileAccess.Write))
        {
            output.Write(new byte[HeaderSize]); // placeholder, rewritten at the end
            var blocks = new byte[names.Count * 16];
            for (var i = 0; i < names.Count; i++)
            {
                var data = all[names[i]];
                var packed = Pack(data);
                var e = blocks.AsSpan(i * 16);
                BinaryPrimitives.WriteUInt32LittleEndian(e, checked((uint)output.Position));
                BinaryPrimitives.WriteUInt32LittleEndian(e[4..], (uint)packed.Length);
                BinaryPrimitives.WriteUInt32LittleEndian(e[8..], (uint)data.Length);
                BinaryPrimitives.WriteUInt32LittleEndian(e[12..], (uint)(MpqFlags.Exists | MpqFlags.Compress));
                output.Write(packed);
            }

            var hashes = new byte[hashSize * 16];
            hashes.AsSpan().Fill(0xFF);
            for (var i = 0; i < names.Count; i++)
            {
                var slot = (int)(MpqCrypto.HashString(names[i], MpqCrypto.HashOffset) % (uint)hashSize);
                while (BinaryPrimitives.ReadUInt32LittleEndian(hashes.AsSpan(slot * 16 + 12)) != 0xFFFFFFFF)
                {
                    slot = (slot + 1) % hashSize;
                }
                var e = hashes.AsSpan(slot * 16);
                BinaryPrimitives.WriteUInt32LittleEndian(e, MpqCrypto.HashString(names[i], MpqCrypto.HashA));
                BinaryPrimitives.WriteUInt32LittleEndian(e[4..], MpqCrypto.HashString(names[i], MpqCrypto.HashB));
                BinaryPrimitives.WriteUInt16LittleEndian(e[8..], 0); // neutral locale
                BinaryPrimitives.WriteUInt16LittleEndian(e[10..], 0); // platform
                BinaryPrimitives.WriteUInt32LittleEndian(e[12..], (uint)i);
            }

            var hashOffset = checked((uint)output.Position);
            MpqCrypto.Encrypt(hashes, MpqCrypto.HashString("(hash table)", MpqCrypto.HashFileKey));
            output.Write(hashes);
            var blockOffset = checked((uint)output.Position);
            MpqCrypto.Encrypt(blocks, MpqCrypto.HashString("(block table)", MpqCrypto.HashFileKey));
            output.Write(blocks);
            var archiveSize = checked((uint)output.Position);

            Span<byte> header = stackalloc byte[HeaderSize];
            BinaryPrimitives.WriteUInt32LittleEndian(header, 0x1A51504D);
            BinaryPrimitives.WriteUInt32LittleEndian(header[4..], HeaderSize);
            BinaryPrimitives.WriteUInt32LittleEndian(header[8..], archiveSize);
            BinaryPrimitives.WriteUInt16LittleEndian(header[12..], 0);
            BinaryPrimitives.WriteUInt16LittleEndian(header[14..], SectorShift);
            BinaryPrimitives.WriteUInt32LittleEndian(header[16..], hashOffset);
            BinaryPrimitives.WriteUInt32LittleEndian(header[20..], blockOffset);
            BinaryPrimitives.WriteUInt32LittleEndian(header[24..], (uint)hashSize);
            BinaryPrimitives.WriteUInt32LittleEndian(header[28..], (uint)names.Count);
            output.Position = 0;
            output.Write(header);
        }
        File.Move(tmp, path, overwrite: true);
    }

    private static byte[] Pack(byte[] data)
    {
        var sectorCount = Math.Max(1, (data.Length + SectorSize - 1) / SectorSize);
        var sectors = new List<byte[]>(sectorCount);
        for (var i = 0; i < sectorCount; i++)
        {
            var start = i * SectorSize;
            var sector = data.AsSpan(start, Math.Min(SectorSize, data.Length - start));
            using var ms = new MemoryStream();
            ms.WriteByte(0x02); // zlib
            using (var z = new ZLibStream(ms, CompressionLevel.SmallestSize, leaveOpen: true))
            {
                z.Write(sector);
            }
            sectors.Add(ms.Length < sector.Length ? ms.ToArray() : sector.ToArray());
        }

        var tableSize = (sectors.Count + 1) * 4;
        var packed = new byte[tableSize + sectors.Sum(s => s.Length)];
        var offset = tableSize;
        for (var i = 0; i < sectors.Count; i++)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(packed.AsSpan(i * 4), (uint)offset);
            sectors[i].CopyTo(packed, offset);
            offset += sectors[i].Length;
        }
        BinaryPrimitives.WriteUInt32LittleEndian(packed.AsSpan(sectors.Count * 4), (uint)offset);
        return packed;
    }
}
