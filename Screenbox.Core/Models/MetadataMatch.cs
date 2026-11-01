namespace Screenbox.Core.Models;

/// <summary>
/// A single candidate result returned by an <see cref="Screenbox.Core.Services.IMetadataProvider"/>
/// search. Pure data - no I/O, no provider-specific state.
/// </summary>
/// <param name="ProviderId">Provider-scoped identifier for the matched item, e.g. an AniList media id.</param>
/// <param name="Title">Canonical title as reported by the provider.</param>
/// <param name="Synopsis">Plot summary, when the provider has one.</param>
/// <param name="Year">Release year, when known.</param>
/// <param name="PosterUrl">Remote URL of the provider's poster art, when it has one.</param>
/// <param name="Confidence">
/// Match confidence in the range [0, 1], assigned by the scrape coordinator - never by a provider.
/// <para>
/// Providers leave this at its default of 0, which means "not yet scored" rather than "certainly
/// wrong". The three catalogs have no comparable notion of relevance - AniList and Jikan return
/// ranked results but no score, and TMDB returns a popularity figure that measures how trendy a
/// title is rather than how well it matches the query - so a provider-set value would mean three
/// different things and could not be ranked across providers. The coordinator instead scores every
/// candidate itself, comparing the provider's title against the normalized query with one
/// deterministic measure applied identically to all of them.
/// </para>
/// </param>
public sealed record MetadataMatch(
    string ProviderId,
    string Title,
    string? Synopsis,
    int? Year,
    string? PosterUrl,
    double Confidence = 0);
