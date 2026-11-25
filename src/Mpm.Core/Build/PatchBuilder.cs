using Mpm.Core.Client;
using Mpm.Core.Dbc;
using Mpm.Core.Mpq;
using Mpm.Core.Store;

namespace Mpm.Core.Build;

/// <summary>Merged files per chain ("root" or a locale) plus the report explaining every decision.</summary>
public sealed record BuildResult(IReadOnlyDictionary<string, Dictionary<string, byte[]>> Chains, BuildReport Report);

/// <summary>
/// Merges mods (lowest priority first) on top of the stock client.
/// DBCs with a layout that matches every copy are merged row by row: a mod only contributes the rows it
/// actually changes relative to stock, so two mods that each ship a full Spell.dbc but edit different
/// spells both keep their edits. Same row changed differently by two mods = conflict, later mod wins.
/// Everything else is file-level: the highest-priority mod that changes a file wins.
/// Mods never delete stock rows: rows a mod's copy lacks are left as they are and reported.
/// </summary>
public sealed class PatchBuilder
{
    private static readonly HashSet<string> LocaleTopLevel = new(StringComparer.OrdinalIgnoreCase) { "interface", "dbfilesclient", "fonts" };

    private readonly ModStore _store;
    private readonly BaselineChain _baseline;
    private readonly DbcLayouts _layouts;

    public PatchBuilder(ModStore store, BaselineChain baseline, DbcLayouts layouts)
    {
        _store = store;
        _baseline = baseline;
        _layouts = layouts;
    }

    public BuildResult Build(IReadOnlyList<ModManifest> mods)
    {
        var warnings = new List<string>();
        var conflicts = new List<Conflict>();
        var decisions = new List<FileDecision>();
        var chains = new Dictionary<string, Dictionary<string, byte[]>>(StringComparer.Ordinal);

        if (mods.Select(m => m.Id).Distinct().Count() != mods.Count)
        {
            throw new ArgumentException("A mod appears more than once in the build order", nameof(mods));
        }

        // path -> contributions in priority order (one per mod).
        var byPath = new Dictionary<string, List<(ModManifest Mod, ModFile File)>>(MpqPath.Comparer);
        foreach (var mod in mods)
        {
            foreach (var group in mod.Files.GroupBy(f => f.Path, MpqPath.Comparer))
            {
                var pick = PickCopy(mod, group.ToList(), warnings);
                if (!byPath.TryGetValue(pick.Path, out var list))
                {
                    byPath[pick.Path] = list = [];
                }
                list.Add((mod, pick));
            }
        }

        foreach (var (path, contributions) in byPath.OrderBy(kv => kv.Key, MpqPath.Comparer))
        {
            var hasStock = _baseline.TryRead(path, out var stock);
            var chain = ChainFor(path);
            var copies = contributions
                .Select(c => (c.Mod, Data: _store.Read(c.File)))
                .Where(c => !hasStock || !c.Data.AsSpan().SequenceEqual(stock))
                .ToList();
            if (copies.Count == 0)
            {
                continue; // every mod's copy equals stock: nothing to write
            }

            byte[] output;
            FileDecision decision;
            if (DbcLayouts.IsDbcPath(path) && TryMergeRows(path, hasStock ? stock : null, copies, conflicts, warnings) is { } merged)
            {
                output = merged.Bytes;
                decision = new FileDecision(path, chain, MergeMethod.Rows, copies.Select(c => c.Mod.Id).ToList(), merged.Changed, merged.Added);
            }
            else
            {
                output = copies[^1].Data;
                var distinct = copies.Select(c => SourceShaKey(c.Data)).Distinct().Count();
                if (distinct > 1)
                {
                    conflicts.Add(new Conflict(path, null, copies.Select(c => c.Mod.Id).ToList(), copies[^1].Mod.Id, []));
                }
                decision = new FileDecision(path, chain, MergeMethod.File, copies.Select(c => c.Mod.Id).ToList(), 0, 0);
            }

            if (hasStock && output.AsSpan().SequenceEqual(stock))
            {
                continue;
            }
            if (!chains.TryGetValue(chain, out var files))
            {
                chains[chain] = files = new Dictionary<string, byte[]>(MpqPath.Comparer);
            }
            files[path] = output;
            decisions.Add(decision);
        }

        var report = new BuildReport(_baseline.Locale, mods.Select(m => m.Id).ToList(), decisions, conflicts, warnings);
        return new BuildResult(chains, report);
    }

    /// <summary>Chain the merged file belongs in: where its stock copy lives, else by top-level folder.</summary>
    public string ChainFor(string path)
    {
        if (_baseline.Locate(path) is { } hit)
        {
            return hit.Chain;
        }
        var top = MpqPath.Normalize(path).Split('\\')[0];
        return LocaleTopLevel.Contains(top) ? _baseline.Locale : ArchiveNames.Root;
    }

    /// <summary>
    /// A mod may ship the same path in several chains (e.g. DBCs in both patch-4 and patch-enUS-4).
    /// Use the copy in the chain the file belongs to; warn when the copies differ.
    /// </summary>
    private ModFile PickCopy(ModManifest mod, List<ModFile> copies, List<string> warnings)
    {
        if (copies.Count == 1)
        {
            return copies[0];
        }
        var target = ChainFor(copies[0].Path);
        var pick = copies.FirstOrDefault(c => c.Chain == target) ?? copies[^1];
        if (copies.Select(c => c.Sha256).Distinct().Count() > 1)
        {
            warnings.Add($"{mod.Id}: {pick.Path} differs between its {string.Join("/", copies.Select(c => c.Chain))} copies; using the {pick.Chain} copy");
        }
        return pick;
    }

