using Mpm.Core.Mpq;

namespace Mpm.Core.Client;

/// <summary>
/// The stock (unmodded) file set of a 3.3.5a client: stock root archives plus the stock archives of one
/// locale. Custom patches (patch-4.., patch-A.. etc.) are never part of the baseline.
/// In stock 3.3.5a no path exists in both the root and the locale chain, so their relative priority
/// does not affect resolution; within each chain later archives win.
/// </summary>
public sealed class BaselineChain : IDisposable
{
    private readonly List<(string Chain, MpqReader Reader)> _archives = []; // lowest priority first
    private Dictionary<string, (string Chain, MpqReader Reader)>? _index;

    public BaselineChain(string dataDir, string locale)
    {
        DataDir = dataDir;
        Locale = ArchiveNames.CanonicalLocale(locale);
        try
        {
            foreach (var name in ArchiveNames.StockRoot)
            {
                Add(ArchiveNames.Root, Path.Combine(dataDir, name));
            }
            foreach (var name in ArchiveNames.StockLocale(Locale))
            {
                Add(Locale, Path.Combine(dataDir, Locale, name));
            }
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    public string DataDir { get; }
    public string Locale { get; }

    /// <summary>Stock archives that were found, lowest priority first.</summary>
    public IEnumerable<string> Archives => _archives.Select(a => a.Reader.SourcePath);

    /// <summary>Stock archives that should exist but were not found.</summary>
    public List<string> Missing { get; } = [];

    /// <summary>Every stock path (normalized) mapped to the chain that provides it.</summary>
    public IReadOnlyDictionary<string, string> Paths => Index().ToDictionary(kv => kv.Key, kv => kv.Value.Chain, MpqPath.Comparer);

    public bool Contains(string path) => Index().ContainsKey(MpqPath.Normalize(path));

    /// <summary>Which chain and archive provide the stock copy of a path, or null when it is not stock.</summary>
    public (string Chain, string Archive)? Locate(string path) =>
        Index().TryGetValue(MpqPath.Normalize(path), out var hit) ? (hit.Chain, hit.Reader.SourcePath) : null;

    public bool TryRead(string path, out byte[] data)
    {
        if (Index().TryGetValue(MpqPath.Normalize(path), out var hit))
        {
            data = hit.Reader.Read(path);
            return true;
        }
        data = [];
        return false;
    }

    public void Dispose()
    {
        foreach (var (_, reader) in _archives)
        {
            reader.Dispose();
        }
        _archives.Clear();
    }

    private void Add(string chain, string path)
    {
        var actual = ClientInstall.FindCaseInsensitive(path);
        if (actual is null)
        {
            Missing.Add(path);
            return;
        }
        _archives.Add((chain, MpqReader.Open(actual)));
    }

    private Dictionary<string, (string Chain, MpqReader Reader)> Index()
    {
        if (_index is not null)
        {
            return _index;
        }
        var index = new Dictionary<string, (string, MpqReader)>(MpqPath.Comparer);
        foreach (var entry in _archives)
        {
            foreach (var file in entry.Reader.Files)
            {
                index[file] = entry; // later archive wins
            }
        }
        return _index = index;
    }
}
