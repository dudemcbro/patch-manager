using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Mpm.Core.Build;
using Mpm.Core.Client;
using Mpm.Core.Dbc;
using Mpm.Core.Install;
using Mpm.Core.Mpq;
using Mpm.Core.Store;

namespace Mpm.Core.Session;

/// <summary>One patch the user added, in merge order (later wins).</summary>
public sealed record SourceEntry(string Path, ModManifest Mod);

/// <summary>A remembered source: where it came from and the stored mod (used when the file was renamed by Install).</summary>
public sealed record ProfileSource(string Path, string ModId);

/// <summary>What a profile remembers for one client: the source list and the answers given.</summary>
public sealed record Profile(int Schema, string DataDir, List<ProfileSource> Sources, Dictionary<string, string> Answers);

/// <summary>
/// Everything the GUI does, without the GUI: pick a client, add patches (the client's own, a folder tree,
/// or dropped files), merge, answer questions, save or install the result. Not thread-safe; the GUI runs
/// one operation at a time.
/// </summary>
public sealed class MergeSession
{
    private readonly ModStore _store;
    private readonly string _profilesDir;
    private readonly string _buildsDir;
    private HashSet<string>? _nameHints;

    public MergeSession(ModStore store, string? appDataDir = null)
    {
        _store = store;
        var root = appDataDir ?? Path.GetDirectoryName(store.Root) ?? store.Root;
        _profilesDir = Path.Combine(root, "profiles");
        _buildsDir = Path.Combine(root, "builds");
    }

    public ClientInstall? Client { get; private set; }
    public string? Locale { get; private set; }
    public List<SourceEntry> Sources { get; } = [];
    public Dictionary<string, string> Answers { get; } = new(StringComparer.Ordinal);
    public BuildResult? LastBuild { get; private set; }

    /// <summary>
    /// Accepts a WoW folder or its Data folder. Restores the saved profile for that client, if any.
    /// Returns warnings (e.g. profile sources that no longer exist).
    /// </summary>
    public List<string> SetClient(string folder)
    {
        var data = ResolveDataDir(folder) ?? throw new DirectoryNotFoundException(
            $"No WoW 3.3.5a Data folder found in {folder} (expected Data/<locale>/locale-<locale>.MPQ)");
        var client = new ClientInstall(data);
        if (client.Locales.Count == 0)
        {
            throw new InvalidDataException($"{data} has no locale folder with a locale-xxXX.MPQ");
        }
        Client = client;
        Locale = client.Locales[0];
        Sources.Clear();
        Answers.Clear();
        LastBuild = null;
        _nameHints = null;
        return LoadProfile();
    }

    /// <summary>Finds the Data folder for a WoW folder, its Data folder, or a file inside either.</summary>
    public static string? ResolveDataDir(string path)
    {
        var dir = File.Exists(path) ? Path.GetDirectoryName(path)! : path;
        foreach (var candidate in new[] { dir, Path.Combine(dir, "Data"), Path.Combine(dir, "data") })
        {
            if (Directory.Exists(candidate) && new ClientInstall(candidate).Locales.Count > 0)
            {
                return Path.GetFullPath(candidate);
            }
        }
        return null;
    }

    private ClientInstall RequireClient() => Client ?? throw new InvalidOperationException("Choose a WoW client first");

    /// <summary>Stock paths of the client, used to identify files in archives without a (listfile).</summary>
    private HashSet<string> NameHints()
    {
        if (_nameHints is null)
        {
            using var baseline = new BaselineChain(RequireClient().DataDir, Locale!);
            _nameHints = baseline.Paths.Keys.ToHashSet(MpqPath.Comparer);
        }
        return _nameHints;
    }

    /// <summary>Reading the client's stock paths takes a second or two, so only do it when an archive needs them.</summary>
    private IEnumerable<string> LazyNameHints()
    {
        foreach (var name in NameHints())
        {
            yield return name;
        }
    }

    // ---- adding sources ---------------------------------------------------------------------

    /// <summary>Adds every custom patch the client currently loads, in the client's load order.</summary>
    public List<string> AddClientPatches()
    {
        var client = RequireClient();
        var warnings = new List<string>();
        foreach (var patch in client.CustomPatches())
        {
            warnings.AddRange(AddOne(patch.Path));
        }
        if (client.CustomPatches().Count == 0)
        {
            warnings.Add("This client has no custom patches.");
        }
        return warnings;
    }

