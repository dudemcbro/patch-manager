using System.Buffers.Binary;
using System.Text;

namespace Mpm.Core.Dbc;

/// <summary>
/// A DBC decoded with a layout. Each row is one long per column: integers by value, floats as raw
/// uint32 bits (exact comparison), strings as ids in a shared <see cref="StringPool"/>. Rows from
/// different files that share a pool compare with a plain sequence check. Writing rebuilds the
/// string block, so rows from different files can be combined.
/// </summary>
public sealed class DbcTable
{
    public DbcTable(DbcLayout layout, StringPool strings, List<long[]> rows)
    {
        Layout = layout;
        Strings = strings;
        Rows = rows;
    }

    public DbcLayout Layout { get; }
    public StringPool Strings { get; }
    public List<long[]> Rows { get; }

    public static DbcTable Read(DbcFile file, DbcLayout layout, StringPool strings)
    {
        if (!layout.Matches(file))
        {
            throw new InvalidDataException(
                $"{layout.Table}: file has {file.FieldCount} fields / {file.RecordSize} bytes, layout expects {layout.FieldCount} / {layout.RecordSize}");
        }
        var offsetCache = new Dictionary<uint, long>();
        var rows = new List<long[]>(file.Records.Count);
        foreach (var record in file.Records)
        {
            var row = new long[layout.Columns.Count];
            for (var c = 0; c < row.Length; c++)
            {
                var col = layout.Columns[c];
                var span = record.AsSpan(col.Offset, col.Size);
                row[c] = col.Kind switch
                {
                    ColumnKind.Int8 => (sbyte)span[0],
                    ColumnKind.UInt8 => span[0],
                    ColumnKind.Int16 => BinaryPrimitives.ReadInt16LittleEndian(span),
                    ColumnKind.UInt16 => BinaryPrimitives.ReadUInt16LittleEndian(span),
                    ColumnKind.Int32 => BinaryPrimitives.ReadInt32LittleEndian(span),
                    ColumnKind.String => StringId(BinaryPrimitives.ReadUInt32LittleEndian(span), col),
                    _ => BinaryPrimitives.ReadUInt32LittleEndian(span), // UInt32, Float bits, LocMask
                };
            }
            rows.Add(row);
        }
        return new DbcTable(layout, strings, rows);

        long StringId(uint offset, DbcColumn col)
        {
            if (!offsetCache.TryGetValue(offset, out var id))
            {
                if (offset >= file.StringBlock.Length)
                {
                    throw new InvalidDataException($"{layout.Table}.{col.Name}: string offset {offset} outside block of {file.StringBlock.Length}");
                }
                offsetCache[offset] = id = strings.Intern(file.GetString(offset));
            }
            return id;
        }
    }

    public DbcFile ToFile()
    {
        var block = new MemoryStream();
        block.WriteByte(0); // offset 0 is the empty string
        var offsets = new Dictionary<long, uint> { [0] = 0 };
        var records = new List<byte[]>(Rows.Count);
        foreach (var row in Rows)
        {
            var record = new byte[Layout.RecordSize];
            for (var c = 0; c < row.Length; c++)
            {
                var col = Layout.Columns[c];
                var span = record.AsSpan(col.Offset, col.Size);
                var value = row[c];
                if (col.Kind == ColumnKind.String)
                {
                    if (!offsets.TryGetValue(value, out var offset))
                    {
                        offset = checked((uint)block.Length);
                        block.Write(Encoding.UTF8.GetBytes(Strings.Get(value)));
                        block.WriteByte(0);
                        offsets[value] = offset;
                    }
                    BinaryPrimitives.WriteUInt32LittleEndian(span, offset);
                    continue;
                }
                switch (col.Size)
                {
                    case 1:
                        span[0] = unchecked((byte)value);
                        break;
                    case 2:
                        BinaryPrimitives.WriteUInt16LittleEndian(span, unchecked((ushort)value));
                        break;
                    default:
                        BinaryPrimitives.WriteUInt32LittleEndian(span, unchecked((uint)value));
                        break;
                }
            }
            records.Add(record);
        }
        return new DbcFile(Layout.FieldCount, Layout.RecordSize, records, block.ToArray());
    }

    /// <summary>Row ID when the layout has an ID column; null for key-less tables.</summary>
    public long? KeyOf(long[] row) => Layout.IdColumn >= 0 ? row[Layout.IdColumn] : null;

    public static bool RowsEqual(long[] a, long[] b) => a.AsSpan().SequenceEqual(b);

    /// <summary>Human-readable value of one cell (for reports).</summary>
    public string Format(long[] row, int column) => Layout.Columns[column].Kind switch
    {
        ColumnKind.String => Strings.Get(row[column]),
        ColumnKind.Float => BitConverter.UInt32BitsToSingle(unchecked((uint)row[column])).ToString("R", System.Globalization.CultureInfo.InvariantCulture),
        _ => row[column].ToString(System.Globalization.CultureInfo.InvariantCulture),
    };
}
