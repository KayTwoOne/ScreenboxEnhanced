using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace Screenbox.Core.Models.Serialization;

/// <summary>Response envelope for TMDB's <c>/search/tv</c> endpoint.</summary>
public sealed class TmdbSearchResponse
{
    [JsonPropertyName("results")]
    public List<TmdbTvResult>? Results { get; set; }
}

public sealed class TmdbTvResult
{
    [JsonPropertyName("id")]
    public int Id { get; set; }

    /// <summary>The title field for the TV endpoint. NOT "title" - that belongs to the movie endpoint.</summary>
    [JsonPropertyName("name")]
    public string? Name { get; set; }

    [JsonPropertyName("overview")]
    public string? Overview { get; set; }

    /// <summary>
    /// A "YYYY-MM-DD" string that can be an empty string. Never <c>int.Parse</c> this raw - parse
    /// defensively to a nullable year.
    /// </summary>
    [JsonPropertyName("first_air_date")]
    public string? FirstAirDate { get; set; }

    /// <summary>Nullable, and a PATH rather than a URL - prefix with TMDB's image base URL.</summary>
    [JsonPropertyName("poster_path")]
    public string? PosterPath { get; set; }
}
