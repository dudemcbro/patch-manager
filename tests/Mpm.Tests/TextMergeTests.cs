using System.Text;
using Mpm.Core.Merge;

namespace Mpm.Tests;

public class TextMergeTests
{
    private static byte[] T(params string[] lines) => Encoding.UTF8.GetBytes(string.Concat(lines.Select(l => l + "\n")));
    private static string S(byte[]? b) => Encoding.UTF8.GetString(b!);

    private static string[] Apply(string[] baseLines, string[] other, List<Hunk> hunks)
    {
        var result = new List<string>();
        var pos = 0;
        foreach (var h in hunks)
        {
            result.AddRange(baseLines[pos..h.BaseStart]);
            result.AddRange(other[h.OtherStart..h.OtherEnd]);
            pos = h.BaseEnd;
        }
        result.AddRange(baseLines[pos..]);
        return [.. result];
    }

    [Fact]
    public void Diff_HunksAlwaysReconstructTheOtherSide()
    {
        var rng = new Random(42);
        for (var round = 0; round < 500; round++)
        {
            var x = Enumerable.Range(0, rng.Next(0, 40)).Select(_ => "L" + rng.Next(0, 8)).ToArray();
            var y = x.ToList();
            for (var e = rng.Next(0, 10); e > 0; e--)
            {
                var p = rng.Next(0, y.Count + 1);
                switch (rng.Next(3))
                {
                    case 0: y.Insert(p, "N" + rng.Next(0, 8)); break;
                    case 1: if (p < y.Count) y.RemoveAt(p); break;
                    default: if (p < y.Count) y[p] = "C" + rng.Next(0, 8); break;
                }
            }
            var hunks = TextMerge.Diff(x, [.. y])!;
            Assert.Equal(y, Apply(x, [.. y], hunks));
            Assert.All(hunks, h => Assert.True(h.BaseEnd > h.BaseStart || h.OtherEnd > h.OtherStart));
        }
    }

    [Fact]
    public void SeparateEdits_AreCombined()
    {
        var b = T("local a = 1", "local b = 2", "local c = 3", "local d = 4", "local e = 5");
        var mine = T("local a = 100", "local b = 2", "local c = 3", "local d = 4", "local e = 5");
        var theirs = T("local a = 1", "local b = 2", "local c = 3", "local d = 4", "local e = 500", "print('added')");

        var r = TextMerge.Merge3(b, mine, theirs);

        Assert.True(r.Clean);
        Assert.Equal(S(T("local a = 100", "local b = 2", "local c = 3", "local d = 4", "local e = 500", "print('added')")), S(r.Bytes));
    }

    [Fact]
    public void IdenticalEdits_AreTakenOnce()
    {
        var b = T("a", "b", "c");
        var both = T("a", "B", "c");
        var r = TextMerge.Merge3(b, both, both);
        Assert.True(r.Clean);
        Assert.Equal(S(both), S(r.Bytes));
    }

    [Theory]
    [InlineData(1, 1)] // same line changed differently
    [InlineData(1, 2)] // adjacent lines: treated as a conflict, like git
    public void OverlappingOrTouchingEdits_AreConflicts(int lineA, int lineB)
    {
        var lines = new[] { "a", "b", "c", "d" };
        var b = T(lines);
        var mine = lines.ToArray();
        mine[lineA] = "mine";
        var theirs = lines.ToArray();
        theirs[lineB] = "theirs";

        var r = TextMerge.Merge3(b, T(mine), T(theirs));

        Assert.False(r.Clean);
        Assert.Equal(Math.Min(lineA, lineB) + 1, r.ConflictLine);
    }

    [Fact]
    public void CrlfAndBom_ArePreserved()
    {
        var bom = new byte[] { 0xEF, 0xBB, 0xBF };
        byte[] Crlf(params string[] lines) => [.. bom, .. Encoding.UTF8.GetBytes(string.Concat(lines.Select(l => l + "\r\n")))];
        var r = TextMerge.Merge3(Crlf("x", "y", "z", "w"), Crlf("X", "y", "z", "w"), Crlf("x", "y", "z", "W"));
        Assert.True(r.Clean);
        Assert.Equal(Crlf("X", "y", "z", "W"), r.Bytes);
    }

    [Fact]
    public void NonUtf8_IsNotMerged()
    {
        var r = TextMerge.Merge3(T("a"), [0xFF, 0xFE, 0x00], T("b"));
        Assert.False(r.Clean);
        Assert.Null(r.ConflictLine);
    }

    [Fact]
    public void MergeAll_CombinesThreeVersions()
    {
        var b = T("1", "2", "3", "4", "5", "6", "7");
        var r = TextMerge.MergeAll(b, [T("one", "2", "3", "4", "5", "6", "7"), T("1", "2", "3", "four", "5", "6", "7"), T("1", "2", "3", "4", "5", "6", "seven")]);
        Assert.True(r.Clean);
        Assert.Equal(S(T("one", "2", "3", "four", "5", "6", "seven")), S(r.Bytes));
    }
}
