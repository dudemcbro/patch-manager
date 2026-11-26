using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Mpm.Core.Client;
using Mpm.Core.Mpq;

namespace Mpm.Core.Build;

/// <summary>Writes a build to a folder laid out like a client Data dir: patch-&lt;slot&gt;.MPQ and &lt;loc&gt;/patch-&lt;loc&gt;-&lt;slot&gt;.MPQ.</summary>
public static class BuildOutput
{
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

    /// <summary>Custom slots in the order the output uses them: highest priority first (Z, Y, ..., A, 9, ..., 4).</summary>
    public static IReadOnlyList<string> DefaultSlots { get; } =
        Enumerable.Range(0, 26).Select(i => ((char)('Z' - i)).ToString()).Concat(Enumerable.Range(0, 6).Select(i => (9 - i).ToString())).ToList();

    /// <summary>
    /// Writes the archives and reports into an empty or new folder. Each chain fills as many archives as it
    /// needs (each under <paramref name="maxArchiveBytes"/>), taking slots from <paramref name="slots"/> in order
    /// (default <see cref="DefaultSlots"/>). Returns archive paths relative to the folder.
    /// </summary>
    public static IReadOnlyList<string> Write(BuildResult result, string outDir, IReadOnlyList<string>? slots = null,
        long maxArchiveBytes = MpqWriter.DefaultMaxArchiveBytes)
    {
        slots ??= DefaultSlots;
        foreach (var slot in slots)
        {
            if (slot.Length != 1 || !char.IsAsciiLetterOrDigit(slot[0]) || ArchiveNames.IsStockSlot(slot))
            {
                throw new ArgumentException($"Not a custom patch slot: '{slot}'", nameof(slots));
            }
        }
        if (Directory.Exists(outDir) && Directory.EnumerateFileSystemEntries(outDir).Any())
        {
            throw new IOException($"Output folder is not empty: {outDir}");
        }
        Directory.CreateDirectory(outDir);
        var written = new List<string>();
        foreach (var (chain, files) in result.Chains.OrderBy(c => c.Key, StringComparer.Ordinal))
        {
            var parts = MpqWriter.WriteParts(files.Loaders, part => part < slots.Count
                ? Path.Combine(outDir, ArchivePath(chain, slots[part].ToUpperInvariant()))
                : throw new IOException($"The merged {chain} files need more than the {slots.Count} free patch slots"), maxArchiveBytes);
            written.AddRange(parts.Select(p => Path.GetRelativePath(outDir, p)));
        }
        File.WriteAllBytes(Path.Combine(outDir, ReportFile), JsonSerializer.SerializeToUtf8Bytes(result.Report, Json));
        File.WriteAllText(Path.Combine(outDir, SummaryFile), Summarize(result.Report, written));
        WriteQuestions(result.Report, Path.Combine(outDir, QuestionsFile));
        return written;
    }

    public const string QuestionsFile = "build-questions.json";

    public static string Summarize(BuildReport report, IReadOnlyList<string> archives)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"Mod order (lowest priority first): {string.Join(" < ", report.ModOrder)}");
        foreach (var t in report.Tiers)
        {
            sb.AppendLine($"  {t.ModId}: {t.Tier.ToString().ToLowerInvariant()} ({t.Source.ToString().ToLowerInvariant()}: {string.Join("; ", t.Reasons)})");
        }
        sb.AppendLine($"Locale: {report.Locale}");
        sb.AppendLine($"Archives: {string.Join(", ", archives)}");
        var rows = report.Files.Where(f => f.Method == MergeMethod.Rows).ToList();
        var texts = report.Files.Count(f => f.Method == MergeMethod.Text);
        sb.AppendLine($"Files: {report.Files.Count} ({rows.Count} DBCs merged by field, {texts} text files combined, {report.Files.Count - rows.Count - texts} whole files)");
        foreach (var f in rows)
        {
            var extra = (f.RowsRemoved > 0 ? $", {f.RowsRemoved} removed" : "") + (f.RowsCombined > 0 ? $", {f.RowsCombined} combine edits from several mods" : "");
            sb.AppendLine($"  {f.Path}: {f.RowsChanged} stock rows changed, {f.RowsAdded} added{extra}; from {string.Join(", ", f.Contributors)}");
        }
        sb.AppendLine($"Conflicts: {report.Conflicts.Count}");
        foreach (var group in report.Conflicts.GroupBy(c => c.QuestionId))
        {
            var sample = group.First();
            var detail = sample.Row is null ? "whole file" : $"{group.Count()} rows, e.g. ID {sample.Row}{(sample.Columns.Count > 0 ? $" ({string.Join(", ", sample.Columns.Take(5))})" : "")}";
            sb.AppendLine($"  {sample.Path}: {detail}; {string.Join(" vs ", sample.Mods)} -> {sample.Winner}");
        }
        if (report.Renumbered is { Count: > 0 } renumbered)
        {
            sb.AppendLine($"Renumbered: {renumbered.Count}");
            foreach (var r in renumbered)
            {
                var ids = string.Join(", ", r.Ids.Take(5).Select(i => $"{i.From}->{i.To}")) + (r.Ids.Count > 5 ? ", ..." : "");
                sb.AppendLine($"  {r.Path}: {r.ModId}'s rows {ids} (clashed with {string.Join(", ", r.ClashedWith)}); updated {string.Join(", ", r.References)}");
            }
        }
        if (report.Dropped.Count > 0)
        {
            sb.AppendLine($"Not applied: {report.Dropped.Count}");
            foreach (var d in report.Dropped)
            {
                sb.AppendLine($"  {d.Path}: {d.ModId}, {d.Rows} row(s): {d.Reason}");
            }
        }
        if (report.KeepLoading.Count > 0)
        {
            sb.AppendLine($"Kept loading alongside the merged patch: {string.Join(", ", report.KeepLoading)}");
        }
        sb.AppendLine($"Questions: {report.Questions.Count} (answer them in build-questions.json and build again)");
        foreach (var q in report.Questions)
        {
            sb.AppendLine($"  [{q.Id}] {q.Prompt} -> {q.Chosen}{(q.Chosen == q.Default ? " (default)" : " (answered)")}");
        }
        sb.AppendLine($"Warnings: {report.Warnings.Count}");
        foreach (var w in report.Warnings)
        {
            sb.AppendLine($"  {w}");
        }
        return sb.ToString();
    }

    /// <summary>Questions as an editable answers file: { "question id": "chosen value", ... } plus the full questions.</summary>
    public static void WriteQuestions(BuildReport report, string path)
    {
        var doc = new
        {
            answers = report.Questions.ToDictionary(q => q.Id, q => q.Chosen),
            questions = report.Questions,
        };
        File.WriteAllBytes(path, JsonSerializer.SerializeToUtf8Bytes(doc, Json));
    }

    /// <summary>Reads the "answers" object of a questions file (or a plain { id: value } object).</summary>
    public static Dictionary<string, string> ReadAnswers(string path)
    {
        using var doc = JsonDocument.Parse(File.ReadAllBytes(path));
        var root = doc.RootElement.TryGetProperty("answers", out var a) ? a : doc.RootElement;
        return root.EnumerateObject().ToDictionary(p => p.Name, p => p.Value.GetString() ?? "", StringComparer.Ordinal);
    }
}
