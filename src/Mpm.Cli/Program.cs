using Mpm.Core.Dbc;
using Mpm.Core.Mpq;

// Minimal M1 CLI: inspect archives and DBCs. Real commands (build/apply/unapply) come later.
return args switch
{
    ["ls", var mpq] => List(mpq),
    ["cat", var mpq, var file, var outPath] => Extract(mpq, file, outPath),
    ["dbc", var mpq, var file] => DbcInfo(mpq, file),
    ["pack", var outPath, var dir] => Pack(outPath, dir),
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
    var ids = dbc.Records.Select(DbcFile.GetId).ToList();
    Console.WriteLine($"records={dbc.Records.Count} fields={dbc.FieldCount} recordSize={dbc.RecordSize} strings={dbc.StringBlock.Length}");
    if (ids.Count > 0)
    {
        Console.WriteLine($"idMin={ids.Min()} idMax={ids.Max()} duplicateIds={ids.Count - ids.Distinct().Count()}");
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

static int Usage()
{
    Console.Error.WriteLine("usage: mpm ls <mpq> | mpm cat <mpq> <file> <out> | mpm dbc <mpq> <file> | mpm pack <out.mpq> <dir>");
    return 2;
}
