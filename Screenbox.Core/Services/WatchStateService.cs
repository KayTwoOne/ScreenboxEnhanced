using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Screenbox.Core.Helpers;
using Screenbox.Core.Models;

namespace Screenbox.Core.Services;

/// <inheritdoc cref="IWatchStateService"/>
public sealed class WatchStateService : IWatchStateService
{
    /// <inheritdoc/>
    public double ThresholdPercent { get; set; } = WatchThreshold.DefaultPercent;

    private readonly IDatabaseService _databaseService;
    private readonly ILogger<WatchStateService> _logger;
    private readonly ConcurrentDictionary<string, WatchStateDto> _cache = new(StringComparer.OrdinalIgnoreCase);

    public WatchStateService(IDatabaseService databaseService, ILogger<WatchStateService> logger)
    {
        _databaseService = databaseService;
        _logger = logger;
    }

    /// <inheritdoc/>
    public async Task LoadAsync()
    {
        try
        {
            foreach (WatchStateDto row in await _databaseService.ListWatchStateAsync())
            {
                _cache[row.Location] = row;
            }
        }
        catch (Exception e)
        {
            // Watched state is an enhancement. Failing to load it must not stop the app starting.
            _logger.LogError(e, "Failed to load watch state.");
        }
    }

    /// <inheritdoc/>
    public async Task RecordProgressAsync(string location, TimeSpan position, TimeSpan duration)
    {
        if (string.IsNullOrEmpty(location)) return;

        WatchStateDto state = _cache.TryGetValue(location, out WatchStateDto? existing)
            ? existing
            : new WatchStateDto { Location = location };

        // Watched never regresses. Restarting a finished episode is a rewatch, not an unwatch.
        state.Completed |= WatchThreshold.IsComplete(position, duration, ThresholdPercent);
        state.LastPlayed = DateTimeOffset.UtcNow;
        state.Duration = duration > TimeSpan.Zero ? duration : state.Duration;
        state.LastPosition = position;
        _cache[location] = state;

        await PersistAsync(state);
    }

    /// <inheritdoc/>
    public bool IsWatched(string location) =>
        _cache.TryGetValue(location, out WatchStateDto? state) && state.Completed;

    /// <inheritdoc/>
    public double GetProgress(string location)
    {
        if (!_cache.TryGetValue(location, out WatchStateDto? state)) return 0d;
        if (state.Completed) return 1d;
        return state.Duration is { } duration
            ? WatchThreshold.Progress(state.LastPosition, duration)
            : 0d;
    }

    /// <inheritdoc/>
    public IReadOnlyList<WatchStateDto> GetContinueWatching(int limit) =>
        _cache.Values
            .Where(state => !state.Completed && state.LastPlayed is not null)
            .OrderByDescending(state => state.LastPlayed!.Value)
            .Take(limit)
            .ToList();

    /// <inheritdoc/>
    public async Task SetWatchedAsync(string location, bool watched)
    {
        WatchStateDto state = _cache.TryGetValue(location, out WatchStateDto? existing)
            ? existing
            : new WatchStateDto { Location = location };

        state.Completed = watched;
        state.LastPlayed ??= DateTimeOffset.UtcNow;
        _cache[location] = state;

        await PersistAsync(state);
    }

    private async Task PersistAsync(WatchStateDto state)
    {
        try
        {
            await _databaseService.SaveWatchStateAsync(state);
        }
        catch (Exception e)
        {
            _logger.LogError(e, "Failed to persist watch state for '{Location}'.", state.Location);
        }
    }
}
