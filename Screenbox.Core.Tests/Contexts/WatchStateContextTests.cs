using Microsoft.Extensions.Logging.Abstractions;
using Screenbox.Core.Contexts;
using Screenbox.Core.Factories;
using Screenbox.Core.Models;
using Screenbox.Core.Services;
using Screenbox.Core.Tests.Helpers;
using Screenbox.Core.ViewModels;
using Windows.Storage;

namespace Screenbox.Core.Tests.Contexts;

/// <summary>
/// Covers <see cref="WatchStateContext.RefreshAsync"/>, whose core job is resolving the
/// upper-invariant <see cref="WatchStateDto.Location"/> back to a real file so the UI shows the
/// file's actual display name, and skipping any location that no longer resolves (e.g. a deleted
/// episode) instead of throwing.
/// </summary>
public sealed class WatchStateContextTests
{
    private static async Task<WatchStateService> CreateWatchStateServiceAsync(string dir)
    {
        var db = new DatabaseService(NullLogger<DatabaseService>.Instance) { DbFolderPath = dir };
        await db.InitializeAsync();
        var service = new WatchStateService(db, NullLogger<WatchStateService>.Instance);
        await service.LoadAsync();
        return service;
    }

    private static MediaViewModelFactory CreateMediaFactory() =>
        new(new TestPlayerService(), new PlayerContext(), new LibraryContext());

    [Test]
    public async Task InitialState_ShouldBeEmptyAndNotLoaded()
    {
        using var fixture = new TestDirectoryFixture();
        WatchStateService service = await CreateWatchStateServiceAsync(fixture.DirectoryPath);
        var context = new WatchStateContext(service, CreateMediaFactory(), NullLogger<WatchStateContext>.Instance);

        await Assert.That(context.ContinueWatching).IsEmpty();
        await Assert.That(context.IsLoaded).IsFalse();
    }

    [Test]
    public async Task RefreshAsync_SkipsLocationThatNoLongerResolvesToAFile()
    {
        // Simulates a deleted episode: watch state was recorded for it, but the file is gone.
        using var fixture = new TestDirectoryFixture();
        WatchStateService service = await CreateWatchStateServiceAsync(fixture.DirectoryPath);
        string missingFile = Path.Combine(fixture.DirectoryPath, "deleted-episode.mkv");
        await service.RecordProgressAsync(missingFile, TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(20));
        var context = new WatchStateContext(service, CreateMediaFactory(), NullLogger<WatchStateContext>.Instance);

        await context.RefreshAsync(25);

        await Assert.That(context.ContinueWatching).IsEmpty();
        await Assert.That(context.IsLoaded).IsTrue();
    }

    [Test]
    public async Task RefreshAsync_ReusesKnownLibraryItemInsteadOfRenderingTheNormalizedLocation()
    {
        // WatchStateDto.Location is normalized upper-invariant for the cache/database key, and
        // StorageFile.GetFileFromPathAsync does not restore on-disk casing by itself - it just
        // echoes back whatever casing it was queried with. What actually recovers the real
        // display name is MediaViewModelFactory.GetOrCreate: for a file that is already known to
        // the library (the common case for episodes), it finds the existing, correctly-cased
        // MediaViewModel via a case-insensitive path match instead of building a new one from the
        // shouting-uppercase query path. This is the mechanism the "no SHOUTING FILENAMES"
        // requirement depends on.
        using var fixture = new TestDirectoryFixture();
        WatchStateService service = await CreateWatchStateServiceAsync(fixture.DirectoryPath);
        string filePath = Path.Combine(fixture.DirectoryPath, "Episode 1.mkv");
        await File.WriteAllTextAsync(filePath, "fake media bytes");
        await service.RecordProgressAsync(filePath, TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(20));

        StorageFile file = await StorageFile.GetFileFromPathAsync(filePath);
        var playerContext = new PlayerContext();
        var testPlayerService = new TestPlayerService();
        var libraryContext = new LibraryContext
        {
            Videos = new VideosLibrary(new List<MediaViewModel>
            {
                new(playerContext, testPlayerService, file)
            })
        };
        var mediaFactory = new MediaViewModelFactory(testPlayerService, playerContext, libraryContext);
        var context = new WatchStateContext(service, mediaFactory, NullLogger<WatchStateContext>.Instance);

        await context.RefreshAsync(25);

        await Assert.That(context.ContinueWatching.Count).IsEqualTo(1);
        await Assert.That(context.ContinueWatching[0].Name).IsEqualTo("Episode 1.mkv");
    }

