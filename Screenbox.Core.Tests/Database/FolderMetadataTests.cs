using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Screenbox.Core.Enums;
using Screenbox.Core.Models;
using Screenbox.Core.Services;
using Screenbox.Core.Tests.Helpers;

namespace Screenbox.Core.Tests.Database;

public sealed class FolderMetadataTests
{
    private static async Task<DatabaseService> CreateServiceAsync(string dir)
    {
        var db = new DatabaseService(NullLogger<DatabaseService>.Instance) { DbFolderPath = dir };
        await db.InitializeAsync();
        return db;
    }

    private static void Execute(SqliteConnection connection, string sql)
    {
        using SqliteCommand cmd = connection.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    private static SqliteConnection OpenRaw(string dir)
    {
        var connection = new SqliteConnection($"Data Source={Path.Combine(dir, "screenbox.db")}");
        connection.Open();
        return connection;
    }

    [Test]
    public async Task SaveFolderMetadataAsync_RoundTripsAllFields()
    {
        using var fixture = new TestDirectoryFixture();
        DatabaseService db = await CreateServiceAsync(fixture.DirectoryPath);

        var record = new FolderMetadataDto
        {
            Path = @"D:\Media\Anime\Dan Da Dan",
            CustomTitle = "Dandadan",
            PosterFile = "a1b2c3.jpg",
            PosterSource = PosterSource.Manual,
            ProviderPin = "anilist:171018",
            SortOrder = 3
        };

        await db.SaveFolderMetadataAsync(record);
        FolderMetadataDto? loaded = await db.LoadFolderMetadataAsync(record.Path);

        await Assert.That(loaded).IsNotNull();
        await Assert.That(loaded!.CustomTitle).IsEqualTo("Dandadan");
        await Assert.That(loaded.PosterFile).IsEqualTo("a1b2c3.jpg");
        await Assert.That(loaded.PosterSource).IsEqualTo(PosterSource.Manual);
        await Assert.That(loaded.ProviderPin).IsEqualTo("anilist:171018");
        await Assert.That(loaded.SortOrder).IsEqualTo(3);
    }

    [Test]
    public async Task LoadFolderMetadataAsync_ReturnsNullForUnknownPath()
    {
        using var fixture = new TestDirectoryFixture();
        DatabaseService db = await CreateServiceAsync(fixture.DirectoryPath);

        FolderMetadataDto? loaded = await db.LoadFolderMetadataAsync(@"D:\does\not\exist");

        await Assert.That(loaded).IsNull();
    }

    [Test]
    public async Task FolderMetadata_SurvivesReinitialization()
    {
        using var fixture = new TestDirectoryFixture();
        DatabaseService db = await CreateServiceAsync(fixture.DirectoryPath);
        await db.SaveFolderMetadataAsync(new FolderMetadataDto
        {
            Path = @"D:\Media\Anime\Gachiakuta",
            CustomTitle = "Gachiakuta",
            PosterSource = PosterSource.Manual
        });

        // A second service over the same folder simulates an app restart.
        DatabaseService db2 = await CreateServiceAsync(fixture.DirectoryPath);
        FolderMetadataDto? loaded = await db2.LoadFolderMetadataAsync(@"D:\Media\Anime\Gachiakuta");

        await Assert.That(loaded).IsNotNull();
        await Assert.That(loaded!.CustomTitle).IsEqualTo("Gachiakuta");
    }

    [Test]
    public async Task FolderMetadata_SurvivesAdditiveSchemaMigration()
    {
        // The load-bearing property of the whole feature: adding a column in a later build must
        // migrate the table, not drop it. Custom titles and manual poster choices cannot be
        // reconstructed from anything else, so losing them is permanent data loss.
        using var fixture = new TestDirectoryFixture();
        DatabaseService db = await CreateServiceAsync(fixture.DirectoryPath);
        await db.SaveFolderMetadataAsync(new FolderMetadataDto
        {
            Path = @"D:\Media\Anime\Frieren",
            CustomTitle = "Frieren: Beyond Journey's End",
            PosterFile = "m_beefcafe_1a2b.jpg",
            PosterSource = PosterSource.Manual,
            ProviderPin = "anilist:154587",
            SortOrder = 7
        });

        // Roll the stored table back to an older shape that predates provider_pin and sort_order,
        // keeping the row. Re-initialising must then add those columns around the existing data.
        using (SqliteConnection connection = OpenRaw(fixture.DirectoryPath))
        {
            Execute(connection, "ALTER TABLE folder_metadata DROP COLUMN provider_pin;");
            Execute(connection, "ALTER TABLE folder_metadata DROP COLUMN sort_order;");
        }

        DatabaseService migrated = await CreateServiceAsync(fixture.DirectoryPath);
        FolderMetadataDto? loaded = await migrated.LoadFolderMetadataAsync(@"D:\Media\Anime\Frieren");

        await Assert.That(loaded).IsNotNull()
            .Because("An added column must migrate folder_metadata in place, never drop it.");
        await Assert.That(loaded!.CustomTitle).IsEqualTo("Frieren: Beyond Journey's End");
        await Assert.That(loaded.PosterFile).IsEqualTo("m_beefcafe_1a2b.jpg");
        await Assert.That(loaded.PosterSource).IsEqualTo(PosterSource.Manual);
        // The re-added columns hold no value for a row written before they existed.
        await Assert.That(loaded.ProviderPin).IsNull();
        await Assert.That(loaded.SortOrder).IsNull();
    }

    [Test]
    public async Task FolderMetadata_SurvivesNonAdditiveSchemaDrift()
    {
        // Drift that ALTER TABLE ADD COLUMN cannot express (here, a column the current build knows
        // nothing about) still has to preserve the rows by copying them into the new shape.
        using var fixture = new TestDirectoryFixture();
        DatabaseService db = await CreateServiceAsync(fixture.DirectoryPath);
        await db.SaveFolderMetadataAsync(new FolderMetadataDto
        {
            Path = @"D:\Media\Anime\Gachiakuta",
            CustomTitle = "Gachiakuta",
            PosterFile = "m_0badf00d_2c3d.png",
            PosterSource = PosterSource.Manual,
            SortOrder = 2
        });

        using (SqliteConnection connection = OpenRaw(fixture.DirectoryPath))
        {
            Execute(connection, "ALTER TABLE folder_metadata ADD COLUMN retired_column TEXT;");
        }

        DatabaseService migrated = await CreateServiceAsync(fixture.DirectoryPath);
        FolderMetadataDto? loaded = await migrated.LoadFolderMetadataAsync(@"D:\Media\Anime\Gachiakuta");

        await Assert.That(loaded).IsNotNull()
            .Because("Unsupported drift must rebuild the table from the existing rows, not drop them.");
        await Assert.That(loaded!.CustomTitle).IsEqualTo("Gachiakuta");
        await Assert.That(loaded.PosterFile).IsEqualTo("m_0badf00d_2c3d.png");
        await Assert.That(loaded.PosterSource).IsEqualTo(PosterSource.Manual);
        await Assert.That(loaded.SortOrder).IsEqualTo(2);

        // The rebuild must leave the table on the expected shape, with no staging table behind.
        using SqliteConnection verify = OpenRaw(fixture.DirectoryPath);
        using SqliteCommand cmd = verify.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='folder_metadata_migrating';";
        await Assert.That((long)(cmd.ExecuteScalar() ?? 0L)).IsEqualTo(0L);
    }

    [Test]
    public async Task DeleteFolderMetadataAsync_RemovesTheRecord()
    {
        using var fixture = new TestDirectoryFixture();
        DatabaseService db = await CreateServiceAsync(fixture.DirectoryPath);
        const string path = @"D:\Media\Anime\Suzume";
        await db.SaveFolderMetadataAsync(new FolderMetadataDto { Path = path, CustomTitle = "Suzume" });

        await db.DeleteFolderMetadataAsync(path);

        await Assert.That(await db.LoadFolderMetadataAsync(path)).IsNull();
    }
}
