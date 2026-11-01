using System.Collections.Generic;
using Microsoft.Extensions.Logging.Abstractions;
using Screenbox.Core.Enums;
using Screenbox.Core.Models;
using Screenbox.Core.Services;
using Screenbox.Core.Tests.Helpers;

namespace Screenbox.Core.Tests.Services;

public sealed class FolderStatsServiceTests
{
    private static async Task<(FolderStatsService service, DatabaseService db)> CreateAsync(string dir)
    {
        var db = new DatabaseService(NullLogger<DatabaseService>.Instance) { DbFolderPath = dir };
        await db.InitializeAsync();
        return (new FolderStatsService(db, NullLogger<FolderStatsService>.Instance), db);
    }

    [Test]
    public async Task GetAsync_ReturnsStatsAndPopulatesTheCache()
    {
        using var fixture = new TestDirectoryFixture();
        (FolderStatsService service, DatabaseService db) = await CreateAsync(fixture.DirectoryPath);
        await db.SaveVideoCacheAsync([@"D:\Anime"],
            [new VideoCacheRecordDto { Path = @"D:\Anime\Show\ep1.mkv", Title = "ep1", Duration = TimeSpan.FromMinutes(20) }]);

        FolderStats first = await service.GetAsync(@"D:\Anime\Show");

        await Assert.That(first.EpisodeCount).IsEqualTo(1);
        await Assert.That(service.TryGetCached(@"D:\Anime\Show", out FolderStats cached)).IsTrue();
        await Assert.That(cached.EpisodeCount).IsEqualTo(1);
    }

    [Test]
    public async Task TryGetCached_ReturnsFalseBeforeAnyLoad()
    {
        using var fixture = new TestDirectoryFixture();
        (FolderStatsService service, _) = await CreateAsync(fixture.DirectoryPath);

        await Assert.That(service.TryGetCached(@"D:\Anime\Show", out _)).IsFalse();
    }

    /// <summary>
    /// Asserting only on <c>GetAsync</c>'s return value here would not discriminate a broken
    /// <c>Invalidate()</c>: <c>GetAsync</c> recomputes on every cache miss regardless of whether
    /// the entry was ever cached in the first place, so the "after" value would come out correct
    /// even with an empty <c>Invalidate()</c> body, purely because the folder was never cached to
    /// begin with in a fresh scenario, or coincidentally because the recomputed value happens to
    /// match. Observing the cache directly with <see cref="FolderStatsService.TryGetCached"/>
    /// immediately after <c>Invalidate()</c> - with no intervening <c>GetAsync</c> - is what
    /// actually proves the entry was dropped.
    /// </summary>
    [Test]
    public async Task Invalidate_ClearsTheCacheSoTryGetCachedMissesAndGetAsyncRecomputes()
    {
        // Watching an episode changes the unwatched count; the cache must not go stale.
        using var fixture = new TestDirectoryFixture();
        (FolderStatsService service, DatabaseService db) = await CreateAsync(fixture.DirectoryPath);
        await db.SaveVideoCacheAsync([@"D:\Anime"],
            [new VideoCacheRecordDto { Path = @"D:\Anime\Show\ep1.mkv", Title = "ep1", Duration = TimeSpan.FromMinutes(20) }]);
        await service.GetAsync(@"D:\Anime\Show");
        await Assert.That(service.TryGetCached(@"D:\Anime\Show", out _)).IsTrue();

        service.Invalidate();

        // The core assertion: the entry is gone from the cache itself, not inferred from a
        // subsequent recompute.
        await Assert.That(service.TryGetCached(@"D:\Anime\Show", out _)).IsFalse();

        // Follow-up: GetAsync recomputes from the database rather than continuing to serve the
        // now-stale watch state that was cached before this watch state write.
        await db.SaveWatchStateAsync(new WatchStateDto { Location = @"D:\ANIME\SHOW\EP1.MKV", Completed = true });
        FolderStats after = await service.GetAsync(@"D:\Anime\Show");
        await Assert.That(after.UnwatchedCount).IsEqualTo(0);
    }

