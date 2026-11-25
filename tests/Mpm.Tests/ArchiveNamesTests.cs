using Mpm.Core.Client;

namespace Mpm.Tests;

public class ArchiveNamesTests
{
    [Theory]
    [InlineData("patch.MPQ", "root", "")]
    [InlineData("patch-2.MPQ", "root", "2")]
    [InlineData("patch-4.MPQ", "root", "4")]
    [InlineData("patch-z.mpq", "root", "Z")]
    [InlineData("patch-enUS.MPQ", "enUS", "")]
    [InlineData("patch-enUS-3.MPQ", "enUS", "3")]
    [InlineData("patch-enus-z.mpq", "enUS", "Z")]
    [InlineData("patch-deDE-A.MPQ", "deDE", "A")]
    public void ClassifyPatch_RecognizesLoadedNames(string name, string chain, string slot)
    {
        Assert.Equal((chain, slot), ArchiveNames.ClassifyPatch(name));
    }

    [Theory]
    [InlineData("patch-4.MPQ.prev")]
    [InlineData("patch-4.MPQ.mpm-off")]
    [InlineData("patch-10.MPQ")]
    [InlineData("common.MPQ")]
    [InlineData("locale-enUS.MPQ")]
    [InlineData("patch-data-4.MPQ")]
    public void ClassifyPatch_IgnoresNamesTheClientDoesNotLoad(string name)
    {
        Assert.Null(ArchiveNames.ClassifyPatch(name));
    }

    [Fact]
    public void ClientInstall_FindsCustomPatchesOnly()
    {
        using var tmp = new TempDir();
        var data = tmp.Path;
        Directory.CreateDirectory(Path.Combine(data, "enUS"));
        foreach (var f in new[] { "patch.MPQ", "patch-2.MPQ", "patch-3.MPQ", "patch-4.MPQ", "patch-4.MPQ.prev", "patch-B.MPQ",
                     "enUS/locale-enUS.MPQ", "enUS/patch-enUS-3.MPQ", "enUS/patch-enUS-A.MPQ", "enUS/patch-enUS-4.MPQ.bak" })
        {
            File.WriteAllBytes(Path.Combine(data, f), []);
        }

        var client = new ClientInstall(data);
        Assert.Equal(["enUS"], client.Locales);
        var custom = client.CustomPatches().Select(p => (p.Chain, p.Slot, Path.GetFileName(p.Path))).ToList();
        Assert.Equal([("root", "4", "patch-4.MPQ"), ("root", "B", "patch-B.MPQ"), ("enUS", "A", "patch-enUS-A.MPQ")], custom);
    }

    [Fact]
    public void ClientInstall_HandlesLowercaseLocaleFolder()
    {
        using var tmp = new TempDir();
        Directory.CreateDirectory(Path.Combine(tmp.Path, "enus"));
        File.WriteAllBytes(Path.Combine(tmp.Path, "enus", "locale-enus.mpq"), []);
        File.WriteAllBytes(Path.Combine(tmp.Path, "enus", "patch-enus-z.mpq"), []);

        var client = new ClientInstall(tmp.Path);
        Assert.Equal(["enUS"], client.Locales);
        Assert.Equal(("enUS", "Z"), client.CustomPatches().Select(p => (p.Chain, p.Slot)).Single());
        Assert.NotNull(ClientInstall.FindCaseInsensitive(Path.Combine(tmp.Path, "enUS", "patch-enUS-Z.MPQ")));
    }
}