    [Test]
    public async Task RefreshAsync_OrdersMostRecentlyPlayedFirstAndSkipsFinishedItems()
    {
        using var fixture = new TestDirectoryFixture();
        WatchStateService service = await CreateWatchStateServiceAsync(fixture.DirectoryPath);
        var duration = TimeSpan.FromMinutes(20);

        string olderPath = Path.Combine(fixture.DirectoryPath, "older.mkv");
        string newerPath = Path.Combine(fixture.DirectoryPath, "newer.mkv");
        string finishedPath = Path.Combine(fixture.DirectoryPath, "finished.mkv");
        await File.WriteAllTextAsync(olderPath, "x");
        await File.WriteAllTextAsync(newerPath, "x");
        await File.WriteAllTextAsync(finishedPath, "x");

        await service.RecordProgressAsync(olderPath, TimeSpan.FromMinutes(5), duration);
        await service.RecordProgressAsync(finishedPath, TimeSpan.FromMinutes(19), duration);
        await service.RecordProgressAsync(newerPath, TimeSpan.FromMinutes(8), duration);

        var context = new WatchStateContext(service, CreateMediaFactory(), NullLogger<WatchStateContext>.Instance);
        await context.RefreshAsync(25);

        // Not asserting exact casing here (that is covered by the library-reuse test above);
        // this test is about ordering and filtering, so compare case-insensitively.
        await Assert.That(context.ContinueWatching.Count).IsEqualTo(2);
        await Assert.That(context.ContinueWatching[0].Name.ToUpperInvariant()).IsEqualTo("NEWER.MKV");
        await Assert.That(context.ContinueWatching[1].Name.ToUpperInvariant()).IsEqualTo("OLDER.MKV");
    }

    [Test]
    public async Task RefreshAsync_ReusesTheSameMediaViewModelInstanceAcrossRefreshes()
    {
        // MediaViewModel has no Equals/GetHashCode override, and the collection-sync helper
        // RefreshAsync uses diffs by reference equality. For a location that
        // MediaViewModelFactory.GetOrCreate does not already recognize from the library (this
        // test deliberately does not seed one, unlike the library-reuse test above), the factory
        // alone hands back a brand-new instance on every call - this test proves
        // WatchStateContext's own location-keyed cache prevents that churn by returning the SAME
        // instance (not merely an equal one) on a second refresh of the same underlying state.
        using var fixture = new TestDirectoryFixture();
        WatchStateService service = await CreateWatchStateServiceAsync(fixture.DirectoryPath);
        string filePath = Path.Combine(fixture.DirectoryPath, "ep1.mkv");
        await File.WriteAllTextAsync(filePath, "x");
        await service.RecordProgressAsync(filePath, TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(20));
        var context = new WatchStateContext(service, CreateMediaFactory(), NullLogger<WatchStateContext>.Instance);

        await context.RefreshAsync(25);
        MediaViewModel first = context.ContinueWatching[0];

        await context.RefreshAsync(25);
        MediaViewModel second = context.ContinueWatching[0];

        await Assert.That(ReferenceEquals(first, second)).IsTrue();
    }

    [Test]
    public async Task RefreshAsync_DropsAnItemWhoseFileIsDeletedAfterBeingCached()
    {
        // The instance cache exists purely to hand back the SAME MediaViewModel instance across
        // refreshes (so a tile doesn't reload its thumbnail every time) - it must never be used
        // as a shortcut that skips re-checking whether the file still exists. This reproduces
        // exactly that regression: resolve successfully once (populating the cache), delete the
        // file, then refresh again and assert the item is dropped from the collection. The
        // never-existed-file test above does not catch this, because in that test the cache is
        // never populated in the first place.
        using var fixture = new TestDirectoryFixture();
        WatchStateService service = await CreateWatchStateServiceAsync(fixture.DirectoryPath);
        string filePath = Path.Combine(fixture.DirectoryPath, "ep1.mkv");
        await File.WriteAllTextAsync(filePath, "x");
        await service.RecordProgressAsync(filePath, TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(20));
        var context = new WatchStateContext(service, CreateMediaFactory(), NullLogger<WatchStateContext>.Instance);

        await context.RefreshAsync(25);
        await Assert.That(context.ContinueWatching.Count).IsEqualTo(1);

        File.Delete(filePath);
        await context.RefreshAsync(25);

        await Assert.That(context.ContinueWatching).IsEmpty();
    }

    [Test]
    public async Task RefreshAsync_RespectsTheLimit()
    {
        using var fixture = new TestDirectoryFixture();
        WatchStateService service = await CreateWatchStateServiceAsync(fixture.DirectoryPath);
        var duration = TimeSpan.FromMinutes(20);

        for (int i = 0; i < 5; i++)
        {
            string path = Path.Combine(fixture.DirectoryPath, $"ep{i}.mkv");
            await File.WriteAllTextAsync(path, "x");
            await service.RecordProgressAsync(path, TimeSpan.FromMinutes(5), duration);
        }

        var context = new WatchStateContext(service, CreateMediaFactory(), NullLogger<WatchStateContext>.Instance);
        await context.RefreshAsync(3);

        await Assert.That(context.ContinueWatching.Count).IsEqualTo(3);
    }
}
