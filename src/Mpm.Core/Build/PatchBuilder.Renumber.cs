using System.Globalization;
using Mpm.Core.Dbc;
using Mpm.Core.Mpq;
using Mpm.Core.Store;

namespace Mpm.Core.Build;

public sealed partial class PatchBuilder
{
    private sealed partial class Run
    {
        private readonly List<RenumberedRows> _renumbered = [];

        /// <summary>
        /// Local mods that add a row with the same new ID as a higher-precedence mod (but different content)
        /// would lose that row. When the Local mod itself points at the row from its own data (a known foreign
        /// key), the row gets a free ID and those references are rewritten, so both mods keep their rows.
        /// Server mods are never renumbered, and a row nothing in the mod points at is left alone (its ID is
        /// probably one the server sends). Returns replacement bytes for the mod copies that changed.
        /// </summary>
        private Dictionary<(string Mod, string Path), byte[]> Renumber(Dictionary<string, List<(ModManifest Mod, ModFile File)>> byPath)
        {
            var overrides = new Dictionary<(string Mod, string Path), byte[]>();
            if (_mods.All(m => _tier[m.Id] == ModTier.Server))
            {
                return overrides;
            }

            // One pool for the whole pass so rows compare across mods and tables.
            var pool = new StringPool();
            var tables = new Dictionary<(string Mod, string Path), DbcTable?>();
            var stocks = new Dictionary<string, (bool Ok, DbcTable? Table)>(MpqPath.Comparer);
            var dirty = new HashSet<(string Mod, string Path)>();
            var modPaths = byPath.Where(kv => DbcLayouts.IsDbcPath(kv.Key))
                .SelectMany(kv => kv.Value.Select(c => (c.Mod.Id, Path: kv.Key)))
                .GroupBy(x => x.Id, x => x.Path)
                .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.Ordinal);

            foreach (var (path, contributions) in byPath.OrderBy(kv => kv.Key, MpqPath.Comparer))
            {
                if (!DbcLayouts.IsDbcPath(path) || contributions.Count < 2 || contributions.All(c => _tier[c.Mod.Id] == ModTier.Server))
                {
                    continue;
                }
                var layout = _b._layouts.Find(path);
                if (layout is null || layout.IdColumn < 0 || !TryStock(path, out var stock))
                {
                    continue;
                }
                var copies = contributions.Select(c => (Mod: c.Mod.Id, Table: Load(c.Mod.Id, path))).ToList();
                if (copies.Any(c => c.Table is null))
                {
                    continue;
                }

                var idCol = layout.IdColumn;
                var stockIds = stock?.Rows.Select(r => r[idCol]).ToHashSet() ?? [];
                var cosmetic = _b._classifier.IsCosmeticTable(layout.Table);
                // Each mod's new rows (IDs not in stock), last duplicate wins as in the merge.
                var newRows = copies.Select(c => (c.Mod, Rows: NewRows(c.Table!, idCol, stockIds))).ToList();

                var clashes = new Dictionary<string, (List<long> Keys, HashSet<string> Against)>(StringComparer.Ordinal);
                foreach (var key in newRows.SelectMany(m => m.Rows.Keys).Distinct().Order())
                {
                    var cands = newRows.Where(m => m.Rows.ContainsKey(key))
                        .Where(m => _tier[m.Mod] == ModTier.Server || cosmetic || AllowLocalGameplay(m.Mod, path, 0))
                        .Select(m => (m.Mod, Row: m.Rows[key])).ToList();
                    if (cands.Count < 2)
                    {
                        continue;
                    }
                    var winner = Best(cands.Select(c => c.Mod));
                    var winnerRow = cands.Last(c => c.Mod == winner).Row;
                    foreach (var (mod, row) in cands)
                    {
                        if (mod == winner || _tier[mod] != ModTier.Local || DbcTable.RowsEqual(row, winnerRow))
                        {
                            continue;
                        }
                        if (!clashes.TryGetValue(mod, out var c))
                        {
                            clashes[mod] = c = ([], new HashSet<string>(StringComparer.Ordinal));
                        }
                        c.Keys.Add(key);
                        c.Against.Add(winner);
                    }
                }

                foreach (var (mod, (keys, against)) in clashes.OrderBy(c => _rank[c.Key]))
                {
                    RenumberMod(mod, path, layout, keys, against, copies);
                }
            }

