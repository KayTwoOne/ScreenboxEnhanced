using System;
using System.Linq;
using System.Text.RegularExpressions;
using Screenbox.Core.Models;

namespace Screenbox.Core.Helpers;

/// <summary>
/// Extracts season, episode, title and specials classification from a media filename.
/// Pure logic with no I/O so the rules can be verified against a real corpus without a host.
/// </summary>
public static partial class EpisodeInfoParser
{
    private static readonly string[] SpecialFolderNames =
        ["extras", "specials", "ncop & nced", "ncop", "nced", "oad", "ova", "bonus"];

    private static readonly string[] SpecialMarkers =
        ["ncop", "nced", "oad", "ova", "special"];

    [GeneratedRegex(@"[Ss](\d{1,2})[Ee](\d{1,4})", RegexOptions.CultureInvariant)]
    private static partial Regex SeasonEpisodeRegex();

    [GeneratedRegex(@"(\d{1,2})[xX](\d{1,4})", RegexOptions.CultureInvariant)]
    private static partial Regex SeasonXEpisodeRegex();

    // A bare number only counts when a hyphen separator precedes it. Without this rule,
    // "Mob Psycho 100" parses as episode 100 and "Jujutsu Kaisen 0" as episode zero.
    // Do NOT relax this to match trailing digits generally - two tests hold this rule in place.
    [GeneratedRegex(@"-\s*(\d{1,4})(?:[vV](\d+))?(?:\s|$|\.)", RegexOptions.CultureInvariant)]
    private static partial Regex SeparatedEpisodeRegex();

    /// <summary>
    /// Parses a filename into structured episode information.
    /// </summary>
    /// <param name="fileName">File name, with or without extension.</param>
    /// <param name="parentFolderName">
    /// Immediate parent folder name. A file inside a specials folder is a special regardless
    /// of its own name.
    /// </param>
    public static EpisodeInfo Parse(string fileName, string? parentFolderName = null)
    {
        if (string.IsNullOrWhiteSpace(fileName))
        {
            return new EpisodeInfo(null, null, null, null, false, false);
        }

        string name = StripExtension(fileName);
        name = StripGroupPrefix(name);
        string normalized = Normalize(name);

        bool isSpecial = IsSpecialFolder(parentFolderName) || HasSpecialMarker(normalized);

        // Episode-bearing text is evaluated against the tag-stripped form, but the hyphen
        // separator rule below means a bracketed CRC (which is never preceded by a bare
        // hyphen) can never be misread as an episode number regardless of strip order.
        string withoutTags = StripTrailingTags(normalized);

        int? season = null;
        int? episode = null;
        int? version = null;

        Match sxe = SeasonEpisodeRegex().Match(withoutTags);
        Match sxxe = SeasonXEpisodeRegex().Match(withoutTags);
        Match sep = SeparatedEpisodeRegex().Match(withoutTags);

        if (sxe.Success)
        {
            season = int.Parse(sxe.Groups[1].Value);
            episode = int.Parse(sxe.Groups[2].Value);
        }
        else if (sxxe.Success)
        {
            season = int.Parse(sxxe.Groups[1].Value);
            episode = int.Parse(sxxe.Groups[2].Value);
        }
        else if (sep.Success)
        {
            episode = int.Parse(sep.Groups[1].Value);
            if (sep.Groups[2].Success) version = int.Parse(sep.Groups[2].Value);
        }

        string? title = ExtractTitle(withoutTags);
        bool isMovie = episode is null && !isSpecial;

        return new EpisodeInfo(season, episode, title, version, isSpecial, isMovie);
    }

    private static string StripExtension(string fileName)
    {
        int dot = fileName.LastIndexOf('.');
        return dot > 0 ? fileName[..dot] : fileName;
    }

    private static string StripGroupPrefix(string name)
    {
        // "[sam] Chainsaw Man - 02" and "[DB]Kimi no Na wa." both start with a release group.
        name = name.TrimStart();
        if (!name.StartsWith('[')) return name;
        int close = name.IndexOf(']');
        return close < 0 ? name : name[(close + 1)..].TrimStart();
    }

    private static string Normalize(string name) =>
        name.Replace('_', ' ')
            .Replace('–', '-')   // en-dash
            .Replace('—', '-')   // em-dash
            .Trim();

    private static string StripTrailingTags(string name)
    {
        // Remove bracketed and parenthesised tag groups: resolutions, codecs, CRC hashes.
        string stripped = Regex.Replace(name, @"\[[^\]]*\]", " ", RegexOptions.CultureInvariant);
        stripped = Regex.Replace(stripped, @"\([^\)]*\)", " ", RegexOptions.CultureInvariant);
        return Regex.Replace(stripped, @"\s+", " ", RegexOptions.CultureInvariant).Trim();
    }

    private static bool IsSpecialFolder(string? folderName) =>
        folderName is { Length: > 0 } &&
        SpecialFolderNames.Contains(folderName.Trim().ToLowerInvariant());

    private static bool HasSpecialMarker(string name)
    {
        string lower = name.ToLowerInvariant();
        return SpecialMarkers.Any(marker =>
            Regex.IsMatch(lower, $@"\b{Regex.Escape(marker)}\b", RegexOptions.CultureInvariant));
    }

    private static string? ExtractTitle(string name)
    {
        // Extract title from everything after the episode number.
        // "Jujutsu Kaisen - 001 - Ryoumen Sukuna" → "Ryoumen Sukuna"
        // "Jujutsu Kaisen - 011 - Narrow-minded" → "Narrow-minded" (not "minded")
        // "Jujutsu Kaisen - 005 - Curse Womb Must Die -II-" → "Curse Womb Must Die -II-"

        Match sep = SeparatedEpisodeRegex().Match(name);
        if (!sep.Success) return null;

        // Everything after the episode number (and optional version)
        int afterEpisode = sep.Groups[0].Index + sep.Groups[0].Length;
        if (afterEpisode >= name.Length) return null;

        string titleCandidate = name[afterEpisode..].Trim();

        // Remove leading title separator hyphen (e.g., " - Narrow-minded" → "Narrow-minded")
        while (titleCandidate.StartsWith("-"))
        {
            titleCandidate = titleCandidate[1..].Trim();
        }

        if (titleCandidate.Length == 0)
            return null;

        // Reject if it's only digits, hyphens, and whitespace—no actual text content.
        // This filters out pure disambiguators like "-2" while preserving real titles
        // that contain hyphens, digits, or Roman numerals.
        if (titleCandidate.All(c => char.IsDigit(c) || c == '-' || char.IsWhiteSpace(c)))
            return null;

        return titleCandidate;
    }
}
