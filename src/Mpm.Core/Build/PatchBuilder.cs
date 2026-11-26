using System.Globalization;
using Mpm.Core.Client;
using Mpm.Core.Dbc;
using Mpm.Core.Merge;
using Mpm.Core.Mpq;
using Mpm.Core.Store;

namespace Mpm.Core.Build;

/// <summary>Merged files per chain ("root" or a locale) plus the report explaining every decision.</summary>
public sealed record BuildResult(IReadOnlyDictionary<string, OutputFiles> Chains, BuildReport Report);

/// <summary>Answers to questions from an earlier build, keyed by <see cref="Question.Id"/>.</summary>
public sealed record BuildOptions(IReadOnlyDictionary<string, string>? Answers = null);

/// <summary>
/// Merges mods (lowest priority first) on top of the stock client.
///
/// DBCs whose layout matches every copy are merged field by field against stock (a 3-way merge): a mod
/// only contributes the fields it changed, so two mods editing different fields of the same row both
/// keep their edits. Each mod is a Server mod (part of the server's patch set) or a Local mod (only this
/// player uses it); Local mods may only change cosmetic fields of rows the server owns, and Server
/// changes win over Local ones. Every judgment call (which mod wins a conflict, a mod's tier, whether
/// to honor rows a mod removed, ...) is recorded as a <see cref="Question"/> with a default; answering
/// it and building again applies the answer.
///
/// Local mods whose new rows clash with another mod's new rows on ID get fresh IDs (with their own
/// references rewritten) when that is safe; see <c>Renumber</c>.
///
/// Everything else is file-level: the highest-priority mod that changes a file wins.
/// </summary>
public sealed partial class PatchBuilder
{
    private static readonly HashSet<string> LocaleTopLevel = new(StringComparer.OrdinalIgnoreCase) { "interface", "dbfilesclient", "fonts" };
    private static readonly IReadOnlyList<string> YesNo = ["yes", "no"];
    private static readonly IReadOnlyList<QuestionOption> YesNoOptions = [new("yes", "Yes"), new("no", "No")];

    private readonly ModStore _store;
    private readonly BaselineChain _baseline;
    private readonly DbcLayouts _layouts;
    private readonly FieldClassifier _classifier;

    public PatchBuilder(ModStore store, BaselineChain baseline, DbcLayouts layouts, FieldClassifier? classifier = null)
    {
        _store = store;
        _baseline = baseline;
        _layouts = layouts;
        _classifier = classifier ?? FieldClassifier.For335();
    }

    public BuildResult Build(IReadOnlyList<ModManifest> mods, BuildOptions? options = null) => new Run(this, mods, options ?? new()).Execute();

    /// <summary>"DBFilesClient\Spell.dbc" -> "Spell" (archive paths use backslashes on every OS).</summary>
    private static string TableOf(string archivePath) => Path.GetFileNameWithoutExtension(archivePath.Replace('\\', '/'));

    /// <summary>"DBFilesClient\Spell.dbc" -> "Spell.dbc".</summary>
    private static string FileOf(string archivePath) => Path.GetFileName(archivePath.Replace('\\', '/'));

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

    /// <summary>One build: holds the per-build state so the builder itself stays reusable.</summary>
    private sealed partial class Run
    {
        private readonly PatchBuilder _b;
        private readonly IReadOnlyList<ModManifest> _mods;
        private readonly Dictionary<string, int> _rank;
        private readonly QuestionBook _q;
        private readonly List<string> _warnings = [];
        private readonly List<Conflict> _conflicts = [];
        private readonly List<DroppedChange> _dropped = [];
        private readonly List<FileDecision> _decisions = [];
        private readonly Dictionary<string, ModTier> _tier = new(StringComparer.Ordinal);
        private readonly Dictionary<string, (int Rows, HashSet<string> Files)> _localGameplay = new(StringComparer.Ordinal);
        private readonly Dictionary<string, ConflictGroup> _groups = new(StringComparer.Ordinal);

        private sealed class ConflictGroup
        {
            public required string Kind;
            public required string Path;
            public required List<string> Mods;
            public int Rows;
            public readonly HashSet<string> Columns = new(StringComparer.Ordinal);
            public string? SampleRow;
            public string? Note;
        }

