using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using Screenbox.Core.Enums;
using Screenbox.Core.Models;
using Screenbox.Core.Services;

namespace Screenbox.Core.Tests.Helpers;

/// <summary>
/// An <see cref="IDatabaseService"/> stand-in that throws on every member. Used to prove the opt-in
/// guarantee at its strongest: with the master toggle off, a scrape must not touch the database at
/// all - not read the cache, not read or write folder metadata - not just skip calling a provider.
/// </summary>
public sealed class ThrowingDatabaseService : IDatabaseService
{
    private static InvalidOperationException Fail([CallerMemberName] string member = "") =>
        new($"{member} should not have been called - the feature was disabled.");

    public Task InitializeAsync() => throw Fail();
    public Task<RawCacheLoadResultDto> LoadLibraryCacheAsync(MediaPlaybackType mediaType) => throw Fail();
    public Task SaveMusicCacheAsync(IReadOnlyList<string> folderPaths, IReadOnlyList<MusicCacheRecordDto> records) => throw Fail();
    public Task SaveVideoCacheAsync(IReadOnlyList<string> folderPaths, IReadOnlyList<VideoCacheRecordDto> records) => throw Fail();
    public Task ReplacePlaybackProgressAsync(IReadOnlyList<MediaPlaybackProgress> snapshot) => throw Fail();
    public Task<List<MediaPlaybackProgress>> LoadPlaybackProgressAsync() => throw Fail();
    public Task SavePlaylistAsync(PlaylistRecordDto playlist) => throw Fail();
    public Task<PlaylistRecordDto?> LoadPlaylistAsync(string id) => throw Fail();
    public Task<List<PlaylistRecordDto>> ListPlaylistsAsync() => throw Fail();
    public Task DeletePlaylistAsync(string id) => throw Fail();
    public Task SaveFolderMetadataAsync(FolderMetadataDto metadata) => throw Fail();
    public Task<FolderMetadataDto?> LoadFolderMetadataAsync(string path) => throw Fail();
    public Task<List<FolderMetadataDto>> ListFolderMetadataAsync() => throw Fail();
    public Task<string?> SetFolderPosterAsync(string path, string posterFile, PosterSource source) => throw Fail();
    public Task SetFolderCustomTitleAsync(string path, string? customTitle) => throw Fail();
    public Task<string?> ClearManualFolderPosterAsync(string path) => throw Fail();
    public Task DeleteFolderMetadataAsync(string path) => throw Fail();
    public Task SaveScrapeCacheAsync(FolderScrapeCacheDto cache) => throw Fail();
    public Task<FolderScrapeCacheDto?> LoadScrapeCacheAsync(string path) => throw Fail();
    public Task ClearScrapeCacheAsync() => throw Fail();
    public Task SaveWatchStateAsync(WatchStateDto state) => throw Fail();
    public Task<WatchStateDto?> LoadWatchStateAsync(string location) => throw Fail();
    public Task<List<WatchStateDto>> ListWatchStateAsync() => throw Fail();
    public Task DeleteWatchStateAsync(string location) => throw Fail();
    public Task<FolderStats> GetFolderStatsAsync(string folderPath) => throw Fail();
}
