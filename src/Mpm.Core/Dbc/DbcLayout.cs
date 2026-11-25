using System.Reflection;
using System.Text.Json;

namespace Mpm.Core.Dbc;

public enum ColumnKind
{
    Int8,
    UInt8,
    Int16,
    UInt16,
    Int32,
    UInt32,
    Float,
    String,
    /// <summary>The flags word after a locstring's 16 string offsets.</summary>
    LocMask,
}

/// <summary>One physical column of a WDBC record (arrays and locstrings are expanded).</summary>
public sealed record DbcColumn(string Name, ColumnKind Kind, int Offset, string? ForeignKey)
{
    public int Size => Kind switch
    {
        ColumnKind.Int8 or ColumnKind.UInt8 => 1,
        ColumnKind.Int16 or ColumnKind.UInt16 => 2,
        _ => 4,
    };
}

/// <summary>A table's record layout for one client build.</summary>
public sealed class DbcLayout
{
    /// <summary>Strings per locstring in 3.3.5a (one per locale slot), followed by a flags word.</summary>
    public const int LocStringSlots = 16;

    public DbcLayout(string table, IReadOnlyList<DbcColumn> columns, int idColumn)
    {
        Table = table;
        Columns = columns;
        IdColumn = idColumn;
        RecordSize = columns.Count == 0 ? 0 : columns[^1].Offset + columns[^1].Size;
    }

    public string Table { get; }
    public IReadOnlyList<DbcColumn> Columns { get; }

    /// <summary>Index of the 32-bit ID column, or -1 when the table has no ID (rows keyed by their whole content).</summary>
    public int IdColumn { get; }

    public int RecordSize { get; }
    public int FieldCount => Columns.Count;

    /// <summary>True when a file's header shape matches this layout (field count and record size).</summary>
    public bool Matches(DbcFile file) => file.FieldCount == FieldCount && file.RecordSize == RecordSize;
}

/// <summary>Known layouts for one client build, loaded from the vendored WoWDBDefs extract.</summary>
public sealed class DbcLayouts
{
    private readonly Dictionary<string, DbcLayout> _tables;

    private DbcLayouts(string build, Dictionary<string, DbcLayout> tables)
    {
        Build = build;
        _tables = tables;
    }

    public string Build { get; }
    public IReadOnlyCollection<string> Tables => _tables.Keys;

    private static readonly Lazy<DbcLayouts> Wotlk = new(() => LoadEmbedded("layouts-3.3.5.12340.json"));

    /// <summary>Layouts for 3.3.5a build 12340.</summary>
    public static DbcLayouts For335() => Wotlk.Value;

    /// <summary>Layout for a DBC archive path or table name ("DBFilesClient\Spell.dbc" or "Spell").</summary>
    public DbcLayout? Find(string pathOrTable)
    {
        var name = Path.GetFileNameWithoutExtension(pathOrTable.Replace('\\', '/'));
        return _tables.GetValueOrDefault(name);
    }

    public static bool IsDbcPath(string path) =>
        path.EndsWith(".dbc", StringComparison.OrdinalIgnoreCase)
        && path.Replace('/', '\\').StartsWith("DBFilesClient\\", StringComparison.OrdinalIgnoreCase);

    private static DbcLayouts LoadEmbedded(string resource)
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(resource)
            ?? throw new InvalidOperationException($"Missing embedded resource {resource}");
        using var doc = JsonDocument.Parse(stream);
        var build = doc.RootElement.GetProperty("build").GetString()!;
        var tables = new Dictionary<string, DbcLayout>(StringComparer.OrdinalIgnoreCase);
        foreach (var table in doc.RootElement.GetProperty("tables").EnumerateObject())
        {
            var columns = new List<DbcColumn>();
            var idColumn = -1;
            var offset = 0;
            foreach (var field in table.Value.EnumerateArray())
            {
                var name = field.GetProperty("name").GetString()!;
                var type = field.GetProperty("type").GetString()!;
                var count = field.TryGetProperty("array", out var a) ? a.GetInt32() : 1;
                var fk = field.TryGetProperty("fk", out var f) ? f.GetString() : null;
                var isId = field.TryGetProperty("id", out var idFlag) && idFlag.GetBoolean();
                for (var i = 0; i < count; i++)
                {
                    var element = count > 1 ? $"{name}[{i}]" : name;
                    if (type == "locstring")
                    {
                        for (var slot = 0; slot < DbcLayout.LocStringSlots; slot++)
                        {
                            Add(new DbcColumn($"{element}[{slot}]", ColumnKind.String, offset, null));
                        }
                        Add(new DbcColumn($"{element}_mask", ColumnKind.LocMask, offset, null));
                        continue;
                    }
                    var kind = type switch
                    {
                        "float" => ColumnKind.Float,
                        "string" => ColumnKind.String,
                        "int" => (field.GetProperty("size").GetInt32(), field.GetProperty("signed").GetBoolean()) switch
                        {
                            (8, true) => ColumnKind.Int8,
                            (8, false) => ColumnKind.UInt8,
                            (16, true) => ColumnKind.Int16,
                            (16, false) => ColumnKind.UInt16,
                            (32, true) => ColumnKind.Int32,
                            (32, false) => ColumnKind.UInt32,
                            var other => throw new InvalidDataException($"{table.Name}.{name}: unsupported int size {other}"),
                        },
                        _ => throw new InvalidDataException($"{table.Name}.{name}: unknown type {type}"),
                    };
                    if (isId && count == 1 && kind is ColumnKind.Int32 or ColumnKind.UInt32)
                    {
                        idColumn = columns.Count;
                    }
                    Add(new DbcColumn(element, kind, offset, fk));
                }
            }
            tables[table.Name] = new DbcLayout(table.Name, columns, idColumn);

            void Add(DbcColumn c)
            {
                columns.Add(c with { Offset = offset });
                offset += c.Size;
            }
        }
        return new DbcLayouts(build, tables);
    }
}
