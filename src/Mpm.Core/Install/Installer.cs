using System.Security.Cryptography;
using System.Text.Json;
using Mpm.Core.Build;
using Mpm.Core.Client;

namespace Mpm.Core.Install;

public sealed record UnapplyResult(IReadOnlyList<string> Restored, IReadOnlyList<string> Removed, IReadOnlyList<string> Problems);

/// <summary>
/// Puts a build into a client and takes it out again. Existing patches are only ever renamed
/// (to "&lt;name&gt;.mpm-off", which the client does not load), never deleted. The only files unapply
/// deletes are archives it installed itself, and only while their hash still matches the ledger.
/// </summary>
public sealed class Installer
{
    public const string DisabledSuffix = ".mpm-off";
    public const string ModifiedSuffix = ".mpm-modified";

    public Installer(string dataDir)
    {
        DataDir = new ClientInstall(dataDir).DataDir;
    }

    public string DataDir { get; }
    public string LedgerPath => Path.Combine(DataDir, Ledger.FileName);

    public Ledger? ReadLedger() =>
        File.Exists(LedgerPath) ? JsonSerializer.Deserialize<Ledger>(File.ReadAllBytes(LedgerPath), BuildOutput.Json) : null;

    /// <summary>
    /// Disables the given patches (absolute or Data-relative paths), then installs the build's archives.
    /// A patch already occupying the build's slot is disabled too. Fails if a build is already applied.
    /// </summary>
    public Ledger Apply(string buildDir, IEnumerable<string> disable)
    {
        if (ReadLedger() is not null)
        {
            throw new InvalidOperationException($"A build is already applied ({LedgerPath}); unapply it first");
        }
        var build = Path.GetFullPath(buildDir);
        var archives = Directory.EnumerateFiles(build, "*.MPQ", SearchOption.AllDirectories)
            .Concat(Directory.EnumerateFiles(build, "*.mpq", SearchOption.AllDirectories))
            .Distinct()
            .Select(p => Path.GetRelativePath(build, p))
            .Where(r => ArchiveNames.ClassifyPatch(Path.GetFileName(r)) is not null)
            .Order(StringComparer.Ordinal)
            .ToList();
        if (archives.Count == 0)
        {
            throw new InvalidDataException($"No patch archives in {build}");
        }

        var toDisable = disable.Select(Relative).Distinct(StringComparer.Ordinal).ToList();
        foreach (var rel in toDisable)
        {
            if (ArchiveNames.ClassifyPatch(Path.GetFileName(rel)) is not { } c || ArchiveNames.IsStockSlot(c.Slot))
            {
                throw new ArgumentException($"Not a custom patch the client loads: {rel}");
            }
            if (!File.Exists(Path.Combine(DataDir, rel)))
            {
                throw new FileNotFoundException($"Patch to disable not found: {rel}");
            }
        }

        var ledger = new Ledger(Ledger.CurrentSchema, DateTimeOffset.UtcNow, build, [], []);
        Save(ledger);
        foreach (var rel in toDisable)
        {
            Disable(ledger, rel, "replaced by build");
        }
        foreach (var rel in archives)
        {
            var target = TargetFor(rel);
            if (ClientInstall.FindCaseInsensitive(Path.Combine(DataDir, target)) is { } occupied)
            {
                Disable(ledger, Relative(occupied), "slot used by build");
            }
            var full = Path.Combine(DataDir, target);
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            var tmp = full + ".mpm-tmp";
            File.Copy(Path.Combine(build, rel), tmp, overwrite: true);
            File.Move(tmp, full);
            ledger.Installed.Add(new InstalledArchive(target, Sha256File(full)));
            Save(ledger);
        }
        return ledger;
    }

