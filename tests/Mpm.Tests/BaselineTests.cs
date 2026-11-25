using Mpm.Core.Client;
using Mpm.Core.Mpq;

namespace Mpm.Tests;

public class BaselineTests
{
    [ClientDataFact]
    public void StockClient_ResolvesFilesToTheRightChainAndArchive()
    {
        var data = ClientDataFactAttribute.DataDir!;
        using var baseline = new BaselineChain(data, "enUS");
        Assert.Empty(baseline.Missing);

        var spell = baseline.Locate("DBFilesClient\\Spell.dbc");
        Assert.NotNull(spell);
        Assert.Equal("enUS", spell.Value.Chain);
        Assert.EndsWith("patch-enUS-3.MPQ", spell.Value.Archive, StringComparison.OrdinalIgnoreCase);
        Assert.True(baseline.TryRead("dbfilesclient/spell.dbc", out var bytes));
        Assert.True(bytes.Length > 1_000_000);

        // Every file in stock patch-3.MPQ resolves to it (it is the top of the root chain).
        using var patch3 = MpqReader.Open(ClientInstall.FindCaseInsensitive(Path.Combine(data, "patch-3.MPQ"))!);
        var sample = patch3.Files[patch3.Files.Count / 2];
        var hit = baseline.Locate(sample);
        Assert.NotNull(hit);
        Assert.Equal("root", hit.Value.Chain);
        Assert.EndsWith("patch-3.MPQ", hit.Value.Archive, StringComparison.OrdinalIgnoreCase);

        Assert.Null(baseline.Locate("DBFilesClient\\NotARealTable.dbc"));
    }

    [ClientDataFact]
    public void StockClient_NoPathIsInBothChains()
    {
        // The baseline relies on this: root vs locale priority never matters for stock files.
        using var baseline = new BaselineChain(ClientDataFactAttribute.DataDir!, "enUS");
        var root = new HashSet<string>(MpqPath.Comparer);
        var locale = new HashSet<string>(MpqPath.Comparer);
        foreach (var archive in baseline.Archives)
        {
            using var reader = MpqReader.Open(archive);
            var target = Path.GetDirectoryName(archive)!.EndsWith("enUS") ? locale : root;
            target.UnionWith(reader.Files);
        }
        root.IntersectWith(locale);
        Assert.Empty(root);
    }
}