    /// <summary>
    /// Adds a folder: every .mpq and .zip under it as its own patch (patch-name order, then path); a folder
    /// with no archives is added as one mod of loose files.
    /// </summary>
    public List<string> AddFolder(string folder)
    {
        var archives = Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories)
            .Where(IsArchive)
            .OrderBy(p => ArchiveNames.SlotOrder(ArchiveNames.ClassifyPatch(Path.GetFileName(p))?.Slot ?? "~"))
            .ThenBy(p => p, StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (archives.Count == 0)
        {
            return AddOne(folder);
        }
        var warnings = new List<string>();
        foreach (var a in archives)
        {
            warnings.AddRange(AddOne(a));
        }
        return warnings;
    }

    /// <summary>Adds dropped or picked files and folders.</summary>
    public List<string> AddPaths(IEnumerable<string> paths)
    {
        var warnings = new List<string>();
        foreach (var p in paths)
        {
            if (Directory.Exists(p))
            {
                warnings.AddRange(AddFolder(p));
            }
            else if (IsArchive(p))
            {
                warnings.AddRange(AddOne(p));
            }
            else
            {
                warnings.Add($"Skipped {Path.GetFileName(p)}: not an .mpq, .zip or folder");
            }
        }
        return warnings;
    }

    private static bool IsArchive(string path) =>
        path.EndsWith(".mpq", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".zip", StringComparison.OrdinalIgnoreCase);

    private List<string> AddOne(string path)
    {
        RequireClient();
        var full = Path.GetFullPath(path);
        if (Sources.Any(s => s.Path == full))
        {
            return [$"{Path.GetFileName(full)} is already in the list"];
        }
        if (IsInstalledBuild(full))
        {
            return [$"Skipped {Path.GetFileName(full)}: it is the merged patch this tool installed"];
        }
        try
        {
            var (mod, _) = _store.Import(full, nameHints: LazyNameHints());
            if (Sources.Any(s => s.Mod.Id == mod.Id))
            {
                return [$"{Path.GetFileName(full)} is identical to a patch already in the list"];
            }
            Sources.Add(new SourceEntry(full, mod));
            LastBuild = null;
            return mod.Warnings.Select(w => $"{mod.Name}: {w}").ToList();
        }
        catch (Exception e) when (e is InvalidDataException or IOException or NotSupportedException)
        {
            return [$"Could not add {Path.GetFileName(full)}: {e.Message}"];
        }
    }

    private bool IsInstalledBuild(string path)
    {
        if (Client is null || new Installer(Client.DataDir).ReadLedger() is not { } ledger)
        {
            return false;
        }
        return ledger.Installed.Any(a => Path.GetFullPath(Path.Combine(Client.DataDir, a.Path)) == path);
    }

    public void Move(int index, int delta)
    {
        var target = index + delta;
        if (index < 0 || index >= Sources.Count || target < 0 || target >= Sources.Count)
        {
            return;
        }
        (Sources[index], Sources[target]) = (Sources[target], Sources[index]);
        LastBuild = null;
    }

    public void Remove(int index)
    {
        if (index >= 0 && index < Sources.Count)
        {
            Sources.RemoveAt(index);
            LastBuild = null;
        }
    }

    // ---- merging ----------------------------------------------------------------------------

    /// <summary>Merges the sources with the current answers. Saves the profile.</summary>
    public BuildResult Merge()
    {
        var client = RequireClient();
        if (Sources.Count == 0)
        {
            throw new InvalidOperationException("Add at least one patch first");
        }
        using var baseline = new BaselineChain(client.DataDir, Locale!);
        var builder = new PatchBuilder(_store, baseline, DbcLayouts.For335());
        LastBuild = builder.Build(Sources.Select(s => s.Mod).ToList(), new BuildOptions(Answers));
        SaveProfile();
        return LastBuild;
    }

    /// <summary>Records answers (question id -> option value) and merges again.</summary>
    public BuildResult Answer(IReadOnlyDictionary<string, string> answers)
    {
        foreach (var (id, value) in answers)
        {
            Answers[id] = value;
        }
        return Merge();
    }

