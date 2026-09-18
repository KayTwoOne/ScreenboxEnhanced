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
    /// </summary>
    /// <param name="folderPath">Absolute path of the folder; the cache and metadata key.</param>
    /// <param name="folderName">
    /// The folder's own name. A bare organizational folder (e.g. "Season 1") normalizes to an empty
    /// search query, in which case the parent folder's name - derived from
    /// <paramref name="folderPath"/> - is used instead.
    /// </param>
    Task<MetadataMatch?> ScrapeFolderAsync(string folderPath, string folderName, CancellationToken ct);

    /// <summary>
    /// Scrapes every known library folder, reporting completion percentage (0-100) as it goes. A
    /// no-op that immediately reports completion when the feature is disabled. A failure scraping
    /// one folder is logged and does not abort the scan of the rest.
    /// </summary>
    Task ScrapeLibraryAsync(IProgress<int>? progress, CancellationToken ct);
}
