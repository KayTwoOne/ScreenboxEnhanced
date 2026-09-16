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

    [Test]
    public async Task Invalidate_ClearsTheCacheSoStatsAreRecomputed()
    {
        // Watching an episode changes the unwatched count; the cache must not go stale.
        using var fixture = new TestDirectoryFixture();
        (FolderStatsService service, DatabaseService db) = await CreateAsync(fixture.DirectoryPath);
        await db.SaveVideoCacheAsync([@"D:\Anime"],
            [new VideoCacheRecordDto { Path = @"D:\Anime\Show\ep1.mkv", Title = "ep1", Duration = TimeSpan.FromMinutes(20) }]);
        await service.GetAsync(@"D:\Anime\Show");

        await db.SaveWatchStateAsync(new WatchStateDto { Location = @"D:\ANIME\SHOW\EP1.MKV", Completed = true });
        service.Invalidate();
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
        public Task DeleteFolderMetadataAsync(string path) => throw new NotSupportedException();
        public Task SaveWatchStateAsync(WatchStateDto state) => throw new NotSupportedException();
        public Task<WatchStateDto?> LoadWatchStateAsync(string location) => throw new NotSupportedException();
        public Task<List<WatchStateDto>> ListWatchStateAsync() => throw new NotSupportedException();
        public Task DeleteWatchStateAsync(string location) => throw new NotSupportedException();

        public Task<FolderStats> GetFolderStatsAsync(string folderPath) =>
            throw new InvalidOperationException("Simulated database failure.");
    }
}