    /// <summary>Removes installed archives and renames disabled patches back. Keeps the ledger if anything could not be undone.</summary>
    public UnapplyResult Unapply()
    {
        var ledger = ReadLedger() ?? throw new InvalidOperationException("No build is applied (no ledger)");
        var restored = new List<string>();
        var removed = new List<string>();
        var problems = new List<string>();

        for (var i = ledger.Installed.Count - 1; i >= 0; i--)
        {
            var entry = ledger.Installed[i];
            var full = Path.Combine(DataDir, entry.Path);
            if (!File.Exists(full))
            {
                problems.Add($"{entry.Path} was already gone");
            }
            else if (Sha256File(full) == entry.Sha256)
            {
                File.Delete(full);
                removed.Add(entry.Path);
            }
            else
            {
                var aside = UniqueName(full + ModifiedSuffix);
                File.Move(full, aside);
                problems.Add($"{entry.Path} changed since apply; moved to {Path.GetRelativePath(DataDir, aside)} instead of deleting it");
            }
            ledger.Installed.RemoveAt(i);
            Save(ledger);
        }

        for (var i = ledger.Renamed.Count - 1; i >= 0; i--)
        {
            var entry = ledger.Renamed[i];
            var original = Path.Combine(DataDir, entry.Original);
            var renamed = Path.Combine(DataDir, entry.RenamedTo);
            if (File.Exists(original))
            {
                problems.Add($"cannot restore {entry.Original}: a file with that name exists; {entry.RenamedTo} left in place");
                continue;
            }
            if (!File.Exists(renamed))
            {
                problems.Add($"cannot restore {entry.Original}: {entry.RenamedTo} is missing");
                continue;
            }
            File.Move(renamed, original);
            if (Sha256File(original) != entry.Sha256)
            {
                problems.Add($"{entry.Original} restored, but its content changed while disabled");
            }
            restored.Add(entry.Original);
            ledger.Renamed.RemoveAt(i);
            Save(ledger);
        }

        if (ledger.Renamed.Count == 0 && ledger.Installed.Count == 0)
        {
            File.Delete(LedgerPath);
        }
        return new UnapplyResult(restored, removed, problems);
    }

    private void Disable(Ledger ledger, string rel, string reason)
    {
        var full = Path.Combine(DataDir, rel);
        var sha = Sha256File(full);
        var target = UniqueName(full + DisabledSuffix);
        File.Move(full, target);
        ledger.Renamed.Add(new RenamedPatch(rel, Path.GetRelativePath(DataDir, target), sha, reason));
        Save(ledger);
    }

    /// <summary>Where a build archive goes in the client, honoring an existing locale folder's casing.</summary>
    private string TargetFor(string buildRel)
    {
        var parts = buildRel.Split(Path.DirectorySeparatorChar);
        if (parts.Length == 1)
        {
            return parts[0];
        }
        var localeDir = Directory.EnumerateDirectories(DataDir)
            .Select(Path.GetFileName)
            .FirstOrDefault(d => string.Equals(d, parts[0], StringComparison.OrdinalIgnoreCase)) ?? parts[0];
        return Path.Combine(localeDir!, parts[^1]);
    }

    private string Relative(string path)
    {
        var full = Path.GetFullPath(Path.IsPathRooted(path) ? path : Path.Combine(DataDir, path));
        var rel = Path.GetRelativePath(DataDir, full);
        if (rel.StartsWith("..", StringComparison.Ordinal) || Path.IsPathRooted(rel))
        {
            throw new ArgumentException($"Path is outside the client Data folder: {path}");
        }
        return rel;
    }

    private void Save(Ledger ledger)
    {
        var tmp = LedgerPath + ".tmp";
        File.WriteAllBytes(tmp, JsonSerializer.SerializeToUtf8Bytes(ledger, BuildOutput.Json));
        File.Move(tmp, LedgerPath, overwrite: true);
    }

    private static string UniqueName(string path)
    {
        var candidate = path;
        for (var n = 1; File.Exists(candidate); n++)
        {
            candidate = $"{path}.{n}";
        }
        return candidate;
    }

    public static string Sha256File(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexStringLower(SHA256.HashData(stream));
    }
}
