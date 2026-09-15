using Microsoft.Extensions.Logging.Abstractions;
using Screenbox.Core.Models;
using Screenbox.Core.Services;
using Screenbox.Core.Tests.Helpers;

namespace Screenbox.Core.Tests.Services;

public sealed class WatchStateServiceTests
{
    private static async Task<(WatchStateService service, DatabaseService db)> CreateAsync(string dir)
    {
        var db = new DatabaseService(NullLogger<DatabaseService>.Instance) { DbFolderPath = dir };
        await db.InitializeAsync();
        var service = new WatchStateService(db, NullLogger<WatchStateService>.Instance);
        await service.LoadAsync();
        return (service, db);
    }

    [Test]
    public async Task RecordProgressAsync_MarksWatchedPastThreshold()
    {
        using var fixture = new TestDirectoryFixture();
        (WatchStateService service, _) = await CreateAsync(fixture.DirectoryPath);
        const string loc = @"D:\ep1.mkv";

        await service.RecordProgressAsync(loc, TimeSpan.FromMinutes(19), TimeSpan.FromMinutes(20));

        await Assert.That(service.IsWatched(loc)).IsTrue();
    }

    [Test]
    public async Task RecordProgressAsync_DoesNotMarkWatchedBelowThreshold()
    {
        using var fixture = new TestDirectoryFixture();
        (WatchStateService service, _) = await CreateAsync(fixture.DirectoryPath);
        const string loc = @"D:\ep2.mkv";

        await service.RecordProgressAsync(loc, TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(20));

        await Assert.That(service.IsWatched(loc)).IsFalse();
        await Assert.That(service.GetProgress(loc)).IsEqualTo(0.25);
    }

    [Test]
    public async Task WatchedStateNeverRegressesOnRewatch()
    {
        // Finishing an episode then restarting it must not clear the watched flag, otherwise
        // rewatching a series silently erases the user's history.
        using var fixture = new TestDirectoryFixture();
        (WatchStateService service, _) = await CreateAsync(fixture.DirectoryPath);
        const string loc = @"D:\ep3.mkv";

        await service.RecordProgressAsync(loc, TimeSpan.FromMinutes(19), TimeSpan.FromMinutes(20));
        await service.RecordProgressAsync(loc, TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(20));

        await Assert.That(service.IsWatched(loc)).IsTrue();
    }

    [Test]
    public async Task SetWatchedAsync_CanClearTheFlagExplicitly()
    {
        using var fixture = new TestDirectoryFixture();
        (WatchStateService service, _) = await CreateAsync(fixture.DirectoryPath);
        const string loc = @"D:\ep4.mkv";
        await service.RecordProgressAsync(loc, TimeSpan.FromMinutes(19), TimeSpan.FromMinutes(20));

        await service.SetWatchedAsync(loc, watched: false);

        await Assert.That(service.IsWatched(loc)).IsFalse();
    }

    [Test]
    public async Task State_SurvivesReload()
    {
        using var fixture = new TestDirectoryFixture();
        (WatchStateService service, DatabaseService db) = await CreateAsync(fixture.DirectoryPath);
        const string loc = @"D:\ep5.mkv";
        await service.RecordProgressAsync(loc, TimeSpan.FromMinutes(19), TimeSpan.FromMinutes(20));

        var reloaded = new WatchStateService(db, NullLogger<WatchStateService>.Instance);
        await reloaded.LoadAsync();

        await Assert.That(reloaded.IsWatched(loc)).IsTrue();
    }

    [Test]
    public async Task GetContinueWatching_ReturnsPartiallyWatchedNewestFirstAndExcludesFinished()
    {
        using var fixture = new TestDirectoryFixture();
        (WatchStateService service, _) = await CreateAsync(fixture.DirectoryPath);
        var duration = TimeSpan.FromMinutes(20);

        await service.RecordProgressAsync(@"D:\older.mkv", TimeSpan.FromMinutes(5), duration);
        await service.RecordProgressAsync(@"D:\finished.mkv", TimeSpan.FromMinutes(19), duration);
        await service.RecordProgressAsync(@"D:\newer.mkv", TimeSpan.FromMinutes(8), duration);

        IReadOnlyList<WatchStateDto> result = service.GetContinueWatching(10);

        // Location is normalized (case-folded) at the service boundary so the cache and the
        // case-sensitive watch_state.location column can never disagree about identity — see
        // WatchStateService.NormalizeLocation. Count, order and filtering are unaffected.
        await Assert.That(result.Count).IsEqualTo(2);
        await Assert.That(result[0].Location).IsEqualTo(@"D:\NEWER.MKV");
        await Assert.That(result[1].Location).IsEqualTo(@"D:\OLDER.MKV");
    }

    [Test]
    public async Task GetContinueWatching_RespectsTheLimit()
    {
        using var fixture = new TestDirectoryFixture();
        (WatchStateService service, _) = await CreateAsync(fixture.DirectoryPath);
        var duration = TimeSpan.FromMinutes(20);

        for (int i = 0; i < 10; i++)
        {
            await service.RecordProgressAsync($@"D:\ep{i}.mkv", TimeSpan.FromMinutes(5), duration);
        }

        await Assert.That(service.GetContinueWatching(3).Count).IsEqualTo(3);
    }

    [Test]
    public async Task IsWatched_ReturnsFalseForUnknownLocation()
    {
        using var fixture = new TestDirectoryFixture();
        (WatchStateService service, _) = await CreateAsync(fixture.DirectoryPath);

        await Assert.That(service.IsWatched(@"D:\never-seen.mkv")).IsFalse();
        await Assert.That(service.GetProgress(@"D:\never-seen.mkv")).IsEqualTo(0d);
    }

    [Test]
    public async Task IsWatched_IsCaseInsensitiveForLocation()
    {
        // watch_state.location is a case-sensitive SQLite TEXT PRIMARY KEY while the in-memory
        // cache is case-insensitive; the service must normalize so the two never disagree about
        // whether a path recorded in one casing is the same item queried in another.
        using var fixture = new TestDirectoryFixture();
        (WatchStateService service, _) = await CreateAsync(fixture.DirectoryPath);

        await service.RecordProgressAsync(@"D:\Ep6.mkv", TimeSpan.FromMinutes(19), TimeSpan.FromMinutes(20));

        await Assert.That(service.IsWatched(@"d:\ep6.mkv")).IsTrue();
    }

    [Test]
    public async Task RecordProgressAsync_CaseVariantLocationsProduceExactlyOneRow()
    {
        using var fixture = new TestDirectoryFixture();
        (WatchStateService service, DatabaseService db) = await CreateAsync(fixture.DirectoryPath);

        await service.RecordProgressAsync(@"D:\Ep7.mkv", TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(20));
        await service.RecordProgressAsync(@"d:\EP7.mkv", TimeSpan.FromMinutes(8), TimeSpan.FromMinutes(20));

        List<WatchStateDto> rows = await db.ListWatchStateAsync();

        await Assert.That(rows.Count).IsEqualTo(1);
    }
}