        public Run(PatchBuilder builder, IReadOnlyList<ModManifest> mods, BuildOptions options)
        {
            if (mods.Select(m => m.Id).Distinct().Count() != mods.Count)
            {
                throw new ArgumentException("A mod appears more than once in the build order", nameof(mods));
            }
            _b = builder;
            _mods = mods;
            _rank = mods.Select((m, i) => (m.Id, i)).ToDictionary(x => x.Id, x => x.i, StringComparer.Ordinal);
            _q = new QuestionBook(options.Answers);
        }

        private string Name(string modId) => _mods.First(m => m.Id == modId).Name;

        public BuildResult Execute()
        {
            var byPath = CollectPaths();
            var tiers = DecideTiers(byPath);
            var keepLoading = AskIncomplete();
            var renumbered = Renumber(byPath);

            var chains = new Dictionary<string, OutputFiles>(StringComparer.Ordinal);
            foreach (var (path, contributions) in byPath.OrderBy(kv => kv.Key, MpqPath.Comparer))
            {
                var isDbc = DbcLayouts.IsDbcPath(path);
                byte[]? stock = null;
                List<Copy> copies;
                if (isDbc || TextMerge.IsTextPath(path))
                {
                    // Merged by content: read every copy and the stock file.
                    stock = _b._baseline.TryRead(path, out var s) ? s : null;
                    copies = contributions
                        .Select(c => Copy.Loaded(c.Mod, renumbered.GetValueOrDefault((c.Mod.Id, path)) ?? _b._store.Read(c.File)))
                        .Where(c => stock is null || !c.Data!.AsSpan().SequenceEqual(stock))
                        .ToList();
                }
                else
                {
                    // Taken whole (textures, models, sounds, ...): decide from the manifests' sizes and hashes and
                    // leave the data in the store. The stock copy is only read when a mod's copy has the same size.
                    var hasStock = _b._baseline.TryGetSize(path, out var stockSize);
                    string? stockSha = null;
                    copies = contributions
                        .Where(c => !hasStock || c.File.Size != stockSize || c.File.Sha256 != (stockSha ??= StockSha(path)))
                        .Select(c => Copy.Stored(c.Mod, c.File, _b._store))
                        .ToList();
                }
                if (copies.Count == 0)
                {
                    continue; // every mod's copy equals stock: nothing to write
                }

                var chain = _b.ChainFor(path);
                var result = isDbc ? MergeDbc(path, chain, stock, copies.Select(c => (c.Mod, c.Data!)).ToList()) : null;
                result ??= MergeWholeFile(path, chain, stock, copies);
                if (result is not { } r || (stock is not null && r.Bytes is not null && r.Bytes.AsSpan().SequenceEqual(stock)))
                {
                    continue;
                }
                if (!chains.TryGetValue(chain, out var files))
                {
                    chains[chain] = files = new OutputFiles();
                }
                if (r.Bytes is not null)
                {
                    files.Add(path, r.Bytes);
                }
                else
                {
                    files.Add(path, r.Load);
                }
                _decisions.Add(r.Decision);
            }

            DescribeQuestions();
            foreach (var unused in _q.UnusedAnswers())
            {
                _warnings.Add($"answer '{unused}' does not match any question in this build; ignored");
            }
            var report = new BuildReport(_b._baseline.Locale, _mods.Select(m => m.Id).ToList(), tiers, _decisions, _conflicts,
                _dropped, _q.All(), keepLoading, _warnings, _renumbered);
            return new BuildResult(chains, report);
        }

        // ---- inputs ---------------------------------------------------------------------------

        private Dictionary<string, List<(ModManifest Mod, ModFile File)>> CollectPaths()
        {
            var byPath = new Dictionary<string, List<(ModManifest Mod, ModFile File)>>(MpqPath.Comparer);
            foreach (var mod in _mods)
            {
                foreach (var group in mod.Files.GroupBy(f => f.Path, MpqPath.Comparer))
                {
                    var pick = PickCopy(mod, group.ToList());
                    if (!byPath.TryGetValue(pick.Path, out var list))
                    {
                        byPath[pick.Path] = list = [];
                    }
                    list.Add((mod, pick));
                }
            }
            return byPath;
        }

