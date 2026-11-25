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
    IReadOnlyList<string> Warnings)
{
    public const int CurrentSchema = 1;
}

public sealed record ImportResult(ModManifest Manifest, bool AlreadyImported);
