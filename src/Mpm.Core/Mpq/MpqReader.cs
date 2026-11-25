using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;
using ICSharpCode.SharpZipLib.BZip2;

namespace Mpm.Core.Mpq;

/// <summary>
/// Reads MPQ archives as the 3.3.5a client ships them: format 0/1 headers (archives under 4 GiB),
/// encrypted files incl. the fix-key variant, single-unit and sectored files, zlib and bzip2 sectors.
/// PKWARE implode and Huffman/ADPCM are not supported (not used by DBC/UI/model data in 3.3.5a).
/// </summary>
public sealed class MpqReader : IDisposable
{
    private readonly FileStream _stream;
    private readonly long _base;
    private readonly int _sectorSize;
    private readonly Dictionary<(uint A, uint B), List<(ushort Locale, uint Block)>> _hashes = new();
    private readonly BlockEntry[] _blocks;
    private IReadOnlyList<string>? _files;

    private readonly record struct BlockEntry(uint Offset, uint PackedSize, uint FileSize, MpqFlags Flags);

    private MpqReader(string path)
    {
        SourcePath = path;
        _stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        try
        {
            _base = FindHeader();
            Span<byte> header = stackalloc byte[32];
            ReadAt(_base, header);
            FormatVersion = BinaryPrimitives.ReadUInt16LittleEndian(header[12..]);
            _sectorSize = 512 << BinaryPrimitives.ReadUInt16LittleEndian(header[14..]);
            var hashOffset = BinaryPrimitives.ReadUInt32LittleEndian(header[16..]);
            var blockOffset = BinaryPrimitives.ReadUInt32LittleEndian(header[20..]);
            var hashCount = (int)BinaryPrimitives.ReadUInt32LittleEndian(header[24..]);
            var blockCount = (int)BinaryPrimitives.ReadUInt32LittleEndian(header[28..]);

            var hashBytes = new byte[hashCount * 16];
            ReadAt(_base + hashOffset, hashBytes);
            MpqCrypto.Decrypt(hashBytes, MpqCrypto.HashString("(hash table)", MpqCrypto.HashFileKey));
            for (var i = 0; i < hashCount; i++)
            {
                var e = hashBytes.AsSpan(i * 16, 16);
                var block = BinaryPrimitives.ReadUInt32LittleEndian(e[12..]);
                if (block >= 0xFFFFFFFE)
                {
                    continue; // empty or deleted slot
                }
                var key = (BinaryPrimitives.ReadUInt32LittleEndian(e), BinaryPrimitives.ReadUInt32LittleEndian(e[4..]));
                if (!_hashes.TryGetValue(key, out var list))
                {
                    _hashes[key] = list = [];
                }
                list.Add((BinaryPrimitives.ReadUInt16LittleEndian(e[8..]), block));
            }

            var blockBytes = new byte[blockCount * 16];
            ReadAt(_base + blockOffset, blockBytes);
            MpqCrypto.Decrypt(blockBytes, MpqCrypto.HashString("(block table)", MpqCrypto.HashFileKey));
            _blocks = new BlockEntry[blockCount];
            for (var i = 0; i < blockCount; i++)
            {
                var e = blockBytes.AsSpan(i * 16, 16);
                _blocks[i] = new BlockEntry(
                    BinaryPrimitives.ReadUInt32LittleEndian(e),
                    BinaryPrimitives.ReadUInt32LittleEndian(e[4..]),
                    BinaryPrimitives.ReadUInt32LittleEndian(e[8..]),
                    (MpqFlags)BinaryPrimitives.ReadUInt32LittleEndian(e[12..]));
            }
        }
        catch
        {
            _stream.Dispose();
            throw;
        }
    }

    public string SourcePath { get; }
    public ushort FormatVersion { get; }

    public static MpqReader Open(string path) => new(path);

    /// <summary>Existing files named in the archive's (listfile), excluding internal "(...)" files.</summary>
    public IReadOnlyList<string> Files => _files ??= LoadListFile();

    public bool Contains(string path) => Find(path) is { } b && IsLive(b);

    /// <summary>
    /// Files named by the (listfile) plus any candidate names that exist in the archive. Archives often
    /// ship without a (listfile); candidates (e.g. every stock path) recover the names of known files.
    /// </summary>
    public IReadOnlyList<string> FilesWith(IEnumerable<string> candidates) =>
        Files.Concat(candidates.Select(MpqPath.Normalize).Where(n => n.Length > 0 && !n.StartsWith('(') && Contains(n)))
            .Distinct(MpqPath.Comparer)
            .OrderBy(n => n, MpqPath.Comparer)
            .ToList();

    /// <summary>Live files whose name is not among <paramref name="named"/> (internal "(...)" files excluded).</summary>
    public int CountUnnamed(IEnumerable<string> named)
    {
        var known = named.Concat(["(listfile)", "(attributes)", "(signature)"])
            .Select(n => (MpqCrypto.HashString(MpqPath.Normalize(n), MpqCrypto.HashA), MpqCrypto.HashString(MpqPath.Normalize(n), MpqCrypto.HashB)))
            .ToHashSet();
        return _hashes.Count(kv => !known.Contains(kv.Key) && kv.Value.Any(e => e.Block < _blocks.Length && IsLive(_blocks[e.Block])));
    }

    /// <summary>True when the archive holds a delete marker for the path (hides it in lower archives).</summary>
    public bool IsDeleted(string path) => Find(path) is { } b && b.Flags.HasFlag(MpqFlags.DeleteMarker);

    public bool TryRead(string path, out byte[] data)
    {
        if (!Contains(path))
        {
            data = [];
            return false;
        }
        data = Read(path);
        return true;
    }

