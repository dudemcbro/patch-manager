using System.Diagnostics.CodeAnalysis;
using Mpm.Core.Mpq;

namespace Mpm.Core.Build;

/// <summary>
/// The files of one output chain. Merged files (DBCs, combined text) are held in memory; files taken whole
/// from one mod stay in the mod store and are read only when the archive is written, so a build of
/// gigabytes of textures does not hold them all at once. Reading a value loads it.
/// </summary>
public sealed class OutputFiles : IReadOnlyDictionary<string, byte[]>
{
    private readonly Dictionary<string, Func<byte[]>> _files = new(MpqPath.Comparer);

    public void Add(string path, byte[] data) => _files[path] = () => data;

    /// <summary>Adds a file read on demand (from the mod store).</summary>
    public void Add(string path, Func<byte[]> load) => _files[path] = load;

    /// <summary>Paths with their loaders, for writing archives without loading every file first.</summary>
    public IEnumerable<KeyValuePair<string, Func<byte[]>>> Loaders => _files;

    public byte[] this[string key] => _files[key]();
    public IEnumerable<string> Keys => _files.Keys;
    public IEnumerable<byte[]> Values => _files.Values.Select(f => f());
    public int Count => _files.Count;
    public bool ContainsKey(string key) => _files.ContainsKey(key);

    public bool TryGetValue(string key, [MaybeNullWhen(false)] out byte[] value)
    {
        if (_files.TryGetValue(key, out var load))
        {
            value = load();
            return true;
        }
        value = null;
        return false;
    }

    public IEnumerator<KeyValuePair<string, byte[]>> GetEnumerator() =>
        _files.Select(kv => new KeyValuePair<string, byte[]>(kv.Key, kv.Value())).GetEnumerator();

    System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
}
