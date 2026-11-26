using Mpm.Core.Store;

namespace Mpm.Core.Build;

public enum MergeMethod
{
    /// <summary>DBC merged field by field against the stock table.</summary>
    Rows,
    /// <summary>Whole file taken from one mod.</summary>
    File,
    /// <summary>Text file (Lua/XML/TOC) combined line by line from several mods.</summary>
    Text,
}

public enum TierSource
{
    /// <summary>The user answered the tier question.</summary>
    Answer,
    /// <summary>The mod ships an mpm-server.json.</summary>
    Declared,
    /// <summary>Detected from what the mod changes.</summary>
    Auto,
}

public sealed record TierDecision(string ModId, ModTier Tier, TierSource Source, IReadOnlyList<string> Reasons);

/// <summary>One choice offered to the user.</summary>
public sealed record QuestionOption(string Value, string Label);

/// <summary>
/// A decision the build made on its own that the user may want to change. Answering it (by <see cref="Id"/>)
/// and building again applies the answer. Ids are stable across builds of the same inputs.
/// </summary>
public sealed record Question(string Id, string Kind, string Prompt, string? Detail, IReadOnlyList<QuestionOption> Options, string Default, string Chosen);

/// <summary>Two or more mods changed the same data differently. Winner is the value used; QuestionId lets the user change it.</summary>
public sealed record Conflict(string Path, string? Row, IReadOnlyList<string> Mods, string Winner, IReadOnlyList<string> Columns, string QuestionId);

/// <summary>Changes a mod made that were not applied (e.g. a local mod editing gameplay data).</summary>
public sealed record DroppedChange(string Path, string ModId, int Rows, string Reason);

/// <summary>What happened to one output file.</summary>
public sealed record FileDecision(
    string Path,
    string Chain,
    MergeMethod Method,
    IReadOnlyList<string> Contributors,
    int RowsChanged,
    int RowsAdded,
    int RowsRemoved = 0,
    int RowsCombined = 0);

public sealed record BuildReport(
    string Locale,
    IReadOnlyList<string> ModOrder,
    IReadOnlyList<TierDecision> Tiers,
    IReadOnlyList<FileDecision> Files,
    IReadOnlyList<Conflict> Conflicts,
    IReadOnlyList<DroppedChange> Dropped,
    IReadOnlyList<Question> Questions,
    IReadOnlyList<string> KeepLoading,
    IReadOnlyList<string> Warnings);
