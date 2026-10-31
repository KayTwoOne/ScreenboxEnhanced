using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
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

    /// <summary>
    /// Caches the resolved <see cref="MediaViewModel"/> for each location seen on a previous
    /// refresh, keyed by the normalized (upper-invariant) location. <see cref="MediaViewModel"/>
    /// has no <c>Equals</c>/<c>GetHashCode</c> override, and the collection sync helper used in
    /// <see cref="RefreshAsync"/> diffs by reference equality, so without this cache a location
    /// that isn't already known to the library (see
    /// <see cref="MediaViewModelFactory.GetOrCreate(StorageFile)"/>) would get a brand-new
    /// instance from the factory on every refresh - churning the tile's thumbnail and any other
    /// transient state on every call instead of being recognized as unchanged.
    /// Rebuilt from scratch each refresh rather than mutated in place, so an entry that stops
    /// resolving or falls out of the continue-watching set is dropped from the cache along with
    /// the collection instead of lingering forever.
    /// </summary>
    private readonly Dictionary<string, MediaViewModel> _mediaByLocation = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Resolves a stored location to a file. A seam for tests only: they need a lookup that
    /// completes genuinely asynchronously, which a local temp file does not reliably do.
    /// </summary>
    internal Func<string, Task<StorageFile?>> ResolveFileAsync { get; init; } = FilesHelpers.TryGetFileFromPathAsync;

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
        Dictionary<string, MediaViewModel> updatedCache = new(StringComparer.OrdinalIgnoreCase);

        try
        {
            // Ensure the watch-state cache is loaded from the database before querying it.
            // LoadAsync is self-guarding: it returns early if already loaded, and does not
            // permanently latch the cache as empty on transient failures, so it is safe and
            // cheap to call more than once. This guarantees that GetContinueWatching has data
            // to work with regardless of what else has or hasn't run.
            await _watchStateService.LoadAsync();

            IReadOnlyList<WatchStateDto> states = _watchStateService.GetContinueWatching(limit);

            // Always re-verify each file still resolves, even for a location already in the
            // cache: the cache exists to reuse the ViewModel INSTANCE (so a tile doesn't reload its
            // thumbnail every refresh), not to skip the existence check. Without this, a file
            // deleted after its first successful refresh would keep showing a stale tile forever.
            //
            // Resolve with the original (un-normalized) location: StorageFile.GetFileFromPathAsync
            // respects UWP's granted scopes, and the upper-invariant key can fail to resolve even
            // when the file exists under its original casing. Rows written before the
            // original_location column existed fall back to the normalized location.
            //
            // The lookups run concurrently and off the UI thread, but the WhenAll below resumes on
            // the caller's context (ConfigureAwait(true)) before anything touches a MediaViewModel
            // or ContinueWatching. Both are bound to the Home page, and changing them from a
            // background thread throws 0x8001010E from the page's state trigger. That left Continue
            // Watching blank on every cold launch until the page was next rebuilt.
            (WatchStateDto State, StorageFile? File)[] lookups = await Task.WhenAll(states.Select(async state =>
            {
                string pathForResolution = state.OriginalLocation ?? state.Location;
                try
                {
                    StorageFile? file = await ResolveFileAsync(pathForResolution).ConfigureAwait(false);
                    return (state, file);
                }
                catch (Exception e)
                {
                    // One unresolvable entry must not abort the whole refresh.
                    _logger.LogError(e, "Failed to resolve continue-watching item for '{Location}'.", state.Location);
                    return (state, (StorageFile?)null);
                }
            })).ConfigureAwait(true);

            foreach ((WatchStateDto state, StorageFile? file) in lookups)
            {
                if (file is null) continue;

                try
                {
                    if (!_mediaByLocation.TryGetValue(state.Location, out MediaViewModel? media))
                    {
                        media = _mediaFactory.GetOrCreate(file);
                    }

                    media.RefreshWatchState(_watchStateService);
                    resolved.Add(media);
                    updatedCache[state.Location] = media;
                }
                catch (Exception e)
                {
                    // Defensive: a single bad entry must not abort the whole refresh or escape into
                    // a caller reached from an async void page handler, where an unhandled
                    // exception terminates the app with no crash log.
                    _logger.LogError(e, "Failed to resolve continue-watching item for '{Location}'.", state.Location);
                }
            }
        }
        catch (Exception e)
        {
            _logger.LogError(e, "Failed to load the continue-watching list.");
        }

        // Replace wholesale rather than merge: any location not resolved this round (deleted,
        // no longer partially watched, aged out of the limit) is dropped from the cache here,
        // not just from the collection below.
        _mediaByLocation.Clear();
        foreach (KeyValuePair<string, MediaViewModel> entry in updatedCache)
        {
            _mediaByLocation[entry.Key] = entry.Value;
        }

        ContinueWatching.SyncItems(resolved);
        IsLoaded = true;
    }
}
