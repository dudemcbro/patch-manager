using Mpm.Core.Dbc;
using Mpm.Core.Mpq;

namespace Mpm.Tests;

/// <summary>Builds tiny stock-named archives so baseline/merge/install can be tested without a real client.</summary>
public static class FakeClient
{
    public static readonly DbcLayout Titles = DbcLayouts.For335().Find("CharTitles")!;
    public static readonly DbcLayout BaseInfo = DbcLayouts.For335().Find("CharBaseInfo")!;

    /// <summary>CharTitles rows: (ID, enUS name). Other columns zero.</summary>
    public static byte[] TitlesDbc(params (int Id, string Name)[] rows)
    {
        var pool = new StringPool();
        var nameCol = Titles.Columns.ToList().FindIndex(c => c.Name == "Name_lang[0]");
        var list = rows.Select(r =>
        {
            var row = new long[Titles.FieldCount];
            row[Titles.IdColumn] = r.Id;
            row[nameCol] = pool.Intern(r.Name);
            return row;
        }).ToList();
        return new DbcTable(Titles, pool, list).ToFile().ToBytes();
    }

    public static Dictionary<long, string> ReadTitles(byte[] dbc)
    {
        var pool = new StringPool();
        var table = DbcTable.Read(DbcFile.Parse(dbc), Titles, pool);
        var nameCol = Titles.Columns.ToList().FindIndex(c => c.Name == "Name_lang[0]");
        return table.Rows.ToDictionary(r => r[Titles.IdColumn], r => pool.Get(r[nameCol]));
    }

    /// <summary>Creates Data/patch.MPQ (root) and Data/enUS/locale-enUS.MPQ (stock CharTitles + a stock lua).</summary>
    public static string Create(string root)
    {
        var data = Path.Combine(root, "Data");
        Directory.CreateDirectory(Path.Combine(data, "enUS"));
        MpqWriter.Write(Path.Combine(data, "patch.MPQ"), new Dictionary<string, byte[]>
        {
            ["Textures\\Stock.blp"] = [1, 2, 3],
        });
        MpqWriter.Write(Path.Combine(data, "enUS", "locale-enUS.MPQ"), new Dictionary<string, byte[]>
        {
            ["DBFilesClient\\CharTitles.dbc"] = TitlesDbc((1, "Private"), (2, "Corporal"), (3, "Sergeant")),
            ["Interface\\FrameXML\\Stock.lua"] = "stock"u8.ToArray(),
        });
        return data;
    }
}
