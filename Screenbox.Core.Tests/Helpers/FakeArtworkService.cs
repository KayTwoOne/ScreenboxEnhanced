using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Screenbox.Core.Models;
using Screenbox.Core.Services;
using Windows.Storage;

namespace Screenbox.Core.Tests.Helpers;

/// <summary>
/// A scriptable <see cref="IArtworkService"/> stand-in used by coordinator tests so downloading a
/// scraped poster never reaches a real <c>HttpClient</c> - the coordinator test suite must stay
/// network-free even though <see cref="Coordinators.MetadataScrapeCoordinator"/> now calls
/// <see cref="SetScrapedPosterAsync"/> for every fresh match that carries a poster URL.
/// <see cref="SetScrapedPosterAsync"/> is the only member the coordinator is expected to call -
/// every other member throws, so an unexpected new dependency on one of them fails loudly instead
/// of silently returning a default.
/// </summary>
public sealed class FakeArtworkService : IArtworkService
{
    /// <summary>Result <see cref="SetScrapedPosterAsync"/> returns. True by default.</summary>
    public bool SetScrapedPosterResult { get; set; } = true;

    /// <summary>Number of times <see cref="SetScrapedPosterAsync"/> was called.</summary>
    public int SetScrapedPosterCallCount { get; private set; }

    /// <summary>The folder path most recently passed to <see cref="SetScrapedPosterAsync"/>.</summary>
    public string? LastFolderPath { get; private set; }

    /// <summary>The poster URL most recently passed to <see cref="SetScrapedPosterAsync"/>.</summary>
    public string? LastPosterUrl { get; private set; }

    /// <inheritdoc/>
    public Task<bool> SetScrapedPosterAsync(string folderPath, string posterUrl, CancellationToken ct)
    {
        SetScrapedPosterCallCount++;
        LastFolderPath = folderPath;
        LastPosterUrl = posterUrl;
        return Task.FromResult(SetScrapedPosterResult);
    }

    /// <inheritdoc/>
    public Task<string?> GetPosterFileNameAsync(StorageFolder folder, FolderMetadataDto? metadata) =>
        throw new NotSupportedException("MetadataScrapeCoordinator never resolves artwork for display.");

    /// <inheritdoc/>
    public Task<string?> SetManualPosterAsync(StorageFolder folder, StorageFile image) =>
        throw new NotSupportedException("MetadataScrapeCoordinator never sets a manual poster.");

    /// <inheritdoc/>
    public Task PruneOrphanedArtworkAsync(IReadOnlyList<string> libraryFolderPaths) =>
        throw new NotSupportedException("MetadataScrapeCoordinator never prunes artwork.");
}
