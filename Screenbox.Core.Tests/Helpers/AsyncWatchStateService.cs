using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Screenbox.Core.Models;
using Screenbox.Core.Services;

namespace Screenbox.Core.Tests.Helpers;

/// <summary>
/// Wraps a real <see cref="IWatchStateService"/> so that writing is genuinely asynchronous: the
/// write is held until <see cref="Gate"/> completes (or a short delay passes), and its continuation
/// then runs on a thread-pool thread, never on the caller's context. A write against a local SQLite
/// file completes synchronously in the test host, which hides two things this exists to expose:
/// whether the caller resumes on its own context afterwards, and what happens to state that moves
/// while the write is still in flight.
/// </summary>
public sealed class AsyncWatchStateService(IWatchStateService inner) : IWatchStateService
{
    /// <summary>When set, writes wait for this task before reaching the wrapped service.</summary>
    public Task? Gate { get; set; }

    /// <summary>True once a write has been fully applied and persisted by the wrapped service.</summary>
    public bool WriteCompleted { get; private set; }

    /// <inheritdoc/>
    public double ThresholdPercent
    {
        get => inner.ThresholdPercent;
        set => inner.ThresholdPercent = value;
    }

    /// <inheritdoc/>
    public Task LoadAsync() => inner.LoadAsync();

    /// <inheritdoc/>
    public async Task RecordProgressAsync(string location, TimeSpan position, TimeSpan duration)
    {
        await (Gate ?? Task.Delay(20)).ConfigureAwait(false);
        await inner.RecordProgressAsync(location, position, duration).ConfigureAwait(false);
        WriteCompleted = true;
    }

    /// <inheritdoc/>
    public async Task SetWatchedAsync(string location, bool watched)
    {
        await (Gate ?? Task.Delay(20)).ConfigureAwait(false);
        await inner.SetWatchedAsync(location, watched).ConfigureAwait(false);
        WriteCompleted = true;
    }

    /// <inheritdoc/>
    public bool IsWatched(string location) => inner.IsWatched(location);

    /// <inheritdoc/>
    public double GetProgress(string location) => inner.GetProgress(location);

    /// <inheritdoc/>
    public IReadOnlyList<WatchStateDto> GetContinueWatching(int limit) => inner.GetContinueWatching(limit);
}
