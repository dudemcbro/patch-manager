using Mpm.Core.Client;
using Mpm.Core.Dbc;
using Mpm.Core.Mpq;

namespace Mpm.Tests;

public class LayoutTests
{
    [Fact]
    public void EmbeddedLayouts_Load()
    {
        var layouts = DbcLayouts.For335();
        Assert.Equal("3.3.5.12340", layouts.Build);
        var titles = layouts.Find("DBFilesClient\\CharTitles.dbc")!;
        Assert.Equal(0, titles.IdColumn);
        Assert.Equal(1 + 1 + 17 + 17 + 1, titles.FieldCount);
        Assert.Equal(titles.FieldCount * 4, titles.RecordSize);
        var baseInfo = layouts.Find("CharBaseInfo")!;
        Assert.Equal(-1, baseInfo.IdColumn);
        Assert.Equal(2, baseInfo.RecordSize);
    }

    [ClientDataFact]
    public void StockDbcs_MatchLayouts_AndRoundTripThroughTable()
    {
        var layouts = DbcLayouts.For335();
        using var baseline = new BaselineChain(ClientDataFactAttribute.DataDir!, "enUS");
        var mismatched = new List<string>();
        var missingLayout = new List<string>();
        var checkedCount = 0;
        foreach (var path in baseline.Paths.Keys.Where(DbcLayouts.IsDbcPath).Order(StringComparer.OrdinalIgnoreCase))
        {
            Assert.True(baseline.TryRead(path, out var bytes));
            var file = DbcFile.Parse(bytes);
            var layout = layouts.Find(path);
            if (layout is null)
            {
                missingLayout.Add(path);
                continue;
            }
            if (!layout.Matches(file))
            {
                mismatched.Add($"{path}: file {file.FieldCount}/{file.RecordSize}, layout {layout.FieldCount}/{layout.RecordSize}");
                continue;
            }
            var pool = new StringPool();
            var table = DbcTable.Read(file, layout, pool);
            var again = DbcTable.Read(table.ToFile(), layout, pool);
            Assert.Equal(table.Rows.Count, again.Rows.Count);
            for (var i = 0; i < table.Rows.Count; i++)
            {
                Assert.True(DbcTable.RowsEqual(table.Rows[i], again.Rows[i]), $"{path} row {i}");
            }
            checkedCount++;
        }
        Assert.True(mismatched.Count == 0, "layout mismatches:\n" + string.Join('\n', mismatched));
        Assert.True(missingLayout.Count == 0, "no layout for:\n" + string.Join('\n', missingLayout));
        Assert.True(checkedCount > 200, $"only {checkedCount} tables checked");
    }
}
