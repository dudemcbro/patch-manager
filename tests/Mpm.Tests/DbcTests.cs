using System.Text;
using Mpm.Core.Dbc;
using Mpm.Core.Mpq;

namespace Mpm.Tests;

public class DbcTests
{
    private static byte[] BuildDbc(params (uint Id, string Name)[] rows)
    {
        var strings = new List<byte> { 0 };
        var records = new List<byte[]>();
        foreach (var (id, name) in rows)
        {
            var record = new byte[8];
            DbcFile.SetUInt(record, 0, id);
            DbcFile.SetUInt(record, 1, (uint)strings.Count);
            strings.AddRange(Encoding.UTF8.GetBytes(name));
            strings.Add(0);
            records.Add(record);
        }
        return new DbcFile(2, 8, records, strings.ToArray()).ToBytes();
    }

    [Fact]
    public void ParseThenWrite_IsByteIdentical()
    {
        var bytes = BuildDbc((1, "Fireball"), (2, "Frostbolt"), (100001, "Custom Spell"));
        var dbc = DbcFile.Parse(bytes);

        Assert.Equal(3, dbc.Records.Count);
        Assert.Equal(100001u, DbcFile.GetId(dbc.Records[2]));
        Assert.Equal("Frostbolt", dbc.GetString(DbcFile.GetUInt(dbc.Records[1], 1)));
        Assert.Equal(bytes, dbc.ToBytes());
    }

    [Fact]
    public void Parse_RejectsTruncatedFile()
    {
        var bytes = BuildDbc((1, "A"));
        Assert.Throws<InvalidDataException>(() => DbcFile.Parse(bytes.AsSpan(0, bytes.Length - 1)));
    }

    [Fact]
    public void Parse_RejectsWrongMagic()
    {
        var bytes = BuildDbc((1, "A"));
        bytes[0] = (byte)'X';
        Assert.Throws<InvalidDataException>(() => DbcFile.Parse(bytes));
    }

    [ClientDataFact]
    public void EveryStockDbc_RoundTripsByteIdentical()
    {
        var enUS = Path.Combine(ClientDataFactAttribute.DataDir!, "enUS");
        var checkedCount = 0;
        foreach (var archive in new[] { "locale-enUS.MPQ", "patch-enUS.MPQ", "patch-enUS-2.MPQ", "patch-enUS-3.MPQ" })
        {
            using var reader = MpqReader.Open(Path.Combine(enUS, archive));
            foreach (var name in reader.Files.Where(f => f.EndsWith(".dbc", StringComparison.OrdinalIgnoreCase)))
            {
                var data = reader.Read(name);
                Assert.True(data.AsSpan().SequenceEqual(DbcFile.Parse(data).ToBytes()), $"{archive}:{name}");
                checkedCount++;
            }
        }
        Assert.True(checkedCount > 500, $"only {checkedCount} DBCs checked");
    }
}