            foreach (var (mod, path) in dirty)
            {
                var t = tables[(mod, path)]!;
                overrides[(mod, path)] = new DbcTable(t.Layout, pool, t.Rows).ToFile().ToBytes();
            }
            return overrides;

            void RenumberMod(string mod, string path, DbcLayout layout, List<long> keys, HashSet<string> against, List<(string Mod, DbcTable? Table)> copies)
            {
                var clashSet = keys.ToHashSet();
                var target = $"{layout.Table}::ID";

                // References to the clashing IDs from this mod's own data (rows or fields it changed itself).
                var refs = new List<(string Path, int Row, int Column)>();
                foreach (var refPath in modPaths[mod])
                {
                    var refLayout = _b._layouts.Find(refPath);
                    if (refLayout is null || Load(mod, refPath) is not { } refTable || !TryStock(refPath, out var refStock))
                    {
                        continue;
                    }
                    var fkCols = Enumerable.Range(0, refLayout.FieldCount)
                        .Where(c => string.Equals(refLayout.Columns[c].ForeignKey, target, StringComparison.OrdinalIgnoreCase))
                        .ToList();
                    if (fkCols.Count == 0)
                    {
                        continue;
                    }
                    var others = byPath[refPath].Where(c => c.Mod.Id != mod)
                        .Select(c => Load(c.Mod.Id, refPath)).Where(t => t is not null)
                        .SelectMany(t => t!.Rows).Select(Key).ToHashSet(StringComparer.Ordinal);
                    var stockById = refLayout.IdColumn >= 0 && refStock is not null
                        ? refStock.Rows.GroupBy(r => r[refLayout.IdColumn]).ToDictionary(g => g.Key, g => g.Last())
                        : null;
                    var stockKeys = refStock?.Rows.Select(Key).ToHashSet(StringComparer.Ordinal) ?? [];
                    for (var r = 0; r < refTable.Rows.Count; r++)
                    {
                        var row = refTable.Rows[r];
                        long[]? baseRow = null;
                        if (stockById is not null && stockById.TryGetValue(row[refLayout.IdColumn], out var s))
                        {
                            baseRow = s;
                        }
                        else if (stockKeys.Contains(Key(row)) || others.Contains(Key(row)))
                        {
                            continue; // a stock or another mod's row, not this mod's own
                        }
                        foreach (var c in fkCols)
                        {
                            if (clashSet.Contains(row[c]) && (baseRow is null || baseRow[c] != row[c]))
                            {
                                refs.Add((refPath, r, c));
                            }
                        }
                    }
                }

                var referenced = keys.Where(k => refs.Any(x => tables[(mod, x.Path)]!.Rows[x.Row][x.Column] == k)).ToList();
                if (referenced.Count == 0)
                {
                    return; // nothing in the mod points at these rows: likely IDs the server sends, so not renumbered
                }

                var id = $"renumber:{path}:{mod}";
                var yes = _q.Choose(id, YesNo, "yes") == "yes";
                var refNames = refs.GroupBy(x => $"{TableOf(x.Path)}.{_b._layouts.Find(x.Path)!.Columns[x.Column].Name}")
                    .Select(g => $"{g.Key} x{g.Count()}").ToList();
                var againstNames = string.Join(" and ", against.OrderBy(m => _rank[m]).Select(m => $"\"{Name(m)}\""));

                // Free IDs above every ID any copy (or stock) uses in this table.
                var idCol = layout.IdColumn;
                var next = copies.SelectMany(c => c.Table!.Rows).Concat(stocks[path].Table?.Rows ?? []).Select(r => r[idCol])
                    .DefaultIfEmpty(0).Max() + 1;
                var map = referenced.Select((k, i) => (From: k, To: next + i)).ToDictionary(x => x.From, x => x.To);
                var tooBig = refs.Select(x => _b._layouts.Find(x.Path)!.Columns[x.Column]).Append(layout.Columns[idCol])
                    .FirstOrDefault(col => map.Values.Max() > MaxValue(col.Kind));

                var sample = string.Join(", ", map.Take(5).Select(kv => $"{kv.Key} -> {kv.Value}")) + (map.Count > 5 ? ", ..." : "");
                _q.Describe(id, "renumber",
                    $"\"{Name(mod)}\" and {againstNames} each add a different row with the same ID in {FileOf(path)} ({map.Count} ID(s)). " +
                    $"Give \"{Name(mod)}\"'s rows new IDs so both are kept?",
                    $"New IDs: {sample}. References inside \"{Name(mod)}\" are updated to match: {string.Join(", ", refNames)}. " +
                    "Server patches are never renumbered. References from Lua, XML or model files are not updated; " +
                    $"answer No to leave \"{Name(mod)}\"'s rows out instead.",
                    [new("yes", "Yes, give them new IDs"), new("no", "No, leave them out")]);

                if (!yes)
                {
                    return;
                }
                if (tooBig is not null)
                {
                    _warnings.Add($"{path}: cannot renumber {mod}'s rows, new IDs do not fit in {tooBig.Name}; they are left out");
                    return;
                }

                var own = tables[(mod, path)]!;
                foreach (var row in own.Rows)
                {
                    if (map.TryGetValue(row[idCol], out var to))
                    {
                        row[idCol] = to;
                    }
                }
                dirty.Add((mod, path));
                foreach (var (refPath, r, c) in refs)
                {
                    var row = tables[(mod, refPath)]!.Rows[r];
                    if (map.TryGetValue(row[c], out var to))
                    {
                        row[c] = to;
                        dirty.Add((mod, refPath));
                    }
                }
                _renumbered.Add(new RenumberedRows(path, mod, against.OrderBy(m => _rank[m]).ToList(),
                    map.Select(kv => new IdChange(kv.Key, kv.Value)).ToList(), refNames, id));
            }

