using System.Text;

namespace Mpm.Core.Merge;

/// <summary>A changed region: base lines [BaseStart, BaseEnd) became other lines [OtherStart, OtherEnd).</summary>
public readonly record struct Hunk(int BaseStart, int BaseEnd, int OtherStart, int OtherEnd);

/// <summary>Outcome of a 3-way text merge. When not clean, <see cref="ConflictLine"/> is the first base line (1-based) where edits collide.</summary>
public sealed record TextMergeResult(bool Clean, byte[]? Bytes, int? ConflictLine, string? Reason);

/// <summary>
/// Line-based 3-way merge for UI text files (Lua, XML, TOC). Each side is diffed against the common base
/// (Myers); edits in separate places are combined, identical edits are taken once, and edits that overlap
/// or touch (adjacent lines) are a conflict. Line endings and a UTF-8 BOM are preserved; files that are
/// not valid UTF-8 are not merged.
/// </summary>
public static class TextMerge
{
    /// <summary>Above this many edits between two versions the merge gives up (the files are too different to combine safely).</summary>
    public const int MaxEdits = 4000;

    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);
    private static readonly byte[] Bom = [0xEF, 0xBB, 0xBF];

    public static bool IsTextPath(string archivePath) =>
        archivePath.EndsWith(".lua", StringComparison.OrdinalIgnoreCase)
        || archivePath.EndsWith(".xml", StringComparison.OrdinalIgnoreCase)
        || archivePath.EndsWith(".toc", StringComparison.OrdinalIgnoreCase);

    /// <summary>Merges several edited versions of <paramref name="base"/>, lowest priority first.</summary>
    public static TextMergeResult MergeAll(byte[] @base, IReadOnlyList<byte[]> versions)
    {
        if (versions.Count == 0)
        {
            return new TextMergeResult(true, @base, null, null);
        }
        var acc = versions[0];
        for (var i = 1; i < versions.Count; i++)
        {
            var r = Merge3(@base, acc, versions[i]);
            if (!r.Clean)
            {
                return r;
            }
            acc = r.Bytes!;
        }
        return new TextMergeResult(true, acc, null, null);
    }

    public static TextMergeResult Merge3(byte[] @base, byte[] a, byte[] b)
    {
        if (!TryLines(@base, out var baseLines, out var bom) || !TryLines(a, out var aLines, out _) || !TryLines(b, out var bLines, out _))
        {
            return new TextMergeResult(false, null, null, "not UTF-8 text");
        }
        var ha = Diff(baseLines, aLines);
        var hb = Diff(baseLines, bLines);
        if (ha is null || hb is null)
        {
            return new TextMergeResult(false, null, null, "the versions are too different to combine");
        }

        var all = ha.Select(h => (H: h, Side: 0)).Concat(hb.Select(h => (H: h, Side: 1)))
            .OrderBy(x => x.H.BaseStart).ThenBy(x => x.H.BaseEnd).ToList();
        var output = new List<string>(baseLines.Length + 16);
        var pos = 0;
        var i = 0;
        while (i < all.Count)
        {
            var start = all[i].H.BaseStart;
            var end = all[i].H.BaseEnd;
            var j = i + 1;
            // Group hunks that overlap or touch; touching edits from both sides are treated as a conflict.
            while (j < all.Count && all[j].H.BaseStart <= end)
            {
                end = Math.Max(end, all[j].H.BaseEnd);
                j++;
            }
            output.AddRange(baseLines[pos..start]);
            var chunk = all[i..j];
            var fromA = chunk.Where(x => x.Side == 0).Select(x => x.H).ToList();
            var fromB = chunk.Where(x => x.Side == 1).Select(x => x.H).ToList();
            if (fromB.Count == 0)
            {
                output.AddRange(Version(baseLines, aLines, fromA, start, end));
            }
            else if (fromA.Count == 0)
            {
                output.AddRange(Version(baseLines, bLines, fromB, start, end));
            }
            else
            {
                var va = Version(baseLines, aLines, fromA, start, end);
                var vb = Version(baseLines, bLines, fromB, start, end);
                if (!va.SequenceEqual(vb, StringComparer.Ordinal))
                {
                    return new TextMergeResult(false, null, start + 1, "both versions change the same lines");
                }
                output.AddRange(va);
            }
            pos = end;
            i = j;
        }
        output.AddRange(baseLines[pos..]);

        var text = StrictUtf8.GetBytes(string.Concat(output));
        return new TextMergeResult(true, bom ? [.. Bom, .. text] : text, null, null);
    }

    /// <summary>The span base[start, end) with one side's hunks applied.</summary>
    private static List<string> Version(string[] baseLines, string[] other, List<Hunk> hunks, int start, int end)
    {
        var result = new List<string>();
        var pos = start;
        foreach (var h in hunks.OrderBy(h => h.BaseStart))
        {
            result.AddRange(baseLines[pos..h.BaseStart]);
            result.AddRange(other[h.OtherStart..h.OtherEnd]);
            pos = h.BaseEnd;
        }
        result.AddRange(baseLines[pos..end]);
        return result;
    }

    /// <summary>Splits into lines that keep their line ending, so concatenating them restores the text exactly.</summary>
    private static bool TryLines(byte[] data, out string[] lines, out bool bom)
    {
        bom = data.AsSpan().StartsWith(Bom);
        string text;
        try
        {
            text = StrictUtf8.GetString(bom ? data[3..] : data);
        }
        catch (DecoderFallbackException)
        {
            lines = [];
            return false;
        }
        var list = new List<string>();
        var startIndex = 0;
        for (var k = 0; k < text.Length; k++)
        {
            if (text[k] == '\n')
            {
                list.Add(text[startIndex..(k + 1)]);
                startIndex = k + 1;
            }
        }
        if (startIndex < text.Length)
        {
            list.Add(text[startIndex..]);
        }
        lines = [.. list];
        return true;
    }

    /// <summary>Myers diff of two line arrays as hunks, or null when there are more than <see cref="MaxEdits"/> edits.</summary>
    public static List<Hunk>? Diff(string[] x, string[] y)
    {
        // Trim the common prefix and suffix; mods usually touch a few places in large files.
        var prefix = 0;
        while (prefix < x.Length && prefix < y.Length && x[prefix] == y[prefix])
        {
            prefix++;
        }
        var suffix = 0;
        while (suffix < x.Length - prefix && suffix < y.Length - prefix && x[^(suffix + 1)] == y[^(suffix + 1)])
        {
            suffix++;
        }
        var ids = new Dictionary<string, int>(StringComparer.Ordinal);
        var a = x[prefix..^suffix].Select(l => Id(ids, l)).ToArray();
        var b = y[prefix..^suffix].Select(l => Id(ids, l)).ToArray();

        var matches = Matches(a, b);
        if (matches is null)
        {
            return null;
        }
        var hunks = new List<Hunk>();
        int ai = 0, bi = 0;
        foreach (var (ma, mb) in matches.Append((a.Length, b.Length)))
        {
            if (ma > ai || mb > bi)
            {
                hunks.Add(new Hunk(prefix + ai, prefix + ma, prefix + bi, prefix + mb));
            }
            ai = ma + 1;
            bi = mb + 1;
        }
        return hunks;
    }

    private static int Id(Dictionary<string, int> ids, string line)
    {
        if (!ids.TryGetValue(line, out var id))
        {
            ids[line] = id = ids.Count;
        }
        return id;
    }

    /// <summary>Matched (equal) line pairs of a longest common subsequence, ascending; null when too many edits.</summary>
    private static List<(int A, int B)>? Matches(int[] a, int[] b)
    {
        int n = a.Length, m = b.Length;
        var max = n + m;
        if (max == 0)
        {
            return [];
        }
        var limit = Math.Min(max, MaxEdits);
        var v = new int[2 * max + 2];
        var trace = new List<int[]>();
        var offset = max;
        var found = -1;
        for (var d = 0; d <= limit && found < 0; d++)
        {
            for (var k = -d; k <= d; k += 2)
            {
                var x = k == -d || (k != d && v[offset + k - 1] < v[offset + k + 1])
                    ? v[offset + k + 1]
                    : v[offset + k - 1] + 1;
                var y = x - k;
                while (x < n && y < m && a[x] == b[y])
                {
                    x++;
                    y++;
                }
                v[offset + k] = x;
                if (x >= n && y >= m)
                {
                    found = d;
                    break;
                }
            }
            // Keep only the diagonals -d..d reachable at this depth.
            trace.Add(v[(offset - d)..(offset + d + 1)]);
        }
        if (found < 0)
        {
            return null;
        }

        var result = new List<(int, int)>();
        int cx = n, cy = m;
        for (var d = found; d > 0; d--)
        {
            var prev = trace[d - 1]; // indexes -(d-1)..(d-1), stored at k + (d-1)
            var k = cx - cy;
            var prevK = k == -d || (k != d && prev[k - 1 + (d - 1)] < prev[k + 1 + (d - 1)]) ? k + 1 : k - 1;
            var prevX = prev[prevK + (d - 1)];
            var prevY = prevX - prevK;
            while (cx > prevX && cy > prevY)
            {
                cx--;
                cy--;
                result.Add((cx, cy));
            }
            cx = prevX;
            cy = prevY;
        }
        while (cx > 0 && cy > 0)
        {
            cx--;
            cy--;
            result.Add((cx, cy));
        }
        result.Reverse();
        return result;
    }
}
