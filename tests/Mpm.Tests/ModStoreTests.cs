using System.IO.Compression;
using System.Text;
using Mpm.Core.Mpq;
using Mpm.Core.Store;

namespace Mpm.Tests;

public class ModStoreTests
{
    private static byte[] B(string s) => Encoding.UTF8.GetBytes(s);

    private static void Mpq(string path, params (string Name, string Content)[] files) =>
        MpqWriter.Write(path, files.ToDictionary(f => f.Name, f => B(f.Content)));

    /// <summary>A mod folder shaped like a client Data dir, plus loose files and junk.</summary>
    private static string BuildModFolder(string root)
    {
        var data = Path.Combine(root, "MyMod", "Data");
        Directory.CreateDirectory(Path.Combine(data, "enUS", "Interface", "FrameXML"));
        Directory.CreateDirectory(Path.Combine(data, "Item", "ObjectComponents"));
        Mpq(Path.Combine(data, "patch-5.MPQ"), ("Item\\ObjectComponents\\Sword.blp", "from-mpq"), ("Spells\\Bolt.m2", "bolt"));
        Mpq(Path.Combine(data, "enUS", "patch-enUS-5.MPQ"), ("DBFilesClient\\Spell.dbc", "spells"));
        File.WriteAllBytes(Path.Combine(data, "Item", "ObjectComponents", "Sword.blp"), B("loose-wins"));
        File.WriteAllBytes(Path.Combine(data, "enUS", "Interface", "FrameXML", "Foo.lua"), B("print(1)"));
        File.WriteAllBytes(Path.Combine(root, "MyMod", "README.txt"), B("hello"));
        return Path.Combine(root, "MyMod");
    }

    [Fact]
    public void ImportMpq_ClassifiesChainFromName()
    {
        using var tmp = new TempDir();
        var store = new ModStore(tmp.File("store"));
        Mpq(tmp.File("patch-enUS-7.MPQ"), ("DBFilesClient\\Item.dbc", "items"));
        Mpq(tmp.File("patch-7.MPQ"), ("Textures\\a.blp", "tex"));

        var loc = store.Import(tmp.File("patch-enUS-7.MPQ")).Manifest;
        var root = store.Import(tmp.File("patch-7.MPQ")).Manifest;

        Assert.Equal(("enUS", "DBFilesClient\\Item.dbc"), (loc.Files.Single().Chain, loc.Files.Single().Path));
        Assert.Equal("root", root.Files.Single().Chain);
        Assert.Equal(B("items"), store.Read(loc.Files.Single()));
        Assert.Equal(SourceKind.Mpq, loc.Source.Kind);
    }

    [Fact]
    public void ImportFolder_ReadsMpqsAndLooseFiles_LooseWins()
    {
        using var tmp = new TempDir();
        var store = new ModStore(tmp.File("store"));
        var m = store.Import(BuildModFolder(tmp.Path)).Manifest;

        var byKey = m.Files.ToDictionary(f => (f.Chain, f.Path));
        Assert.Equal(4, m.Files.Count);
        Assert.Equal(B("loose-wins"), store.Read(byKey[("root", "Item\\ObjectComponents\\Sword.blp")]));
        Assert.Equal(B("bolt"), store.Read(byKey[("root", "Spells\\Bolt.m2")]));
        Assert.Equal(B("spells"), store.Read(byKey[("enUS", "DBFilesClient\\Spell.dbc")]));
        Assert.Equal(B("print(1)"), store.Read(byKey[("enUS", "Interface\\FrameXML\\Foo.lua")]));
        Assert.Contains(m.Warnings, w => w.Contains("README.txt"));
        Assert.Contains(m.Warnings, w => w.Contains("Sword.blp") && w.Contains("more than once"));
        Assert.Equal("MyMod", m.Name);
    }

    [Fact]
    public void ImportZip_MatchesFolderImport()
    {
        using var tmp = new TempDir();
        var folder = BuildModFolder(tmp.Path);
        var zipPath = tmp.File("MyMod.zip");
        ZipFile.CreateFromDirectory(folder, zipPath);

        var store = new ModStore(tmp.File("store"));
        var fromZip = store.Import(zipPath).Manifest;
        var fromFolder = store.Import(folder).Manifest;

        Assert.Equal(SourceKind.Zip, fromZip.Source.Kind);
        Assert.Equal(fromFolder.Files.Select(f => (f.Chain, f.Path, f.Sha256)), fromZip.Files.Select(f => (f.Chain, f.Path, f.Sha256)));
        Assert.False(Directory.Exists(Path.Combine(store.Root, "tmp")) && Directory.EnumerateFileSystemEntries(Path.Combine(store.Root, "tmp")).Any());
    }

