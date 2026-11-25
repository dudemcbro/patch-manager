namespace Mpm.Core.Store;

public enum SourceKind
{
    Mpq,
    Folder,
    Zip,
}

public sealed record ModSource(string Path, SourceKind Kind, string Sha256);

/// <summary>One file a mod provides. Chain is "root" or a locale code (e.g. "enUS").</summary>
public sealed record ModFile(string Path, string Chain, string Sha256, long Size, string Origin);

public sealed record ModManifest(
    int Schema,
    string Id,
    string Name,
    DateTimeOffset ImportedAt,
    ModSource Source,
    IReadOnlyList<ModFile> Files,
    IReadOnlyList<string> Warnings,
    int UnnamedFiles = 0)
{
    /// <summary>
    /// True when the source holds files that could not be identified (archive without a (listfile) entry
    /// for them). Those files are not in the store, so the source patch must keep loading.
    /// </summary>
    public bool IsComplete => UnnamedFiles == 0;

    public const int CurrentSchema = 1;
}

public sealed record ImportResult(ModManifest Manifest, bool AlreadyImported);
