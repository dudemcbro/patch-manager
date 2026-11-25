namespace Mpm.Core.Dbc;

/// <summary>Interns strings so rows from different DBC files can be compared as plain integers.</summary>
public sealed class StringPool
{
    private readonly Dictionary<string, long> _ids = new(StringComparer.Ordinal) { [""] = 0 };
    private readonly List<string> _strings = [""];

    public long Intern(string value)
    {
        if (!_ids.TryGetValue(value, out var id))
        {
            id = _strings.Count;
            _strings.Add(value);
            _ids[value] = id;
        }
        return id;
    }

    public string Get(long id) => _strings[(int)id];
}
