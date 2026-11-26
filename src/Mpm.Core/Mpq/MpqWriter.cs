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

    /// <summary>
    /// Largest archive <see cref="WriteParts"/> produces. Format-0 offsets are 32-bit (hard limit 4 GiB); 3.5 GiB
    /// stays below the 3.7 GiB stock patch.MPQ, a size the client is known to load.
    /// </summary>
    public const long DefaultMaxArchiveBytes = 3584L * 1024 * 1024;

    public static void Write(string path, IReadOnlyDictionary<string, byte[]> files) => Write(path, files, includeListFile: true);

    /// <summary>Writes an archive with no (listfile), like many hand-made mod patches. Intended for tests.</summary>
    public static void WriteWithoutListfile(string path, IReadOnlyDictionary<string, byte[]> files) => Write(path, files, includeListFile: false);

    private static void Write(string path, IReadOnlyDictionary<string, byte[]> files, bool includeListFile)
    {
        var names = SortedNames(files.Keys);
        var part = new Part(path);
        try
        {
            foreach (var (name, original) in names)
            {
                var data = files[original];
                part.Add(name, Pack(data), data.Length);
            }
            part.Finish(includeListFile);
        }
        finally
        {
            part.Dispose();
        }
    }

    /// <summary>
    /// Writes files into as many archives as needed so none exceeds <paramref name="maxArchiveBytes"/>; each
    /// has its own (listfile) and every path is in exactly one, so their relative priority does not matter.
    /// Files are loaded and compressed a batch at a time (in parallel), so memory stays bounded however large
    /// the set is. <paramref name="partPath"/> gives the path of part 0, 1, ... Returns the paths written.
    /// </summary>
    public static IReadOnlyList<string> WriteParts(IEnumerable<KeyValuePair<string, Func<byte[]>>> files, Func<int, string> partPath,
        long maxArchiveBytes = DefaultMaxArchiveBytes)
    {
        var loaders = new Dictionary<string, Func<byte[]>>(MpqPath.Comparer);
        foreach (var (name, load) in files)
        {
            loaders[name] = load;
        }
        var names = SortedNames(loaders.Keys);
        var written = new List<string>();
        Part? part = null;
        var batch = Math.Max(4, Environment.ProcessorCount * 4);
        try
        {
            for (var start = 0; start < names.Count; start += batch)
            {
                var slice = names.Skip(start).Take(batch).ToList();
                var packed = new (byte[] Packed, int Size)[slice.Count];
                try
                {
                    Parallel.For(0, slice.Count, new ParallelOptions { MaxDegreeOfParallelism = Environment.ProcessorCount }, i =>
                    {
                        var data = loaders[slice[i].Original]();
                        packed[i] = (Pack(data), data.Length);
                    });
                }
                catch (AggregateException e) when (e.InnerExceptions.Count > 0)
                {
                    System.Runtime.ExceptionServices.ExceptionDispatchInfo.Throw(e.InnerExceptions[0]);
                }
                for (var i = 0; i < slice.Count; i++)
                {
                    if (part is not null && part.SizeWith(slice[i].Name, packed[i].Packed.Length) > maxArchiveBytes)
                    {
                        part.Finish(includeListFile: true);
                        written.Add(part.Path);
                        part.Dispose();
                        part = null;
                    }
                    part ??= new Part(partPath(written.Count));
                    if (part.SizeWith(slice[i].Name, packed[i].Packed.Length) > maxArchiveBytes)
                    {
                        throw new IOException($"{slice[i].Name} ({packed[i].Packed.Length} bytes packed) does not fit in one archive");
                    }
                    part.Add(slice[i].Name, packed[i].Packed, packed[i].Size);
                }
            }
            if (part is not null)
            {
                part.Finish(includeListFile: true);
                written.Add(part.Path);
            }
            return written;
        }
        finally
        {
            part?.Dispose();
        }
    }

    private static List<(string Name, string Original)> SortedNames(IEnumerable<string> keys)
    {
        var seen = new HashSet<string>(MpqPath.Comparer);
        var names = new List<(string Name, string Original)>();
        foreach (var key in keys)
        {
            var normalized = MpqPath.Normalize(key);
            if (normalized.StartsWith('('))
            {
                continue; // internal files are generated here
            }
            if (!seen.Add(normalized))
            {
                throw new ArgumentException($"Duplicate archive path (case-insensitive): {normalized}", nameof(keys));
            }
            names.Add((normalized, key));
        }
        names.Sort((x, y) => MpqPath.Comparer.Compare(x.Name, y.Name));
        return names;
    }

    /// <summary>One archive being written: file data first, then (listfile), hash and block tables, header.</summary>
    private sealed class Part : IDisposable
    {
        private readonly FileStream _output;
        private readonly string _tmp;
        private readonly List<(string Name, uint Offset, uint Packed, uint Size)> _entries = [];
        private long _listfileBytes;

        public Part(string path)
        {
            Path = path;
            var dir = System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(path));
            if (dir is not null)
            {
                Directory.CreateDirectory(dir);
            }
            _tmp = path + ".tmp";
            _output = new FileStream(_tmp, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20);
            _output.Write(new byte[HeaderSize]); // placeholder, rewritten at the end
        }

        public string Path { get; }
        private bool _finished;

        /// <summary>Final archive size if one more file of <paramref name="packed"/> bytes were added (tables and listfile included, upper bound).</summary>
        public long SizeWith(string name, long packed)
        {
            var count = _entries.Count + 2; // the file and the (listfile)
            var listfile = _listfileBytes + Encoding.Latin1.GetByteCount(name) + 2;
            var listfilePacked = listfile + (listfile / SectorSize + 2) * 4 + 64;
            return _output.Position + packed + listfilePacked + HashSizeFor(count) * 16L + count * 16L;
        }

        public void Add(string name, byte[] packed, int size)
        {
            _entries.Add((name, checked((uint)_output.Position), (uint)packed.Length, (uint)size));
            _output.Write(packed);
            _listfileBytes += Encoding.Latin1.GetByteCount(name) + 2;
        }

        public void Finish(bool includeListFile)
        {
            if (includeListFile)
            {
                var list = Encoding.Latin1.GetBytes(string.Concat(_entries.Select(e => e.Name + "\r\n")));
                Add("(listfile)", Pack(list), list.Length);
            }
            var hashSize = HashSizeFor(_entries.Count);
            var blocks = new byte[_entries.Count * 16];
            var hashes = new byte[hashSize * 16];
            hashes.AsSpan().Fill(0xFF);
            for (var i = 0; i < _entries.Count; i++)
            {
                var (name, offset, packed, size) = _entries[i];
                var b = blocks.AsSpan(i * 16);
                BinaryPrimitives.WriteUInt32LittleEndian(b, offset);
                BinaryPrimitives.WriteUInt32LittleEndian(b[4..], packed);
                BinaryPrimitives.WriteUInt32LittleEndian(b[8..], size);
                BinaryPrimitives.WriteUInt32LittleEndian(b[12..], (uint)(MpqFlags.Exists | MpqFlags.Compress));

                var slot = (int)(MpqCrypto.HashString(name, MpqCrypto.HashOffset) % (uint)hashSize);
                while (BinaryPrimitives.ReadUInt32LittleEndian(hashes.AsSpan(slot * 16 + 12)) != 0xFFFFFFFF)
                {
                    slot = (slot + 1) % hashSize;
                }
                var e = hashes.AsSpan(slot * 16);
                BinaryPrimitives.WriteUInt32LittleEndian(e, MpqCrypto.HashString(name, MpqCrypto.HashA));
                BinaryPrimitives.WriteUInt32LittleEndian(e[4..], MpqCrypto.HashString(name, MpqCrypto.HashB));
                BinaryPrimitives.WriteUInt16LittleEndian(e[8..], 0); // neutral locale
                BinaryPrimitives.WriteUInt16LittleEndian(e[10..], 0); // platform
                BinaryPrimitives.WriteUInt32LittleEndian(e[12..], (uint)i);
            }

            var hashOffset = checked((uint)_output.Position);
            MpqCrypto.Encrypt(hashes, MpqCrypto.HashString("(hash table)", MpqCrypto.HashFileKey));
            _output.Write(hashes);
            var blockOffset = checked((uint)_output.Position);
            MpqCrypto.Encrypt(blocks, MpqCrypto.HashString("(block table)", MpqCrypto.HashFileKey));
            _output.Write(blocks);
            var archiveSize = checked((uint)_output.Position);

            Span<byte> header = stackalloc byte[HeaderSize];
            BinaryPrimitives.WriteUInt32LittleEndian(header, 0x1A51504D);
            BinaryPrimitives.WriteUInt32LittleEndian(header[4..], HeaderSize);
            BinaryPrimitives.WriteUInt32LittleEndian(header[8..], archiveSize);
            BinaryPrimitives.WriteUInt16LittleEndian(header[12..], 0);
            BinaryPrimitives.WriteUInt16LittleEndian(header[14..], SectorShift);
            BinaryPrimitives.WriteUInt32LittleEndian(header[16..], hashOffset);
            BinaryPrimitives.WriteUInt32LittleEndian(header[20..], blockOffset);
            BinaryPrimitives.WriteUInt32LittleEndian(header[24..], (uint)hashSize);
            BinaryPrimitives.WriteUInt32LittleEndian(header[28..], (uint)_entries.Count);
            _output.Position = 0;
            _output.Write(header);
            _output.Dispose();
            File.Move(_tmp, Path, overwrite: true);
            _finished = true;
        }

        public void Dispose()
        {
            _output.Dispose();
            if (!_finished && File.Exists(_tmp))
            {
                File.Delete(_tmp);
            }
        }
    }

    private static int HashSizeFor(int count)
    {
        var hashSize = 16;
        while (hashSize < count * 2)
        {
            hashSize *= 2;
        }
        return hashSize;
    }

    /// <summary>4 KiB sectors, each zlib-compressed (best ratio) when that saves space, else stored.</summary>
    private static byte[] Pack(byte[] data)
    {
        var sectorCount = Math.Max(1, (data.Length + SectorSize - 1) / SectorSize);
        var tableSize = (sectorCount + 1) * 4;
        var packed = new byte[tableSize + data.Length + sectorCount];
        var scratch = new byte[ZLibEncoder.GetMaxCompressedLength(SectorSize)];
        var offset = tableSize;
        for (var i = 0; i < sectorCount; i++)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(packed.AsSpan(i * 4), (uint)offset);
            var start = i * SectorSize;
            var sector = data.AsSpan(start, Math.Min(SectorSize, data.Length - start));
            if (ZLibEncoder.TryCompress(sector, scratch, out var written, quality: 9) && written + 1 < sector.Length)
            {
                packed[offset] = 0x02; // zlib
                scratch.AsSpan(0, written).CopyTo(packed.AsSpan(offset + 1));
                offset += written + 1;
            }
            else
            {
                sector.CopyTo(packed.AsSpan(offset));
                offset += sector.Length;
            }
        }
        BinaryPrimitives.WriteUInt32LittleEndian(packed.AsSpan(sectorCount * 4), (uint)offset);
        return offset == packed.Length ? packed : packed[..offset];
    }
}