    private sealed record RowMergeResult(byte[] Bytes, int Changed, int Added);

    private RowMergeResult? TryMergeRows(
        string path,
        byte[]? stock,
        List<(ModManifest Mod, byte[] Data)> copies,
        List<Conflict> conflicts,
        List<string> warnings)
    {
        var layout = _layouts.Find(path);
        if (layout is null)
        {
            warnings.Add($"{path}: no known layout; merged as a whole file");
            return null;
        }

        var files = new List<(string Mod, DbcFile File)>();
        try
        {
            if (stock is not null)
            {
                files.Add(("stock", DbcFile.Parse(stock)));
            }
            files.AddRange(copies.Select(c => (c.Mod.Id, DbcFile.Parse(c.Data))));
        }
        catch (InvalidDataException e)
        {
            warnings.Add($"{path}: not a valid WDBC file ({e.Message}); merged as a whole file");
            return null;
        }
        if (files.FirstOrDefault(f => !layout.Matches(f.File)) is { File: not null } bad)
        {
            warnings.Add($"{path}: {bad.Mod}'s copy has {bad.File.FieldCount} fields / {bad.File.RecordSize} bytes, " +
                         $"layout expects {layout.FieldCount} / {layout.RecordSize}; merged as a whole file");
            return null;
        }

        var pool = new StringPool();
        var stockTable = stock is null ? null : DbcTable.Read(files[0].File, layout, pool);
        var mods = files.Skip(stock is null ? 0 : 1).Select(f => (f.Mod, Table: DbcTable.Read(f.File, layout, pool))).ToList();
        var template = stockTable ?? mods[0].Table;

        if (layout.IdColumn < 0)
        {
            return MergeKeyless(path, stockTable, mods, template, warnings);
        }

        var stockRows = new Dictionary<long, long[]>();
        foreach (var row in stockTable?.Rows ?? [])
        {
            stockRows[row[layout.IdColumn]] = row;
        }
        var merged = new Dictionary<long, (long[] Row, string? Owner)>(stockRows.ToDictionary(kv => kv.Key, kv => (kv.Value, (string?)null)));
        int changed = 0, added = 0;
        foreach (var (mod, table) in mods)
        {
            var seen = new HashSet<long>();
            foreach (var row in table.Rows)
            {
                var key = row[layout.IdColumn];
                if (!seen.Add(key))
                {
                    warnings.Add($"{path}: {mod} has more than one row with ID {key}; the last one is used");
                }
                if (stockRows.TryGetValue(key, out var stockRow) && DbcTable.RowsEqual(row, stockRow))
                {
                    continue; // unchanged from stock: this mod does not claim the row
                }
                if (merged.TryGetValue(key, out var current) && current.Owner is not null && current.Owner != mod
                    && !DbcTable.RowsEqual(current.Row, row))
                {
                    var columns = Enumerable.Range(0, row.Length).Where(i => row[i] != current.Row[i]).Select(i => layout.Columns[i].Name).ToList();
                    conflicts.Add(new Conflict(path, key.ToString(System.Globalization.CultureInfo.InvariantCulture), [current.Owner, mod], mod, columns));
                }
                if (current.Owner is null && !merged.ContainsKey(key))
                {
                    added++;
                }
                else if (current.Owner is null)
                {
                    changed++;
                }
                merged[key] = (row, mod);
            }
            if (stockTable is not null)
            {
                var missing = stockRows.Keys.Count(k => !seen.Contains(k));
                if (missing > 0 && table.Rows.Count > 0)
                {
                    warnings.Add($"{path}: {mod} lacks {missing} stock rows; they are kept (mods cannot delete stock rows)");
                }
            }
        }

        var result = new DbcTable(layout, pool, merged.OrderBy(kv => kv.Key).Select(kv => kv.Value.Row).ToList());
        return new RowMergeResult(result.ToFile().ToBytes(), changed, added);
    }

    /// <summary>Tables without an ID column (e.g. CharBaseInfo): union of distinct rows, stock order first.</summary>
    private static RowMergeResult MergeKeyless(
        string path,
        DbcTable? stock,
        List<(string Mod, DbcTable Table)> mods,
        DbcTable template,
        List<string> warnings)
    {
        var rows = new List<long[]>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var row in stock?.Rows ?? [])
        {
            if (seen.Add(string.Join(',', row)))
            {
                rows.Add(row);
            }
        }
        var stockCount = rows.Count;
        foreach (var (_, table) in mods)
        {
            foreach (var row in table.Rows)
            {
                if (seen.Add(string.Join(',', row)))
                {
                    rows.Add(row);
                }
            }
        }
        if (rows.Count > stockCount && stock is not null)
        {
            warnings.Add($"{path}: table has no ID column; mod rows were added alongside stock rows (edits to a stock row appear as an extra row)");
        }
        var result = new DbcTable(template.Layout, template.Strings, rows);
        return new RowMergeResult(result.ToFile().ToBytes(), 0, rows.Count - stockCount);
    }

    private static string SourceShaKey(byte[] data) => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(data));
}
