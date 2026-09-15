using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.Extensions.Logging;
using Screenbox.Core.Factories;
using Screenbox.Core.Helpers;
using Screenbox.Core.Models;
using Screenbox.Core.Services;
using Screenbox.Core.ViewModels;
using Windows.Storage;

namespace Screenbox.Core.Contexts;

/// <summary>
/// Context for holding the application-wide continue-watching media items: partially watched
/// items, most recently played first.
/// </summary>
public sealed partial class WatchStateContext : ObservableObject
{
    /// <summary>
    /// Gets the collection of partially watched media items, most recently played first.
    /// </summary>
    public ObservableCollection<MediaViewModel> ContinueWatching { get; } = new();

    /// <summary>
    /// Gets or sets a value indicating whether the continue-watching list has been loaded at least once.
    /// </summary>
    [ObservableProperty]
    public partial bool IsLoaded { get; set; }

    private readonly IWatchStateService _watchStateService;
    private readonly MediaViewModelFactory _mediaFactory;
    private readonly ILogger<WatchStateContext> _logger;

    public WatchStateContext(
        IWatchStateService watchStateService,
        MediaViewModelFactory mediaFactory,
        ILogger<WatchStateContext> logger)
    {
        _watchStateService = watchStateService;
        _mediaFactory = mediaFactory;
        _logger = logger;
    }

    /// <summary>
    /// Refreshes <see cref="ContinueWatching"/> from the current watch-state cache.
    /// <see cref="WatchStateDto.Location"/> is stored upper-invariant so the cache and the SQLite
    /// key never diverge, so each entry is resolved back to a real <see cref="StorageFile"/> here
    /// and the display name is taken from that file rather than the normalized location -
    /// otherwise the UI would render SHOUTING FILENAMES. A location that no longer resolves
    /// (e.g. a deleted episode) is skipped rather than surfaced as an error, so one stale row can
    /// never break the row or crash the page.
    /// </summary>
    public async Task RefreshAsync(int limit)
    {
        List<MediaViewModel> resolved = new();

        try
        {
            IReadOnlyList<WatchStateDto> states = _watchStateService.GetContinueWatching(limit);

            foreach (WatchStateDto state in states)
            {
                try
                {
                    StorageFile? file = await FilesHelpers.TryGetFileFromPathAsync(state.Location).ConfigureAwait(false);
                    if (file == null) continue;

                    MediaViewModel media = _mediaFactory.GetOrCreate(file);
                    media.RefreshWatchState(_watchStateService);
                    resolved.Add(media);
                }
                catch (Exception e)
                {
                    // Defensive: a single bad entry (e.g. an inaccessible file) must not abort
                    // the whole refresh or escape into a caller reached from an async void page
                    // handler, where an unhandled exception terminates the app with no crash log.
                    _logger.LogError(e, "Failed to resolve continue-watching item for '{Location}'.", state.Location);
                }
            }
        }
        catch (Exception e)
        {
            _logger.LogError(e, "Failed to load the continue-watching list.");
        }

        ContinueWatching.SyncItems(resolved);
        IsLoaded = true;
    }
}
