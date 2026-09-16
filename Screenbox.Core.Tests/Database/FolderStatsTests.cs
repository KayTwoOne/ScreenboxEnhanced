using Microsoft.Extensions.Logging.Abstractions;
using Screenbox.Core.Enums;
using Screenbox.Core.Models;
using Screenbox.Core.Services;
using Screenbox.Core.Tests.Helpers;

namespace Screenbox.Core.Tests.Database;

public sealed class FolderStatsTests
{
    private static async Task<DatabaseService> CreateServiceAsync(string dir)
    {
        var db = new DatabaseService(NullLogger<DatabaseService>.Instance) { DbFolderPath = dir };
        await db.InitializeAsync();
        return db;
    }

    private static VideoCacheRecordDto Video(string path, TimeSpan duration) => new()
    {
        Path = path,
        Title = System.IO.Path.GetFileNameWithoutExtension(path),
        Duration = duration
    };

    [Test]
    public async Task GetFolderStatsAsync_CountsEpisodesAndSumsRuntime()
    {
        using var fixture = new TestDirectoryFixture();
        DatabaseService db = await CreateServiceAsync(fixture.DirectoryPath);

        await db.SaveVideoCacheAsync([@"D:\Anime"],
        [
            Video(@"D:\Anime\Death Note\ep1.mkv", TimeSpan.FromMinutes(23)),
            Video(@"D:\Anime\Death Note\ep2.mkv", TimeSpan.FromMinutes(23))
        ]);

        FolderStats stats = await db.GetFolderStatsAsync(@"D:\Anime\Death Note");

        await Assert.That(stats.EpisodeCount).IsEqualTo(2);
        await Assert.That(stats.TotalDuration).IsEqualTo(TimeSpan.FromMinutes(46));
        await Assert.That(stats.UnwatchedCount).IsEqualTo(2);
    }

    [Test]
    public async Task GetFolderStatsAsync_SubtractsWatchedEpisodes()
    {
        using var fixture = new TestDirectoryFixture();
        DatabaseService db = await CreateServiceAsync(fixture.DirectoryPath);

        await db.SaveVideoCacheAsync([@"D:\Anime"],
        [
            Video(@"D:\Anime\Death Note\ep1.mkv", TimeSpan.FromMinutes(23)),
            Video(@"D:\Anime\Death Note\ep2.mkv", TimeSpan.FromMinutes(23))
        ]);
        await db.SaveWatchStateAsync(new WatchStateDto
        {
            Location = @"D:\ANIME\DEATH NOTE\EP1.MKV",
            Completed = true
        });

        FolderStats stats = await db.GetFolderStatsAsync(@"D:\Anime\Death Note");

        await Assert.That(stats.EpisodeCount).IsEqualTo(2);
        await Assert.That(stats.UnwatchedCount).IsEqualTo(1);
    }

    [Test]
    public async Task GetFolderStatsAsync_JoinIsCaseInsensitive()
    {
        // watch_state.location is stored upper-invariant; media_records.path keeps its original
        // casing. A case-sensitive join would report every episode as unwatched.
        using var fixture = new TestDirectoryFixture();
        DatabaseService db = await CreateServiceAsync(fixture.DirectoryPath);

        await db.SaveVideoCacheAsync([@"D:\Anime"],
            [Video(@"D:\Anime\Gachiakuta\Episode 01.mkv", TimeSpan.FromMinutes(24))]);
        await db.SaveWatchStateAsync(new WatchStateDto
        {
            Location = @"D:\ANIME\GACHIAKUTA\EPISODE 01.MKV",
            Completed = true
        });

        FolderStats stats = await db.GetFolderStatsAsync(@"D:\Anime\Gachiakuta");

        await Assert.That(stats.UnwatchedCount).IsEqualTo(0);
    }

    [Test]
    public async Task GetFolderStatsAsync_DoesNotMatchSiblingFolderWithSharedPrefix()
    {
        // "Dan Da Dan" must not absorb rows from "Dandadan 2nd Season".
        using var fixture = new TestDirectoryFixture();
        DatabaseService db = await CreateServiceAsync(fixture.DirectoryPath);

        await db.SaveVideoCacheAsync([@"D:\Anime"],
        [
            Video(@"D:\Anime\Dan Da Dan\ep1.mkv", TimeSpan.FromMinutes(24)),
            Video(@"D:\Anime\Dandadan 2nd Season\ep1.mkv", TimeSpan.FromMinutes(24))
        ]);

        FolderStats stats = await db.GetFolderStatsAsync(@"D:\Anime\Dan Da Dan");

        await Assert.That(stats.EpisodeCount).IsEqualTo(1);
    }

    [Test]
    public async Task GetFolderStatsAsync_IncludesNestedSeasonFolders()
    {
        // Series folders hold only subfolders; the count must reach into them.
        using var fixture = new TestDirectoryFixture();
        DatabaseService db = await CreateServiceAsync(fixture.DirectoryPath);

        await db.SaveVideoCacheAsync([@"D:\Anime"],
        [
            Video(@"D:\Anime\Attack On Titan\Season 1\ep1.mkv", TimeSpan.FromMinutes(24)),
            Video(@"D:\Anime\Attack On Titan\Season 2\ep1.mkv", TimeSpan.FromMinutes(24))
        ]);

        FolderStats stats = await db.GetFolderStatsAsync(@"D:\Anime\Attack On Titan");

        await Assert.That(stats.EpisodeCount).IsEqualTo(2);
    }

    [Test]
    public async Task GetFolderStatsAsync_ReturnsEmptyStatsForUnknownFolder()
    {
        using var fixture = new TestDirectoryFixture();
        DatabaseService db = await CreateServiceAsync(fixture.DirectoryPath);

        FolderStats stats = await db.GetFolderStatsAsync(@"D:\does\not\exist");

        await Assert.That(stats.EpisodeCount).IsEqualTo(0);
        await Assert.That(stats.TotalDuration).IsEqualTo(TimeSpan.Zero);
        await Assert.That(stats.UnwatchedCount).IsEqualTo(0);
    }

    [Test]
    public async Task GetFolderStatsAsync_TreatsNullDurationAsZeroRatherThanThrowing()
    {
        using var fixture = new TestDirectoryFixture();
        DatabaseService db = await CreateServiceAsync(fixture.DirectoryPath);

        await db.SaveVideoCacheAsync([@"D:\Anime"],
            [Video(@"D:\Anime\Suzume\movie.mkv", TimeSpan.Zero)]);

        FolderStats stats = await db.GetFolderStatsAsync(@"D:\Anime\Suzume");

        await Assert.That(stats.EpisodeCount).IsEqualTo(1);
        await Assert.That(stats.TotalDuration).IsEqualTo(TimeSpan.Zero);
    }
}
