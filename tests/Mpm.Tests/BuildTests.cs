using Mpm.Core.Build;
using Mpm.Core.Client;
using Mpm.Core.Dbc;
using Mpm.Core.Mpq;
using Mpm.Core.Store;

namespace Mpm.Tests;

public class BuildTests
{
    private sealed class Fixture : IDisposable
    {
        public readonly TempDir Tmp = new();
        public readonly string Data;
        public readonly ModStore Store;
        public readonly BaselineChain Baseline;
        private int _n;

        public Fixture()
        {
            Data = FakeClient.Create(Tmp.Path);
            Store = new ModStore(Tmp.File("store"));
            Baseline = new BaselineChain(Data, "enUS");
        }

        public ModManifest Mod(string archiveName, Dictionary<string, byte[]> files)
        {
            var dir = Tmp.File($"src{_n++}");
            Directory.CreateDirectory(dir);
            var path = Path.Combine(dir, archiveName);
            MpqWriter.Write(path, files);
            return Store.Import(path).Manifest;
        }

        public BuildResult Build(params ModManifest[] mods) => new PatchBuilder(Store, Baseline, DbcLayouts.For335()).Build(mods);

        public void Dispose()
        {
            Baseline.Dispose();
            Tmp.Dispose();
        }
    }

    private const string Titles = "DBFilesClient\\CharTitles.dbc";

    [Fact]
    public void TwoFullDbcCopies_EditingDifferentRows_BothKeepTheirEdits()
    {
        using var f = new Fixture();
        var a = f.Mod("patch-enUS-4.MPQ", new() { [Titles] = FakeClient.TitlesDbc((1, "Private A"), (2, "Corporal"), (3, "Sergeant")) });
        var b = f.Mod("patch-enUS-5.MPQ", new() { [Titles] = FakeClient.TitlesDbc((1, "Private"), (2, "Corporal B"), (3, "Sergeant"), (200, "Custom")) });

        var result = f.Build(a, b);

        var merged = FakeClient.ReadTitles(result.Chains["enUS"][Titles]);
        Assert.Equal(new Dictionary<long, string> { [1] = "Private A", [2] = "Corporal B", [3] = "Sergeant", [200] = "Custom" }, merged);
        Assert.Empty(result.Report.Conflicts);
        var decision = result.Report.Files.Single();
        Assert.Equal((MergeMethod.Rows, 2, 1), (decision.Method, decision.RowsChanged, decision.RowsAdded));
    }

    [Fact]
    public void SameRowChangedByTwoMods_IsAConflict_LaterModWins()
    {
        using var f = new Fixture();
        var a = f.Mod("patch-enUS-4.MPQ", new() { [Titles] = FakeClient.TitlesDbc((1, "From A"), (2, "Corporal"), (3, "Sergeant")) });
        var b = f.Mod("patch-enUS-5.MPQ", new() { [Titles] = FakeClient.TitlesDbc((1, "From B"), (2, "Corporal"), (3, "Sergeant")) });

        var result = f.Build(a, b);

        Assert.Equal("From B", FakeClient.ReadTitles(result.Chains["enUS"][Titles])[1]);
        var conflict = Assert.Single(result.Report.Conflicts);
        Assert.Equal(("1", b.Id), (conflict.Row, conflict.Winner));
        Assert.Equal([a.Id, b.Id], conflict.Mods);
        Assert.Contains("Name_lang[0]", conflict.Columns);

        var reversed = f.Build(b, a);
        Assert.Equal("From A", FakeClient.ReadTitles(reversed.Chains["enUS"][Titles])[1]);
    }

    [Fact]
    public void ModFileIdenticalToStock_ProducesNothing()
    {
        using var f = new Fixture();
        var a = f.Mod("patch-enUS-4.MPQ", new() { [Titles] = FakeClient.TitlesDbc((1, "Private"), (2, "Corporal"), (3, "Sergeant")) });
        var result = f.Build(a);
        Assert.Empty(result.Chains);
        Assert.Empty(result.Report.Files);
    }

