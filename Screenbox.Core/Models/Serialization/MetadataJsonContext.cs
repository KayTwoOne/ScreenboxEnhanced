using System.Text.Json.Serialization;

namespace Screenbox.Core.Models.Serialization;

/// <summary>
/// Source-generated JSON contract for online metadata provider models (AniList, Jikan, TMDB,
/// etc.). Kept separate from <see cref="CoreJsonContext"/>, which is marked
/// <c>[Obsolete]</c> pending removal - new provider DTOs must not be attached to a class that is
/// scheduled for deletion.
/// </summary>
[JsonSourceGenerationOptions(GenerationMode = JsonSourceGenerationMode.Metadata)]
[JsonSerializable(typeof(AniListRequest))]
[JsonSerializable(typeof(AniListResponse))]
[JsonSerializable(typeof(JikanResponse))]
[JsonSerializable(typeof(TmdbSearchResponse))]
public sealed partial class MetadataJsonContext : JsonSerializerContext
{
}
