using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Screenbox.Core.Helpers;
using Screenbox.Core.Models;
using Screenbox.Core.Services;

namespace Screenbox.Core.Coordinators;

/// <inheritdoc cref="IMetadataScrapeCoordinator"/>
public sealed class MetadataScrapeCoordinator : IMetadataScrapeCoordinator
{
    // AniList documents 90 requests/minute plus a separate, stricter short-burst limiter; Jikan
    // documents 60 requests/minute. 60/min is the stricter of the two named limits (1 request/sec
    // average); this delay is deliberately more conservative than that floor - 1.5s between folders
    // is 40/min - to leave headroom under AniList's burst limiter as well, since a fresh-cache scan
    // issues one request per folder back-to-back. This is intentionally a plain fixed delay, not a
    // backoff/retry policy - see the phase-5 plan's "out of scope" section.
    private static readonly TimeSpan DefaultInterFolderDelay = TimeSpan.FromMilliseconds(1500);

    private readonly IMetadataProviderFactory _providerFactory;
    private readonly ISettingsService _settingsService;
    private readonly IDatabaseService _databaseService;
    private readonly IArtworkService _artworkService;
    private readonly ILogger<MetadataScrapeCoordinator> _logger;
    private readonly TimeSpan _interFolderDelay;

    public MetadataScrapeCoordinator(
        IMetadataProviderFactory providerFactory,
        ISettingsService settingsService,
        IDatabaseService databaseService,
        IArtworkService artworkService,
        ILogger<MetadataScrapeCoordinator> logger,
        TimeSpan? interFolderDelay = null)
    {
        _providerFactory = providerFactory;
        _settingsService = settingsService;
        _databaseService = databaseService;
        _artworkService = artworkService;
        _logger = logger;
        _interFolderDelay = interFolderDelay ?? DefaultInterFolderDelay;
    }

    /// <inheritdoc/>
    public async Task<MetadataMatch?> ScrapeFolderAsync(string folderPath, string folderName, CancellationToken ct)
    {
        (MetadataMatch? match, bool _) = await ScrapeFolderCoreAsync(folderPath, folderName, ct);
        return match;
    }

