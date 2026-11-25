using System.Buffers.Binary;
using System.Text;

namespace Mpm.Core.Dbc;

/// <summary>
/// WDBC file (3.3.5a client DBC). Records are kept as raw bytes; the string block is preserved
/// verbatim so an unmodified file round-trips byte-identically. Field meaning and width come from a
/// layout: most fields are 4 bytes, but some files (e.g. CharBaseInfo) pack 1-byte fields.
/// </summary>
public sealed class DbcFile
{
    public const uint Magic = 0x43424457; // "WDBC" little-endian
    public const int HeaderSize = 20;

    public DbcFile(int fieldCount, int recordSize, List<byte[]> records, byte[] stringBlock)
    {
        if (fieldCount < 0 || recordSize < 0)
        {
            throw new InvalidDataException($"Invalid WDBC shape: {fieldCount} fields, {recordSize} bytes per record");
        }
        FieldCount = fieldCount;
        RecordSize = recordSize;
        Records = records;
        StringBlock = stringBlock;
    }

    public int FieldCount { get; }
    public int RecordSize { get; }
    public List<byte[]> Records { get; }
    public byte[] StringBlock { get; set; }

    public static DbcFile Parse(ReadOnlySpan<byte> data)
    {
        if (data.Length < HeaderSize || BinaryPrimitives.ReadUInt32LittleEndian(data) != Magic)
        {
            throw new InvalidDataException("Not a WDBC file");
        }
        var recordCount = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(data[4..]));
        var fieldCount = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(data[8..]));
        var recordSize = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(data[12..]));
        var stringSize = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(data[16..]));

        var expected = (long)HeaderSize + (long)recordCount * recordSize + stringSize;
        if (data.Length != expected)
        {
            throw new InvalidDataException($"WDBC size mismatch: header says {expected} bytes, file has {data.Length}");
        }

        var records = new List<byte[]>(recordCount);
        var offset = HeaderSize;
        for (var i = 0; i < recordCount; i++, offset += recordSize)
        {
            records.Add(data.Slice(offset, recordSize).ToArray());
        }
        return new DbcFile(fieldCount, recordSize, records, data.Slice(offset, stringSize).ToArray());
    }

    public byte[] ToBytes()
    {
        var output = new byte[HeaderSize + Records.Count * RecordSize + StringBlock.Length];
        var span = output.AsSpan();
        BinaryPrimitives.WriteUInt32LittleEndian(span, Magic);
        BinaryPrimitives.WriteUInt32LittleEndian(span[4..], (uint)Records.Count);
        BinaryPrimitives.WriteUInt32LittleEndian(span[8..], (uint)FieldCount);
        BinaryPrimitives.WriteUInt32LittleEndian(span[12..], (uint)RecordSize);
        BinaryPrimitives.WriteUInt32LittleEndian(span[16..], (uint)StringBlock.Length);
        var offset = HeaderSize;
        foreach (var record in Records)
        {
            if (record.Length != RecordSize)
            {
                throw new InvalidOperationException($"Record has {record.Length} bytes, expected {RecordSize}");
            }
            record.CopyTo(span[offset..]);
            offset += RecordSize;
        }
        StringBlock.CopyTo(span[offset..]);
        return output;
    }

    public static uint GetUInt(byte[] record, int field) =>
        BinaryPrimitives.ReadUInt32LittleEndian(record.AsSpan(field * 4));

    public static void SetUInt(byte[] record, int field, uint value) =>
        BinaryPrimitives.WriteUInt32LittleEndian(record.AsSpan(field * 4), value);

    /// <summary>First 4 bytes as uint. This is the record ID only when the layout says field 0 is a 4-byte ID.</summary>
    public static uint GetId(byte[] record) => GetUInt(record, 0);

    /// <summary>Reads a NUL-terminated UTF-8 string at an offset into the string block.</summary>
    public string GetString(uint offset)
    {
        if (offset >= StringBlock.Length)
        {
            throw new ArgumentOutOfRangeException(nameof(offset), $"String offset {offset} outside block of {StringBlock.Length}");
        }
        var rest = StringBlock.AsSpan((int)offset);
        var end = rest.IndexOf((byte)0);
        return Encoding.UTF8.GetString(end < 0 ? rest : rest[..end]);
    }
}