    [Fact]
    public void PartialDbc_DoesNotDeleteStockRows()
    {
        using var f = new Fixture();
        var a = f.Mod("patch-enUS-4.MPQ", new() { [Titles] = FakeClient.TitlesDbc((2, "Only Two")) });
        var merged = FakeClient.ReadTitles(f.Build(a).Chains["enUS"][Titles]);
        Assert.Equal(["Private", "Only Two", "Sergeant"], merged.OrderBy(kv => kv.Key).Select(kv => kv.Value));
    }

    [Fact]
    public void NonDbcFiles_LaterModWins_AndArePlacedByStockChain()
    {
        using var f = new Fixture();
        var a = f.Mod("patch-4.MPQ", new()
        {
            ["Interface\\FrameXML\\Stock.lua"] = "a"u8.ToArray(),   // stock lives in enUS even though the mod used root
            ["Textures\\New.blp"] = [9],
        });
        var b = f.Mod("patch-5.MPQ", new() { ["Interface\\FrameXML\\Stock.lua"] = "b"u8.ToArray() });

        var result = f.Build(a, b);

        Assert.Equal("b"u8.ToArray(), result.Chains["enUS"]["Interface\\FrameXML\\Stock.lua"]);
        Assert.Equal([9], result.Chains["root"]["Textures\\New.blp"]);
        var conflict = Assert.Single(result.Report.Conflicts);
        Assert.Null(conflict.Row);
        Assert.Equal(b.Id, conflict.Winner);
    }

    [Fact]
    public void DbcWithWrongShape_FallsBackToWholeFile_WithWarning()
    {
        using var f = new Fixture();
        var bogus = new DbcFile(3, 12, [new byte[12]], [0]).ToBytes();
        var a = f.Mod("patch-enUS-4.MPQ", new() { [Titles] = bogus });

        var result = f.Build(a);

        Assert.Equal(bogus, result.Chains["enUS"][Titles]);
        Assert.Equal(MergeMethod.File, result.Report.Files.Single().Method);
        Assert.Contains(result.Report.Warnings, w => w.Contains("layout expects"));
    }

    [Fact]
    public void SameModShipsPathInBothChains_UsesTheStockChainCopy()
    {
        using var f = new Fixture();
        var dir = f.Tmp.File("both");
        Directory.CreateDirectory(Path.Combine(dir, "enUS"));
        MpqWriter.Write(Path.Combine(dir, "patch-4.MPQ"), new Dictionary<string, byte[]> { [Titles] = FakeClient.TitlesDbc((1, "Root copy")) });
        MpqWriter.Write(Path.Combine(dir, "enUS", "patch-enUS-4.MPQ"), new Dictionary<string, byte[]> { [Titles] = FakeClient.TitlesDbc((1, "Locale copy")) });
        var mod = f.Store.Import(dir).Manifest;

        var result = f.Build(mod);

        Assert.Equal("Locale copy", FakeClient.ReadTitles(result.Chains["enUS"][Titles])[1]);
        Assert.False(result.Chains.ContainsKey("root"));
        Assert.Contains(result.Report.Warnings, w => w.Contains("differs between"));
    }

    [Fact]
    public void Write_CreatesArchivesAndReports()
    {
        using var f = new Fixture();
        var a = f.Mod("patch-4.MPQ", new() { ["Textures\\New.blp"] = [9], ["Interface\\FrameXML\\New.lua"] = [7] });
        var outDir = f.Tmp.File("out");

        var written = BuildOutput.Write(f.Build(a), outDir);

        Assert.Equal(["enUS/patch-enUS-Z.MPQ", "patch-Z.MPQ"], written.Select(w => w.Replace('\\', '/')).Order());
        using var root = MpqReader.Open(Path.Combine(outDir, "patch-Z.MPQ"));
        Assert.Equal([9], root.Read("Textures\\New.blp"));
        Assert.True(File.Exists(Path.Combine(outDir, BuildOutput.ReportFile)));
        Assert.Throws<IOException>(() => BuildOutput.Write(f.Build(a), outDir));
    }
}