    /// <summary>
    /// Does the actual work behind <see cref="ScrapeFolderAsync"/>, additionally reporting whether
    /// this call reached a provider over the network (as opposed to short-circuiting from the scrape
    /// cache, the disabled gate, or an empty query) - <see cref="ScrapeLibraryAsync"/> needs that bit
    /// to know whether the inter-folder throttle applies. This is also the single place a fresh
    /// match's poster is downloaded and stored (via <see cref="IArtworkService.SetScrapedPosterAsync"/>),
    /// so both <see cref="ScrapeFolderAsync"/> and <see cref="ScrapeLibraryAsync"/> apply artwork the
    /// same way - a cached reconstruction never carries a poster URL, so that branch never reaches
    /// this step.
    /// <para>
    /// A scrape never writes the folder's title. <c>custom_title</c> is user data; the scraped
    /// synopsis and year are cache data and stay in <c>folder_scrape_cache</c>. The provider's title
    /// is not persisted at all - that table has no title column (see
    /// <see cref="BuildMatchFromCacheAsync"/>). The poster is the only thing a scrape ever applies
    /// to a folder.
    /// </para>
    /// </summary>
    private async Task<(MetadataMatch? Match, bool HitNetwork)> ScrapeFolderCoreAsync(
        string folderPath, string folderName, CancellationToken ct)
    {
        // The whole feature is opt-in. Nothing below this line may run - not the cache read, and
        // certainly not a provider - until the user has turned the master toggle on. This is the
        // single gate every path through this method passes through first.
        if (!_settingsService.MetadataEnabled)
        {
            return (null, false);
        }

        string query = ResolveQuery(folderPath, folderName);

        FolderScrapeCacheDto? cached = await _databaseService.LoadScrapeCacheAsync(folderPath);
        if (cached is not null)
        {
            // Already scraped (matched or not) - short-circuit without touching a provider, and
            // therefore without costing a request the throttle needs to account for.
            return (await BuildMatchFromCacheAsync(folderPath, query, cached), false);
        }

        if (string.IsNullOrEmpty(query))
        {
            // A bare season/specials folder whose parent yielded nothing searchable either.
            await SaveNoMatchAsync(folderPath);
            return (null, false);
        }

        (MetadataMatch? accepted, string? providerName) = await FindBestMatchAsync(query, ct);

        // Reaching FindBestMatchAsync means at least one provider request was made, whether or not
        // it produced an accepted match.
        if (accepted is null)
        {
            await SaveNoMatchAsync(folderPath);
            return (null, true);
        }

        await _databaseService.SaveScrapeCacheAsync(new FolderScrapeCacheDto
        {
            Path = folderPath,
            Provider = providerName,
            ProviderId = accepted.ProviderId,
            Synopsis = accepted.Synopsis,
            Year = accepted.Year,
            // ScrapedPoster holds a local Artwork file name, never a remote URL. Downloading and
            // storing the image is a later step (via IArtworkService) - leaving this null here
            // means "matched, artwork not yet fetched" rather than mis-recording a remote URL into
            // a column documented as a local file name.
            ScrapedPoster = null,
            LastScraped = DateTimeOffset.UtcNow
        });

        // Deliberately NO title write. folder_metadata.custom_title is durable user data - it is what
        // the Edit folder dialog writes, and nothing distinguishes a scraped value from a typed one -
        // so copying a provider's title into it would turn a lookup into a permanent rename the user
        // never asked for (and the provider's title is often oddly styled or simply the wrong show).
        // The folder names in a library are already the names the user chose.
        //
        // The only folder_metadata write a scrape causes is the poster, and that goes through
        // IArtworkService, which carries its own Manual-source guard.
        if (accepted.PosterUrl is { Length: > 0 } posterUrl)
        {
            // Guarded internally by IArtworkService: a folder whose poster source is already
            // Manual is never displaced, so this can never route around that check no matter how
            // many callers end up going through this method.
            await _artworkService.SetScrapedPosterAsync(folderPath, posterUrl, ct);
        }

        return (accepted, true);
    }

