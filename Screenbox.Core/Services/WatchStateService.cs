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

    // Guards LoadAsync so it is safe and cheap to call more than once. Callers should not need
    // to know that loading happens exactly once - the external "is loaded" flags they gate on
    // (e.g. IPlaybackProgressTracker.IsLoaded) belong to a different component and are not a
    // reliable signal for this service. Only set on a SUCCESSFUL load: a transient database
    // failure must not permanently latch the cache as empty for the rest of the session.
    private volatile bool _isLoaded;

    public WatchStateService(IDatabaseService databaseService, ILogger<WatchStateService> logger)
    {
        _databaseService = databaseService;
        _logger = logger;
    }

    /// <summary>
    /// Canonicalizes a location so the same path always maps to the same cache key and the
    /// same database row, regardless of casing. <c>watch_state.location</c> is a SQLite
    /// TEXT PRIMARY KEY with no COLLATE clause (case-sensitive BINARY collation), while the
    /// in-memory cache compares case-insensitively — without this normalization the two would
    /// disagree about identity and case-variant calls for the same file could silently create
    /// diverging rows or drop state on load.
    /// </summary>
    private static string NormalizeLocation(string location) => location.ToUpperInvariant();

    /// <inheritdoc/>
    public async Task LoadAsync()
    {
        // Already loaded: return immediately rather than re-querying the database. This also
        // protects the in-memory cache from being overwritten by an older snapshot on disk if
        // something calls LoadAsync again after RecordProgressAsync/SetWatchedAsync have already
        // moved the cache ahead of what was last persisted.
        if (_isLoaded) return;

        try
        {
            foreach (WatchStateDto row in await _databaseService.ListWatchStateAsync())
            {
                string normalized = NormalizeLocation(row.Location);
                row.Location = normalized;
                _cache[normalized] = row;
            }

            _isLoaded = true;
        }
        catch (Exception e)
        {
            // Watched state is an enhancement. Failing to load it must not stop the app starting.
            // Do not set _isLoaded here: a transient failure should not permanently leave the
            // cache empty for the rest of the session - a later call should retry.
            _logger.LogError(e, "Failed to load watch state.");
        }
    }

    /// <inheritdoc/>
    public async Task RecordProgressAsync(string location, TimeSpan position, TimeSpan duration)
    {
        if (string.IsNullOrEmpty(location)) return;
        string normalized = NormalizeLocation(location);
        bool completedNow = WatchThreshold.IsComplete(position, duration, ThresholdPercent);
        DateTimeOffset now = DateTimeOffset.UtcNow;

        // Built as a new instance rather than mutated in place: the dictionary only guards
        // slot assignment, not the fields of the object inside a slot, so two concurrent
        // updates for the same tile could otherwise interleave their read-modify-write and
        // lose one. AddOrUpdate's factories may run more than once under contention, but each
        // run only reads the snapshot handed to it and builds a fresh object, so there is
        // nothing shared left to race on.
        WatchStateDto state = _cache.AddOrUpdate(
            normalized,
            _ => new WatchStateDto
            {
                Location = normalized,
                OriginalLocation = location,
                // Watched never regresses; there is no prior state to OR against here.
                Completed = completedNow,
                LastPlayed = now,
                Duration = duration > TimeSpan.Zero ? duration : null,
                LastPosition = position
            },
            (_, existing) => new WatchStateDto
            {
                Location = normalized,
                OriginalLocation = location,
                // Watched never regresses. Restarting a finished episode is a rewatch, not an unwatch.
                Completed = existing.Completed | completedNow,
                LastPlayed = now,
                Duration = duration > TimeSpan.Zero ? duration : existing.Duration,
                LastPosition = position
            });

        await PersistAsync(state);
    }

    /// <inheritdoc/>
    public bool IsWatched(string location) =>
        _cache.TryGetValue(NormalizeLocation(location), out WatchStateDto? state) && state.Completed;

    /// <inheritdoc/>
    public double GetProgress(string location)
    {
        if (!_cache.TryGetValue(NormalizeLocation(location), out WatchStateDto? state)) return 0d;
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
        string normalized = NormalizeLocation(location);
        DateTimeOffset now = DateTimeOffset.UtcNow;

        // Same immutable-update approach as RecordProgressAsync: build a new instance instead
        // of mutating the cached one in place, so a concurrent update can't be lost.
        WatchStateDto state = _cache.AddOrUpdate(
            normalized,
            _ => new WatchStateDto
            {
                Location = normalized,
                OriginalLocation = location,
                Completed = watched,
                LastPlayed = now
            },
            (_, existing) => new WatchStateDto
            {
                Location = normalized,
                OriginalLocation = location,
                Completed = watched,
                LastPlayed = existing.LastPlayed ?? now,
                Duration = existing.Duration,
                LastPosition = existing.LastPosition
            });

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