    [Test]
    public async Task GetAsync_IsCaseInsensitiveOnTheCacheKey()
    {
        using var fixture = new TestDirectoryFixture();
        (FolderStatsService service, DatabaseService db) = await CreateAsync(fixture.DirectoryPath);
        await db.SaveVideoCacheAsync([@"D:\Anime"],
            [new VideoCacheRecordDto { Path = @"D:\Anime\Show\ep1.mkv", Title = "ep1", Duration = TimeSpan.Zero }]);
        await service.GetAsync(@"D:\Anime\Show");

        await Assert.That(service.TryGetCached(@"d:\anime\show", out _)).IsTrue();
    }

    /// <summary>
    /// Pins the caching behavior itself: without the cache-first check in
    /// <see cref="FolderStatsService.GetAsync"/>, a broken implementation that always re-queries
    /// the database would still return the same (correct) stats on a second call, so asserting on
    /// the returned value alone would not catch it. Counting actual database calls is the only
    /// way to prove the second call was served from the cache.
    /// </summary>
    [Test]
    public async Task GetAsync_SecondCallForSameFolder_ServesFromCacheWithoutRequeryingTheDatabase()
    {
        var db = new CountingDatabaseService();
        var service = new FolderStatsService(db, NullLogger<FolderStatsService>.Instance);

        await service.GetAsync(@"D:\Anime\Show");
        await service.GetAsync(@"D:\Anime\Show");

        await Assert.That(db.QueryCount).IsEqualTo(1);
    }

    /// <summary>
    /// Passing an empty path does not actually make the real <see cref="DatabaseService"/> throw
    /// (it short-circuits on <c>string.IsNullOrEmpty</c>), so that alone would not discriminate a
    /// missing try/catch in <see cref="FolderStatsService.GetAsync"/>. Using a fake
    /// <see cref="IDatabaseService"/> that always throws from <c>GetFolderStatsAsync</c> makes the
    /// failure real: without a try/catch the exception would propagate and fail this test.
    /// </summary>
    [Test]
    public async Task GetAsync_DoesNotThrowWhenTheDatabaseFails()
    {
        // Captions are an enhancement. A failure must degrade to empty stats, not break the grid.
        var service = new FolderStatsService(new ThrowingDatabaseService(), NullLogger<FolderStatsService>.Instance);

        FolderStats stats = await service.GetAsync(@"D:\Anime\Show");

        await Assert.That(stats.EpisodeCount).IsEqualTo(0);
    }

    /// <summary>
    /// A minimal <see cref="IDatabaseService"/> stub whose only implemented behavior is throwing
    /// from <c>GetFolderStatsAsync</c>, so the failure path in <see cref="FolderStatsService"/> can
    /// be exercised directly without depending on how the real database happens to react to a
    /// particular input.
    /// </summary>
    private sealed class ThrowingDatabaseService : IDatabaseService
    {
        public Task InitializeAsync() => throw new NotSupportedException();
        public Task<RawCacheLoadResultDto> LoadLibraryCacheAsync(MediaPlaybackType mediaType) => throw new NotSupportedException();
        public Task SaveMusicCacheAsync(IReadOnlyList<string> folderPaths, IReadOnlyList<MusicCacheRecordDto> records) => throw new NotSupportedException();
        public Task SaveVideoCacheAsync(IReadOnlyList<string> folderPaths, IReadOnlyList<VideoCacheRecordDto> records) => throw new NotSupportedException();
        public Task ReplacePlaybackProgressAsync(IReadOnlyList<MediaPlaybackProgress> snapshot) => throw new NotSupportedException();
        public Task<List<MediaPlaybackProgress>> LoadPlaybackProgressAsync() => throw new NotSupportedException();
        public Task SavePlaylistAsync(PlaylistRecordDto playlist) => throw new NotSupportedException();
        public Task<PlaylistRecordDto?> LoadPlaylistAsync(string id) => throw new NotSupportedException();
        public Task<List<PlaylistRecordDto>> ListPlaylistsAsync() => throw new NotSupportedException();
        public Task DeletePlaylistAsync(string id) => throw new NotSupportedException();
        public Task SaveFolderMetadataAsync(FolderMetadataDto metadata) => throw new NotSupportedException();
        public Task<FolderMetadataDto?> LoadFolderMetadataAsync(string path) => throw new NotSupportedException();
        public Task<List<FolderMetadataDto>> ListFolderMetadataAsync() => throw new NotSupportedException();
        public Task<string?> SetFolderPosterAsync(string path, string posterFile, PosterSource source) => throw new NotSupportedException();
        public Task SetFolderCustomTitleAsync(string path, string? customTitle) => throw new NotSupportedException();
        public Task<string?> ClearManualFolderPosterAsync(string path) => throw new NotSupportedException();
        public Task DeleteFolderMetadataAsync(string path) => throw new NotSupportedException();
        public Task SaveScrapeCacheAsync(FolderScrapeCacheDto cache) => throw new NotSupportedException();
        public Task<FolderScrapeCacheDto?> LoadScrapeCacheAsync(string path) => throw new NotSupportedException();
        public Task ClearScrapeCacheAsync() => throw new NotSupportedException();
        public Task SaveWatchStateAsync(WatchStateDto state) => throw new NotSupportedException();
        public Task<WatchStateDto?> LoadWatchStateAsync(string location) => throw new NotSupportedException();
        public Task<List<WatchStateDto>> ListWatchStateAsync() => throw new NotSupportedException();
        public Task DeleteWatchStateAsync(string location) => throw new NotSupportedException();

