using System.IO.Compression;
using System.Security.Cryptography;
using Mpm.Core.Client;
using Mpm.Core.Mpq;

namespace Mpm.Core.Store;

/// <summary>A file found in a mod source, before it is stored. Later contributions win within a mod.</summary>
internal sealed record Contribution(string Path, string Chain, byte[] Data, string Origin);

/// <summary>Receives files one at a time, in priority order (lowest first), so large mods are never held in memory.</summary>
internal delegate void ContributionSink(Contribution contribution);

/// <summary>Turns an MPQ, folder or zip into an ordered list of archive files.</summary>
internal static class SourceScanner
{
    /// <summary>Refuse zips that would expand beyond this (guards against zip bombs).</summary>
    public const long MaxZipBytes = 16L * 1024 * 1024 * 1024;

    /// <summary>Returns how many files in the archive could not be named.</summary>
    public static int ScanMpq(string path, ContributionSink sink, List<string> warnings, IReadOnlyCollection<string> nameHints,
        string? parentHint = null, string? origin = null)
    {
        var chain = ChainForMpq(Path.GetFileName(path), parentHint ?? Path.GetFileName(Path.GetDirectoryName(path)));
        var label = origin ?? Path.GetFileName(path);
        using var reader = MpqReader.Open(path);
        var names = reader.FilesWith(nameHints);
        var recovered = names.Count - reader.Files.Count;
        if (recovered > 0)
        {
            warnings.Add($"{label}: {recovered} file name(s) recovered from known paths (archive has an incomplete or no (listfile))");
        }
        var unnamed = reader.CountUnnamed(names);
        if (unnamed > 0)
        {
            warnings.Add($"{label}: {unnamed} file(s) could not be identified and were not imported; this patch will be kept loading when a build is applied");
        }
        foreach (var f in names.Where(n => !n.Equals(ServerDeclaration.FileName, StringComparison.OrdinalIgnoreCase)))
        {
            sink(new Contribution(f, chain, reader.Read(f), label));
        }
        return unnamed;
    }

