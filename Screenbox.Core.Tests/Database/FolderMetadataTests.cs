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
