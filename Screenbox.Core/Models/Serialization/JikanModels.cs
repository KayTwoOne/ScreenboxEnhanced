using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace Screenbox.Core.Models.Serialization;

/// <summary>Response envelope for Jikan v4's anime search endpoint.</summary>
public sealed class JikanResponse
{
    [JsonPropertyName("data")]
    public List<JikanAnime>? Data { get; set; }
}

public sealed class JikanAnime
{
    [JsonPropertyName("mal_id")]
    public int MalId { get; set; }

    [JsonPropertyName("title")]
    public string? Title { get; set; }

    /// <summary>Nullable - fall back to <see cref="Title"/> when absent.</summary>
    [JsonPropertyName("title_english")]
    public string? TitleEnglish { get; set; }

    [JsonPropertyName("synopsis")]
    public string? Synopsis { get; set; }

    /// <summary>Nullable release year.</summary>
    [JsonPropertyName("year")]
    public int? Year { get; set; }

    [JsonPropertyName("images")]
    public JikanImages? Images { get; set; }
}

public sealed class JikanImages
{
    [JsonPropertyName("jpg")]
    public JikanImageSet? Jpg { get; set; }
}

/// <summary>
/// Poster art at three sizes. <see cref="LargeImageUrl"/> is nested two levels deep from the
/// anime entry (<c>images.jpg.large_image_url</c>).
/// </summary>
public sealed class JikanImageSet
{
    [JsonPropertyName("image_url")]
    public string? ImageUrl { get; set; }

    [JsonPropertyName("small_image_url")]
    public string? SmallImageUrl { get; set; }

    [JsonPropertyName("large_image_url")]
    public string? LargeImageUrl { get; set; }
}