    /// <summary>Returns how many files in the folder's archives could not be named.</summary>
    public static int ScanFolder(string root, ContributionSink sink, List<string> warnings, IReadOnlyCollection<string> nameHints)
    {
        var mpqs = new List<(string Path, string Chain, int Order)>();
        var loose = new List<(string ArchivePath, string Chain, string File, string Rel)>();

        foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal))
        {
            var rel = Path.GetRelativePath(root, file);
            var segments = rel.Split(Path.DirectorySeparatorChar);
            if (file.EndsWith(".mpq", StringComparison.OrdinalIgnoreCase))
            {
                var parent = segments.Length > 1 ? segments[^2] : null;
                var name = Path.GetFileName(file);
                var slot = ArchiveNames.ClassifyPatch(name)?.Slot ?? "";
                mpqs.Add((file, ChainForMpq(name, parent), ArchiveNames.SlotOrder(slot)));
                continue;
            }

            var top = Array.FindIndex(segments, s => ArchiveNames.KnownTopLevel.Contains(s));
            if (top < 0 && segments.Length == 1 && segments[0].Equals(ServerDeclaration.FileName, StringComparison.OrdinalIgnoreCase))
            {
                continue; // read separately by ModStore
            }
            if (top < 0)
            {
                warnings.Add($"skipped {rel.Replace('\\', '/')}: not under a known client folder");
                continue;
            }
            var chain = top > 0 && ArchiveNames.IsLocale(segments[top - 1])
                ? ArchiveNames.CanonicalLocale(segments[top - 1])
                : ArchiveNames.Root;
            loose.Add((string.Join('\\', segments[top..]), chain, file, rel.Replace('\\', '/')));
        }

        // MPQs in client priority order, then loose files on top.
        var unnamed = 0;
        foreach (var (path, _, _) in mpqs.OrderBy(m => m.Order).ThenBy(m => m.Path, StringComparer.Ordinal))
        {
            var rel = Path.GetRelativePath(root, path).Split(Path.DirectorySeparatorChar);
            unnamed += ScanMpq(path, sink, warnings, nameHints, rel.Length > 1 ? rel[^2] : null, string.Join('/', rel));
        }
        foreach (var (archivePath, chain, file, rel) in loose)
        {
            sink(new Contribution(archivePath, chain, File.ReadAllBytes(file), rel));
        }
        return unnamed;
    }

    /// <summary>Extracts a zip into <paramref name="destination"/>, rejecting entries that escape it.</summary>
    public static void ExtractZip(string zipPath, string destination)
    {
        using var zip = ZipFile.OpenRead(zipPath);
        var total = zip.Entries.Sum(e => e.Length);
        if (total > MaxZipBytes)
        {
            throw new InvalidDataException($"{zipPath} expands to {total} bytes, over the {MaxZipBytes} byte limit");
        }
        var root = Path.GetFullPath(destination) + Path.DirectorySeparatorChar;
        foreach (var entry in zip.Entries)
        {
            if (entry.FullName.EndsWith('/') || entry.FullName.EndsWith('\\'))
            {
                continue;
            }
            var target = Path.GetFullPath(Path.Combine(root, entry.FullName.Replace('\\', '/')));
            if (!target.StartsWith(root, StringComparison.Ordinal))
            {
                throw new InvalidDataException($"{zipPath}: entry '{entry.FullName}' points outside the archive");
            }
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            entry.ExtractToFile(target, overwrite: false);
        }
    }

    /// <summary>Reads an mpm-server.json from a folder root, or from inside an MPQ. Invalid files produce a warning.</summary>
    public static ServerDeclaration? ReadDeclaration(string source, SourceKind kind, List<string> warnings)
    {
        byte[]? bytes = null;
        if (kind == SourceKind.Folder)
        {
            var path = Directory.EnumerateFiles(source)
                .FirstOrDefault(f => Path.GetFileName(f).Equals(ServerDeclaration.FileName, StringComparison.OrdinalIgnoreCase));
            bytes = path is null ? null : File.ReadAllBytes(path);
        }
        else if (kind == SourceKind.Mpq)
        {
            using var reader = MpqReader.Open(source);
            bytes = reader.TryRead(ServerDeclaration.FileName, out var data) ? data : null;
        }
        if (bytes is null)
        {
            return null;
        }
        try
        {
            var d = System.Text.Json.JsonSerializer.Deserialize<ServerDeclaration>(bytes, DeclarationJson);
            if (d is null || d.Schema != 1)
            {
                warnings.Add($"{ServerDeclaration.FileName}: unsupported or empty; ignored");
                return null;
            }
            return d;
        }
        catch (System.Text.Json.JsonException e)
        {
            warnings.Add($"{ServerDeclaration.FileName}: not valid ({e.Message}); ignored");
            return null;
        }
    }

    private static readonly System.Text.Json.JsonSerializerOptions DeclarationJson = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter(System.Text.Json.JsonNamingPolicy.CamelCase) },
    };

    public static string ChainForMpq(string fileName, string? parentDir)
    {
        if (ArchiveNames.ClassifyPatch(fileName) is { } c)
        {
            return c.Chain;
        }
        return parentDir is not null && ArchiveNames.IsLocale(parentDir)
            ? ArchiveNames.CanonicalLocale(parentDir)
            : ArchiveNames.Root;
    }

    public static string Sha256(byte[] data) => Convert.ToHexStringLower(SHA256.HashData(data));

    public static string Sha256File(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexStringLower(SHA256.HashData(stream));
    }

    /// <summary>Content hash of a folder: relative paths and file hashes, in ordinal order.</summary>
    public static string Sha256Folder(string root)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
                     .Select(f => (Rel: Path.GetRelativePath(root, f).Replace('\\', '/'), Full: f))
                     .OrderBy(f => f.Rel, StringComparer.Ordinal))
        {
            hash.AppendData(System.Text.Encoding.UTF8.GetBytes($"{file.Rel}\0{Sha256File(file.Full)}\n"));
        }
        return Convert.ToHexStringLower(hash.GetHashAndReset());
    }
}
