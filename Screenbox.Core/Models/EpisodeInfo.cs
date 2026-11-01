namespace Screenbox.Core.Models;

/// <summary>
/// Structured information extracted from a media filename.
/// </summary>
/// <param name="Season">Season number, or null when the name carries none.</param>
/// <param name="Episode">Episode number, or null for movies and unparsable names.</param>
/// <param name="Title">Episode title when the name carries one.</param>
/// <param name="Version">Release version from a "v2" suffix, or null.</param>
/// <param name="IsSpecial">True for NCOP, NCED, OAD, OVA and anything under a specials folder.</param>
/// <param name="IsMovie">True when no episode number was found and the item is not a special.</param>
public readonly record struct EpisodeInfo(
    int? Season,
    int? Episode,
    string? Title,
    int? Version,
    bool IsSpecial,
    bool IsMovie);