        /// <summary>A mod may ship one path in several chains; use the copy in the chain the file belongs to.</summary>
        private ModFile PickCopy(ModManifest mod, List<ModFile> copies)
        {
            if (copies.Count == 1)
            {
                return copies[0];
            }
            var target = _b.ChainFor(copies[0].Path);
            var pick = copies.FirstOrDefault(c => c.Chain == target) ?? copies[^1];
            if (copies.Select(c => c.Sha256).Distinct().Count() > 1)
            {
                _warnings.Add($"{mod.Id}: {pick.Path} differs between its {string.Join("/", copies.Select(c => c.Chain))} copies; using the {pick.Chain} copy");
            }
            return pick;
        }

        // ---- tiers ----------------------------------------------------------------------------

        private List<TierDecision> DecideTiers(Dictionary<string, List<(ModManifest Mod, ModFile File)>> byPath)
        {
            var decisions = new List<TierDecision>();
            foreach (var mod in _mods)
            {
                var id = $"tier:{mod.Id}";
                ModTier detected;
                TierSource source;
                List<string> reasons;
                if (mod.Declaration is { } d)
                {
                    detected = d.Tier;
                    source = TierSource.Declared;
                    reasons = [$"declared in {ServerDeclaration.FileName}{(d.Server is null ? "" : $" ({d.Server})")}"];
                }
                else
                {
                    (detected, reasons) = DetectTier(mod, byPath);
                    source = TierSource.Auto;
                }
                var chosen = _q.Choose(id, ["server", "local"], detected == ModTier.Server ? "server" : "local");
                var tier = chosen == "server" ? ModTier.Server : ModTier.Local;
                if (_q.IsAnswered(id))
                {
                    source = TierSource.Answer;
                }
                _tier[mod.Id] = tier;
                decisions.Add(new TierDecision(mod.Id, tier, source, reasons));
                _q.Describe(id, "tier",
                    $"Is \"{mod.Name}\" part of your server's patches, or a mod only you use?",
                    $"Detected: {(detected == ModTier.Server ? "server" : "local")} ({string.Join("; ", reasons)}). " +
                    "Server patches win over local mods; local mods may only change visuals on server data.",
                    [new("server", "Server patch"), new("local", "Local mod (only I use it)")]);
            }
            return decisions;
        }

        /// <summary>Server if the mod changes gameplay data (any non-cosmetic DBC field or new gameplay rows); else Local.</summary>
        private (ModTier, List<string>) DetectTier(ModManifest mod, Dictionary<string, List<(ModManifest Mod, ModFile File)>> byPath)
        {
            var reasons = new List<string>();
            foreach (var (path, contributions) in byPath.Where(kv => DbcLayouts.IsDbcPath(kv.Key)))
            {
                if (contributions.FirstOrDefault(c => c.Mod.Id == mod.Id) is not { File: not null } mine)
                {
                    continue;
                }
                var table = TableOf(path);
                var hasStock = _b._baseline.TryRead(path, out var stock);
                var data = _b._store.Read(mine.File);
                if (hasStock && data.AsSpan().SequenceEqual(stock))
                {
                    continue;
                }
                if (_b._classifier.IsCosmeticTable(table))
                {
                    continue;
                }
                var layout = _b._layouts.Find(path);
                DbcFile modFile;
                DbcFile? stockFile;
                try
                {
                    modFile = DbcFile.Parse(data);
                    stockFile = hasStock ? DbcFile.Parse(stock) : null;
                }
                catch (InvalidDataException)
                {
                    reasons.Add($"replaces {table}.dbc (unreadable)");
                    continue;
                }
                if (layout is null || !layout.Matches(modFile) || (stockFile is not null && !layout.Matches(stockFile)))
                {
                    reasons.Add($"replaces {table}.dbc");
                    continue;
                }
                var pool = new StringPool();
                var modRows = DbcTable.Read(modFile, layout, pool).Rows;
                if (stockFile is null)
                {
                    reasons.Add($"adds new table {table}.dbc");
                    continue;
                }
                var stockTable = DbcTable.Read(stockFile, layout, pool);
                if (layout.IdColumn < 0)
                {
                    var stockKeys = stockTable.Rows.Select(r => string.Join(',', r)).ToHashSet();
                    var extra = modRows.Count(r => !stockKeys.Contains(string.Join(',', r)));
                    if (extra > 0)
                    {
                        reasons.Add($"adds {extra} rows to {table}.dbc");
                    }
                    continue;
                }
                var stockRows = stockTable.Rows.ToDictionary(r => r[layout.IdColumn]);
                int added = 0, changed = 0;
                foreach (var row in modRows)
                {
                    if (!stockRows.TryGetValue(row[layout.IdColumn], out var s))
                    {
                        added++;
                    }
                    else if (Enumerable.Range(0, row.Length).Any(c => row[c] != s[c] && _b._classifier.Classify(layout, c) == FieldClass.Gameplay))
                    {
                        changed++;
                    }
                }
                if (added > 0)
                {
                    reasons.Add($"adds {added} rows to {table}.dbc");
                }
                if (changed > 0)
                {
                    reasons.Add($"changes gameplay data in {changed} {table}.dbc rows");
                }
            }
            return reasons.Count > 0
                ? (ModTier.Server, reasons.Take(4).Concat(reasons.Count > 4 ? [$"and {reasons.Count - 4} more tables"] : []).ToList())
                : (ModTier.Local, ["only visuals, sounds, UI or other client files"]);
        }

