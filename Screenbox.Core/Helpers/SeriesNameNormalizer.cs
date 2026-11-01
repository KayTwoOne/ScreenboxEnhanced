using System.Linq;
using System.Text.RegularExpressions;

namespace Screenbox.Core.Helpers;

/// <summary>
/// Turns a library folder name into a search query for an online metadata provider.
/// Folder names are not clean titles: they carry season suffixes, get organized into
/// specials subfolders, and sometimes end in digits that are part of the title itself.
/// A bad query produces a confident wrong match, which is worse than no match at all -
/// this type exists to keep that from happening.
/// </summary>
public static partial class SeriesNameNormalizer
{
    // Folders that are organizational, not a series in their own right. Searching a provider
    // for one of these would return nothing useful (or something actively misleading), and the
    // parent series' result must not be applied to them either: they are not scraped at all.
    // This is the one list of them - EpisodeInfoParser classifies specials through it too.
    private static readonly string[] NonSeriesFolderNames =
        ["extras", "specials", "ncop & nced", "ncop", "nced", "oad", "ova", "bonus"];

    // Strips a trailing "Season N" or "Nth Season" suffix. The (?:^|\s+) alternation lets this
    // match both "Attack on Titan Season 1" (suffix after a space) and a bare "Season 1" folder
    // (suffix is the whole string, so it can only be preceded by the start of the string).
    //
    // Do NOT broaden this to strip trailing digits generally. "Mob Psycho 100" and
    // "Jujutsu Kaisen 0" are real folder names in this library whose trailing numbers are part
    // of the title - EpisodeInfoParser has the same trap and the same rule. This regex only
    // fires when the literal word "Season" is present, so it never touches either of them.
    [GeneratedRegex(@"(?:^|\s+)(?:Season\s+\d+|\d+(?:st|nd|rd|th)\s+Season)\s*$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex SeasonSuffixRegex();

    [GeneratedRegex(@"\s+", RegexOptions.CultureInvariant)]
    private static partial Regex WhitespaceRegex();

    /// <summary>
    /// Normalizes a folder name into a search query. Returns an empty string when the folder
    /// is not itself a searchable series - a bare season folder or a specials folder. The two
    /// look identical here but mean different things: a bare season folder belongs to the series
    /// named by its parent, so a caller may search under the parent's name, whereas a specials
    /// folder (<see cref="IsNonSeriesFolder"/>) is not part of any series and must not be
    /// searched at all.
    /// </summary>
    public static string Normalize(string folderName)
    {
        if (string.IsNullOrWhiteSpace(folderName))
        {
            return string.Empty;
        }

        string collapsed = CollapseWhitespace(folderName);

        if (IsNonSeriesName(collapsed))
        {
            return string.Empty;
        }

        return SeasonSuffixRegex().Replace(collapsed, string.Empty).Trim();
    }

    /// <summary>
    /// True when the folder is organizational rather than a series or a season of one: a specials
    /// folder such as <c>Extras</c> or <c>NCOP &amp; NCED</c>. <see cref="Normalize"/> returns an
    /// empty string for these, exactly as it does for a bare <c>Season 1</c> folder, so an empty
    /// query cannot distinguish them - this can. Matching ignores case and stray whitespace.
    /// </summary>
    public static bool IsNonSeriesFolder(string? folderName) =>
        !string.IsNullOrWhiteSpace(folderName) && IsNonSeriesName(CollapseWhitespace(folderName));

    private static string CollapseWhitespace(string name) =>
        WhitespaceRegex().Replace(name.Trim(), " ");

    private static bool IsNonSeriesName(string collapsedName) =>
        NonSeriesFolderNames.Contains(collapsedName.ToLowerInvariant());
}