            DbcTable? Load(string mod, string path)
            {
                if (tables.TryGetValue((mod, path), out var t))
                {
                    return t;
                }
                var layout = _b._layouts.Find(path);
                var file = byPath[path].First(c => c.Mod.Id == mod).File;
                t = null;
                try
                {
                    var dbc = DbcFile.Parse(_b._store.Read(file));
                    if (layout is not null && layout.Matches(dbc))
                    {
                        var read = DbcTable.Read(dbc, layout, pool);
                        // Own copies of the rows, since renumbering edits them in place.
                        t = new DbcTable(layout, pool, read.Rows.Select(r => (long[])r.Clone()).ToList());
                    }
                }
                catch (InvalidDataException)
                {
                }
                tables[(mod, path)] = t;
                return t;
            }

            // False when the stock copy exists but cannot be used with the layout.
            bool TryStock(string path, out DbcTable? stock)
            {
                if (!stocks.TryGetValue(path, out var cached))
                {
                    cached = (true, null);
                    if (_b._baseline.TryRead(path, out var bytes))
                    {
                        try
                        {
                            var dbc = DbcFile.Parse(bytes);
                            var layout = _b._layouts.Find(path);
                            cached = layout is not null && layout.Matches(dbc) ? (true, DbcTable.Read(dbc, layout, pool)) : (false, null);
                        }
                        catch (InvalidDataException)
                        {
                            cached = (false, null);
                        }
                    }
                    stocks[path] = cached;
                }
                stock = cached.Table;
                return cached.Ok;
            }
        }

        private static Dictionary<long, long[]> NewRows(DbcTable table, int idCol, HashSet<long> stockIds)
        {
            var rows = new Dictionary<long, long[]>();
            foreach (var row in table.Rows.Where(r => !stockIds.Contains(r[idCol])))
            {
                rows[row[idCol]] = row;
            }
            return rows;
        }

        private static string Key(long[] row) => string.Join(',', row);

        private static long MaxValue(ColumnKind kind) => kind switch
        {
            ColumnKind.Int8 => sbyte.MaxValue,
            ColumnKind.UInt8 => byte.MaxValue,
            ColumnKind.Int16 => short.MaxValue,
            ColumnKind.UInt16 => ushort.MaxValue,
            ColumnKind.Int32 => int.MaxValue,
            _ => uint.MaxValue,
        };
    }
}
