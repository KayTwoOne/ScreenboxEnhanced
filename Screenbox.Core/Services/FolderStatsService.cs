using System;
using System.Collections.Concurrent;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Screenbox.Core.Models;

namespace Screenbox.Core.Services;

/// <inheritdoc cref="IFolderStatsService"/>
public sealed class FolderStatsService : IFolderStatsService
{
    private readonly IDatabaseService _databaseService;
    private readonly ILogger<FolderStatsService> _logger;

    // Keyed case-insensitively: tiles resolve concurrently as the grid virtualizes, and Windows
    // paths are case-insensitive, so "D:\Anime\Show" and "d:\anime\show" must hit the same entry.
    private readonly ConcurrentDictionary<string, FolderStats> _cache = new(StringComparer.OrdinalIgnoreCase);

    public FolderStatsService(IDatabaseService databaseService, ILogger<FolderStatsService> logger)
    {
        _databaseService = databaseService;
        _logger = logger;
    }

    /// <inheritdoc/>
    public async Task<FolderStats> GetAsync(string folderPath)
    {
        // Check the cache first so a hit never re-queries the database, and so Invalidate() is
        // actually observable through this method rather than only through TryGetCached: without
        // this check GetAsync would always recompute, and callers who don't happen to call
        // TryGetCached themselves would get none of the benefit the cache exists to provide.
        if (_cache.TryGetValue(folderPath, out FolderStats cached)) return cached;

        try
        {
            FolderStats stats = await _databaseService.GetFolderStatsAsync(folderPath);
            _cache[folderPath] = stats;
            return stats;
        }
        catch (Exception e)
        {
            // Captions are an enhancement. A database failure here must not break the grid.
            _logger.LogError(e, "Failed to compute folder stats for '{FolderPath}'.", folderPath);
            return default;
        }
    }

    /// <inheritdoc/>
    public bool TryGetCached(string folderPath, out FolderStats stats) =>
        _cache.TryGetValue(folderPath, out stats);

    /// <inheritdoc/>
    public void Invalidate() => _cache.Clear();
}
