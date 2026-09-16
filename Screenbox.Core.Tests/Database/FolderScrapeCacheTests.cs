using Microsoft.Extensions.Logging.Abstractions;
using Screenbox.Core.Enums;
using Screenbox.Core.Models;
using Screenbox.Core.Services;
using Screenbox.Core.Tests.Helpers;

namespace Screenbox.Core.Tests.Database;

public sealed class FolderScrapeCacheTests
{
    private static async Task<DatabaseService> CreateServiceAsync(string dir)
    {
        var db = new DatabaseService(NullLogger<DatabaseService>.Instance) { DbFolderPath = dir };
        await db.InitializeAsync();
        return db;
    }

    [Test]
    public async Task SaveScrapeCacheAsync_RoundTripsAllFields()
    {
        using var fixture = new TestDirectoryFixture();
        DatabaseService db = await CreateServiceAsync(fixture.DirectoryPath);

        var lastScraped = new DateTimeOffset(2026, 9, 16, 12, 0, 0, TimeSpan.Zero);
        var record = new FolderScrapeCacheDto
        {
            Path = @"D:\Anime\Dan Da Dan",
            Provider = "anilist",
            ProviderId = "171018",
            Synopsis = "A high school romantic comedy that spirals into the occult and the extraterrestrial.",
            Year = 2024,
            ScrapedPoster = "s_abc123.jpg",
            LastScraped = lastScraped
        };

        await db.SaveScrapeCacheAsync(record);
        FolderScrapeCacheDto? loaded = await db.LoadScrapeCacheAsync(record.Path);

        await Assert.That(loaded).IsNotNull();
        await Assert.That(loaded!.Path).IsEqualTo(record.Path);
        await Assert.That(loaded.Provider).IsEqualTo("anilist");
        await Assert.That(loaded.ProviderId).IsEqualTo("171018");
        await Assert.That(loaded.Synopsis).IsEqualTo(record.Synopsis);
        await Assert.That(loaded.Year).IsEqualTo(2024);
        await Assert.That(loaded.ScrapedPoster).IsEqualTo("s_abc123.jpg");
        await Assert.That(loaded.LastScraped).IsEqualTo(lastScraped);
    }

    [Test]
    public async Task LoadScrapeCacheAsync_ReturnsNullForUnknownPath()
    {
        using var fixture = new TestDirectoryFixture();
        DatabaseService db = await CreateServiceAsync(fixture.DirectoryPath);

        FolderScrapeCacheDto? loaded = await db.LoadScrapeCacheAsync(@"D:\does\not\exist");

        await Assert.That(loaded).IsNull();
    }

    [Test]
    public async Task SaveScrapeCacheAsync_OverwritesExistingRowRatherThanDuplicating()
    {
        using var fixture = new TestDirectoryFixture();
        DatabaseService db = await CreateServiceAsync(fixture.DirectoryPath);
        const string path = @"D:\Anime\Frieren";

        await db.SaveScrapeCacheAsync(new FolderScrapeCacheDto
        {
            Path = path,
            Provider = "anilist",
            ProviderId = "154587",
            Synopsis = "Old synopsis",
            Year = 2023
        });
        await db.SaveScrapeCacheAsync(new FolderScrapeCacheDto
        {
            Path = path,
            Provider = "anilist",
            ProviderId = "154587",
            Synopsis = "New synopsis",
            Year = 2023
        });

        FolderScrapeCacheDto? loaded = await db.LoadScrapeCacheAsync(path);
        await Assert.That(loaded).IsNotNull();
        await Assert.That(loaded!.Synopsis).IsEqualTo("New synopsis");
    }

    [Test]
    public async Task ClearScrapeCacheAsync_EmptiesTheTable()
    {
        using var fixture = new TestDirectoryFixture();
        DatabaseService db = await CreateServiceAsync(fixture.DirectoryPath);
        await db.SaveScrapeCacheAsync(new FolderScrapeCacheDto
        {
            Path = @"D:\Anime\Gachiakuta",
            Provider = "anilist"
        });

        await db.ClearScrapeCacheAsync();

        FolderScrapeCacheDto? loaded = await db.LoadScrapeCacheAsync(@"D:\Anime\Gachiakuta");
        await Assert.That(loaded).IsNull();
    }

    [Test]
    public async Task ClearScrapeCacheAsync_DoesNotTouchDurableFolderMetadata()
    {
        using var fixture = new TestDirectoryFixture();
        DatabaseService db = await CreateServiceAsync(fixture.DirectoryPath);
        await db.SaveFolderMetadataAsync(new FolderMetadataDto
        {
            Path = @"D:\Anime\Dan Da Dan",
            CustomTitle = "Dandadan",
            PosterFile = "m_abc.png",
            PosterSource = PosterSource.Manual
        });
        await db.SaveScrapeCacheAsync(new FolderScrapeCacheDto
        {
            Path = @"D:\Anime\Dan Da Dan",
            Provider = "anilist",
            Synopsis = "..."
        });

        await db.ClearScrapeCacheAsync();

        FolderMetadataDto? durable = await db.LoadFolderMetadataAsync(@"D:\Anime\Dan Da Dan");
        await Assert.That(durable).IsNotNull();
        await Assert.That(durable!.CustomTitle).IsEqualTo("Dandadan");
        await Assert.That(durable.PosterFile).IsEqualTo("m_abc.png");
    }
}
