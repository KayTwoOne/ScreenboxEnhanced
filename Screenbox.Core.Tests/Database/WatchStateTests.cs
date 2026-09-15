using Microsoft.Extensions.Logging.Abstractions;
using Screenbox.Core.Models;
using Screenbox.Core.Services;
using Screenbox.Core.Tests.Helpers;

namespace Screenbox.Core.Tests.Database;

public sealed class WatchStateTests
{
    private static async Task<DatabaseService> CreateServiceAsync(string dir)
    {
        var db = new DatabaseService(NullLogger<DatabaseService>.Instance) { DbFolderPath = dir };
        await db.InitializeAsync();
        return db;
    }

    [Test]
    public async Task SaveWatchStateAsync_RoundTripsAllFields()
    {
        using var fixture = new TestDirectoryFixture();
        DatabaseService db = await CreateServiceAsync(fixture.DirectoryPath);
        var played = new DateTimeOffset(2026, 9, 15, 20, 30, 0, TimeSpan.Zero);

        await db.SaveWatchStateAsync(new WatchStateDto
        {
            Location = @"D:\Media\Anime\Death Note\Death Note - 01x01.mkv",
            Completed = true,
            LastPlayed = played,
            Duration = TimeSpan.FromMinutes(23),
            LastPosition = TimeSpan.FromMinutes(21)
        });

        WatchStateDto? loaded = await db.LoadWatchStateAsync(@"D:\Media\Anime\Death Note\Death Note - 01x01.mkv");

        await Assert.That(loaded).IsNotNull();
        await Assert.That(loaded!.Completed).IsTrue();
        await Assert.That(loaded.LastPlayed).IsEqualTo(played);
        await Assert.That(loaded.Duration).IsEqualTo(TimeSpan.FromMinutes(23));
        await Assert.That(loaded.LastPosition).IsEqualTo(TimeSpan.FromMinutes(21));
    }

    [Test]
    public async Task LoadWatchStateAsync_ReturnsNullForUnknownLocation()
    {
        using var fixture = new TestDirectoryFixture();
        DatabaseService db = await CreateServiceAsync(fixture.DirectoryPath);

        await Assert.That(await db.LoadWatchStateAsync(@"D:\nope.mkv")).IsNull();
    }

    [Test]
    public async Task SaveWatchStateAsync_OverwritesExistingRow()
    {
        using var fixture = new TestDirectoryFixture();
        DatabaseService db = await CreateServiceAsync(fixture.DirectoryPath);
        const string loc = @"D:\Media\Anime\Gachiakuta\ep1.mkv";

        await db.SaveWatchStateAsync(new WatchStateDto { Location = loc, Completed = false });
        await db.SaveWatchStateAsync(new WatchStateDto { Location = loc, Completed = true });

        WatchStateDto? loaded = await db.LoadWatchStateAsync(loc);
        await Assert.That(loaded!.Completed).IsTrue();
        await Assert.That((await db.ListWatchStateAsync()).Count).IsEqualTo(1);
    }

    [Test]
    public async Task WatchState_IsNotCappedLikePlaybackProgress()
    {
        // playback_progress holds at most 64 entries by design. Watched history must not:
        // a single series folder in the reference library has 37 episodes.
        using var fixture = new TestDirectoryFixture();
        DatabaseService db = await CreateServiceAsync(fixture.DirectoryPath);

        for (int i = 0; i < 200; i++)
        {
            await db.SaveWatchStateAsync(new WatchStateDto { Location = $@"D:\ep{i}.mkv", Completed = true });
        }

        await Assert.That((await db.ListWatchStateAsync()).Count).IsEqualTo(200);
    }

    [Test]
    public async Task WatchState_SurvivesAdditiveSchemaMigration()
    {
        using var fixture = new TestDirectoryFixture();
        DatabaseService db = await CreateServiceAsync(fixture.DirectoryPath);
        await db.SaveWatchStateAsync(new WatchStateDto { Location = @"D:\keep.mkv", Completed = true });

        // Simulate a future column being added, the way Phase 1's folder_metadata test does.
        await using (var connection = new Microsoft.Data.Sqlite.SqliteConnection(
            new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder
            {
                DataSource = System.IO.Path.Combine(fixture.DirectoryPath, "screenbox.db")
            }.ToString()))
        {
            await connection.OpenAsync();
            using var cmd = connection.CreateCommand();
            cmd.CommandText = "ALTER TABLE watch_state ADD COLUMN future_column TEXT;";
            cmd.ExecuteNonQuery();
        }

        DatabaseService db2 = await CreateServiceAsync(fixture.DirectoryPath);
        WatchStateDto? loaded = await db2.LoadWatchStateAsync(@"D:\keep.mkv");

        await Assert.That(loaded).IsNotNull();
        await Assert.That(loaded!.Completed).IsTrue();
    }

    [Test]
    public async Task DeleteWatchStateAsync_RemovesTheRow()
    {
        using var fixture = new TestDirectoryFixture();
        DatabaseService db = await CreateServiceAsync(fixture.DirectoryPath);
        const string loc = @"D:\gone.mkv";
        await db.SaveWatchStateAsync(new WatchStateDto { Location = loc, Completed = true });

        await db.DeleteWatchStateAsync(loc);

        await Assert.That(await db.LoadWatchStateAsync(loc)).IsNull();
    }
}
