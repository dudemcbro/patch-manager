using System.IO.Compression;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Mpm.Core.Mpq;

namespace Mpm.Core.Store;

/// <summary>
/// Managed copy of every imported mod. Layout:
/// <c>mods/&lt;id&gt;/manifest.json</c> lists the mod's files; contents live once in
/// <c>blobs/&lt;sha[0..2]&gt;/&lt;sha&gt;</c>, zlib-compressed and named by the SHA-256 of the uncompressed
/// content (so case-insensitive archive paths never collide on a case-sensitive file system and
/// identical files are stored once).
/// The store never touches the original source or the client.
/// </summary>
public sealed partial class ModStore
{
    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    public ModStore(string root)
    {
        Root = Path.GetFullPath(root);
        Directory.CreateDirectory(ModsDir);
        Directory.CreateDirectory(BlobsDir);
    }

    public string Root { get; }
    private string ModsDir => Path.Combine(Root, "mods");
    private string BlobsDir => Path.Combine(Root, "blobs");

    /// <summary>$MPM_STORE, else $XDG_DATA_HOME/mpm/store, else ~/.local/share/mpm/store.</summary>
    public static string DefaultRoot()
    {
        if (Environment.GetEnvironmentVariable("MPM_STORE") is { Length: > 0 } explicitRoot)
        {
            return explicitRoot;
        }
        var data = Environment.GetEnvironmentVariable("XDG_DATA_HOME") is { Length: > 0 } xdg
            ? xdg
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "share");
        return Path.Combine(data, "mpm", "store");
    }

    public ImportResult Import(string sourcePath, string? name = null)
    {
        var full = Path.GetFullPath(sourcePath);
        SourceKind kind;
        string sha;
        if (Directory.Exists(full))
        {
            kind = SourceKind.Folder;
            sha = SourceScanner.Sha256Folder(full);
        }
        else if (File.Exists(full))
        {
            kind = full.EndsWith(".mpq", StringComparison.OrdinalIgnoreCase) ? SourceKind.Mpq
                : full.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) ? SourceKind.Zip
                : throw new NotSupportedException($"Unsupported mod source (expected .mpq, .zip or a folder): {full}");
            sha = SourceScanner.Sha256File(full);
        }
        else
        {
            throw new FileNotFoundException("Mod source not found", full);
        }

        if (List().FirstOrDefault(m => m.Source.Sha256 == sha) is { } existing)
        {
            return new ImportResult(existing, AlreadyImported: true);
        }

        // Collapse to one file per (chain, path); later contributions win, differing duplicates are reported.
        var warnings = new List<string>();
        var files = new Dictionary<(string Chain, string Path), ModFile>(new ChainPathComparer());
        void Sink(Contribution c)
        {
            var path = MpqPath.Normalize(c.Path);
            var fileSha = SourceScanner.Sha256(c.Data);
            WriteBlob(fileSha, c.Data);
            if (files.TryGetValue((c.Chain, path), out var previous) && previous.Sha256 != fileSha)
            {
                warnings.Add($"{c.Chain}:{path} appears more than once with different content; using {c.Origin} over {previous.Origin}");
            }
            files[(c.Chain, path)] = new ModFile(path, c.Chain, fileSha, c.Data.LongLength, c.Origin);
        }

        switch (kind)
        {
            case SourceKind.Mpq:
                SourceScanner.ScanMpq(full, Sink, warnings);
                break;
            case SourceKind.Folder:
                SourceScanner.ScanFolder(full, Sink, warnings);
                break;
            default:
                var temp = Path.Combine(Root, "tmp", Guid.NewGuid().ToString("N"));
                try
                {
                    SourceScanner.ExtractZip(full, temp);
                    SourceScanner.ScanFolder(temp, Sink, warnings);
                }
                finally
                {
                    if (Directory.Exists(temp))
                    {
                        Directory.Delete(temp, recursive: true);
                    }
                }
                break;
        }

        if (files.Count == 0)
        {
            throw new InvalidDataException($"No client files found in {full}");
        }

        var displayName = name ?? Path.GetFileNameWithoutExtension(full.TrimEnd(Path.DirectorySeparatorChar));
        var manifest = new ModManifest(
            ModManifest.CurrentSchema,
            $"{Slug(displayName)}-{sha[..8]}",
            displayName,
            DateTimeOffset.UtcNow,
            new ModSource(full, kind, sha),
            files.Values.OrderBy(f => f.Chain, StringComparer.Ordinal).ThenBy(f => f.Path, MpqPath.Comparer).ToList(),
            warnings);

        var dir = Path.Combine(ModsDir, manifest.Id);
        Directory.CreateDirectory(dir);
        WriteAtomic(Path.Combine(dir, "manifest.json"), JsonSerializer.SerializeToUtf8Bytes(manifest, Json));
        return new ImportResult(manifest, AlreadyImported: false);
    }

    public IReadOnlyList<ModManifest> List() =>
        Directory.EnumerateDirectories(ModsDir)
            .Select(d => Path.Combine(d, "manifest.json"))
            .Where(File.Exists)
            .Select(Load)
            .OrderBy(m => m.ImportedAt)
            .ToList();

    public ModManifest Get(string id)
    {
        var path = Path.Combine(ModsDir, SafeId(id), "manifest.json");
        return File.Exists(path) ? Load(path) : throw new KeyNotFoundException($"No mod with id {id}");
    }

    public byte[] Read(ModFile file)
    {
        using var source = new ZLibStream(File.OpenRead(BlobPath(file.Sha256)), CompressionMode.Decompress);
        var data = new byte[file.Size];
        source.ReadExactly(data);
        return data;
    }

    /// <summary>Removes a mod from the store (never the original source), then drops blobs nothing references.</summary>
    public void Remove(string id)
    {
        var dir = Path.Combine(ModsDir, SafeId(id));
        if (!Directory.Exists(dir))
        {
            throw new KeyNotFoundException($"No mod with id {id}");
        }
        Directory.Delete(dir, recursive: true);
        CollectGarbage();
    }

    /// <summary>Deletes blobs no manifest references. Returns how many were removed.</summary>
    public int CollectGarbage()
    {
        var live = List().SelectMany(m => m.Files).Select(f => f.Sha256).ToHashSet(StringComparer.Ordinal);
        var removed = 0;
        foreach (var blob in Directory.EnumerateFiles(BlobsDir, "*", SearchOption.AllDirectories))
        {
            if (!live.Contains(Path.GetFileName(blob)))
            {
                File.Delete(blob);
                removed++;
            }
        }
        return removed;
    }

    private static ModManifest Load(string path) =>
        JsonSerializer.Deserialize<ModManifest>(File.ReadAllBytes(path), Json)
        ?? throw new InvalidDataException($"Empty manifest: {path}");

    private string BlobPath(string sha) => Path.Combine(BlobsDir, sha[..2], sha);

    private void WriteBlob(string sha, byte[] data)
    {
        var path = BlobPath(sha);
        if (File.Exists(path))
        {
            return;
        }
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var tmp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        using (var z = new ZLibStream(File.Create(tmp), CompressionLevel.Fastest))
        {
            z.Write(data);
        }
        File.Move(tmp, path, overwrite: true);
    }

    private static void WriteAtomic(string path, byte[] data)
    {
        var tmp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        File.WriteAllBytes(tmp, data);
        File.Move(tmp, path, overwrite: true);
    }

    private static string SafeId(string id) =>
        IdRegex().IsMatch(id) ? id : throw new ArgumentException($"Invalid mod id: {id}", nameof(id));

    private static string Slug(string name)
    {
        var slug = SlugRegex().Replace(name.ToLowerInvariant(), "-").Trim('-');
        return slug.Length == 0 ? "mod" : slug.Length > 40 ? slug[..40].TrimEnd('-') : slug;
    }

    [GeneratedRegex("[^a-z0-9]+")]
    private static partial Regex SlugRegex();

    [GeneratedRegex("^[a-z0-9-]+$")]
    private static partial Regex IdRegex();

    private sealed class ChainPathComparer : IEqualityComparer<(string Chain, string Path)>
    {
        public bool Equals((string Chain, string Path) x, (string Chain, string Path) y) =>
            x.Chain == y.Chain && MpqPath.Comparer.Equals(x.Path, y.Path);

        public int GetHashCode((string Chain, string Path) obj) =>
            HashCode.Combine(obj.Chain, MpqPath.Comparer.GetHashCode(obj.Path));
    }
}