        private List<string> AskIncomplete()
        {
            var keep = new List<string>();
            foreach (var mod in _mods.Where(m => !m.IsComplete))
            {
                var id = $"incomplete:{mod.Id}";
                if (_q.Choose(id, ["keep", "replace"], "keep") == "keep")
                {
                    keep.Add(mod.Id);
                }
                _q.Describe(id, "incomplete",
                    $"\"{mod.Name}\" has {mod.UnnamedFiles} file(s) that could not be identified. Keep the original patch loading?",
                    "Unidentified files are not in the merged patch. Keeping the original patch loading preserves them; " +
                    "replacing it drops them.",
                    [new("keep", "Keep it loading (safe)"), new("replace", "Replace it with the merged patch")]);
            }
            return keep;
        }

        // ---- rules ----------------------------------------------------------------------------

        /// <summary>Precedence: Server over Local, then later in the order.</summary>
        private string Best(IEnumerable<string> modIds, bool useTier = true) =>
            modIds.OrderBy(m => useTier && _tier[m] == ModTier.Server ? 1 : 0).ThenBy(m => _rank[m]).Last();

        /// <summary>Whether a Local mod's gameplay edits are kept (asked once per mod, default no).</summary>
        private bool AllowLocalGameplay(string modId, string path, int rows)
        {
            if (!_localGameplay.TryGetValue(modId, out var seen))
            {
                _localGameplay[modId] = seen = (0, new HashSet<string>(MpqPath.Comparer));
            }
            _localGameplay[modId] = (seen.Rows + rows, seen.Files);
            seen.Files.Add(FileOf(path));
            return _q.Choose($"localgameplay:{modId}", YesNo, "no") == "yes";
        }

        private bool CanChange(string modId, DbcLayout layout, int column) =>
            _tier[modId] == ModTier.Server || _b._classifier.Classify(layout, column) == FieldClass.Cosmetic;

        private string Choose(string kind, string path, IReadOnlyCollection<string> mods, string? row, IEnumerable<string> columns, bool useTier = true)
        {
            var ordered = mods.OrderBy(m => _rank[m]).ToList();
            var id = $"{kind}:{path}:{string.Join('+', ordered)}";
            var chosen = _q.Choose(id, ordered, Best(ordered, useTier));
            if (!_groups.TryGetValue(id, out var g))
            {
                _groups[id] = g = new ConflictGroup { Kind = kind, Path = path, Mods = ordered, SampleRow = row };
            }
            g.Rows++;
            g.Columns.UnionWith(columns);
            return chosen;
        }

        // ---- merging --------------------------------------------------------------------------

        /// <summary>A merge result: Bytes when built in memory, else Load reads the chosen copy from the store.</summary>
        private sealed record Merged(byte[]? Bytes, Func<byte[]> Load, FileDecision Decision)
        {
            public Merged(byte[] bytes, FileDecision decision) : this(bytes, () => bytes, decision)
            {
            }
        }

