using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace Screenbox.Core.Models.Serialization;

/// <summary>Request envelope for AniList's GraphQL endpoint: a query document plus its variables.</summary>
public sealed class AniListRequest
{
    [JsonPropertyName("query")]
    public string Query { get; set; } = string.Empty;

    [JsonPropertyName("variables")]
    public AniListVariables Variables { get; set; } = new();
}

public sealed class AniListVariables
{
    [JsonPropertyName("q")]
    public string Q { get; set; } = string.Empty;
}

/// <summary>
/// Response envelope for AniList's GraphQL endpoint. AniList signals a failed query with an
/// HTTP 200 status and a populated <see cref="Errors"/> array rather than a non-2xx status, so
/// callers must check both <see cref="Errors"/> and a null <see cref="Data"/> independently of
/// the HTTP status code.
/// </summary>
public sealed class AniListResponse
{
    [JsonPropertyName("data")]
    public AniListData? Data { get; set; }

    [JsonPropertyName("errors")]
    public List<AniListError>? Errors { get; set; }
}

public sealed class AniListError
{
    [JsonPropertyName("message")]
    public string? Message { get; set; }
}

public sealed class AniListData
{
    [JsonPropertyName("Page")]
    public AniListPage? Page { get; set; }
}

public sealed class AniListPage
{
    [JsonPropertyName("media")]
    public List<AniListMedia>? Media { get; set; }
}

public sealed class AniListMedia
{
    [JsonPropertyName("id")]
    public int Id { get; set; }

    [JsonPropertyName("title")]
    public AniListTitle? Title { get; set; }

    [JsonPropertyName("description")]
    public string? Description { get; set; }

    [JsonPropertyName("startDate")]
    public AniListFuzzyDate? StartDate { get; set; }

    [JsonPropertyName("coverImage")]
    public AniListCoverImage? CoverImage { get; set; }
}

public sealed class AniListTitle
{
    [JsonPropertyName("romaji")]
    public string? Romaji { get; set; }

    [JsonPropertyName("english")]
    public string? English { get; set; }
}

public sealed class AniListFuzzyDate
{
    [JsonPropertyName("year")]
    public int? Year { get; set; }
}

public sealed class AniListCoverImage
{
    [JsonPropertyName("large")]
    public string? Large { get; set; }
}
