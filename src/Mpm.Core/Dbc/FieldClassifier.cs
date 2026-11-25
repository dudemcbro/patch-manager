using System.Reflection;
using System.Text.Json;

namespace Mpm.Core.Dbc;

public enum FieldClass
{
    /// <summary>Anything the server is authoritative for, all text, and IDs the server sends.</summary>
    Gameplay,
    /// <summary>Client-only visuals and sound; local mods may change these on server-owned rows.</summary>
    Cosmetic,
}

/// <summary>Classifies DBC columns as gameplay or cosmetic, from data/classification-3.3.5.12340.json.</summary>
public sealed class FieldClassifier
{
    private readonly HashSet<string> _cosmeticTables;
    private readonly Dictionary<string, HashSet<string>> _cosmeticColumns;

    private FieldClassifier(HashSet<string> tables, Dictionary<string, HashSet<string>> columns)
    {
        _cosmeticTables = tables;
        _cosmeticColumns = columns;
    }

    private static readonly Lazy<FieldClassifier> Wotlk = new(Load);

    public static FieldClassifier For335() => Wotlk.Value;

    public bool IsCosmeticTable(string table) => _cosmeticTables.Contains(table);

    /// <summary>Class of one physical column. The ID column is always gameplay (it is the row's identity).</summary>
    public FieldClass Classify(DbcLayout layout, int column)
    {
        if (column == layout.IdColumn)
        {
            return FieldClass.Gameplay;
        }
        if (_cosmeticTables.Contains(layout.Table))
        {
            return FieldClass.Cosmetic;
        }
        return _cosmeticColumns.TryGetValue(layout.Table, out var cols) && cols.Contains(BaseName(layout.Columns[column].Name))
            ? FieldClass.Cosmetic
            : FieldClass.Gameplay;
    }

    /// <summary>"SpellVisualID[1]" -> "SpellVisualID", "Name_lang[3]" / "Name_lang_mask" -> "Name_lang".</summary>
    public static string BaseName(string column)
    {
        var name = column.EndsWith("_mask", StringComparison.Ordinal) ? column[..^5] : column;
        var bracket = name.IndexOf('[');
        return bracket < 0 ? name : name[..bracket];
    }

    /// <summary>Names in the classification that do not exist in the layouts (should be empty).</summary>
    public IEnumerable<string> UnknownNames(DbcLayouts layouts)
    {
        foreach (var t in _cosmeticTables.Concat(_cosmeticColumns.Keys).Where(t => layouts.Find(t) is null))
        {
            yield return t;
        }
        foreach (var (table, cols) in _cosmeticColumns)
        {
            if (layouts.Find(table) is not { } layout)
            {
                continue;
            }
            var names = layout.Columns.Select(c => BaseName(c.Name)).ToHashSet(StringComparer.Ordinal);
            foreach (var c in cols.Where(c => !names.Contains(c)))
            {
                yield return $"{table}.{c}";
            }
        }
    }

    private static FieldClassifier Load()
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("classification-3.3.5.12340.json")
            ?? throw new InvalidOperationException("Missing embedded classification");
        using var doc = JsonDocument.Parse(stream);
        var tables = doc.RootElement.GetProperty("cosmeticTables").EnumerateArray()
            .Select(e => e.GetString()!).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var columns = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var t in doc.RootElement.GetProperty("cosmeticColumns").EnumerateObject())
        {
            columns[t.Name] = t.Value.EnumerateArray().Select(e => e.GetString()!).ToHashSet(StringComparer.Ordinal);
        }
        return new FieldClassifier(tables, columns);
    }
}
