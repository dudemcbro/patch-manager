using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Mpm.Core.Client;
using Mpm.Core.Mpq;

namespace Mpm.Core.Build;

/// <summary>Writes a build to a folder laid out like a client Data dir: patch-&lt;slot&gt;.MPQ and &lt;loc&gt;/patch-&lt;loc&gt;-&lt;slot&gt;.MPQ.</summary>
public static class BuildOutput
{
    public const string DefaultSlot = "Z";
    public const string ReportFile = "build-report.json";
    public const string SummaryFile = "build-report.txt";

    internal static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    public static BuildReport ReadReport(string buildDir) =>
        JsonSerializer.Deserialize<BuildReport>(File.ReadAllBytes(Path.Combine(buildDir, ReportFile)), Json)
        ?? throw new InvalidDataException($"Empty build report in {buildDir}");

    /// <summary>Archive path (relative to Data) for a chain and slot.</summary>
    public static string ArchivePath(string chain, string slot) =>
        chain == ArchiveNames.Root ? $"patch-{slot}.MPQ" : Path.Combine(chain, $"patch-{chain}-{slot}.MPQ");

    /// <summary>Writes the archives and reports into an empty or new folder. Returns archive paths relative to it.</summary>
    public static IReadOnlyList<string> Write(BuildResult result, string outDir, string slot = DefaultSlot)
    {
        if (slot.Length != 1 || !char.IsAsciiLetterOrDigit(slot[0]))
        {
            throw new ArgumentException("Slot must be one letter or digit", nameof(slot));
        }
        if (Directory.Exists(outDir) && Directory.EnumerateFileSystemEntries(outDir).Any())
        {
            throw new IOException($"Output folder is not empty: {outDir}");
        }
        Directory.CreateDirectory(outDir);
        var written = new List<string>();
        foreach (var (chain, files) in result.Chains.OrderBy(c => c.Key, StringComparer.Ordinal))
        {
            var rel = ArchivePath(chain, slot.ToUpperInvariant());
            MpqWriter.Write(Path.Combine(outDir, rel), files);
            written.Add(rel);
        }
        File.WriteAllBytes(Path.Combine(outDir, ReportFile), JsonSerializer.SerializeToUtf8Bytes(result.Report, Json));
        File.WriteAllText(Path.Combine(outDir, SummaryFile), Summarize(result.Report, written));
        return written;
    }

    public static string Summarize(BuildReport report, IReadOnlyList<string> archives)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"Mod order (lowest priority first): {string.Join(" < ", report.ModOrder)}");
        sb.AppendLine($"Locale: {report.Locale}");
        sb.AppendLine($"Archives: {string.Join(", ", archives)}");
        var rows = report.Files.Where(f => f.Method == MergeMethod.Rows).ToList();
        sb.AppendLine($"Files: {report.Files.Count} ({rows.Count} DBCs merged by row, {report.Files.Count - rows.Count} whole files)");
        foreach (var f in rows)
        {
            sb.AppendLine($"  {f.Path}: {f.RowsChanged} stock rows changed, {f.RowsAdded} rows added, from {string.Join(", ", f.Contributors)}");
        }
        sb.AppendLine($"Conflicts: {report.Conflicts.Count}");
        foreach (var group in report.Conflicts.GroupBy(c => c.Path))
        {
            var sample = group.First();
            var detail = sample.Row is null ? "whole file" : $"{group.Count()} rows, e.g. ID {sample.Row} ({string.Join(", ", sample.Columns.Take(5))})";
            sb.AppendLine($"  {group.Key}: {detail}; {string.Join(" vs ", sample.Mods)} -> {sample.Winner}");
        }
        sb.AppendLine($"Warnings: {report.Warnings.Count}");
        foreach (var w in report.Warnings)
        {
            sb.AppendLine($"  {w}");
        }
        return sb.ToString();
    }
}