    public byte[] Read(string path)
    {
        var name = MpqPath.Normalize(path);
        if (Find(name) is not { } block || !IsLive(block))
        {
            throw new FileNotFoundException($"{name} not found in {SourcePath}");
        }

        var raw = new byte[block.PackedSize];
        ReadAt(_base + block.Offset, raw);
        var encrypted = block.Flags.HasFlag(MpqFlags.Encrypted);
        var key = encrypted ? MpqCrypto.FileKey(name, block.Offset, block.FileSize, block.Flags.HasFlag(MpqFlags.FixKey)) : 0;
        var compressed = (block.Flags & (MpqFlags.Compress | MpqFlags.Implode)) != 0;

        if (block.Flags.HasFlag(MpqFlags.SingleUnit))
        {
            if (encrypted)
            {
                MpqCrypto.Decrypt(raw, key);
            }
            return compressed && block.PackedSize < block.FileSize ? Decompress(raw, block.Flags, (int)block.FileSize) : raw;
        }

        var sectors = (int)((block.FileSize + _sectorSize - 1) / _sectorSize);
        var output = new byte[block.FileSize];
        if (!compressed)
        {
            for (var i = 0; i < sectors; i++)
            {
                var start = i * _sectorSize;
                var chunk = raw.AsSpan(start, Math.Min(_sectorSize, (int)block.FileSize - start));
                if (encrypted)
                {
                    MpqCrypto.Decrypt(chunk, key + (uint)i);
                }
                chunk.CopyTo(output.AsSpan(start));
            }
            return output;
        }

        var table = raw.AsSpan(0, (sectors + 1) * 4);
        if (encrypted)
        {
            MpqCrypto.Decrypt(table, key - 1);
        }
        var written = 0;
        for (var i = 0; i < sectors; i++)
        {
            var from = (int)BinaryPrimitives.ReadUInt32LittleEndian(table[(i * 4)..]);
            var to = (int)BinaryPrimitives.ReadUInt32LittleEndian(table[((i + 1) * 4)..]);
            var chunk = raw.AsSpan(from, to - from);
            if (encrypted)
            {
                MpqCrypto.Decrypt(chunk, key + (uint)i);
            }
            var expected = Math.Min(_sectorSize, (int)block.FileSize - i * _sectorSize);
            var plain = chunk.Length < expected ? Decompress(chunk.ToArray(), block.Flags, expected) : chunk.ToArray();
            if (plain.Length != expected)
            {
                throw new InvalidDataException($"{name}: sector {i} decoded to {plain.Length} bytes, expected {expected}");
            }
            plain.CopyTo(output, written);
            written += plain.Length;
        }
        return output;
    }

    public void Dispose() => _stream.Dispose();

    private static bool IsLive(BlockEntry b) => b.Flags.HasFlag(MpqFlags.Exists) && !b.Flags.HasFlag(MpqFlags.DeleteMarker);

    private BlockEntry? Find(string path)
    {
        var name = MpqPath.Normalize(path);
        var key = (MpqCrypto.HashString(name, MpqCrypto.HashA), MpqCrypto.HashString(name, MpqCrypto.HashB));
        if (!_hashes.TryGetValue(key, out var entries))
        {
            return null;
        }
        // Neutral locale (0) first, matching what the client resolves without a locale override.
        var pick = entries.OrderBy(e => e.Locale != 0).First();
        return pick.Block < _blocks.Length ? _blocks[pick.Block] : null;
    }

    private IReadOnlyList<string> LoadListFile()
    {
        if (!TryRead("(listfile)", out var bytes))
        {
            return [];
        }
        return Encoding.Latin1.GetString(bytes)
            .Split(['\r', '\n', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(MpqPath.Normalize)
            .Where(n => n.Length > 0 && !n.StartsWith('(') && Contains(n))
            .Distinct(MpqPath.Comparer)
            .OrderBy(n => n, MpqPath.Comparer)
            .ToList();
    }

    private long FindHeader()
    {
        Span<byte> buf = stackalloc byte[12];
        long offset = 0;
        while (offset + 32 <= _stream.Length)
        {
            ReadAt(offset, buf);
            var magic = BinaryPrimitives.ReadUInt32LittleEndian(buf);
            if (magic == 0x1A51504D)
            {
                return offset; // "MPQ\x1A"
            }
            if (magic == 0x1B51504D)
            {
                offset += BinaryPrimitives.ReadUInt32LittleEndian(buf[8..]); // user data -> header offset
                continue;
            }
            offset += 0x200;
        }
        throw new InvalidDataException($"{SourcePath}: no MPQ header");
    }

    private void ReadAt(long offset, Span<byte> buffer)
    {
        _stream.Position = offset;
        _stream.ReadExactly(buffer);
    }

    private static byte[] Decompress(byte[] data, MpqFlags flags, int expected)
    {
        if (flags.HasFlag(MpqFlags.Implode))
        {
            throw new NotSupportedException("PKWARE implode is not supported");
        }
        var mask = data[0];
        var body = data.AsSpan(1).ToArray();
        // Decompression undoes compressors in reverse order of application: bzip2 before zlib.
        if ((mask & 0x10) != 0)
        {
            body = Inflate(new BZip2InputStream(new MemoryStream(body)), expected);
            mask &= unchecked((byte)~0x10);
        }
        if ((mask & 0x02) != 0)
        {
            body = Inflate(new ZLibStream(new MemoryStream(body), CompressionMode.Decompress), expected);
            mask &= unchecked((byte)~0x02);
        }
        if (mask != 0)
        {
            throw new NotSupportedException($"MPQ compression mask 0x{mask:X2} is not supported");
        }
        return body;
    }

    private static byte[] Inflate(Stream source, int expected)
    {
        using (source)
        {
            using var output = new MemoryStream(expected);
            source.CopyTo(output);
            return output.ToArray();
        }
    }
}
