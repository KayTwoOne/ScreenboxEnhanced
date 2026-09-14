using System.Threading.Tasks;
using Windows.Storage;

namespace Screenbox.Core.Services;

/// <summary>
/// Resolves and produces poster artwork for library folders.
/// </summary>
public interface IArtworkService
{
    /// <summary>
    /// Returns the poster file name within the local Artwork folder for the given folder,
    /// generating one from a video frame when necessary. Returns null when no artwork
    /// could be produced, in which case the caller should fall back to the folder glyph.
    /// </summary>
    Task<string?> GetPosterFileNameAsync(StorageFolder folder);

    /// <summary>
    /// Copies the chosen image into local storage and records it as the manual poster.
    /// Returns the stored file name.
    /// </summary>
    Task<string?> SetManualPosterAsync(StorageFolder folder, StorageFile image);

    /// <summary>
    /// Deletes generated and scraped artwork from local storage. Manual posters are preserved.
    /// </summary>
    Task ClearGeneratedArtworkAsync();
}
