using Mpm.Core.Build;
using Mpm.Core.Mpq;
using Mpm.Core.Session;
using Mpm.Core.Store;

namespace Mpm.Tests;

/// <summary>Builds too large for one archive, and outputs that must not take a slot another patch uses.</summary>
public class LargeBuildTests
{
    private static byte[] Noise(int seed, int size)
    {
        var data = new byte[size];
        new Random(seed).NextBytes(data); // incompressible, so packed size is predictable
        return data;
    }

    [Fact]
    public void WriteParts_SplitsAtTheLimit_EveryFileInExactlyOnePart()
    {
        using var tmp = new TempDir();
        var files = Enumerable.Range(0, 40).ToDictionary(i => $"Textures\\T{i:D2}.blp", i => Noise(i, 10_000 + i));
        var loads = 0;
        var loaders = files.Select(kv => new KeyValuePair<string, Func<byte[]>>(kv.Key, () =>
        {
            Interlocked.Increment(ref loads);
            return kv.Value;
        }));

        var parts = MpqWriter.WriteParts(loaders, i => tmp.File($"part{i}.MPQ"), maxArchiveBytes: 100_000);

        Assert.True(parts.Count >= 4, $"expected several parts, got {parts.Count}");
        Assert.Equal(files.Count, loads); // each file loaded once
        var seen = new Dictionary<string, byte[]>(MpqPath.Comparer);
        foreach (var part in parts)
        {
            Assert.True(new FileInfo(part).Length <= 100_000);
            using var reader = MpqReader.Open(part);
            foreach (var name in reader.Files)
            {
                seen.Add(name, reader.Read(name)); // throws on a file in two parts
            }
        }
        Assert.Equal(files.Keys.Order(), seen.Keys.Order());
        Assert.All(files, kv => Assert.Equal(kv.Value, seen[kv.Key]));
    }

    [Fact]
    public void WriteParts_RefusesAFileLargerThanAnArchive()
    {
        using var tmp = new TempDir();
        var big = new KeyValuePair<string, Func<byte[]>>("Textures\\Big.blp", () => Noise(1, 50_000));
        Assert.Throws<IOException>(() => MpqWriter.WriteParts([big], i => tmp.File($"p{i}.MPQ"), maxArchiveBytes: 10_000));
        Assert.Empty(Directory.EnumerateFiles(tmp.Path)); // no partial archives left behind
    }

    [Fact]
    public void OverlappingAssetPacks_AreOneQuestion_AndTheAnswerAppliesToEveryFile()
    {
        using var tmp = new TempDir();
        var data = FakeClient.Create(tmp.Path);
        var store = new ModStore(tmp.File("store"));
        using var baseline = new Mpm.Core.Client.BaselineChain(data, "enUS");
        ModManifest Pack(string slot, byte tag)
        {
            var path = tmp.File($"src{slot}/patch-{slot}.MPQ");
            MpqWriter.Write(path, Enumerable.Range(0, 5).ToDictionary(i => $"Textures\\Shared{i}.blp", i => new[] { tag, (byte)i }));
            return store.Import(path).Manifest;
        }
        var a = Pack("4", 1);
        var b = Pack("5", 2);
        BuildResult Build(Dictionary<string, string>? answers = null) =>
            new PatchBuilder(store, baseline, Mpm.Core.Dbc.DbcLayouts.For335()).Build([a, b], new BuildOptions(answers));

        var result = Build();

        var q = Assert.Single(result.Report.Questions, q => q.Kind == "files");
        Assert.Contains("5 file(s)", q.Prompt);
        Assert.Equal(5, result.Report.Conflicts.Count(c => c.QuestionId == q.Id));
        Assert.All(Enumerable.Range(0, 5), i => Assert.Equal(2, result.Chains["root"][$"Textures\\Shared{i}.blp"][0]));

        var flipped = Build(new() { [q.Id] = a.Id });
        Assert.All(Enumerable.Range(0, 5), i => Assert.Equal(1, flipped.Chains["root"][$"Textures\\Shared{i}.blp"][0]));
    }

    [Fact]
    public void LargeBuild_SpreadsOverSeveralSlots_AndInstallSkipsSlotsOtherPatchesUse()
    {
        using var tmp = new TempDir();
        var wow = Path.Combine(tmp.Path, "WoW");
        Directory.CreateDirectory(wow);
        var data = FakeClient.Create(wow);
        var textures = Enumerable.Range(0, 30).ToDictionary(i => $"Textures\\HD{i:D2}.blp", i => Noise(i, 20_000));
        MpqWriter.Write(Path.Combine(data, "patch-4.MPQ"), textures);
        // A patch the user did not add keeps loading, so its slot (Y) must not be taken or renamed.
        MpqWriter.Write(Path.Combine(data, "patch-Y.MPQ"), new Dictionary<string, byte[]> { ["Textures\\Other.blp"] = [7] });

        var session = new MergeSession(new ModStore(Path.Combine(tmp.Path, "app", "store")), Path.Combine(tmp.Path, "app"));
        session.SetClient(wow);
        session.AddPaths([Path.Combine(data, "patch-4.MPQ")]);
        var build = session.Merge();
        Assert.Equal(textures[$"Textures\\HD03.blp"], build.Chains["root"]["Textures\\HD03.blp"]); // read from the store on demand

        var outDir = tmp.File("out");
        var written = BuildOutput.Write(build, outDir, ["Z", "X", "W"], maxArchiveBytes: 250_000);
        Assert.Equal(["patch-Z.MPQ", "patch-X.MPQ", "patch-W.MPQ"], written.Take(3));

        Assert.Equal([Path.Combine(data, "patch-4.MPQ")], session.InstallPreview());
        var ledger = session.Install();
        Assert.True(File.Exists(Path.Combine(data, "patch-Y.MPQ")));
        Assert.DoesNotContain(ledger.Renamed, r => r.Original.Contains("patch-Y", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(ledger.Installed, a => a.Path == "patch-Z.MPQ");
        Assert.DoesNotContain(ledger.Installed, a => a.Path == "patch-Y.MPQ");
    }
}
