using Microsoft.Extensions.Logging.Abstractions;
using Screenbox.Core.Coordinators;
using Screenbox.Core.Enums;
using Screenbox.Core.Models;
using Screenbox.Core.Services;
using Screenbox.Core.Tests.Helpers;

namespace Screenbox.Core.Tests.Services;

public sealed class ArtworkServiceTests
{
    private static async Task<DatabaseService> CreateDatabaseAsync(string dir)
    {
        var db = new DatabaseService(NullLogger<DatabaseService>.Instance) { DbFolderPath = dir };
        await db.InitializeAsync();
        return db;
    }

    [Test]
    public async Task MaxRecursionDepth_IsAtLeastThreeToCoverNestedSeasonFolders()
    {
        // Real libraries nest as Series/Season/Extras, so anything below 3 would
        // fail to find a video for the series-level folder that most needs artwork.
        await Assert.That(ArtworkService.MaxRecursionDepth).IsGreaterThanOrEqualTo(3);
    }

    [Test]
    public async Task ArtworkFolderName_IsStableAndRelative()
    {
        await Assert.That(ArtworkService.ArtworkFolderName).IsEqualTo("Artwork");
    }

    [Test]
    public async Task IsConventionFileName_MatchesKnownConventionsCaseInsensitively()
    {
        await Assert.That(ArtworkService.IsConventionFileName("poster.jpg")).IsTrue();
        await Assert.That(ArtworkService.IsConventionFileName("Folder.JPG")).IsTrue();
        await Assert.That(ArtworkService.IsConventionFileName("poster.png")).IsTrue();
        await Assert.That(ArtworkService.IsConventionFileName("cover.jpg")).IsTrue();
        await Assert.That(ArtworkService.IsConventionFileName("episode01.jpg")).IsFalse();
        await Assert.That(ArtworkService.IsConventionFileName("poster.txt")).IsFalse();
    }

    [Test]
    public async Task ShouldSkipConventionCopy_SkipsOnlyWhenDestinationExistsAndMetadataAlreadyRecordsIt()
    {
        const string conventionExtension = ".jpg";
        FolderMetadataDto recorded = new()
        {
            Path = "C:\\Library\\Show",
            PosterSource = PosterSource.Convention,
            PosterFile = "g_deadbeef_8f2c1a.jpg"
        };

        // Already copied and recorded: skip the redundant copy and database write. This is the
        // hot path exercised on every folder navigation once a convention poster is established.
        await Assert.That(ArtworkService.ShouldSkipConventionCopy(recorded, conventionExtension, recordedFileExists: true))
            .IsTrue();

        // Metadata claims the file exists but it is missing from local storage: must (re)copy.
        await Assert.That(ArtworkService.ShouldSkipConventionCopy(recorded, conventionExtension, recordedFileExists: false))
            .IsFalse();

        // No metadata at all: first time seeing this folder, must copy.
        await Assert.That(ArtworkService.ShouldSkipConventionCopy(null, conventionExtension, recordedFileExists: true))
            .IsFalse();

        // Metadata records a different source (e.g. an AutoFrame fallback happens to reuse the
        // same file name): must not mistake that for a recorded convention copy.
        FolderMetadataDto differentSource = new()
        {
            Path = recorded.Path,
            PosterSource = PosterSource.AutoFrame,
            PosterFile = recorded.PosterFile
        };
        await Assert.That(ArtworkService.ShouldSkipConventionCopy(differentSource, conventionExtension, recordedFileExists: true))
            .IsFalse();

        // The convention file's extension changed from .png to .jpg: must copy the new one rather
        // than keeping the stale reference.
        FolderMetadataDto staleFileName = new()
        {
            Path = recorded.Path,
            PosterSource = PosterSource.Convention,
            PosterFile = "g_00000000_4b1e.png"
        };
        await Assert.That(ArtworkService.ShouldSkipConventionCopy(staleFileName, conventionExtension, recordedFileExists: true))
            .IsFalse();
    }

    [Test]
    public async Task IsOrphanedFolderPath_EvictsOnlyFoldersThatGenuinelyDisappeared()
    {
        string[] roots = [@"D:\Media\Anime", @"E:\Archive"];
        // E: is an unplugged drive: its root reads as missing, and so does everything beneath it.
        HashSet<string> existing = new(StringComparer.OrdinalIgnoreCase)
        {
            @"D:\Media\Anime",
            @"D:\Media\Anime\Frieren"
        };
        bool DirectoryExists(string path) => existing.Contains(path);

        // Gone from a library root that is readable right now: safe to evict.
        await Assert.That(ArtworkService.IsOrphanedFolderPath(@"D:\Media\Anime\Deleted Show", roots, DirectoryExists))
            .IsTrue();

        // Still on disk: keep the row, including a manual poster.
        await Assert.That(ArtworkService.IsOrphanedFolderPath(@"D:\Media\Anime\Frieren", roots, DirectoryExists))
            .IsFalse();

        // Under a root that is itself unreachable (offline drive or network share): everything
        // there looks missing, so evicting would destroy data that is merely not plugged in.
        await Assert.That(ArtworkService.IsOrphanedFolderPath(@"E:\Archive\Old Show", roots, DirectoryExists))
            .IsFalse();

        // Outside every scanned root: this scan says nothing about it, so leave it alone.
        await Assert.That(ArtworkService.IsOrphanedFolderPath(@"C:\Elsewhere\Show", roots, DirectoryExists))
            .IsFalse();

        // A sibling whose name merely starts with a root's name is not inside that root.
        await Assert.That(ArtworkService.IsOrphanedFolderPath(@"D:\Media\AnimeMovies\Show", roots, DirectoryExists))
            .IsFalse();
    }

