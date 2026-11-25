namespace Mpm.Core.Client;

/// <summary>A patch archive found in a client's Data directory.</summary>
public sealed record InstalledPatch(string Path, string Chain, string Slot, bool IsStock);

/// <summary>Read-only inspection of a 3.3.5a client's Data directory.</summary>
public sealed class ClientInstall
{
    public ClientInstall(string dataDir)
    {
        if (!Directory.Exists(dataDir))
        {
            throw new DirectoryNotFoundException(dataDir);
        }
        DataDir = Path.GetFullPath(dataDir);
    }

    public string DataDir { get; }

    /// <summary>Locale folders that hold a locale-xxXX.MPQ, in canonical casing ("enUS").</summary>
    public IReadOnlyList<string> Locales => LocaleDirs().Select(d => d.Locale).ToList();

    private IEnumerable<(string Locale, string Dir)> LocaleDirs() =>
        Directory.EnumerateDirectories(DataDir)
            .Select(d => (Name: Path.GetFileName(d), Dir: d))
            .Where(x => ArchiveNames.IsLocale(x.Name) && FindCaseInsensitive(Path.Combine(x.Dir, $"locale-{x.Name}.MPQ")) is not null)
            .Select(x => (ArchiveNames.CanonicalLocale(x.Name), x.Dir))
            .OrderBy(x => x.Item1, StringComparer.Ordinal);

    /// <summary>
    /// Every file the client loads as a patch (stock and custom), lowest priority first within each chain.
    /// Renamed copies such as "patch-4.MPQ.prev" do not match and are ignored, as the client ignores them.
    /// </summary>
    public IReadOnlyList<InstalledPatch> Patches()
    {
        var found = new List<InstalledPatch>();
        void Scan(string dir, string expectedChain)
        {
            foreach (var file in Directory.EnumerateFiles(dir))
            {
                var name = Path.GetFileName(file);
                if (ArchiveNames.ClassifyPatch(name) is { } c && c.Chain == expectedChain)
                {
                    found.Add(new InstalledPatch(file, c.Chain, c.Slot, ArchiveNames.IsStockSlot(c.Slot)));
                }
            }
        }
        Scan(DataDir, ArchiveNames.Root);
        foreach (var (locale, dir) in LocaleDirs())
        {
            Scan(dir, locale);
        }
        return found
            .OrderBy(p => p.Chain == ArchiveNames.Root ? "" : p.Chain, StringComparer.Ordinal)
            .ThenBy(p => ArchiveNames.SlotOrder(p.Slot))
            .ToList();
    }

    public IReadOnlyList<InstalledPatch> CustomPatches() => Patches().Where(p => !p.IsStock).ToList();

    /// <summary>
    /// Finds a file ignoring the case of its name and of its parent folder (clients copied from Windows
    /// often mix case, e.g. "enus/patch-enus-3.mpq").
    /// </summary>
    public static string? FindCaseInsensitive(string path)
    {
        if (File.Exists(path))
        {
            return path;
        }
        var dir = Path.GetDirectoryName(path);
        if (dir is null)
        {
            return null;
        }
        if (!Directory.Exists(dir))
        {
            var parent = Path.GetDirectoryName(dir);
            var dirName = Path.GetFileName(dir);
            dir = parent is not null && Directory.Exists(parent)
                ? Directory.EnumerateDirectories(parent).FirstOrDefault(d => string.Equals(Path.GetFileName(d), dirName, StringComparison.OrdinalIgnoreCase))
                : null;
            if (dir is null)
            {
                return null;
            }
        }
        var name = Path.GetFileName(path);
        return Directory.EnumerateFiles(dir).FirstOrDefault(f => string.Equals(Path.GetFileName(f), name, StringComparison.OrdinalIgnoreCase));
    }
}