        /// <summary>One mod's copy of a path. Key identifies the content (SHA-256); Data is set when it was read.</summary>
        private sealed record Copy(ModManifest Mod, string Key, Func<byte[]> Load, byte[]? Data)
        {
            public static Copy Loaded(ModManifest mod, byte[] data) => new(mod, Store.SourceScanner.Sha256(data), () => data, data);

            public static Copy Stored(ModManifest mod, ModFile file, ModStore store) => new(mod, file.Sha256, () => store.Read(file), null);
        }

        private string StockSha(string path) =>
            _b._baseline.TryRead(path, out var data) ? Store.SourceScanner.Sha256(data) : "";

        private Merged? MergeWholeFile(string path, string chain, byte[]? stock, List<Copy> copies)
        {
            var isDbc = DbcLayouts.IsDbcPath(path);
            var table = TableOf(path);
            var gameplayTable = isDbc && !_b._classifier.IsCosmeticTable(table);
            var allowed = copies.Where(c => !gameplayTable || _tier[c.Mod.Id] == ModTier.Server || AllowLocalGameplay(c.Mod.Id, path, 1)).ToList();
            foreach (var c in copies.Except(allowed))
            {
                _dropped.Add(new DroppedChange(path, c.Mod.Id, 0, "local mod replacing a gameplay table"));
            }
            if (allowed.Count == 0)
            {
                return null;
            }
            var distinct = allowed.GroupBy(c => c.Key, StringComparer.Ordinal).ToList();
            if (distinct.Count == 1)
            {
                var only = allowed.OrderBy(c => _rank[c.Mod.Id]).Last();
                return new Merged(only.Data, only.Load, new FileDecision(path, chain, MergeMethod.File, allowed.Select(c => c.Mod.Id).ToList(), 0, 0));
            }

            // One representative per distinct content: the highest-priority mod shipping it, in priority order.
            var reps = distinct.Select(g => g.OrderBy(c => _rank[c.Mod.Id]).Last()).OrderBy(c => _rank[c.Mod.Id]).ToList();
            var repIds = reps.Select(c => c.Mod.Id).ToList();
            var contributors = allowed.Select(c => c.Mod.Id).ToList();

            string? note = null;
            if (stock is not null && TextMerge.IsTextPath(path))
            {
                var merged = TextMerge.MergeAll(stock, reps.Select(c => c.Data ?? c.Load()).ToList());
                if (merged.Clean)
                {
                    var id = $"text:{path}:{string.Join('+', repIds)}";
                    var chosenText = _q.Choose(id, ["merge", .. repIds], "merge");
                    _groups.TryAdd(id, new ConflictGroup { Kind = "text", Path = path, Mods = repIds });
                    var bytes = chosenText == "merge" ? merged.Bytes! : reps.Last(c => c.Mod.Id == chosenText).Load();
                    if (chosenText != "merge")
                    {
                        _conflicts.Add(new Conflict(path, null, repIds, chosenText, [], id));
                    }
                    return new Merged(bytes, new FileDecision(path, chain, MergeMethod.Text, contributors, 0, 0));
                }
                note = merged.ConflictLine is { } line
                    ? $"Their edits overlap near line {line} of the original file, so they cannot be combined automatically."
                    : $"They cannot be combined automatically ({merged.Reason}).";
            }

            // Assets (textures, models, sounds) are asked once per set of mods: HD packs overlap on hundreds of files.
            // DBC and UI files are asked per file, since each one matters on its own.
            var asset = !isDbc && !TextMerge.IsTextPath(path);
            var (kind, key) = asset ? ("files", "*") : ("file", path);
            var chosen = Choose(kind, key, repIds, asset ? path : null, [], useTier: isDbc);
            var questionId = $"{kind}:{key}:{string.Join('+', repIds)}";
            if (note is not null)
            {
                _groups[questionId].Note = note;
            }
            _conflicts.Add(new Conflict(path, null, repIds, chosen, [], questionId));
            var winner = allowed.Last(c => c.Mod.Id == chosen);
            return new Merged(winner.Data, winner.Load, new FileDecision(path, chain, MergeMethod.File, contributors, 0, 0));
        }

