using System.Text;
using Mpm.Core.Build;
using Mpm.Core.Client;
using Mpm.Core.Dbc;
using Mpm.Core.Mpq;
using Mpm.Core.Store;

namespace Mpm.Tests;

public class TierMergeTests
{
    private const string Titles = "DBFilesClient\\CharTitles.dbc";
    private const string Icons = "DBFilesClient\\SpellIcon.dbc";

    private sealed class Fixture : IDisposable
    {
        public readonly TempDir Tmp = new();
        public readonly ModStore Store;
        public readonly BaselineChain Baseline;
        private int _n;

        public Fixture()
        {
            var data = FakeClient.Create(Tmp.Path);
            Store = new ModStore(Tmp.File("store"));
            Baseline = new BaselineChain(data, "enUS");
        }

        public ModManifest Mod(string name, Dictionary<string, byte[]> files)
        {
            var dir = Tmp.File($"src{_n++}");
            Directory.CreateDirectory(dir);
            var path = Path.Combine(dir, $"patch-enUS-{name}.MPQ");
            MpqWriter.Write(path, files);
            return Store.Import(path).Manifest;
        }

        public BuildResult Build(IReadOnlyDictionary<string, string>? answers, params ModManifest[] mods) =>
            new PatchBuilder(Store, Baseline, DbcLayouts.For335()).Build(mods, new BuildOptions(answers));

        public void Dispose()
        {
            Baseline.Dispose();
            Tmp.Dispose();
        }
    }

    private static Dictionary<string, string> A(params (string Id, string Value)[] answers) => answers.ToDictionary(a => a.Id, a => a.Value);

    [Fact]
    public void ClassificationNames_AllExistInLayouts()
    {
        Assert.Empty(FieldClassifier.For335().UnknownNames(DbcLayouts.For335()));
        var spell = DbcLayouts.For335().Find("Spell")!;
        var icon = spell.Columns.ToList().FindIndex(c => c.Name == "SpellIconID");
        var name = spell.Columns.ToList().FindIndex(c => c.Name == "Name_lang[0]");
        Assert.Equal(FieldClass.Cosmetic, FieldClassifier.For335().Classify(spell, icon));
        Assert.Equal(FieldClass.Gameplay, FieldClassifier.For335().Classify(spell, name));
        Assert.Equal(FieldClass.Gameplay, FieldClassifier.For335().Classify(spell, spell.IdColumn));
    }

    [Fact]
    public void DifferentFieldsOfTheSameRow_FromTwoMods_AreCombined()
    {
        using var f = new Fixture();
        var a = f.Mod("4", new() { [Titles] = FakeClient.TitlesDbcFull((1, "Renamed", 0), (2, "Corporal", 0), (3, "Sergeant", 0)) });
        var b = f.Mod("5", new() { [Titles] = FakeClient.TitlesDbcFull((1, "Private", 7), (2, "Corporal", 0), (3, "Sergeant", 0)) });

        var result = f.Build(null, a, b);

        Assert.Equal(("Renamed", 7L), FakeClient.ReadTitlesFull(result.Chains["enUS"][Titles])[1]);
        Assert.Empty(result.Report.Conflicts);
        Assert.Equal(1, result.Report.Files.Single().RowsCombined);
    }

    [Fact]
    public void SameField_IsAQuestion_DefaultLaterWins_AnswerFlipsIt()
    {
        using var f = new Fixture();
        var a = f.Mod("4", new() { [Titles] = FakeClient.TitlesDbc((1, "From A"), (2, "Corporal"), (3, "Sergeant")) });
        var b = f.Mod("5", new() { [Titles] = FakeClient.TitlesDbc((1, "From B"), (2, "Corporal"), (3, "Sergeant")) });

        var first = f.Build(null, a, b);
        var q = Assert.Single(first.Report.Questions, q => q.Kind == "rows");
        Assert.Equal(b.Id, q.Default);
        Assert.Equal([a.Id, b.Id], q.Options.Select(o => o.Value));
        Assert.Equal("From B", FakeClient.ReadTitles(first.Chains["enUS"][Titles])[1]);

        var second = f.Build(A((q.Id, a.Id)), a, b);
        Assert.Equal("From A", FakeClient.ReadTitles(second.Chains["enUS"][Titles])[1]);
        Assert.Equal(a.Id, second.Report.Questions.Single(x => x.Id == q.Id).Chosen);
    }

