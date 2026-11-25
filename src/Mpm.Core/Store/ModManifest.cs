namespace Mpm.Core.Store;

public enum SourceKind
{
    Mpq,
    Folder,
    Zip,
}

/// <summary>Server: part of a server's patch set (authoritative). Local: a mod only this player uses.</summary>
public enum ModTier
{
    Server,
    Local,
}

/// <summary>
/// Optional "mpm-server.json" a server owner can ship at the root of a patch folder/zip or inside an MPQ:
/// <c>{"schema": 1, "tier": "server", "server": "ChromieCraft"}</c>.
/// </summary>
public sealed record ServerDeclaration(int Schema, ModTier Tier, string? Server)
{
    public const string FileName = "mpm-server.json";
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
    int UnnamedFiles = 0,
    ServerDeclaration? Declaration = null)
{
    /// <summary>
    /// True when the source holds files that could not be identified (archive without a (listfile) entry
    /// for them). Those files are not in the store, so the source patch must keep loading.
    /// </summary>
    public bool IsComplete => UnnamedFiles == 0;

    public const int CurrentSchema = 1;
}

public sealed record ImportResult(ModManifest Manifest, bool AlreadyImported);
