using Mpm.Core.Mpq;
using Mpm.Core.Session;
using Mpm.Core.Store;

namespace Mpm.Tests;

public class MergeSessionTests
{
    private const string Titles = "DBFilesClient\\CharTitles.dbc";

    private static (TempDir Tmp, string Wow, MergeSession Session) Setup()
    {
        var tmp = new TempDir();
        var wow = Path.Combine(tmp.Path, "WoW");
        Directory.CreateDirectory(wow);
        var data = FakeClient.Create(wow);
        MpqWriter.Write(Path.Combine(data, "patch-4.MPQ"), new Dictionary<string, byte[]> { ["Textures\\A.blp"] = [1] });
        MpqWriter.Write(Path.Combine(data, "enUS", "patch-enUS-5.MPQ"), new Dictionary<string, byte[]>
        {
            [Titles] = FakeClient.TitlesDbc((1, "Renamed"), (2, "Corporal"), (3, "Sergeant")),
        });
        var store = new ModStore(Path.Combine(tmp.Path, "app", "store"));
        return (tmp, wow, new MergeSession(store, Path.Combine(tmp.Path, "app")));
    }

    [Fact]
    public void ClientMode_AddsTheClientsPatches_MergesInstallsAndUninstalls()
    {
        var (tmp, wow, session) = Setup();
        using var _ = tmp;

        session.SetClient(wow); // the WoW folder, not Data: resolved automatically
        Assert.Empty(session.AddClientPatches());
        Assert.Equal(["patch-4.MPQ", "patch-enUS-5.MPQ"], session.Sources.Select(s => Path.GetFileName(s.Path)));

        var build = session.Merge();
        Assert.True(build.Chains.ContainsKey("root"));
        Assert.True(build.Chains.ContainsKey("enUS"));

        var ledger = session.Install();
        var data = session.Client!.DataDir;
        Assert.True(File.Exists(Path.Combine(data, "patch-Z.MPQ")));
        Assert.True(File.Exists(Path.Combine(data, "patch-4.MPQ.mpm-off")));
        Assert.Equal(2, ledger.Renamed.Count);
        Assert.Throws<InvalidOperationException>(() => session.Install());

        // Reopening the client restores the list from the profile even though the originals are renamed.
        var reopened = new MergeSession(new ModStore(Path.Combine(tmp.Path, "app", "store")), Path.Combine(tmp.Path, "app"));
        Assert.Empty(reopened.SetClient(data));
        Assert.Equal(2, reopened.Sources.Count);
        Assert.Contains(reopened.AddClientPatches(), w => w.Contains("installed") || w.Contains("already"));

        var result = reopened.Uninstall();
        Assert.Empty(result.Problems);
        Assert.True(File.Exists(Path.Combine(data, "patch-4.MPQ")));
        Assert.False(File.Exists(Path.Combine(data, "patch-Z.MPQ")));
    }

    [Fact]
    public void FolderAndDropModes_AddEachArchive_AndAnswersAreRemembered()
    {
        var (tmp, wow, session) = Setup();
        using var _ = tmp;
        var mods = Path.Combine(tmp.Path, "downloads", "nested");
        Directory.CreateDirectory(mods);
        MpqWriter.Write(Path.Combine(mods, "patch-A.MPQ"), new Dictionary<string, byte[]> { [Titles] = FakeClient.TitlesDbc((1, "From A"), (2, "Corporal"), (3, "Sergeant")) });
        MpqWriter.Write(Path.Combine(tmp.Path, "downloads", "patch-B.MPQ"), new Dictionary<string, byte[]> { [Titles] = FakeClient.TitlesDbc((1, "From B"), (2, "Corporal"), (3, "Sergeant")) });
        File.WriteAllText(Path.Combine(tmp.Path, "downloads", "readme.txt"), "hi");

        session.SetClient(wow);
        Assert.Empty(session.AddFolder(Path.Combine(tmp.Path, "downloads")));
        Assert.Equal(["patch-A.MPQ", "patch-B.MPQ"], session.Sources.Select(s => Path.GetFileName(s.Path)));
        Assert.Contains(session.AddPaths([Path.Combine(tmp.Path, "downloads", "readme.txt")]), w => w.Contains("Skipped"));
        Assert.Contains(session.AddPaths([Path.Combine(mods, "patch-A.MPQ")]), w => w.Contains("already"));

        var q = session.Merge().Report.Questions.Single(x => x.Kind == "rows");
        var a = session.Sources[0].Mod.Id;
        var answered = session.Answer(new Dictionary<string, string> { [q.Id] = a });
        Assert.Equal("From A", FakeClient.ReadTitles(answered.Chains["enUS"][Titles])[1]);

        var output = session.Save(Path.Combine(tmp.Path, "out"));
        Assert.True(File.Exists(Path.Combine(output, "patch-Z.MPQ")) || File.Exists(Path.Combine(output, "enUS", "patch-enUS-Z.MPQ")));
        Assert.NotEqual(output, session.Save(Path.Combine(tmp.Path, "out"))); // non-empty folder -> new subfolder

        var reopened = new MergeSession(new ModStore(Path.Combine(tmp.Path, "app", "store")), Path.Combine(tmp.Path, "app"));
        reopened.SetClient(wow);
        Assert.Equal(a, reopened.Answers[q.Id]);
        session.Move(0, 1);
        Assert.Equal("patch-B.MPQ", Path.GetFileName(session.Sources[0].Path));
    }

    [Fact]
    public void SetClient_RejectsFoldersWithoutAClient()
    {
        using var tmp = new TempDir();
        var session = new MergeSession(new ModStore(tmp.File("store")));
        Assert.Throws<DirectoryNotFoundException>(() => session.SetClient(tmp.Path));
        Assert.Throws<InvalidOperationException>(() => session.AddClientPatches());
    }
}
