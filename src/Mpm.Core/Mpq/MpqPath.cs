namespace Mpm.Core.Mpq;

/// <summary>Archive path helpers. MPQ lookups are case-insensitive and use backslashes.</summary>
public static class MpqPath
{
    public static string Normalize(string path) => path.Replace('/', '\\').TrimStart('\\');

    public static StringComparer Comparer => StringComparer.OrdinalIgnoreCase;
}
