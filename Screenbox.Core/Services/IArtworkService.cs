using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Screenbox.Core.Enums;
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
    /// Returns the file name within the local Artwork folder that should be DRAWN for a poster
    /// returned by <see cref="GetPosterFileNameAsync"/>. A portrait-ish poster (width under 1.4x its
    /// height) comes back as a derived wide card: the whole image, uncropped, centred on a blurred,
    /// darkened wash of its own colours, sized so a wide folder tile does not centre-crop it to a
    /// thin band. Any other image comes back unchanged. The card is cached beside the poster and
    /// rebuilt when the poster is newer than it; the poster itself is never altered, so it stays
    /// available at full size for anything that wants the whole portrait.
    /// <para>
    /// Never throws and never returns null for a non-empty name: when a card cannot be built, for
    /// any reason, the original name comes back, so artwork never disappears over it.
    /// </para>
    /// </summary>
    /// <param name="posterFileName">A poster file name as <see cref="GetPosterFileNameAsync"/> returned it.</param>
    Task<string?> GetDisplayFileNameAsync(string? posterFileName);

    /// <summary>
    /// Copies the chosen image into local storage and records it as the manual poster.
    /// Returns the stored file name.
    /// </summary>
    Task<string?> SetManualPosterAsync(StorageFolder folder, StorageFile image);

    /// <summary>
    /// Fetches the image behind a link the user pasted and, when it is a real image, saves it to a
    /// temporary file that <see cref="SetManualPosterAsync"/> accepts like any file the user picked.
    /// The link may point straight at an image, or at a web page, in which case the one image the
    /// page names as its Open Graph (or Twitter card) image is fetched instead. Nothing further is
    /// followed from that image, and nothing but https is ever requested.
    /// <para>
    /// Every way a link can be unusable comes back as an <see cref="ImageLinkFailure"/> rather than
    /// an exception: the address is not https, the answer is not a decodable image, it is too
    /// large, the page names no image, the download failed, or it took too long.
    /// </para>
    /// </summary>
    /// <param name="url">The link as pasted. Surrounding whitespace is ignored.</param>
    /// <param name="ct">Cancels the fetch, for example when the user keeps typing.</param>
    /// <exception cref="System.OperationCanceledException">
    /// <paramref name="ct"/> was cancelled. A timeout is not this: it comes back as
    /// <see cref="ImageLinkFailure.TimedOut"/>.
    /// </exception>
    Task<ImageLinkResult> FetchImageFromLinkAsync(string url, CancellationToken ct);

    /// <summary>
    /// Removes a folder's manual poster, deleting its artwork file, so the folder can return to
    /// automatic, convention and online artwork. A manual poster outranks all of those, and online
    /// metadata deliberately never overwrites it, so without this a folder that was ever given an
    /// image could never get anything else. Does nothing, and deletes nothing, when the folder's
    /// poster is not a manual one. Also forgets that the folder was once searched without finding
    /// any artwork, so artwork is resolved again the next time the folder is shown.
    /// </summary>
    /// <param name="folderPath">Absolute folder path; the row whose manual poster is removed.</param>
    /// <returns>True when a manual poster was removed; false when there was none to remove.</returns>
    Task<bool> RemoveManualPosterAsync(string folderPath);

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
