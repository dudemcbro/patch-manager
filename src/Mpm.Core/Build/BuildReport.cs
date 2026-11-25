namespace Mpm.Core.Build;

public enum MergeMethod
{
    /// <summary>DBC merged row by row against the stock table.</summary>
    Rows,
    /// <summary>Whole file taken from the highest-priority mod that changes it.</summary>
    File,
}

/// <summary>Two or more mods changed the same thing differently; the later (higher priority) mod won.</summary>
public sealed record Conflict(string Path, string? Row, IReadOnlyList<string> Mods, string Winner, IReadOnlyList<string> Columns);

/// <summary>What happened to one output file.</summary>
public sealed record FileDecision(
    string Path,
    string Chain,
    MergeMethod Method,
    IReadOnlyList<string> Contributors,
    int RowsChanged,
    int RowsAdded);

public sealed record BuildReport(
    string Locale,
    IReadOnlyList<string> ModOrder,
    IReadOnlyList<FileDecision> Files,
    IReadOnlyList<Conflict> Conflicts,
    IReadOnlyList<string> Warnings);