        private Merged? MergeDbc(string path, string chain, byte[]? stock, List<(ModManifest Mod, byte[] Data)> copies)
        {
            var layout = _b._layouts.Find(path);
            if (layout is null)
            {
                _warnings.Add($"{path}: no known layout; merged as a whole file");
                return null;
            }
            var files = new List<(string Mod, DbcFile File)>();
            DbcFile? stockFile;
            try
            {
                stockFile = stock is null ? null : DbcFile.Parse(stock);
                files.AddRange(copies.Select(c => (c.Mod.Id, DbcFile.Parse(c.Data))));
            }
            catch (InvalidDataException e)
            {
                _warnings.Add($"{path}: not a valid WDBC file ({e.Message}); merged as a whole file");
                return null;
            }
            if (stockFile is not null && !layout.Matches(stockFile))
            {
                _warnings.Add($"{path}: stock copy does not match the known layout; merged as a whole file");
                return null;
            }
            if (files.FirstOrDefault(f => !layout.Matches(f.File)) is { File: not null } bad)
            {
                _warnings.Add($"{path}: {bad.Mod}'s copy has {bad.File.FieldCount} fields / {bad.File.RecordSize} bytes, " +
                              $"layout expects {layout.FieldCount} / {layout.RecordSize}; merged as a whole file");
                return null;
            }

            var pool = new StringPool();
            var stockTable = stockFile is null ? null : DbcTable.Read(stockFile, layout, pool);
            var mods = files.Select(f => (f.Mod, Table: DbcTable.Read(f.File, layout, pool))).ToList();
            return layout.IdColumn < 0
                ? MergeKeyless(path, chain, layout, pool, stockTable, mods)
                : MergeKeyed(path, chain, layout, pool, stockTable, mods);
        }