    /// <summary>
    /// Writes the merged patch into <paramref name="folder"/>: into it when empty, else into a new
    /// "merged-patch-&lt;time&gt;" subfolder. Returns the folder written.
    /// </summary>
    public string Save(string folder)
    {
        var build = LastBuild ?? throw new InvalidOperationException("Merge first");
        var target = Directory.Exists(folder) && Directory.EnumerateFileSystemEntries(folder).Any()
            ? Path.Combine(folder, $"merged-patch-{DateTime.Now:yyyyMMdd-HHmmss}")
            : folder;
        BuildOutput.Write(build, target);
        return target;
    }

    // ---- installing -------------------------------------------------------------------------

    public Ledger? Installed => Client is null ? null : new Installer(Client.DataDir).ReadLedger();

    /// <summary>
    /// Installs the last merge into the client. The client's custom patches that are part of the merge are
    /// renamed (never deleted) so they stop loading, except ones the user chose to keep loading.
    /// </summary>
    public Ledger Install()
    {
        var client = RequireClient();
        var build = LastBuild ?? throw new InvalidOperationException("Merge first");
        if (Installed is not null)
        {
            throw new InvalidOperationException("A merged patch is already installed; uninstall it first");
        }
        var disable = InstallPreview();
        var dir = Path.Combine(_buildsDir, DateTime.Now.ToString("yyyyMMdd-HHmmss-fff"));
        BuildOutput.Write(build, dir);
        return new Installer(client.DataDir).Apply(dir, disable);
    }

    /// <summary>
    /// The client patches Install would rename so they stop loading: custom patches identical to a merged
    /// source, minus ones kept loading, plus whatever occupies the output slot.
    /// </summary>
    public List<string> InstallPreview()
    {
        var client = RequireClient();
        var build = LastBuild ?? throw new InvalidOperationException("Merge first");
        var keep = build.Report.KeepLoading.ToHashSet(StringComparer.Ordinal);
        var merged = Sources.Where(s => !keep.Contains(s.Mod.Id)).Select(s => s.Mod.Source.Sha256).ToHashSet(StringComparer.Ordinal);
        return client.CustomPatches()
            .Where(p => merged.Contains(Installer.Sha256File(p.Path)) || p.Slot == BuildOutput.DefaultSlot)
            .Select(p => p.Path)
            .ToList();
    }

    public UnapplyResult Uninstall() => new Installer(RequireClient().DataDir).Unapply();

    // ---- profiles ---------------------------------------------------------------------------

    private string ProfilePath(string dataDir) =>
        Path.Combine(_profilesDir, Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(dataDir)))[..16] + ".json");

    private void SaveProfile()
    {
        if (Client is null)
        {
            return;
        }
        Directory.CreateDirectory(_profilesDir);
        var profile = new Profile(1, Client.DataDir, Sources.Select(s => new ProfileSource(s.Path, s.Mod.Id)).ToList(),
            new Dictionary<string, string>(Answers));
        File.WriteAllBytes(ProfilePath(Client.DataDir), JsonSerializer.SerializeToUtf8Bytes(profile, ProfileJson));
    }

    private List<string> LoadProfile()
    {
        var warnings = new List<string>();
        var path = ProfilePath(Client!.DataDir);
        if (!File.Exists(path))
        {
            return warnings;
        }
        Profile? profile;
        try
        {
            profile = JsonSerializer.Deserialize<Profile>(File.ReadAllBytes(path), ProfileJson);
        }
        catch (JsonException)
        {
            return ["The saved settings for this client could not be read; starting fresh"];
        }
        if (profile is null)
        {
            return warnings;
        }
        foreach (var (k, v) in profile.Answers)
        {
            Answers[k] = v;
        }
        foreach (var source in profile.Sources)
        {
            ModManifest? stored = null;
            try
            {
                stored = _store.Get(source.ModId);
            }
            catch (KeyNotFoundException)
            {
            }
            if (stored is not null)
            {
                // Use the stored copy: the original may have been renamed by Install (or changed since).
                if (Sources.All(s => s.Mod.Id != stored.Id))
                {
                    Sources.Add(new SourceEntry(source.Path, stored));
                }
            }
            else if (File.Exists(source.Path) || Directory.Exists(source.Path))
            {
                warnings.AddRange(AddOne(source.Path));
            }
            else
            {
                warnings.Add($"{Path.GetFileName(source.Path)} from the last session is gone (renamed or moved?)");
            }
        }
        return warnings;
    }

    private static readonly JsonSerializerOptions ProfileJson = new() { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
}
