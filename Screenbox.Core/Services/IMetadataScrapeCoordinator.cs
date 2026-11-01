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
    /// disabled, the folder is a specials folder, the folder yields no searchable query, or no
    /// provider candidate clears the acceptance threshold - a confident wrong match is worse than
    /// no match, so a weak result is reported as no match rather than guessed at.
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
    /// The folder's own name. A specials folder (e.g. "Extras", "NCOP &amp; NCED") is not part of any
    /// series and is not scraped at all: no provider is consulted and nothing is recorded. A bare
    /// season folder (e.g. "Season 1") normalizes to an empty search query, in which case the parent
    /// folder's name - derived from <paramref name="folderPath"/> - is searched instead. That
    /// fallback is for searching only: the season folder keeps its own name and may receive the
    /// series poster, nothing more.
    /// </param>
    Task<MetadataMatch?> ScrapeFolderAsync(string folderPath, string folderName, CancellationToken ct);

    /// <summary>
    /// Scrapes every known library folder, reporting completion percentage (0-100) as it goes. A
    /// no-op that immediately reports completion when the feature is disabled. A failure scraping
    /// one folder is logged and does not abort the scan of the rest.
    /// <para>
    /// This is the scan the user starts explicitly, and it REFRESHES: every eligible folder is
    /// searched again and its poster downloaded again, ignoring any result already in the scrape
    /// cache. The cache records which title matched but not the poster's URL, so trusting it would
    /// mean artwork scraped once - at whatever resolution a provider then served - could never be
    /// replaced. The rules that keep a scrape safe are unchanged: nothing runs until the feature is
    /// enabled, specials folders are skipped entirely, a season folder is searched under its parent
    /// and receives the poster but never a title, a <see cref="Enums.PosterSource.Manual"/> poster
    /// is never replaced, and no title is ever written. A refresh that finds nothing keeps the
    /// earlier cached match, since an empty answer may only mean the provider was unreachable.
    /// </para>
    /// <para>
    /// A short, fixed delay is inserted between folders that actually reach a provider over the
    /// network, to stay under the providers' documented rate limits - which, as nothing is served
    /// from the cache here, is every folder except those turned away before a search (specials
    /// folders, folders with no searchable name). The delay honours <paramref name="ct"/>, so a
    /// cancelled scan stops promptly rather than waiting it out.
    /// </para>
    /// </summary>
    Task ScrapeLibraryAsync(IProgress<int>? progress, CancellationToken ct);
}