        private Merged MergeKeyed(string path, string chain, DbcLayout layout, StringPool pool, DbcTable? stockTable, List<(string Mod, DbcTable Table)> mods)
        {
            var idCol = layout.IdColumn;
            var stockRows = new Dictionary<long, long[]>();
            foreach (var row in stockTable?.Rows ?? [])
            {
                stockRows[row[idCol]] = row;
            }

            // Each mod's rows by ID (a duplicated ID: last one wins).
            var modRows = new List<(string Mod, Dictionary<long, long[]> Rows)>();
            foreach (var (mod, table) in mods)
            {
                var rows = new Dictionary<long, long[]>();
                foreach (var row in table.Rows)
                {
                    if (!rows.TryAdd(row[idCol], row))
                    {
                        _warnings.Add($"{path}: {mod} has more than one row with ID {row[idCol]}; the last one is used");
                        rows[row[idCol]] = row;
                    }
                }
                modRows.Add((mod, rows));
            }

            // Rows a mod's copy lacks: honor the removal only if the user says so.
            var removals = new Dictionary<long, string>();
            if (stockTable is not null)
            {
                foreach (var (mod, rows) in modRows.Where(m => m.Rows.Count > 0))
                {
                    var missing = stockRows.Keys.Where(k => !rows.ContainsKey(k)).ToList();
                    if (missing.Count == 0)
                    {
                        continue;
                    }
                    var id = $"removals:{path}:{mod}";
                    var wanted = _q.Choose(id, YesNo, "no") == "yes";
                    var allowed = _tier[mod] == ModTier.Server || _b._classifier.IsCosmeticTable(layout.Table)
                        || (wanted && AllowLocalGameplay(mod, path, missing.Count));
                    if (wanted && allowed)
                    {
                        foreach (var k in missing)
                        {
                            removals.TryAdd(k, mod);
                        }
                    }
                    _q.Describe(id, "removals",
                        $"\"{Name(mod)}\" leaves out {missing.Count} of the stock rows in {FileOf(path)}. Remove them from the merged patch too?",
                        "Usually a mod built from an older copy simply lacks those rows (answer No). Answer Yes only if the mod is meant to remove them." +
                        (allowed ? "" : " (This is a local mod, so removing gameplay rows also needs the local gameplay question answered Yes.)"),
                        YesNoOptions);
                }
            }

            var merged = new SortedDictionary<long, long[]>();
            int changed = 0, added = 0, removed = 0, combined = 0;
            var dropRows = new Dictionary<string, int>(StringComparer.Ordinal);

            foreach (var (key, stockRow) in stockRows)
            {
                var claims = modRows.Where(m => m.Rows.TryGetValue(key, out var r) && !DbcTable.RowsEqual(r, stockRow))
                    .Select(m => (m.Mod, Row: m.Rows[key])).ToList();
                if (claims.Count == 0)
                {
                    if (removals.ContainsKey(key))
                    {
                        removed++;
                        continue;
                    }
                    merged[key] = stockRow;
                    continue;
                }

                var result = (long[])stockRow.Clone();
                var contributors = new HashSet<string>(StringComparer.Ordinal);
                var droppedBy = new HashSet<string>(StringComparer.Ordinal);
                var conflictCols = new List<string>();
                IReadOnlyList<string>? conflictMods = null;
                string? conflictWinner = null;
                for (var c = 0; c < result.Length; c++)
                {
                    var cands = new List<(string Mod, long Value)>();
                    foreach (var (mod, row) in claims)
                    {
                        if (row[c] == stockRow[c])
                        {
                            continue;
                        }
                        if (CanChange(mod, layout, c) || AllowLocalGameplay(mod, path, 0))
                        {
                            cands.Add((mod, row[c]));
                        }
                        else
                        {
                            droppedBy.Add(mod);
                        }
                    }
                    if (cands.Count == 0)
                    {
                        continue;
                    }
                    var values = cands.Select(x => x.Value).Distinct().ToList();
                    string winner;
                    if (values.Count == 1)
                    {
                        winner = Best(cands.Select(x => x.Mod));
                    }
                    else
                    {
                        // One representative per distinct value: its highest-priority mod.
                        var reps = cands.GroupBy(x => x.Value).Select(g => g.OrderBy(x => _rank[x.Mod]).Last().Mod).ToList();
                        winner = Choose("rows", path, reps, key.ToString(CultureInfo.InvariantCulture), [layout.Columns[c].Name]);
                        conflictCols.Add(layout.Columns[c].Name);
                        conflictMods = reps.OrderBy(m => _rank[m]).ToList();
                        conflictWinner = winner;
                    }
                    result[c] = cands.Last(x => x.Mod == winner).Value;
                    contributors.Add(winner);
                }
                foreach (var mod in droppedBy)
                {
                    dropRows[mod] = dropRows.GetValueOrDefault(mod) + 1;
                }
                if (conflictMods is not null)
                {
                    _conflicts.Add(new Conflict(path, key.ToString(CultureInfo.InvariantCulture), conflictMods, conflictWinner!, conflictCols,
                        $"rows:{path}:{string.Join('+', conflictMods)}"));
                }
                if (contributors.Count > 1)
                {
                    combined++;
                }
                if (!DbcTable.RowsEqual(result, stockRow))
                {
                    changed++;
                }
                merged[key] = result;
            }

            // Rows no stock copy has: whole-row, since there is no baseline to merge fields against.
            foreach (var key in modRows.SelectMany(m => m.Rows.Keys).Where(k => !stockRows.ContainsKey(k)).Distinct())
            {
                var cands = new List<(string Mod, long[] Row)>();
                foreach (var (mod, rows) in modRows)
                {
                    if (!rows.TryGetValue(key, out var row))
                    {
                        continue;
                    }
                    if (_tier[mod] == ModTier.Server || _b._classifier.IsCosmeticTable(layout.Table) || AllowLocalGameplay(mod, path, 0))
                    {
                        cands.Add((mod, row));
                    }
                    else
                    {
                        dropRows[mod] = dropRows.GetValueOrDefault(mod) + 1;
                    }
                }
                if (cands.Count == 0)
                {
                    continue;
                }
                var distinct = cands.GroupBy(x => string.Join(',', x.Row)).ToList();
                var winner = Best(cands.Select(x => x.Mod));
                if (distinct.Count > 1)
                {
                    var reps = distinct.Select(g => g.OrderBy(x => _rank[x.Mod]).Last().Mod).ToList();
                    winner = Choose("newrows", path, reps, key.ToString(CultureInfo.InvariantCulture), []);
                    var ordered = reps.OrderBy(m => _rank[m]).ToList();
                    _conflicts.Add(new Conflict(path, key.ToString(CultureInfo.InvariantCulture), ordered, winner, [], $"newrows:{path}:{string.Join('+', ordered)}"));
                }
                merged[key] = cands.Last(x => x.Mod == winner).Row;
                added++;
            }

            foreach (var (mod, rows) in dropRows)
            {
                _dropped.Add(new DroppedChange(path, mod, rows, "local mod changing gameplay data"));
            }

            var output = new DbcTable(layout, pool, merged.Values.ToList()).ToFile().ToBytes();
            var contributorsAll = mods.Select(m => m.Mod).ToList();
            return new Merged(output, new FileDecision(path, chain, MergeMethod.Rows, contributorsAll, changed, added, removed, combined));
        }

