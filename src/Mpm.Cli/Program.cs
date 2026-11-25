using Mpm.Core.Build;
using Mpm.Core.Client;
using Mpm.Core.Dbc;
using Mpm.Core.Install;
using Mpm.Core.Mpq;
using Mpm.Core.Store;

// Developer CLI. The GUI is the primary interface; this exists for testing and scripting.
// The store location comes from $MPM_STORE, else $XDG_DATA_HOME/mpm/store, else ~/.local/share/mpm/store.
return args switch
{
    ["ls", var mpq] => List(mpq),
    ["cat", var mpq, var file, var outPath] => Extract(mpq, file, outPath),
    ["dbc", var mpq, var file] => DbcInfo(mpq, file),
    ["pack", var outPath, var dir] => Pack(outPath, dir),
    ["import", var source, "--client", var data] => Import(source, null, data),
    ["import", var source, var name, "--client", var data] => Import(source, name, data),
    ["import", var source] => Import(source, null, null),
    ["import", var source, var name] => Import(source, name, null),
    ["mods"] => Mods(),
    ["show", var id] => Show(id),
    ["remove", var id] => Remove(id),
    ["client", var data] => Client(data),
    ["baseline", var data, var locale, var path] => Baseline(data, locale, path),
    ["build", var data, var locale, var outDir, "--answers", var answers, .. var ids] when ids.Length > 0 => BuildPatch(data, locale, outDir, ids, answers),
    ["build", var data, var locale, var outDir, .. var ids] when ids.Length > 0 => BuildPatch(data, locale, outDir, ids, null),
    ["apply", var data, var buildDir, .. var extra] => Apply(data, buildDir, extra),
    ["unapply", var data] => Unapply(data),
    ["status", var data] => Status(data),
    _ => Usage(),
};

static int List(string mpq)
{
    using var reader = MpqReader.Open(mpq);
    foreach (var f in reader.Files)
    {
        Console.WriteLine(f);
    }
    return 0;
}

static int Extract(string mpq, string file, string outPath)
{
    using var reader = MpqReader.Open(mpq);
    File.WriteAllBytes(outPath, reader.Read(file));
    return 0;
}

static int DbcInfo(string mpq, string file)
{
    using var reader = MpqReader.Open(mpq);
    var dbc = DbcFile.Parse(reader.Read(file));
    Console.WriteLine($"records={dbc.Records.Count} fields={dbc.FieldCount} recordSize={dbc.RecordSize} strings={dbc.StringBlock.Length}");
    if (dbc.Records.Count > 0 && dbc.RecordSize >= 4)
    {
        var ids = dbc.Records.Select(DbcFile.GetId).ToList();
        Console.WriteLine($"field0: min={ids.Min()} max={ids.Max()} duplicates={ids.Count - ids.Distinct().Count()}");
    }
    return 0;
}

static int Pack(string outPath, string dir)
{
    var root = Path.GetFullPath(dir);
    var files = Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
        .ToDictionary(f => Path.GetRelativePath(root, f), File.ReadAllBytes);
    MpqWriter.Write(outPath, files);
    Console.WriteLine($"wrote {files.Count} files to {outPath}");
    return 0;
}

static int Import(string source, string? name, string? clientData)
{
    var store = new ModStore(ModStore.DefaultRoot());
    var hints = new List<string>();
    if (clientData is not null)
    {
        foreach (var locale in new ClientInstall(clientData).Locales)
        {
            using var baseline = new BaselineChain(clientData, locale);
            hints.AddRange(baseline.Paths.Keys);
        }
    }
    var (manifest, already) = store.Import(source, name, hints);
    Console.WriteLine(already ? $"already imported as {manifest.Id}" : $"imported {manifest.Id}: {manifest.Files.Count} files");
    foreach (var w in manifest.Warnings)
    {
        Console.WriteLine($"  warning: {w}");
    }
    return 0;
}

static int Mods()
{
    foreach (var m in new ModStore(ModStore.DefaultRoot()).List())
    {
        var chains = string.Join(",", m.Files.Select(f => f.Chain).Distinct());
        Console.WriteLine($"{m.Id}\t{m.Name}\t{m.Files.Count} files\t[{chains}]\t{m.Source.Kind}");
    }
    return 0;
}

static int Show(string id)
{
    var m = new ModStore(ModStore.DefaultRoot()).Get(id);
    Console.WriteLine($"{m.Name} ({m.Id}) from {m.Source.Path} [{m.Source.Kind}] imported {m.ImportedAt:u}");
    foreach (var group in m.Files.GroupBy(f => (f.Chain, Top: f.Path.Split('\\')[0].ToLowerInvariant())).OrderBy(g => g.Key.Chain).ThenBy(g => g.Key.Top))
    {
        Console.WriteLine($"  {group.Key.Chain,-5} {group.Key.Top,-16} {group.Count(),6} files");
    }
    foreach (var w in m.Warnings)
    {
        Console.WriteLine($"  warning: {w}");
    }
    return 0;
}

static int Remove(string id)
{
    new ModStore(ModStore.DefaultRoot()).Remove(id);
    Console.WriteLine($"removed {id} from the store (source files untouched)");
    return 0;
}

static int Client(string data)
{
    var client = new ClientInstall(data);
    Console.WriteLine($"locales: {string.Join(", ", client.Locales)}");
    foreach (var p in client.Patches())
    {
        Console.WriteLine($"  {(p.IsStock ? "stock " : "custom")} {p.Chain,-5} slot={(p.Slot.Length == 0 ? "-" : p.Slot)} {Path.GetRelativePath(client.DataDir, p.Path)}");
    }
    return 0;
}

