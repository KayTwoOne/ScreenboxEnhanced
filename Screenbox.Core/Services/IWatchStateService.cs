using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Screenbox.Core.Models;

namespace Screenbox.Core.Services;

/// <summary>
/// Tracks which media has been watched, and how far through the rest is.
/// </summary>
public interface IWatchStateService
{
    /// <summary>Fraction of runtime that counts as watched. Defaults to 0.9.</summary>
    double ThresholdPercent { get; set; }

    /// <summary>Loads persisted state into memory. Call once at startup.</summary>
    Task LoadAsync();

    /// <summary>Records a playback position and marks the item watched once past the threshold.</summary>
    Task RecordProgressAsync(string location, TimeSpan position, TimeSpan duration);

    /// <summary>Returns whether the item has been watched. Synchronous: called per tile.</summary>
    bool IsWatched(string location);

    /// <summary>Returns progress from 0 to 1. Synchronous: called per tile.</summary>
    double GetProgress(string location);

    /// <summary>Returns partially watched items, most recently played first.</summary>
    IReadOnlyList<WatchStateDto> GetContinueWatching(int limit);

    /// <summary>Explicitly sets or clears the watched flag.</summary>
    Task SetWatchedAsync(string location, bool watched);
}