    [Fact]
    public void Tiers_AreDetectedFromWhatAModChanges()
    {
        using var f = new Fixture();
        var visual = f.Mod("4", new()
        {
            [Icons] = FakeClient.IconsDbc((1, "Interface\\Icons\\HD1"), (2, "Interface\\Icons\\Stock2")),
            ["Textures\\HD.blp"] = [1],
        });
        var gameplay = f.Mod("5", new() { [Titles] = FakeClient.TitlesDbc((1, "Private"), (2, "Corporal"), (3, "Sergeant"), (9, "New Title")) });

        var report = f.Build(null, visual, gameplay).Report;

        var tiers = report.Tiers.ToDictionary(t => t.ModId);
        Assert.Equal((ModTier.Local, TierSource.Auto), (tiers[visual.Id].Tier, tiers[visual.Id].Source));
        Assert.Equal((ModTier.Server, TierSource.Auto), (tiers[gameplay.Id].Tier, tiers[gameplay.Id].Source));
        Assert.Contains(tiers[gameplay.Id].Reasons, r => r.Contains("adds 1 rows to CharTitles"));
        Assert.Equal("local", report.Questions.Single(q => q.Id == $"tier:{visual.Id}").Default);
    }

    [Fact]
    public void LocalModGameplayEdits_AreDropped_UnlessTheUserKeepsThem()
    {
        using var f = new Fixture();
        var mod = f.Mod("4", new() { [Titles] = FakeClient.TitlesDbc((1, "Local Rename"), (2, "Corporal"), (3, "Sergeant")) });
        var asLocal = A(($"tier:{mod.Id}", "local"));

        var dropped = f.Build(asLocal, mod);
        Assert.False(dropped.Chains.ContainsKey("enUS"));
        Assert.Contains(dropped.Report.Dropped, d => d.ModId == mod.Id && d.Rows == 1);
        var keep = Assert.Single(dropped.Report.Questions, q => q.Kind == "localgameplay");
        Assert.Equal("no", keep.Chosen);
        Assert.Equal(TierSource.Answer, dropped.Report.Tiers.Single().Source);

        var kept = f.Build(new Dictionary<string, string>(asLocal) { [keep.Id] = "yes" }, mod);
        Assert.Equal("Local Rename", FakeClient.ReadTitles(kept.Chains["enUS"][Titles])[1]);
    }

    [Fact]
    public void ServerBeatsLocal_OnTheSameCosmeticField_ByDefault()
    {
        using var f = new Fixture();
        var server = f.Mod("4", new()
        {
            [Icons] = FakeClient.IconsDbc((1, "Interface\\Icons\\Server"), (2, "Interface\\Icons\\Stock2")),
            [Titles] = FakeClient.TitlesDbc((1, "Private"), (2, "Corporal"), (3, "Sergeant"), (9, "Server Title")),
        });
        var local = f.Mod("5", new() { [Icons] = FakeClient.IconsDbc((1, "Interface\\Icons\\HD"), (2, "Interface\\Icons\\Stock2")) });

        var result = f.Build(null, server, local); // local is later in the order, but server still wins
        Assert.Equal("Interface\\Icons\\Server", FakeClient.ReadIcons(result.Chains["enUS"][Icons])[1]);
        var q = Assert.Single(result.Report.Questions, q => q.Kind == "rows");
        Assert.Equal(server.Id, q.Default);

        var flipped = f.Build(A((q.Id, local.Id)), server, local);
        Assert.Equal("Interface\\Icons\\HD", FakeClient.ReadIcons(flipped.Chains["enUS"][Icons])[1]);
    }

    [Fact]
    public void MissingStockRows_AreKeptByDefault_RemovedWhenAnsweredYes()
    {
        using var f = new Fixture();
        var mod = f.Mod("4", new() { [Titles] = FakeClient.TitlesDbc((1, "Private"), (2, "Only Two")) });

        var kept = f.Build(null, mod);
        var q = Assert.Single(kept.Report.Questions, q => q.Kind == "removals");
        Assert.Equal("no", q.Chosen);
        Assert.Equal(3, FakeClient.ReadTitles(kept.Chains["enUS"][Titles]).Count);

        var removed = f.Build(A((q.Id, "yes")), mod);
        Assert.Equal([1L, 2L], FakeClient.ReadTitles(removed.Chains["enUS"][Titles]).Keys.Order());
        Assert.Equal(1, removed.Report.Files.Single().RowsRemoved);
    }

    [Fact]
    public void SameNewIdFromTwoMods_IsAQuestion()
    {
        using var f = new Fixture();
        var a = f.Mod("4", new() { [Titles] = FakeClient.TitlesDbc((1, "Private"), (2, "Corporal"), (3, "Sergeant"), (50, "A's title")) });
        var b = f.Mod("5", new() { [Titles] = FakeClient.TitlesDbc((1, "Private"), (2, "Corporal"), (3, "Sergeant"), (50, "B's title")) });

        var result = f.Build(null, a, b);

        var q = Assert.Single(result.Report.Questions, q => q.Kind == "newrows");
        Assert.Equal("B's title", FakeClient.ReadTitles(result.Chains["enUS"][Titles])[50]);
        var flipped = f.Build(A((q.Id, a.Id)), a, b);
        Assert.Equal("A's title", FakeClient.ReadTitles(flipped.Chains["enUS"][Titles])[50]);
    }