    [Fact]
    public void ImportZip_RejectsEntriesEscapingTheArchive()
    {
        using var tmp = new TempDir();
        var zipPath = tmp.File("evil.zip");
        using (var zip = ZipFile.Open(zipPath, ZipArchiveMode.Create))
        {
            using var s = zip.CreateEntry("../../escaped.txt").Open();
            s.Write(B("x"));
        }
        var store = new ModStore(tmp.File("store"));

        Assert.Throws<InvalidDataException>(() => store.Import(zipPath));
        Assert.False(File.Exists(Path.Combine(store.Root, "escaped.txt")));
        Assert.Empty(store.List());
    }

    [Fact]
    public void Reimport_ReturnsExistingMod()
    {
        using var tmp = new TempDir();
        var store = new ModStore(tmp.File("store"));
        Mpq(tmp.File("patch-6.MPQ"), ("Textures\\a.blp", "tex"));

        var first = store.Import(tmp.File("patch-6.MPQ"));
        var second = store.Import(tmp.File("patch-6.MPQ"), "renamed");

        Assert.False(first.AlreadyImported);
        Assert.True(second.AlreadyImported);
        Assert.Equal(first.Manifest.Id, second.Manifest.Id);
        Assert.Single(store.List());
    }

    [Fact]
    public void Remove_DropsOnlyUnsharedBlobs_AndLeavesSourceAlone()
    {
        using var tmp = new TempDir();
        var store = new ModStore(tmp.File("store"));
        Mpq(tmp.File("patch-6.MPQ"), ("Textures\\shared.blp", "same"), ("Textures\\only6.blp", "six"));
        Mpq(tmp.File("patch-7.MPQ"), ("Textures\\shared.blp", "same"));
        var six = store.Import(tmp.File("patch-6.MPQ")).Manifest;
        var seven = store.Import(tmp.File("patch-7.MPQ")).Manifest;

        store.Remove(six.Id);

        Assert.Equal([seven.Id], store.List().Select(m => m.Id));
        Assert.Equal(B("same"), store.Read(seven.Files.Single()));
        var only6 = six.Files.Single(f => f.Path.EndsWith("only6.blp"));
        Assert.Throws<FileNotFoundException>(() => store.Read(only6));
        Assert.True(File.Exists(tmp.File("patch-6.MPQ")));
    }

    [Fact]
    public void Get_RejectsPathTraversalIds()
    {
        using var tmp = new TempDir();
        var store = new ModStore(tmp.File("store"));
        Assert.Throws<ArgumentException>(() => store.Get("../x"));
        Assert.Throws<ArgumentException>(() => store.Remove("../../etc"));
    }
}

public class ListfileRecoveryTests
{
    [Fact]
    public void ArchiveWithoutListfile_RecoversKnownNames_AndCountsTheRest()
    {
        using var tmp = new TempDir();
        var path = tmp.File("patch-B.MPQ");
        MpqWriter.Write(path, new Dictionary<string, byte[]>
        {
            ["DBFilesClient\\Spell.dbc"] = [1, 2, 3],
            ["Textures\\Secret.blp"] = [4],
            ["Interface\\Known.lua"] = [5],
        });
        StripListfile(path);

        var store = new ModStore(tmp.File("store"));
        var m = store.Import(path, nameHints: ["Interface\\Known.lua"]).Manifest;

        Assert.Equal(["DBFilesClient\\Spell.dbc", "Interface\\Known.lua"], m.Files.Select(f => f.Path).Order(StringComparer.OrdinalIgnoreCase));
        Assert.Equal(1, m.UnnamedFiles);
        Assert.False(m.IsComplete);
        Assert.Contains(m.Warnings, w => w.Contains("could not be identified"));
    }

    [Fact]
    public void ArchiveWithNothingIdentifiable_FailsWithAHint()
    {
        using var tmp = new TempDir();
        var path = tmp.File("patch-C.MPQ");
        MpqWriter.Write(path, new Dictionary<string, byte[]> { ["Textures\\Secret.blp"] = [4] });
        StripListfile(path);

        var e = Assert.Throws<InvalidDataException>(() => new ModStore(tmp.File("store")).Import(path));
        Assert.Contains("name hints", e.Message);
    }

    /// <summary>Rewrites the archive's (listfile) hash entry as deleted, as stripped mod patches have.</summary>
    private static void StripListfile(string path)
    {
        using var reader = MpqReader.Open(path);
        var files = reader.Files.ToDictionary(f => f, reader.Read);
        reader.Dispose();
        Mpm.Core.Mpq.MpqWriter.WriteWithoutListfile(path, files);
    }
}
