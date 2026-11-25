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

    public static void ScanMpq(string path, ContributionSink sink, List<string> warnings, string? parentHint = null, string? origin = null)
    {
        var chain = ChainForMpq(Path.GetFileName(path), parentHint ?? Path.GetFileName(Path.GetDirectoryName(path)));
        using var reader = MpqReader.Open(path);
        if (reader.Files.Count == 0)
        {
            warnings.Add($"{Path.GetFileName(path)}: no (listfile) or no files listed; nothing imported from it");
        }
        foreach (var f in reader.Files)
        {
            sink(new Contribution(f, chain, reader.Read(f), origin ?? Path.GetFileName(path)));
        }
    }

    public static void ScanFolder(string root, ContributionSink sink, List<string> warnings)
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
        foreach (var (path, _, _) in mpqs.OrderBy(m => m.Order).ThenBy(m => m.Path, StringComparer.Ordinal))
        {
            var rel = Path.GetRelativePath(root, path).Split(Path.DirectorySeparatorChar);
            ScanMpq(path, sink, warnings, rel.Length > 1 ? rel[^2] : null, string.Join('/', rel));
        }
        foreach (var (archivePath, chain, file, rel) in loose)
        {
            sink(new Contribution(archivePath, chain, File.ReadAllBytes(file), rel));
        }
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
