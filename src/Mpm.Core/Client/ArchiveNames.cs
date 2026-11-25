using System.Text.RegularExpressions;

namespace Mpm.Core.Client;

/// <summary>
/// 3.3.5a archive naming, taken from the name patterns embedded in Wow.exe (build 12340):
/// root "patch-?.MPQ" in Data\, locale "patch-%s-?.MPQ" in Data\&lt;locale&gt;\ (one character each).
/// </summary>
public static partial class ArchiveNames
{
    public const string Root = "root";

    /// <summary>Stock root archives, lowest priority first.</summary>
    public static readonly string[] StockRoot =
        ["common.MPQ", "common-2.MPQ", "expansion.MPQ", "lichking.MPQ", "patch.MPQ", "patch-2.MPQ", "patch-3.MPQ"];

    /// <summary>Stock locale archives for a locale, lowest priority first.</summary>
    public static string[] StockLocale(string locale) =>
    [
        $"locale-{locale}.MPQ", $"speech-{locale}.MPQ",
        $"expansion-locale-{locale}.MPQ", $"expansion-speech-{locale}.MPQ",
        $"lichking-locale-{locale}.MPQ", $"lichking-speech-{locale}.MPQ",
        $"patch-{locale}.MPQ", $"patch-{locale}-2.MPQ", $"patch-{locale}-3.MPQ",
    ];

    /// <summary>Locales 3.3.5a clients shipped in.</summary>
    public static readonly IReadOnlySet<string> KnownLocales = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "enUS", "enGB", "deDE", "frFR", "esES", "esMX", "ruRU", "koKR", "zhCN", "zhTW", "enCN", "enTW", "ptBR", "ptPT", "itIT",
    };

    [GeneratedRegex(@"^patch-(?<c>[^-.])\.mpq$", RegexOptions.IgnoreCase)]
    private static partial Regex RootPatchRegex();

    [GeneratedRegex(@"^patch-(?<loc>[a-z]{2}[a-z]{2})(-(?<c>[^-.]))?\.mpq$", RegexOptions.IgnoreCase)]
    private static partial Regex LocalePatchRegex();

    /// <summary>True for a known locale code in any casing ("enUS", "enus").</summary>
    public static bool IsLocale(string name) => KnownLocales.Contains(name);

    /// <summary>Canonical locale casing ("enus" -> "enUS").</summary>
    public static string CanonicalLocale(string locale) =>
        locale.Length == 4 ? locale[..2].ToLowerInvariant() + locale[2..].ToUpperInvariant() : locale;

    /// <summary>
    /// Classifies a patch archive file name. Returns null when the name is not one the client loads as a patch.
    /// Chain is <see cref="Root"/> or a locale code; Slot is the single priority character ("" for patch.MPQ / patch-xxXX.MPQ).
    /// </summary>
    public static (string Chain, string Slot)? ClassifyPatch(string fileName)
    {
        if (fileName.Equals("patch.MPQ", StringComparison.OrdinalIgnoreCase))
        {
            return (Root, "");
        }
        var root = RootPatchRegex().Match(fileName);
        if (root.Success)
        {
            return (Root, root.Groups["c"].Value.ToUpperInvariant());
        }
        var loc = LocalePatchRegex().Match(fileName);
        if (loc.Success && IsLocale(loc.Groups["loc"].Value))
        {
            return (CanonicalLocale(loc.Groups["loc"].Value), loc.Groups["c"].Success ? loc.Groups["c"].Value.ToUpperInvariant() : "");
        }
        return null;
    }

    /// <summary>True for the stock patch slots ("", "2", "3"); anything else is a custom patch.</summary>
    public static bool IsStockSlot(string slot) => slot is "" or "2" or "3";

    /// <summary>
    /// Ordering key for custom patch slots. ASCII order (digits before letters) is assumed;
    /// the exact order the client uses for "?" matches is not yet verified.
    /// </summary>
    public static int SlotOrder(string slot) => slot.Length == 0 ? -1 : char.ToUpperInvariant(slot[0]);

    /// <summary>Top-level folders seen in stock 3.3.5a archives (lower case).</summary>
    public static readonly HashSet<string> KnownTopLevel = new(StringComparer.OrdinalIgnoreCase)
    {
        "world", "item", "textures", "character", "creature", "sound", "dungeons", "spells", "spell", "tileset",
        "shaders", "environments", "_shaders", "xtextures", "particles", "cameras", "interiors", "interface",
        "dbfilesclient", "fonts",
    };
}
