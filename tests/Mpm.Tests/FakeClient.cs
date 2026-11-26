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

    public static readonly DbcLayout Icons = DbcLayouts.For335().Find("SpellIcon")!;

    /// <summary>SpellIcon rows: (ID, texture path). A cosmetic table.</summary>
    public static byte[] IconsDbc(params (int Id, string Texture)[] rows)
    {
        var pool = new StringPool();
        var list = rows.Select(r => new long[] { r.Id, pool.Intern(r.Texture) }).ToList();
        return new DbcTable(Icons, pool, list).ToFile().ToBytes();
    }

    public static Dictionary<long, string> ReadIcons(byte[] dbc)
    {
        var pool = new StringPool();
        return DbcTable.Read(DbcFile.Parse(dbc), Icons, pool).Rows.ToDictionary(r => r[0], r => pool.Get(r[1]));
    }

    /// <summary>CharTitles rows with every column given by name (others zero).</summary>
    public static byte[] TitlesDbcFull(params (int Id, string Name, long Mask)[] rows)
    {
        var pool = new StringPool();
        var cols = Titles.Columns.ToList();
        var nameCol = cols.FindIndex(c => c.Name == "Name_lang[0]");
        var maskCol = cols.FindIndex(c => c.Name == "Mask_ID");
        var list = rows.Select(r =>
        {
            var row = new long[Titles.FieldCount];
            row[Titles.IdColumn] = r.Id;
            row[nameCol] = pool.Intern(r.Name);
            row[maskCol] = r.Mask;
            return row;
        }).ToList();
        return new DbcTable(Titles, pool, list).ToFile().ToBytes();
    }

    public static Dictionary<long, (string Name, long Mask)> ReadTitlesFull(byte[] dbc)
    {
        var pool = new StringPool();
        var cols = Titles.Columns.ToList();
        var nameCol = cols.FindIndex(c => c.Name == "Name_lang[0]");
        var maskCol = cols.FindIndex(c => c.Name == "Mask_ID");
        return DbcTable.Read(DbcFile.Parse(dbc), Titles, pool).Rows.ToDictionary(r => r[0], r => (pool.Get(r[nameCol]), r[maskCol]));
    }

    public static readonly DbcLayout Spell = DbcLayouts.For335().Find("Spell")!;
    private static readonly int SpellIconCol = Spell.Columns.ToList().FindIndex(c => c.Name == "SpellIconID");

    /// <summary>Spell rows: (ID, SpellIconID). Other columns zero.</summary>
    public static byte[] SpellDbc(params (int Id, int Icon)[] rows)
    {
        var list = rows.Select(r =>
        {
            var row = new long[Spell.FieldCount];
            row[Spell.IdColumn] = r.Id;
            row[SpellIconCol] = r.Icon;
            return row;
        }).ToList();
        return new DbcTable(Spell, new StringPool(), list).ToFile().ToBytes();
    }

    public static Dictionary<long, long> ReadSpellIcons(byte[] dbc) =>
        DbcTable.Read(DbcFile.Parse(dbc), Spell, new StringPool()).Rows.ToDictionary(r => r[Spell.IdColumn], r => r[SpellIconCol]);

    public static readonly byte[] StockLua = System.Text.Encoding.UTF8.GetBytes(
        "local a = 1\r\nlocal b = 2\r\nlocal c = 3\r\nlocal d = 4\r\nlocal e = 5\r\nlocal f = 6\r\n");

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
            ["DBFilesClient\\SpellIcon.dbc"] = IconsDbc((1, "Interface\\Icons\\Stock1"), (2, "Interface\\Icons\\Stock2")),
            ["DBFilesClient\\Spell.dbc"] = SpellDbc((1, 1), (2, 2)),
            ["Interface\\FrameXML\\Stock.lua"] = StockLua,
        });
        return data;
    }
}
