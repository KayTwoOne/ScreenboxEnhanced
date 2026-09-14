using Screenbox.Core.Enums;

namespace Screenbox.Core.Models;

/// <summary>
/// Durable, user-authored metadata for a single library folder.
/// </summary>
public sealed class FolderMetadataDto
{
    /// <summary>Absolute path of the folder. Primary key.</summary>
    public string Path { get; set; } = string.Empty;

    /// <summary>Display title overriding the folder name, or null to use the folder name.</summary>
    public string? CustomTitle { get; set; }

    /// <summary>File name within the local Artwork folder. Never an absolute path.</summary>
    public string? PosterFile { get; set; }

    /// <summary>Where <see cref="PosterFile"/> came from.</summary>
    public PosterSource PosterSource { get; set; } = PosterSource.None;

    /// <summary>Confirmed external provider match, for example "anilist:171018".</summary>
    public string? ProviderPin { get; set; }

    /// <summary>Optional manual ordering position.</summary>
    public int? SortOrder { get; set; }
}
