using System;
using System.Threading.Tasks;
using Screenbox.Core.Models;
using Screenbox.Core.Services;

namespace Screenbox.Core.Tests.Helpers;

/// <summary>
/// An <see cref="IFolderStatsService"/> stand-in that counts invalidations. The real service keeps
/// its cache private and recomputes from the database, so a test that needs to know whether - and
/// when - the cache was dropped has nothing else to observe.
/// </summary>
public sealed class FakeFolderStatsService : IFolderStatsService
{
    /// <summary>Number of times <see cref="Invalidate"/> was called.</summary>
    public int InvalidateCount { get; private set; }

    /// <summary>
    /// Runs on every <see cref="Invalidate"/>, so a test can record what was true at that instant
    /// (for example, whether the watch state had already been persisted).
    /// </summary>
    public Action? OnInvalidate { get; set; }

    /// <inheritdoc/>
    public void Invalidate()
    {
        InvalidateCount++;
        OnInvalidate?.Invoke();
    }

    /// <inheritdoc/>
    public Task<FolderStats> GetAsync(string folderPath) => Task.FromResult(default(FolderStats));

    /// <inheritdoc/>
    public bool TryGetCached(string folderPath, out FolderStats stats)
    {
        stats = default;
        return false;
    }
}
