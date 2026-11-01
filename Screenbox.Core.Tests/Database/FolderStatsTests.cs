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
        // "Attack On Titan" is a true string prefix of "Attack On Titan Abridged", so without the
        // trailing-separator append, "Attack On Titan%" would match both folders. This is the pair
        // that actually exercises the trailing-separator logic (unlike "Dan Da Dan" vs "Dandadan 2nd
        // Season", which diverge at the 4th character and never collide as a LIKE prefix regardless
        // of the trailing separator).
        using var fixture = new TestDirectoryFixture();
        DatabaseService db = await CreateServiceAsync(fixture.DirectoryPath);

        await db.SaveVideoCacheAsync([@"D:\Anime"],
        [
            Video(@"D:\Anime\Attack On Titan\ep1.mkv", TimeSpan.FromMinutes(24)),
            Video(@"D:\Anime\Attack On Titan Abridged\ep1.mkv", TimeSpan.FromMinutes(24))
        ]);

        FolderStats stats = await db.GetFolderStatsAsync(@"D:\Anime\Attack On Titan");

        await Assert.That(stats.EpisodeCount).IsEqualTo(1);
    }

    [Test]
    public async Task GetFolderStatsAsync_UnderscoreInFolderNameIsNotAWildcard()
    {
        // "_" is a single-character LIKE wildcard when unescaped, which would let "Season_1" match
        // the unrelated sibling "Season-1". This pins down the EscapeLike behavior the brief called
        // out explicitly.
        using var fixture = new TestDirectoryFixture();
        DatabaseService db = await CreateServiceAsync(fixture.DirectoryPath);

        await db.SaveVideoCacheAsync([@"D:\Anime"],
        [
            Video(@"D:\Anime\Show\Season_1\ep1.mkv", TimeSpan.FromMinutes(24)),
            Video(@"D:\Anime\Show\Season-1\ep1.mkv", TimeSpan.FromMinutes(24))
        ]);

        FolderStats stats = await db.GetFolderStatsAsync(@"D:\Anime\Show\Season_1");

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
    public async Task GetFolderStatsAsync_ZeroDurationRecordDoesNotThrow()
    {
        // VideoCacheRecordDto.Duration is a non-nullable TimeSpan, so SaveVideoCacheAsync always
        // writes a concrete tick value (0 here), never a SQL NULL, through the public API. This test
        // covers the zero-duration case reachable from that API; it does not exercise a NULL
        // duration_ticks column (COALESCE covers that defensively, but nothing in this suite can
        // produce it without writing raw SQL).
        using var fixture = new TestDirectoryFixture();
        DatabaseService db = await CreateServiceAsync(fixture.DirectoryPath);

        await db.SaveVideoCacheAsync([@"D:\Anime"],
            [Video(@"D:\Anime\Suzume\movie.mkv", TimeSpan.Zero)]);

        FolderStats stats = await db.GetFolderStatsAsync(@"D:\Anime\Suzume");

        await Assert.That(stats.EpisodeCount).IsEqualTo(1);
        await Assert.That(stats.TotalDuration).IsEqualTo(TimeSpan.Zero);
    }
}