    [Fact]
    public void ServerDeclaration_InsideAnMpq_SetsTheTier_AndIsNotImportedAsAFile()
    {
        using var f = new Fixture();
        var mod = f.Mod("4", new()
        {
            [Icons] = FakeClient.IconsDbc((1, "Interface\\Icons\\Srv"), (2, "Interface\\Icons\\Stock2")),
            [ServerDeclaration.FileName] = Encoding.UTF8.GetBytes("""{"schema": 1, "tier": "server", "server": "Test Realm"}"""),
        });

        Assert.Equal(ModTier.Server, mod.Declaration?.Tier);
        Assert.DoesNotContain(mod.Files, x => x.Path.Contains("mpm-server", StringComparison.OrdinalIgnoreCase));
        var tier = f.Build(null, mod).Report.Tiers.Single();
        Assert.Equal((ModTier.Server, TierSource.Declared), (tier.Tier, tier.Source));
    }

    [Fact]
    public void UnknownAnswers_AreReportedNotApplied()
    {
        using var f = new Fixture();
        var mod = f.Mod("4", new() { ["Textures\\X.blp"] = [1] });
        var report = f.Build(A(("rows:nothing", "x")), mod).Report;
        Assert.Contains(report.Warnings, w => w.Contains("rows:nothing"));
    }
}

public class TextFileMergeTests
{
    private const string Lua = "Interface\\FrameXML\\Stock.lua";

    private static byte[] Edit(params (int Line, string Text)[] edits)
    {
        var lines = System.Text.Encoding.UTF8.GetString(FakeClient.StockLua).Split("\r\n")[..^1];
        foreach (var (line, text) in edits)
        {
            lines[line] = text;
        }
        return System.Text.Encoding.UTF8.GetBytes(string.Concat(lines.Select(l => l + "\r\n")));
    }

    private static (TempDir, ModStore, BaselineChain) Setup()
    {
        var tmp = new TempDir();
        var data = FakeClient.Create(tmp.Path);
        return (tmp, new ModStore(tmp.File("store")), new BaselineChain(data, "enUS"));
    }

    private static ModManifest Mod(TempDir tmp, ModStore store, string slot, byte[] lua)
    {
        var path = tmp.File($"patch-{slot}.MPQ");
        MpqWriter.Write(path, new Dictionary<string, byte[]> { [Lua] = lua });
        return store.Import(path).Manifest;
    }

    [Fact]
    public void SeparateLuaEdits_AreCombined_ByDefault()
    {
        var (tmp, store, baseline) = Setup();
        using var _ = tmp;
        using var __ = baseline;
        var a = Mod(tmp, store, "4", Edit((0, "local a = 100")));
        var b = Mod(tmp, store, "5", Edit((4, "local e = 500")));
        var builder = new PatchBuilder(store, baseline, DbcLayouts.For335());

        var result = builder.Build([a, b]);

        Assert.Equal(Edit((0, "local a = 100"), (4, "local e = 500")), result.Chains["enUS"][Lua]);
        Assert.Equal(MergeMethod.Text, result.Report.Files.Single().Method);
        Assert.Empty(result.Report.Conflicts);
        var q = Assert.Single(result.Report.Questions, q => q.Kind == "text");
        Assert.Equal("merge", q.Default);
        Assert.Equal(["merge", a.Id, b.Id], q.Options.Select(o => o.Value));

        var onlyA = builder.Build([a, b], new BuildOptions(new Dictionary<string, string> { [q.Id] = a.Id }));
        Assert.Equal(Edit((0, "local a = 100")), onlyA.Chains["enUS"][Lua]);
    }

    [Fact]
    public void OverlappingLuaEdits_FallBackToPickingOne_WithTheLineInTheQuestion()
    {
        var (tmp, store, baseline) = Setup();
        using var _ = tmp;
        using var __ = baseline;
        var a = Mod(tmp, store, "4", Edit((2, "local c = 'A'")));
        var b = Mod(tmp, store, "5", Edit((2, "local c = 'B'")));

        var result = new PatchBuilder(store, baseline, DbcLayouts.For335()).Build([a, b]);

        Assert.Equal(Edit((2, "local c = 'B'")), result.Chains["enUS"][Lua]);
        var q = Assert.Single(result.Report.Questions, q => q.Kind == "file");
        Assert.Contains("near line 3", q.Detail);
        Assert.Single(result.Report.Conflicts);
    }
}