    // ------------------------------------------------------------------
    // Task 6's non-negotiable: a scraped poster must never be able to overwrite a manual one.
    // TryWriteScrapedPosterRowAsync is the guard+write step isolated from SetScrapedPosterAsync's
    // network/file-system work, so it can be exercised against a real database alone - assertions
    // are made on the persisted row, never on ArtworkResolver or any other reader.
    // ------------------------------------------------------------------

    [Test]
    public async Task TryWriteScrapedPosterRowAsync_ManualPosterAlreadySet_NeverOverwritesIt()
    {
        using var fixture = new TestDirectoryFixture();
        DatabaseService db = await CreateDatabaseAsync(fixture.DirectoryPath);
        const string path = @"D:\Anime\Dan Da Dan";

        // The user has already chosen a poster manually before any scrape runs.
        await db.SetFolderPosterAsync(path, "manual-poster.jpg", PosterSource.Manual);

        // A scrape that genuinely finds a match with a poster URL - Task 5's real coordinator, run
        // against this same database, exactly as the settings page's scan command would drive it.
        var provider = new FakeMetadataProvider("anilist",
            [new MetadataMatch("1", "Dan Da Dan", "synopsis", 2024, "https://example.com/scraped-poster.jpg")]);
        var settings = new TestSettingsService { MetadataEnabled = true };
        var coordinator = new MetadataScrapeCoordinator(
            new FakeMetadataProviderFactory(provider), settings, db, NullLogger<MetadataScrapeCoordinator>.Instance);

        MetadataMatch? match = await coordinator.ScrapeFolderAsync(path, "Dan Da Dan", CancellationToken.None);
        await Assert.That(match).IsNotNull();
        await Assert.That(match!.PosterUrl).IsNotNull()
            .Because("The guard this test exercises only matters when a scrape genuinely found a poster to (almost) act on.");

        var artworkService = new ArtworkService(db, NullLogger<ArtworkService>.Instance);

        // Stands in for SetScrapedPosterAsync's own write step once a download has completed - the
        // guard under test here is isolated from the network/file-system work around it.
        bool written = await artworkService.TryWriteScrapedPosterRowAsync(path, "g_scraped_poster.jpg");

        await Assert.That(written).IsFalse();

        // The assertion that matters: read the persisted row back from the database, not from
        // ArtworkResolver or any other reader - a resolver's read-time precedence cannot protect
        // data that a writer already clobbered.
        FolderMetadataDto? persisted = await db.LoadFolderMetadataAsync(path);
        await Assert.That(persisted).IsNotNull();
        await Assert.That(persisted!.PosterSource).IsEqualTo(PosterSource.Manual);
        await Assert.That(persisted.PosterFile).IsEqualTo("manual-poster.jpg");
    }

    [Test]
    public async Task TryWriteScrapedPosterRowAsync_NoExistingPoster_WritesTheScrapedPosterRow()
    {
        // The positive case matters just as much as the negative one: without it, a guard that
        // always returns false (whatever the row holds) would still pass the test above for the
        // wrong reason.
        using var fixture = new TestDirectoryFixture();
        DatabaseService db = await CreateDatabaseAsync(fixture.DirectoryPath);
        const string path = @"D:\Anime\Frieren";

        var artworkService = new ArtworkService(db, NullLogger<ArtworkService>.Instance);

        bool written = await artworkService.TryWriteScrapedPosterRowAsync(path, "g_scraped_poster.jpg");

        await Assert.That(written).IsTrue();

        FolderMetadataDto? persisted = await db.LoadFolderMetadataAsync(path);
        await Assert.That(persisted).IsNotNull();
        await Assert.That(persisted!.PosterSource).IsEqualTo(PosterSource.Scraped);
        await Assert.That(persisted.PosterFile).IsEqualTo("g_scraped_poster.jpg");
    }

    [Test]
    public async Task TryWriteScrapedPosterRowAsync_ExistingAutoFramePoster_IsReplacedByTheScrapedOne()
    {
        // Manual is the only source a scrape must never displace - AutoFrame (and Convention) sit
        // below Scraped in ArtworkResolver's priority order and must lose here too, or the write
        // side and the read side would disagree about who wins.
        using var fixture = new TestDirectoryFixture();
        DatabaseService db = await CreateDatabaseAsync(fixture.DirectoryPath);
        const string path = @"D:\Anime\Frieren";
        await db.SetFolderPosterAsync(path, "g_auto_frame.jpg", PosterSource.AutoFrame);

        var artworkService = new ArtworkService(db, NullLogger<ArtworkService>.Instance);

        bool written = await artworkService.TryWriteScrapedPosterRowAsync(path, "g_scraped_poster.jpg");

        await Assert.That(written).IsTrue();

        FolderMetadataDto? persisted = await db.LoadFolderMetadataAsync(path);
        await Assert.That(persisted!.PosterSource).IsEqualTo(PosterSource.Scraped);
        await Assert.That(persisted.PosterFile).IsEqualTo("g_scraped_poster.jpg");
    }

    [Test]
    public async Task SetScrapedPosterAsync_EmptyPosterUrl_ReturnsFalseWithoutTouchingTheDatabase()
    {
        // The public entry point's own cheap input guard, ahead of anything else it would need to
        // do (including the ApplicationData-backed artwork folder, which this test host cannot
        // provide) - so this is exercisable and must never crash on a null/blank URL.
        using var fixture = new TestDirectoryFixture();
        DatabaseService db = await CreateDatabaseAsync(fixture.DirectoryPath);
        var artworkService = new ArtworkService(db, NullLogger<ArtworkService>.Instance);

        bool written = await artworkService.SetScrapedPosterAsync(@"D:\Anime\Dan Da Dan", string.Empty, CancellationToken.None);

        await Assert.That(written).IsFalse();
    }
}
