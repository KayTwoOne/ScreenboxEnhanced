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
/// <param name="Confidence">Provider- or caller-computed match confidence in the range [0, 1].</param>
public sealed record MetadataMatch(
    string ProviderId,
    string Title,
    string? Synopsis,
    int? Year,
    string? PosterUrl,
    double Confidence);
