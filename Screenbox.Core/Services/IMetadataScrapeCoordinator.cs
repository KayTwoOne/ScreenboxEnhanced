using System;
using System.Threading;
using System.Threading.Tasks;
using Screenbox.Core.Models;

namespace Screenbox.Core.Services;

/// <summary>
/// Coordinates opt-in online metadata scraping for library folders against
/// <see cref="IMetadataProvider"/> implementations obtained through
/// <see cref="IMetadataProviderFactory"/>. Every entry point checks
/// <see cref="ISettingsService.MetadataEnabled"/> before doing anything else - including before
/// asking the factory for a single provider - so nothing network-capable is even constructed while
/// the feature is off. That check is the basis on which the user accepted this feature: a folder
/// name is their data, and nothing about it leaves the device until they opt in.
/// </summary>
public interface IMetadataScrapeCoordinator
{
    /// <summary>
    /// Scrapes online metadata for a single library folder, using a cached result when one exists
    /// instead of calling a provider again. Returns <see langword="null"/> when the feature is
    /// disabled, the folder yields no searchable query, or no provider candidate clears the
    /// acceptance threshold - a confident wrong match is worse than no match, so a weak result is
    /// reported as no match rather than guessed at.
    /// <para>
    /// The only thing a scrape ever applies to a folder is its poster: when a fresh (uncached) match
    /// carries a poster URL, this downloads and stores it as the folder's artwork, subject to
    /// <see cref="IArtworkService"/>'s own guarantee that a <see cref="Enums.PosterSource.Manual"/>
    /// poster is never displaced. A scrape NEVER writes the folder's title: the custom title is
    /// user data, indistinguishable from one the user typed, and the folder names in a library are
    /// already the names the user chose. The matched synopsis and year are kept in the scrape cache;
    /// the provider's title is not persisted.
    /// </para>
    /// </summary>
    /// <param name="folderPath">Absolute path of the folder; the cache and metadata key.</param>
    /// <param name="folderName">
    /// The folder's own name. A bare organizational folder (e.g. "Season 1") normalizes to an empty
    /// search query, in which case the parent folder's name - derived from
    /// <paramref name="folderPath"/> - is searched instead. That fallback is for searching only:
    /// the folder keeps its own name and may receive the series poster, nothing more.
    /// </param>
    Task<MetadataMatch?> ScrapeFolderAsync(string folderPath, string folderName, CancellationToken ct);

    /// <summary>
    /// Scrapes every known library folder, reporting completion percentage (0-100) as it goes. A
    /// no-op that immediately reports completion when the feature is disabled. A failure scraping
    /// one folder is logged and does not abort the scan of the rest.
    /// <para>
    /// A short, fixed delay is inserted between folders that actually reach a provider over the
    /// network, to stay under the providers' documented rate limits during a first scan of a fresh
    /// cache. A folder that short-circuits from the scrape cache costs no request and is never
    /// delayed, so a rescan of an already-scraped library runs at full speed. The delay honours
    /// <paramref name="ct"/>, so a cancelled scan stops promptly rather than waiting it out.
    /// </para>
    /// </summary>
    Task ScrapeLibraryAsync(IProgress<int>? progress, CancellationToken ct);
}
