using Mpm.Core.Client;
using Mpm.Core.Dbc;
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
    ["import", var source] => Import(source, null),
    ["import", var source, var name] => Import(source, name),
    ["mods"] => Mods(),
    ["show", var id] => Show(id),
    ["remove", var id] => Remove(id),
    ["client", var data] => Client(data),
    ["baseline", var data, var locale, var path] => Baseline(data, locale, path),
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

static int Import(string source, string? name)
{
    var store = new ModStore(ModStore.DefaultRoot());
    var (manifest, already) = store.Import(source, name);
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

static int Usage()
{
    Console.Error.WriteLine("""
        usage:
          mpm ls <mpq>                     list files in an archive
          mpm cat <mpq> <file> <out>       extract one file
          mpm dbc <mpq> <file>             DBC summary
          mpm pack <out.mpq> <dir>         build an archive from a folder
          mpm import <mpq|zip|dir> [name]  import a mod into the store
          mpm mods                         list imported mods
          mpm show <id>                    summarize one mod
          mpm remove <id>                  remove a mod from the store
          mpm client <Data dir>            list locales and patch archives
          mpm baseline <Data dir> <locale> <path>   which stock archive provides a file
        """);
    return 2;
}