    /// <inheritdoc/>
    public async Task ScrapeLibraryAsync(IProgress<int>? progress, CancellationToken ct)
    {
        if (!_settingsService.MetadataEnabled)
        {
            progress?.Report(100);
            return;
        }

        List<FolderMetadataDto> knownFolders = await _databaseService.ListFolderMetadataAsync();
        if (knownFolders.Count == 0)
        {
            progress?.Report(100);
            return;
        }

        for (int i = 0; i < knownFolders.Count; i++)
        {
            ct.ThrowIfCancellationRequested();

            FolderMetadataDto folder = knownFolders[i];
            string folderName = GetLastSegment(folder.Path);
            bool hitNetwork = false;

            try
            {
                (_, hitNetwork) = await ScrapeFolderCoreAsync(folder.Path, folderName, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception e)
            {
                // One folder's failure (a locked path, an unexpected fault that somehow still
                // escaped a provider's own guarantee to swallow its errors) must not abort the
                // whole-library scan.
                _logger.LogError(e, "Failed to scrape metadata for '{Path}'.", folder.Path);
            }

            progress?.Report((int)((i + 1) * 100.0 / knownFolders.Count));

            bool isLastFolder = i == knownFolders.Count - 1;
            if (hitNetwork && !isLastFolder)
            {
                // Only a folder that actually made a provider request costs the throttle - a
                // cache-only rescan must run at full speed. The token is honoured here so a
                // cancelled scan stops promptly instead of sitting out the rest of the delay.
                await Task.Delay(_interFolderDelay, ct);
            }
        }
    }

    /// <summary>
    /// Tries each enabled provider in order and stops at the first one that produces an ACCEPTED
    /// match - not merely the first one that returns anything. Within a given provider's results,
    /// the candidate with the highest score is the one considered; it is only "accepted" when that
    /// score clears <see cref="MetadataMatchScorer.AcceptThreshold"/>. A provider that answers with
    /// only weak candidates (none of which clear the threshold) is treated exactly like a provider
    /// that answered with nothing at all - the search falls through to the next enabled provider
    /// rather than stopping. This is what makes a fallback provider (e.g. Jikan behind AniList)
    /// actually useful: a handful of poor primary-provider candidates must not permanently block a
    /// later provider from being consulted. A no-match is only reported once every enabled provider
    /// has been tried and none produced an accepted candidate.
    /// </summary>
    private async Task<(MetadataMatch? Match, string? ProviderName)> FindBestMatchAsync(string query, CancellationToken ct)
    {
        // Providers are only ever asked for here, after the caller has already confirmed the
        // feature is enabled - this is the call that (lazily, on first use) creates the shared
        // HttpClient behind whichever providers are enabled.
        IReadOnlyList<IMetadataProvider> providers = _providerFactory.GetEnabledProviders();

        foreach (IMetadataProvider provider in providers)
        {
            ct.ThrowIfCancellationRequested();

            IReadOnlyList<MetadataMatch> results = await provider.SearchAsync(query, ct);
            if (results.Count == 0)
            {
                continue;
            }

            MetadataMatch best = results[0];
            double bestScore = MetadataMatchScorer.Score(query, best.Title);
            for (int i = 1; i < results.Count; i++)
            {
                double score = MetadataMatchScorer.Score(query, results[i].Title);
                if (score > bestScore)
                {
                    bestScore = score;
                    best = results[i];
                }
            }

            if (bestScore >= MetadataMatchScorer.AcceptThreshold)
            {
                return (best with { Confidence = bestScore }, provider.Name);
            }

            // Every candidate from this provider scored below the threshold - the same as an empty
            // result set. Move on to the next enabled provider instead of stopping here.
        }

        return (null, null);
    }

    private static string ResolveQuery(string folderPath, string folderName)
    {
        string query = SeriesNameNormalizer.Normalize(folderName);
        if (!string.IsNullOrEmpty(query))
        {
            return query;
        }

        // "Season 1" and similar organizational folders normalize to empty - fall back to the
        // parent folder's name, which is the actual series (e.g. "Jujutsu Kaisen\Season 1"). This
        // fallback is for SEARCHING only: the season folder keeps its own name, and receives the
        // series result as a poster at most.
        string parentDirectory = Path.GetDirectoryName(TrimTrailingSeparators(folderPath)) ?? string.Empty;
        string parentName = GetLastSegment(parentDirectory);

        return string.IsNullOrEmpty(parentName) ? string.Empty : SeriesNameNormalizer.Normalize(parentName);
    }

    private static string GetLastSegment(string path)
    {
        string trimmed = TrimTrailingSeparators(path);
        return string.IsNullOrEmpty(trimmed) ? string.Empty : Path.GetFileName(trimmed);
    }

    private static string TrimTrailingSeparators(string path) =>
        path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

    private async Task<MetadataMatch?> BuildMatchFromCacheAsync(string folderPath, string query, FolderScrapeCacheDto cache)
    {
        if (string.IsNullOrEmpty(cache.Provider) || string.IsNullOrEmpty(cache.ProviderId))
        {
            // A cached no-match: already checked, nothing to reconstruct.
            return null;
        }

        // The cache does not store the provider's title - only the provider id and the other fields
        // it returned - and a scrape no longer writes a title anywhere else. So a reconstructed
        // match cannot carry the provider's canonical title. It carries the folder's own custom
        // title when the user has set one (the best display name available), and the provider id
        // otherwise. Callers must not treat Title as what the provider called the series.
        FolderMetadataDto? metadata = await _databaseService.LoadFolderMetadataAsync(folderPath);
        string title = !string.IsNullOrWhiteSpace(metadata?.CustomTitle) ? metadata!.CustomTitle! : cache.ProviderId;
        double confidence = string.IsNullOrEmpty(query) ? 0 : MetadataMatchScorer.Score(query, title);

        return new MetadataMatch(cache.ProviderId, title, cache.Synopsis, cache.Year, null, confidence);
    }

    private Task SaveNoMatchAsync(string folderPath) =>
        _databaseService.SaveScrapeCacheAsync(new FolderScrapeCacheDto
        {
            Path = folderPath,
            LastScraped = DateTimeOffset.UtcNow
        });
}
