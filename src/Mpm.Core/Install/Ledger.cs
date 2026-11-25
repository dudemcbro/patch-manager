namespace Mpm.Core.Install;

/// <summary>A client patch the tool renamed so the client stops loading it. Never deleted.</summary>
public sealed record RenamedPatch(string Original, string RenamedTo, string Sha256, string Reason);

/// <summary>An archive the tool placed in the client.</summary>
public sealed record InstalledArchive(string Path, string Sha256);

/// <summary>
/// Record of everything apply changed in a client Data dir (paths relative to it), so unapply can
/// restore it exactly. Written after every step, so an interrupted apply can still be undone.
/// </summary>
public sealed record Ledger(int Schema, DateTimeOffset AppliedAt, string BuildDir, List<RenamedPatch> Renamed, List<InstalledArchive> Installed)
{
    public const int CurrentSchema = 1;
    public const string FileName = ".mpm-ledger.json";
}
