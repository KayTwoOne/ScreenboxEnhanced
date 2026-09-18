using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Screenbox.Core.Models;
using Windows.Storage;

namespace Screenbox.Core.Services;

/// <summary>
/// Resolves and produces poster artwork for library folders.
/// </summary>
public interface IArtworkService
{
    /// <summary>
    /// Returns the poster file name within the local Artwork folder for the given folder,
    /// generating one from a video frame when necessary. Returns null when no artwork can be
    /// displayed, in which case the caller should fall back to the folder glyph - including when a
    /// recorded poster file has gone missing, which leaves the stored choice untouched.
    /// </summary>
    /// <param name="folder">The folder to resolve artwork for.</param>
    /// <param name="metadata">
    /// The folder's stored metadata as the caller already read it, or null when the folder has no
    /// row. Passing it avoids a second database read on a path that runs per item, per scroll.
    /// </param>
    Task<string?> GetPosterFileNameAsync(StorageFolder folder, FolderMetadataDto? metadata);

    /// <summary>
    /// Copies the chosen image into local storage and records it as the manual poster.
    /// Returns the stored file name.
    /// </summary>
    Task<string?> SetManualPosterAsync(StorageFolder folder, StorageFile image);

    /// <summary>
    /// Evicts stored folder metadata, and the artwork files it references, for folders that have
    /// disappeared from beneath the supplied library roots. Folders that still exist keep their
    /// titles and posters, manual choices included.
    /// </summary>
    /// <param name="libraryFolderPaths">Absolute paths of the library roots that were just scanned.</param>
    Task PruneOrphanedArtworkAsync(IReadOnlyList<string> libraryFolderPaths);

    /// <summary>
    /// Downloads a scraped poster from a provider's remote URL and stores it as the folder's
    /// poster, recording <see cref="Enums.PosterSource.Scraped"/>. Refuses when the folder's
    /// existing poster source is <see cref="Enums.PosterSource.Manual"/> - a manual poster is
    /// user-authored and a scrape must never be able to replace it. A failed, cancelled, or
    /// incomplete download never throws and never touches the folder's existing poster.
    /// </summary>
    /// <param name="folderPath">Absolute folder path; the row this poster is recorded against.</param>
    /// <param name="posterUrl">Remote URL of the poster image, as returned by a provider match.</param>
    /// <returns>True when the poster was downloaded and recorded; false otherwise.</returns>
    Task<bool> SetScrapedPosterAsync(string folderPath, string posterUrl, CancellationToken ct);
}