        /// <summary>Tables without an ID column (e.g. CharBaseInfo): union of distinct rows, stock order first.</summary>
        private Merged MergeKeyless(string path, string chain, DbcLayout layout, StringPool pool, DbcTable? stock, List<(string Mod, DbcTable Table)> mods)
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
            var cosmetic = _b._classifier.IsCosmeticTable(layout.Table);
            foreach (var (mod, table) in mods)
            {
                var extra = table.Rows.Where(r => !seen.Contains(string.Join(',', r))).ToList();
                if (extra.Count == 0)
                {
                    continue;
                }
                if (!cosmetic && _tier[mod] == ModTier.Local && !AllowLocalGameplay(mod, path, extra.Count))
                {
                    _dropped.Add(new DroppedChange(path, mod, extra.Count, "local mod adding gameplay rows"));
                    continue;
                }
                foreach (var row in extra)
                {
                    if (seen.Add(string.Join(',', row)))
                    {
                        rows.Add(row);
                    }
                }
            }
            if (rows.Count > stockCount && stock is not null)
            {
                _warnings.Add($"{path}: table has no ID column; mod rows were added alongside stock rows (edits to a stock row appear as an extra row)");
            }
            var output = new DbcTable(layout, pool, rows).ToFile().ToBytes();
            return new Merged(output, new FileDecision(path, chain, MergeMethod.Rows, mods.Select(m => m.Mod).ToList(), 0, rows.Count - stockCount));
        }

        // ---- wording --------------------------------------------------------------------------

        private void DescribeQuestions()
        {
            foreach (var (id, g) in _groups)
            {
                var file = FileOf(g.Path);
                var names = string.Join(" and ", g.Mods.Select(m => $"\"{Name(m)}\""));
                var options = g.Mods.Select(m => new QuestionOption(m, $"{Name(m)} ({_tier[m].ToString().ToLowerInvariant()})")).ToList();
                var (prompt, detail) = g.Kind switch
                {
                    "rows" => ($"{names} change {g.Rows} row(s) of {file} differently. Which should win?",
                        $"Fields: {string.Join(", ", g.Columns.Take(8))}{(g.Columns.Count > 8 ? ", ..." : "")}. Example row ID {g.SampleRow}. " +
                        "Fields only one mod changed are kept from that mod either way."),
                    "newrows" => ($"{names} each add a different row with the same ID in {file} ({g.Rows} ID(s)). Which should win?",
                        $"Example ID {g.SampleRow}. The other mod's row is left out."),
                    "files" => ($"{names} each ship their own version of {g.Rows} file(s). Which should win?",
                        $"For example {g.SampleRow}. Usually texture, model or sound packs that overlap; the other mod's versions of these files are not used."),
                    "text" => ($"{names} each change {file}. Their edits do not overlap, so they were combined. Keep the combined file?",
                        $"Path: {g.Path}. Pick a single mod only if the combined file misbehaves in game."),
                    _ => ($"{names} each ship a different {file}. Which should win?", $"{(g.Note is null ? "" : g.Note + " ")}Path: {g.Path}"),
                };
                if (g.Kind == "text")
                {
                    options = [new("merge", "Combined (both mods' edits)"), .. g.Mods.Select(m => new QuestionOption(m, $"Only {Name(m)}"))];
                }
                _q.Describe(id, g.Kind, prompt, detail, options);
            }
            foreach (var (mod, (_, files)) in _localGameplay)
            {
                var id = $"localgameplay:{mod}";
                _q.Describe(id, "localgameplay",
                    $"\"{Name(mod)}\" is a local mod but changes gameplay data in {string.Join(", ", files.Order())}. Keep those changes anyway?",
                    "The server decides gameplay, so these edits only change what your client shows and can make it disagree with the server. " +
                    "If this mod actually came from your server, answer the tier question instead.",
                    YesNoOptions);
            }
        }
    }
}