static int Baseline(string data, string locale, string path)
{
    using var chain = new BaselineChain(data, locale);
    foreach (var missing in chain.Missing)
    {
        Console.WriteLine($"missing stock archive: {missing}");
    }
    Console.WriteLine(chain.Locate(path) is { } hit ? $"{hit.Chain}\t{hit.Archive}" : "not a stock file");
    return 0;
}

static int BuildPatch(string data, string locale, string outDir, string[] ids, string? answersFile)
{
    var store = new ModStore(ModStore.DefaultRoot());
    var mods = ids.Select(store.Get).ToList();
    using var baseline = new BaselineChain(data, locale);
    foreach (var missing in baseline.Missing)
    {
        Console.WriteLine($"warning: missing stock archive {missing}");
    }
    var answers = answersFile is null ? null : BuildOutput.ReadAnswers(answersFile);
    var result = new PatchBuilder(store, baseline, DbcLayouts.For335()).Build(mods, new BuildOptions(answers));
    var archives = BuildOutput.Write(result, outDir);
    Console.Write(BuildOutput.Summarize(result.Report, archives));
    return 0;
}

static int Apply(string data, string buildDir, string[] extra)
{
    // Default: disable the client's custom patches that are byte-identical to a mod in this build.
    var report = BuildOutput.ReadReport(buildDir);
    var store = new ModStore(ModStore.DefaultRoot());
    var buildMods = report.ModOrder.Select(store.Get).ToList();
    var keepLoading = report.KeepLoading.ToHashSet();
    var sources = buildMods.Where(m => !keepLoading.Contains(m.Id)).Select(m => m.Source.Sha256).ToHashSet();
    foreach (var m in buildMods.Where(m => keepLoading.Contains(m.Id)))
    {
        Console.WriteLine($"note: {m.Id} is kept loading (it has {m.UnnamedFiles} unidentified file(s))");
    }
    var client = new ClientInstall(data);
    var disable = new List<string>();
    var keep = new List<string>();
    foreach (var patch in client.CustomPatches())
    {
        (sources.Contains(Installer.Sha256File(patch.Path)) ? disable : keep).Add(patch.Path);
    }
    for (var i = 0; i < extra.Length; i++)
    {
        if (extra[i] == "--disable" && i + 1 < extra.Length)
        {
            disable.Add(extra[++i]);
        }
        else
        {
            Console.Error.WriteLine($"unknown option {extra[i]}");
            return 2;
        }
    }
    var ledger = new Installer(data).Apply(buildDir, disable);
    foreach (var r in ledger.Renamed)
    {
        Console.WriteLine($"disabled {r.Original} -> {r.RenamedTo} ({r.Reason})");
    }
    foreach (var a in ledger.Installed)
    {
        Console.WriteLine($"installed {a.Path}");
    }
    foreach (var k in keep.Where(k => !ledger.Renamed.Any(r => Path.Combine(client.DataDir, r.Original) == k)))
    {
        Console.WriteLine($"warning: {Path.GetRelativePath(client.DataDir, k)} is not part of this build and still loads");
    }
    return 0;
}

static int Unapply(string data)
{
    var result = new Installer(data).Unapply();
    foreach (var r in result.Removed)
    {
        Console.WriteLine($"removed {r}");
    }
    foreach (var r in result.Restored)
    {
        Console.WriteLine($"restored {r}");
    }
    foreach (var p in result.Problems)
    {
        Console.WriteLine($"problem: {p}");
    }
    return result.Problems.Count == 0 ? 0 : 1;
}

static int Status(string data)
{
    var ledger = new Installer(data).ReadLedger();
    if (ledger is null)
    {
        Console.WriteLine("no build applied");
        return 0;
    }
    Console.WriteLine($"applied {ledger.AppliedAt:u} from {ledger.BuildDir}");
    foreach (var r in ledger.Renamed)
    {
        Console.WriteLine($"  disabled {r.Original} -> {r.RenamedTo} ({r.Reason})");
    }
    foreach (var a in ledger.Installed)
    {
        Console.WriteLine($"  installed {a.Path}");
    }
    return 0;
}

static int Usage()
{
    Console.Error.WriteLine("""
        usage:
          mpm ls <mpq>                     list files in an archive
          mpm cat <mpq> <file> <out>       extract one file
          mpm dbc <mpq> <file>             DBC summary
          mpm pack <out.mpq> <dir>         build an archive from a folder
          mpm import <mpq|zip|dir> [name] [--client <Data dir>]
                                           import a mod; --client uses the client's stock paths to
                                           identify files in archives without a (listfile)
          mpm mods                         list imported mods
          mpm show <id>                    summarize one mod
          mpm remove <id>                  remove a mod from the store
          mpm client <Data dir>            list locales and patch archives
          mpm baseline <Data dir> <locale> <path>   which stock archive provides a file
          mpm build <Data dir> <locale> <out dir> [--answers <file>] <mod id>...
                                           merge mods (last = highest priority); writes
                                           build-questions.json: edit its answers, pass it back
          mpm apply <Data dir> <build dir> [--disable <patch>]...  install a build; custom patches identical
                                           to a mod in the build are renamed to *.mpm-off
          mpm unapply <Data dir>           remove the build and restore renamed patches
          mpm status <Data dir>            show what is applied
        """);
    return 2;
}
