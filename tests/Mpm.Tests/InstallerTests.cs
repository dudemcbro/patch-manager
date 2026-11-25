using Mpm.Core.Install;
using Mpm.Core.Mpq;

namespace Mpm.Tests;

public class InstallerTests
{
    private static Dictionary<string, string> Snapshot(string dir) =>
        Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories)
            .ToDictionary(f => Path.GetRelativePath(dir, f), Installer.Sha256File);

    private static (string Data, string Build) Setup(TempDir tmp)
    {
        var data = FakeClient.Create(tmp.Path);
        MpqWriter.Write(Path.Combine(data, "patch-4.MPQ"), new Dictionary<string, byte[]> { ["Textures\\Mod.blp"] = [4] });
        MpqWriter.Write(Path.Combine(data, "patch-Z.MPQ"), new Dictionary<string, byte[]> { ["Textures\\Other.blp"] = [26] });
        File.WriteAllBytes(Path.Combine(data, "patch-4.MPQ.prev"), [1]);

        var build = tmp.File("build");
        Directory.CreateDirectory(Path.Combine(build, "enUS"));
        MpqWriter.Write(Path.Combine(build, "patch-Z.MPQ"), new Dictionary<string, byte[]> { ["Textures\\Merged.blp"] = [42] });
        MpqWriter.Write(Path.Combine(build, "enUS", "patch-enUS-Z.MPQ"), new Dictionary<string, byte[]> { ["Interface\\FrameXML\\M.lua"] = [1] });
        return (data, build);
    }

    [Fact]
    public void ApplyThenUnapply_RestoresTheClientExactly()
    {
        using var tmp = new TempDir();
        var (data, build) = Setup(tmp);
        var before = Snapshot(data);
        var installer = new Installer(data);

        var ledger = installer.Apply(build, ["patch-4.MPQ"]);

        Assert.True(File.Exists(Path.Combine(data, "patch-4.MPQ.mpm-off")));
        Assert.True(File.Exists(Path.Combine(data, "patch-Z.MPQ.mpm-off"))); // occupied slot renamed, not overwritten
        Assert.False(File.Exists(Path.Combine(data, "patch-4.MPQ")));
        using (var installed = MpqReader.Open(Path.Combine(data, "patch-Z.MPQ")))
        {
            Assert.Equal([42], installed.Read("Textures\\Merged.blp"));
        }
        Assert.True(File.Exists(Path.Combine(data, "enUS", "patch-enUS-Z.MPQ")));
        Assert.Equal(2, ledger.Renamed.Count);
        Assert.Equal(2, ledger.Installed.Count);
        Assert.True(File.Exists(Path.Combine(data, "patch-4.MPQ.prev")));

        var result = installer.Unapply();

        Assert.Empty(result.Problems);
        Assert.Equal(before, Snapshot(data));
        Assert.Null(installer.ReadLedger());
    }

    [Fact]
    public void Unapply_NeverDeletesAnInstalledArchiveThatChanged()
    {
        using var tmp = new TempDir();
        var (data, build) = Setup(tmp);
        var installer = new Installer(data);
        installer.Apply(build, []);
        File.AppendAllText(Path.Combine(data, "patch-Z.MPQ"), "edited");

        var result = installer.Unapply();

        Assert.Contains(result.Problems, p => p.Contains("changed since apply"));
        Assert.True(File.Exists(Path.Combine(data, "patch-Z.MPQ" + Installer.ModifiedSuffix)));
        Assert.True(File.Exists(Path.Combine(data, "patch-Z.MPQ"))); // the original occupant is back
    }

    [Fact]
    public void Apply_Refuses_WhenAlreadyApplied_OrDisablingStock_OrOutsideData()
    {
        using var tmp = new TempDir();
        var (data, build) = Setup(tmp);
        var installer = new Installer(data);

        Assert.Throws<ArgumentException>(() => installer.Apply(build, ["patch.MPQ"]));
        Assert.Throws<ArgumentException>(() => installer.Apply(build, ["../outside/patch-4.MPQ"]));
        Assert.Null(installer.ReadLedger());

        installer.Apply(build, []);
        Assert.Throws<InvalidOperationException>(() => installer.Apply(build, []));
    }
}
