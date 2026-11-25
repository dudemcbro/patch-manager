using Mpm.Core.Mpq;

namespace Mpm.Tests;

public class MpqTests
{
    [Fact]
    public void WriteThenRead_RoundTripsAllFiles()
    {
        var rng = new Random(1234);
        var random = new byte[20_000];
        rng.NextBytes(random);
        var files = new Dictionary<string, byte[]>
        {
            ["DBFilesClient\\Fake.dbc"] = Enumerable.Repeat((byte)7, 10_000).ToArray(),
            ["Interface/Icons/Thing.blp"] = random,
            ["empty.txt"] = [],
            ["tiny.txt"] = [1, 2, 3],
            ["exact.bin"] = new byte[4096],
        };

        using var tmp = new TempDir();
        var path = tmp.File("patch-Z.MPQ");
        MpqWriter.Write(path, files);

        using var reader = MpqReader.Open(path);
        Assert.Equal(files.Count, reader.Files.Count);
        foreach (var (name, data) in files)
        {
            Assert.True(reader.Contains(name), name);
            Assert.Equal(data, reader.Read(name));
        }
    }

    [Fact]
    public void Lookup_IsCaseInsensitive_AndAcceptsForwardSlashes()
    {
        using var tmp = new TempDir();
        var path = tmp.File("a.MPQ");
        MpqWriter.Write(path, new Dictionary<string, byte[]> { ["Interface\\FrameXML\\Foo.lua"] = [42] });

        using var reader = MpqReader.Open(path);
        Assert.Equal([42], reader.Read("interface/framexml/FOO.LUA"));
        Assert.False(reader.Contains("Interface\\FrameXML\\Bar.lua"));
    }

    [Fact]
    public void Write_RejectsCaseInsensitiveDuplicates()
    {
        using var tmp = new TempDir();
        var files = new Dictionary<string, byte[]> { ["a\\B.txt"] = [1], ["A/b.txt"] = [2] };
        Assert.Throws<ArgumentException>(() => MpqWriter.Write(tmp.File("x.MPQ"), files));
    }

    [ClientDataFact]
    public void StockLocalePatch_AllDbcsRepackIdentically()
    {
        var source = Path.Combine(ClientDataFactAttribute.DataDir!, "enUS", "patch-enUS-3.MPQ");
        using var stock = MpqReader.Open(source);
        var dbcs = stock.Files.Where(f => f.EndsWith(".dbc", StringComparison.OrdinalIgnoreCase))
            .ToDictionary(f => f, stock.Read);
        Assert.NotEmpty(dbcs);

        using var tmp = new TempDir();
        var path = tmp.File("patch-enUS-Z.MPQ");
        MpqWriter.Write(path, dbcs);

        using var repacked = MpqReader.Open(path);
        foreach (var (name, data) in dbcs)
        {
            Assert.Equal(data, repacked.Read(name));
        }
    }
}