        public Task<FolderStats> GetFolderStatsAsync(string folderPath) =>
            throw new InvalidOperationException("Simulated database failure.");
    }

    /// <summary>
    /// A minimal <see cref="IDatabaseService"/> stub that counts calls to
    /// <c>GetFolderStatsAsync</c> instead of returning real data, so a test can assert on how many
    /// times the database was actually queried rather than only on the (potentially identical)
    /// returned value.
    /// </summary>
    private sealed class CountingDatabaseService : IDatabaseService
    {
        public int QueryCount { get; private set; }

        public Task InitializeAsync() => throw new NotSupportedException();
        public Task<RawCacheLoadResultDto> LoadLibraryCacheAsync(MediaPlaybackType mediaType) => throw new NotSupportedException();
        public Task SaveMusicCacheAsync(IReadOnlyList<string> folderPaths, IReadOnlyList<MusicCacheRecordDto> records) => throw new NotSupportedException();
        public Task SaveVideoCacheAsync(IReadOnlyList<string> folderPaths, IReadOnlyList<VideoCacheRecordDto> records) => throw new NotSupportedException();
        public Task ReplacePlaybackProgressAsync(IReadOnlyList<MediaPlaybackProgress> snapshot) => throw new NotSupportedException();
        public Task<List<MediaPlaybackProgress>> LoadPlaybackProgressAsync() => throw new NotSupportedException();
        public Task SavePlaylistAsync(PlaylistRecordDto playlist) => throw new NotSupportedException();
        public Task<PlaylistRecordDto?> LoadPlaylistAsync(string id) => throw new NotSupportedException();
        public Task<List<PlaylistRecordDto>> ListPlaylistsAsync() => throw new NotSupportedException();
        public Task DeletePlaylistAsync(string id) => throw new NotSupportedException();
        public Task SaveFolderMetadataAsync(FolderMetadataDto metadata) => throw new NotSupportedException();
        public Task<FolderMetadataDto?> LoadFolderMetadataAsync(string path) => throw new NotSupportedException();
        public Task<List<FolderMetadataDto>> ListFolderMetadataAsync() => throw new NotSupportedException();
        public Task<string?> SetFolderPosterAsync(string path, string posterFile, PosterSource source) => throw new NotSupportedException();
        public Task SetFolderCustomTitleAsync(string path, string? customTitle) => throw new NotSupportedException();
        public Task<string?> ClearManualFolderPosterAsync(string path) => throw new NotSupportedException();
        public Task DeleteFolderMetadataAsync(string path) => throw new NotSupportedException();
        public Task SaveScrapeCacheAsync(FolderScrapeCacheDto cache) => throw new NotSupportedException();
        public Task<FolderScrapeCacheDto?> LoadScrapeCacheAsync(string path) => throw new NotSupportedException();
        public Task ClearScrapeCacheAsync() => throw new NotSupportedException();
        public Task SaveWatchStateAsync(WatchStateDto state) => throw new NotSupportedException();
        public Task<WatchStateDto?> LoadWatchStateAsync(string location) => throw new NotSupportedException();
        public Task<List<WatchStateDto>> ListWatchStateAsync() => throw new NotSupportedException();
        public Task DeleteWatchStateAsync(string location) => throw new NotSupportedException();

        public Task<FolderStats> GetFolderStatsAsync(string folderPath)
        {
            QueryCount++;
            return Task.FromResult(new FolderStats(1, TimeSpan.FromMinutes(20), 1));
        }
    }
}
