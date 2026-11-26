using Mpm.Core.Build;
using Mpm.Core.Client;
using Mpm.Core.Dbc;
using Mpm.Core.Mpq;
using Mpm.Core.Store;

namespace Mpm.Tests;

public class RenumberTests : IDisposable
{
    private const string Icons = "DBFilesClient\\SpellIcon.dbc";
    private const string Spell = "DBFilesClient\\Spell.dbc";

    private readonly TempDir _tmp = new();
    private readonly ModStore _store;
    private readonly BaselineChain _baseline;
    private int _n;

    public RenumberTests()
    {
        var data = FakeClient.Create(_tmp.Path);
        _store = new ModStore(_tmp.File("store"));
        _baseline = new BaselineChain(data, "enUS");
    }

    public void Dispose()
    {
        _baseline.Dispose();
        _tmp.Dispose();
    }

    private ModManifest Mod(Dictionary<string, byte[]> files)
    {
        var dir = _tmp.File($"src{_n}");
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, $"patch-enUS-{_n++}.MPQ");
        MpqWriter.Write(path, files);
        return _store.Import(path).Manifest;
    }

    private BuildResult Build(Dictionary<string, string> answers, params ModManifest[] mods) =>
        new PatchBuilder(_store, _baseline, DbcLayouts.For335()).Build(mods, new BuildOptions(answers));

    private static byte[] StockIconsPlus(params (int Id, string Texture)[] extra) =>
        FakeClient.IconsDbc([(1, "Interface\\Icons\\Stock1"), (2, "Interface\\Icons\\Stock2"), .. extra]);

    /// <summary>A server patch adding icon 100, and a local mod adding its own icon 100 and pointing stock spell 1 at it.</summary>
    private (ModManifest Server, ModManifest Local, Dictionary<string, string> Answers) ServerAndLocal(bool localReferencesIt = true)
    {
        var server = Mod(new() { [Icons] = StockIconsPlus((100, "Interface\\Icons\\Server")) });
        var localFiles = new Dictionary<string, byte[]> { [Icons] = StockIconsPlus((100, "Interface\\Icons\\Local")) };
        if (localReferencesIt)
        {
            localFiles[Spell] = FakeClient.SpellDbc((1, 100), (2, 2));
        }
        var local = Mod(localFiles);
        return (server, local, new() { [$"tier:{server.Id}"] = "server", [$"tier:{local.Id}"] = "local" });
    }

    [Theory]
    [InlineData(true)] // local mod later in the order
    [InlineData(false)] // local mod earlier
    public void ClashingLocalRow_ItReferences_GetsANewId_AndTheReferenceFollows(bool localLast)
    {
        var (server, local, answers) = ServerAndLocal();

        var result = localLast ? Build(answers, server, local) : Build(answers, local, server);

        var icons = FakeClient.ReadIcons(result.Chains["enUS"][Icons]);
        Assert.Equal("Interface\\Icons\\Server", icons[100]);
        Assert.Equal("Interface\\Icons\\Local", icons[101]);
        Assert.Equal(101, FakeClient.ReadSpellIcons(result.Chains["enUS"][Spell])[1]);
        var r = Assert.Single(result.Report.Renumbered!);
        Assert.Equal(local.Id, r.ModId);
        Assert.Equal([new IdChange(100, 101)], r.Ids);
        Assert.Equal(["Spell.SpellIconID x1"], r.References);
        Assert.DoesNotContain(result.Report.Conflicts, c => c.Path == Icons);
        Assert.Equal("yes", result.Report.Questions.Single(q => q.Id == $"renumber:{Icons}:{local.Id}").Chosen);
    }

    [Fact]
    public void AnsweringNo_LeavesTheLocalRowOut()
    {
        var (server, local, answers) = ServerAndLocal();
        answers[$"renumber:{Icons}:{local.Id}"] = "no";

        var result = Build(answers, server, local);

        var icons = FakeClient.ReadIcons(result.Chains["enUS"][Icons]);
        Assert.Equal("Interface\\Icons\\Server", icons[100]);
        Assert.False(icons.ContainsKey(101));
        Assert.Empty(result.Report.Renumbered!);
        Assert.Contains(result.Report.Conflicts, c => c.Path == Icons && c.Winner == server.Id);
    }

    [Fact]
    public void UnreferencedRow_IsNotRenumbered()
    {
        var (server, local, answers) = ServerAndLocal(localReferencesIt: false);

        var result = Build(answers, server, local);

        Assert.Empty(result.Report.Renumbered!);
        Assert.DoesNotContain(result.Report.Questions, q => q.Kind == "renumber");
        Assert.Equal("Interface\\Icons\\Server", FakeClient.ReadIcons(result.Chains["enUS"][Icons])[100]);
    }

    [Fact]
    public void TwoLocalMods_TheLowerPriorityOneIsRenumbered()
    {
        var low = Mod(new() { [Icons] = StockIconsPlus((100, "Low")), [Spell] = FakeClient.SpellDbc((1, 100), (2, 2)) });
        var high = Mod(new() { [Icons] = StockIconsPlus((100, "High")), [Spell] = FakeClient.SpellDbc((1, 1), (2, 100)) });

        var result = Build(new(), low, high);

        var icons = FakeClient.ReadIcons(result.Chains["enUS"][Icons]);
        Assert.Equal("High", icons[100]);
        Assert.Equal("Low", icons[101]);
        var spells = FakeClient.ReadSpellIcons(result.Chains["enUS"][Spell]);
        Assert.Equal(101, spells[1]); // low's edit, renumbered
        Assert.Equal(100, spells[2]); // high's edit, untouched
        Assert.Equal(low.Id, Assert.Single(result.Report.Renumbered!).ModId);
    }

    [Fact]
    public void ServerMods_AreNeverRenumbered()
    {
        var a = Mod(new() { [Icons] = StockIconsPlus((100, "A")), [Spell] = FakeClient.SpellDbc((1, 100), (2, 2)) });
        var b = Mod(new() { [Icons] = StockIconsPlus((100, "B")), [Spell] = FakeClient.SpellDbc((1, 1), (2, 100)) });

        var result = Build(new() { [$"tier:{a.Id}"] = "server", [$"tier:{b.Id}"] = "server" }, a, b);

        Assert.Empty(result.Report.Renumbered!);
        Assert.Equal("B", FakeClient.ReadIcons(result.Chains["enUS"][Icons])[100]);
    }
}
