using Microsoft.Extensions.Logging.Abstractions;
using Screenbox.Core.Enums;
using Screenbox.Core.Helpers;
using Screenbox.Core.Models;
using Screenbox.Core.Services;
using Screenbox.Core.Tests.Helpers;
using static Screenbox.Core.Tests.Helpers.TestImages;

namespace Screenbox.Core.Tests.Services;

/// <summary>
/// The display card end to end, against real image files and the real Windows imaging codecs:
/// what <see cref="ArtworkService.GetDisplayFileNameAsync"/> hands a folder tile to draw.
/// </summary>
public sealed class ArtworkCardServiceTests
{
    private static readonly Rgb Orange = new(240, 120, 40);
    private static readonly Rgb Magenta = new(255, 0, 255);
    private static readonly Rgb Green = new(0, 200, 0);
    private static readonly Rgb Blue = new(0, 0, 255);
    private static readonly Rgb Red = new(255, 0, 0);
    private static readonly Rgb Yellow = new(255, 255, 0);

    // Wide enough for JPEG's own rounding, which a card goes through, and well short of the
    // difference between any two colours the tests tell apart.
    private const int JpegTolerance = 14;

    private const int PosterWidth = 120;
    private const int PosterHeight = 180;
    private const int CardWidth = ArtworkCard.Width;
    private const int CardHeight = ArtworkCard.Height;

    /// <summary>
    /// A display-only service: it never reads the database, so one that throws on any call proves it.
    /// </summary>
    private static ArtworkService CreateService(string artworkDirectory) =>
        new(new ThrowingDatabaseService(), NullLogger<ArtworkService>.Instance) { ArtworkFolderPath = artworkDirectory };

    /// <summary>
    /// A 120x180 portrait poster: a solid background, a coloured block in its centre, a green marker
    /// in its top-left corner and a blue one in its bottom-right.
    /// </summary>
    private static async Task SavePortraitPosterAsync(string path, Rgb background, Rgb centre)
    {
        byte[] pixels = Solid(PosterWidth, PosterHeight, background);
        Fill(pixels, PosterWidth, 40, 60, 80, 120, centre);
        Fill(pixels, PosterWidth, 0, 0, 12, 12, Green);
        Fill(pixels, PosterWidth, PosterWidth - 12, PosterHeight - 12, PosterWidth, PosterHeight, Blue);
        await SavePngAsync(path, pixels, PosterWidth, PosterHeight);
    }

    private static string[] CardFilesIn(string directory) =>
        Directory.GetFiles(directory, "*" + ArtworkService.CardSuffix);

    // ------------------------------------------------------------------
    // What a card is
    // ------------------------------------------------------------------

    [Test]
    public async Task GetDisplayFileNameAsync_PortraitPoster_ReturnsAWideCardShowingTheWholePosterOnADarkenedWash()
    {
        using var fixture = new TestDirectoryFixture();
        const string poster = "g_portrait.png";
        string posterPath = Path.Combine(fixture.DirectoryPath, poster);
        await SavePortraitPosterAsync(posterPath, Orange, Magenta);
        byte[] posterBytesBefore = await File.ReadAllBytesAsync(posterPath);
        ArtworkService service = CreateService(fixture.DirectoryPath);

        string? display = await service.GetDisplayFileNameAsync(poster);

        await Assert.That(display).IsEqualTo(poster + ".card.jpg");
        (byte[] pixels, int width, int height) = await ReadAsync(Path.Combine(fixture.DirectoryPath, display!));

        // Exactly the card size: 4x the 232x128 tile, so the tile's UniformToFill crops nothing more.
        await Assert.That(width).IsEqualTo(CardWidth);
        await Assert.That(height).IsEqualTo(CardHeight);

        // The centre column is the poster itself.
        await Assert.That(PixelAt(pixels, width, width / 2, height / 2).DistanceTo(Magenta)).IsLessThanOrEqualTo(JpegTolerance);

        // The far left and right edges are NOT the poster's own edge pixel: they are a darkened wash
        // of its colours - half brightness, so (120, 60, 20) - not the orange itself.
        Rgb darkenedOrange = new(120, 60, 20);
        foreach (int x in new[] { 0, width - 1 })
        {
            Rgb edge = PixelAt(pixels, width, x, height / 2);
            await Assert.That(edge.DistanceTo(Orange)).IsGreaterThan(60);
            await Assert.That(edge.DistanceTo(darkenedOrange)).IsLessThanOrEqualTo(JpegTolerance);
        }

        // The whole poster is there, uncropped: its top-left and bottom-right corner markers sit in
        // the corners of the centred, full-height foreground (341 wide, so 293..633).
        int left = (width - 341) / 2;
        int right = left + 341 - 1;
        await Assert.That(PixelAt(pixels, width, left + 10, 10).DistanceTo(Green)).IsLessThanOrEqualTo(JpegTolerance);
        await Assert.That(PixelAt(pixels, width, right - 10, height - 11).DistanceTo(Blue)).IsLessThanOrEqualTo(JpegTolerance);

        // The stored poster is what it was: a card is derived, never a replacement.
        await Assert.That((await File.ReadAllBytesAsync(posterPath)).AsSpan().SequenceEqual(posterBytesBefore)).IsTrue();
        await Assert.That(service.CardsComposed).IsEqualTo(1);
    }

    [Test]
    public async Task GetDisplayFileNameAsync_LandscapePoster_ComesBackUnchangedAndNoCardIsMade()
    {
        using var fixture = new TestDirectoryFixture();
        const string poster = "g_landscape.png";
        await SavePngAsync(Path.Combine(fixture.DirectoryPath, poster), Solid(200, 100, Orange), 200, 100);
        ArtworkService service = CreateService(fixture.DirectoryPath);

        string? display = await service.GetDisplayFileNameAsync(poster);

        await Assert.That(display).IsEqualTo(poster);
        await Assert.That(CardFilesIn(fixture.DirectoryPath)).IsEmpty();
        await Assert.That(Directory.GetFiles(fixture.DirectoryPath)).Count().IsEqualTo(1)
            .Because("Not even a scratch file may be left behind.");
        await Assert.That(service.CardsComposed).IsEqualTo(0);
    }

    [Test]
    public async Task GetDisplayFileNameAsync_NothingToDisplay_ReturnsItsInputWithoutThrowing()
    {
        using var fixture = new TestDirectoryFixture();
        ArtworkService service = CreateService(fixture.DirectoryPath);

        await Assert.That(await service.GetDisplayFileNameAsync(null)).IsNull();
        await Assert.That(await service.GetDisplayFileNameAsync(string.Empty)).IsEqualTo(string.Empty);
        await Assert.That(await service.GetDisplayFileNameAsync("g_missing.png")).IsEqualTo("g_missing.png");
        await Assert.That(CardFilesIn(fixture.DirectoryPath)).IsEmpty();
    }

    [Test]
    public async Task GetDisplayFileNameAsync_PosterTheDecoderCannotRead_FallsBackToTheOriginal()
    {
        // Artwork must never vanish because a card could not be built: a poster that is not an image
        // at all still comes back by its own name, and nothing escapes.
        using var fixture = new TestDirectoryFixture();
        const string poster = "g_corrupt.png";
        await File.WriteAllBytesAsync(Path.Combine(fixture.DirectoryPath, poster), new byte[200]);
        ArtworkService service = CreateService(fixture.DirectoryPath);

        string? display = await service.GetDisplayFileNameAsync(poster);

        await Assert.That(display).IsEqualTo(poster);
        await Assert.That(CardFilesIn(fixture.DirectoryPath)).IsEmpty();
    }

    [Test]
    public async Task GetDisplayFileNameAsync_PhotoStoredSidewaysWithAnExifTag_IsJudgedAndDrawnTheWayItIsMeantToBeSeen()
    {
        // Stored 180 wide by 120 tall, tagged "rotate a quarter turn clockwise": displayed it is
        // 120x180, a portrait, so it needs a card - and the card must show it the right way up.
        // Stored quadrants: top-left red, top-right green, bottom-left blue, bottom-right yellow.
        // A quarter turn clockwise carries them to: top-left blue, top-right red, bottom-right
        // green, bottom-left yellow.
        using var fixture = new TestDirectoryFixture();
        const string poster = "g_sideways.jpg";
        byte[] pixels = Solid(180, 120, Red);
        Fill(pixels, 180, 90, 0, 180, 60, Green);
        Fill(pixels, 180, 0, 60, 90, 120, Blue);
        Fill(pixels, 180, 90, 60, 180, 120, Yellow);
        await SaveJpegAsync(Path.Combine(fixture.DirectoryPath, poster), pixels, 180, 120, orientation: 6);
        ArtworkService service = CreateService(fixture.DirectoryPath);

        string? display = await service.GetDisplayFileNameAsync(poster);

        await Assert.That(display).IsEqualTo(poster + ".card.jpg");
        (byte[] card, int width, _) = await ReadAsync(Path.Combine(fixture.DirectoryPath, display!));
        int left = (width - 341) / 2;
        int right = left + 341 - 1;
        await Assert.That(PixelAt(card, width, left + 85, 128).DistanceTo(Blue)).IsLessThanOrEqualTo(JpegTolerance);
        await Assert.That(PixelAt(card, width, right - 85, 128).DistanceTo(Red)).IsLessThanOrEqualTo(JpegTolerance);
        await Assert.That(PixelAt(card, width, right - 85, 384).DistanceTo(Green)).IsLessThanOrEqualTo(JpegTolerance);
        await Assert.That(PixelAt(card, width, left + 85, 384).DistanceTo(Yellow)).IsLessThanOrEqualTo(JpegTolerance);
    }

    // ------------------------------------------------------------------
    // Caching and staleness
    // ------------------------------------------------------------------

    [Test]
    public async Task GetDisplayFileNameAsync_AskedAgain_ReusesTheCardInsteadOfRebuildingIt()
    {
        using var fixture = new TestDirectoryFixture();
        const string poster = "g_reuse.png";
        await SavePortraitPosterAsync(Path.Combine(fixture.DirectoryPath, poster), Orange, Magenta);
        ArtworkService service = CreateService(fixture.DirectoryPath);

        string? first = await service.GetDisplayFileNameAsync(poster);
        string? second = await service.GetDisplayFileNameAsync(poster);

        await Assert.That(second).IsEqualTo(first);
        await Assert.That(service.CardsComposed).IsEqualTo(1);
    }

    [Test]
    public async Task GetDisplayFileNameAsync_CardFromAPreviousSession_IsReusedWithoutADecode()
    {
        // A fresh service has no memory of the first: the card on disk alone must be enough.
        using var fixture = new TestDirectoryFixture();
        const string poster = "g_restart.png";
        await SavePortraitPosterAsync(Path.Combine(fixture.DirectoryPath, poster), Orange, Magenta);
        await CreateService(fixture.DirectoryPath).GetDisplayFileNameAsync(poster);
        ArtworkService restarted = CreateService(fixture.DirectoryPath);

        string? display = await restarted.GetDisplayFileNameAsync(poster);

        await Assert.That(display).IsEqualTo(poster + ".card.jpg");
        await Assert.That(restarted.CardsComposed).IsEqualTo(0);
    }

    [Test]
    public async Task GetDisplayFileNameAsync_PosterReplacedUnderTheSameName_RebuildsTheCard()
    {
        // A poster of the same name can be replaced; the card made from the old one is then stale
        // and the name alone cannot say so. The poster being NEWER than the card is what does.
        using var fixture = new TestDirectoryFixture();
        const string poster = "g_replaced.png";
        string posterPath = Path.Combine(fixture.DirectoryPath, poster);
        string cardPath = Path.Combine(fixture.DirectoryPath, poster + ".card.jpg");
        await SavePortraitPosterAsync(posterPath, Orange, Magenta);
        ArtworkService service = CreateService(fixture.DirectoryPath);
        await service.GetDisplayFileNameAsync(poster);
        (byte[] before, int width, _) = await ReadAsync(cardPath);
        await Assert.That(PixelAt(before, width, width / 2, CardHeight / 2).DistanceTo(Magenta)).IsLessThanOrEqualTo(JpegTolerance);

        await SavePortraitPosterAsync(posterPath, Orange, Green);
        File.SetLastWriteTimeUtc(posterPath, File.GetLastWriteTimeUtc(cardPath).AddMinutes(1));

        string? display = await service.GetDisplayFileNameAsync(poster);

        await Assert.That(display).IsEqualTo(poster + ".card.jpg");
        (byte[] after, _, _) = await ReadAsync(cardPath);
        await Assert.That(PixelAt(after, width, width / 2, CardHeight / 2).DistanceTo(Green)).IsLessThanOrEqualTo(JpegTolerance)
            .Because("The card must show the new poster, not the one it was first made from.");
        await Assert.That(service.CardsComposed).IsEqualTo(2);
    }

    [Test]
    public async Task GetDisplayFileNameAsync_PosterNotNewerThanTheCard_KeepsTheCardAsItIs()
    {
        // The control for the test above: the same replacement, but dated BEFORE the card. The card
        // is then not stale by the rule, and must be left alone - so the rule is the poster's date
        // against the card's, not "rebuild whenever anything changed".
        using var fixture = new TestDirectoryFixture();
        const string poster = "g_not_newer.png";
        string posterPath = Path.Combine(fixture.DirectoryPath, poster);
        string cardPath = Path.Combine(fixture.DirectoryPath, poster + ".card.jpg");
        await SavePortraitPosterAsync(posterPath, Orange, Magenta);
        ArtworkService service = CreateService(fixture.DirectoryPath);
        await service.GetDisplayFileNameAsync(poster);

        await SavePortraitPosterAsync(posterPath, Orange, Green);
        File.SetLastWriteTimeUtc(posterPath, File.GetLastWriteTimeUtc(cardPath).AddMinutes(-1));

        await service.GetDisplayFileNameAsync(poster);

        (byte[] after, int width, _) = await ReadAsync(cardPath);
        await Assert.That(PixelAt(after, width, width / 2, CardHeight / 2).DistanceTo(Magenta)).IsLessThanOrEqualTo(JpegTolerance);
        await Assert.That(service.CardsComposed).IsEqualTo(1);
    }

    [Test]
    public async Task GetDisplayFileNameAsync_PosterDatedInTheFuture_IsNotRebuiltOnEveryRequest()
    {
        // A poster whose timestamp is ahead of the clock would otherwise always look newer than the
        // card just written from it, and be rebuilt every time a tile appeared.
        using var fixture = new TestDirectoryFixture();
        const string poster = "g_future.png";
        string posterPath = Path.Combine(fixture.DirectoryPath, poster);
        await SavePortraitPosterAsync(posterPath, Orange, Magenta);
        File.SetLastWriteTimeUtc(posterPath, DateTime.UtcNow.AddDays(2));
        ArtworkService service = CreateService(fixture.DirectoryPath);

        await service.GetDisplayFileNameAsync(poster);
        await service.GetDisplayFileNameAsync(poster);
        await service.GetDisplayFileNameAsync(poster);

        await Assert.That(service.CardsComposed).IsEqualTo(1);
    }

    [Test]
    public async Task GetDisplayFileNameAsync_PortraitPosterReplacedByALandscapeOne_DropsTheStaleCard()
    {
        using var fixture = new TestDirectoryFixture();
        const string poster = "g_now_landscape.png";
        string posterPath = Path.Combine(fixture.DirectoryPath, poster);
        string cardPath = Path.Combine(fixture.DirectoryPath, poster + ".card.jpg");
        await SavePortraitPosterAsync(posterPath, Orange, Magenta);
        ArtworkService service = CreateService(fixture.DirectoryPath);
        await service.GetDisplayFileNameAsync(poster);
        await Assert.That(File.Exists(cardPath)).IsTrue();

        await SavePngAsync(posterPath, Solid(200, 100, Orange), 200, 100);
        File.SetLastWriteTimeUtc(posterPath, File.GetLastWriteTimeUtc(cardPath).AddMinutes(1));

        string? display = await service.GetDisplayFileNameAsync(poster);

        await Assert.That(display).IsEqualTo(poster);
        await Assert.That(File.Exists(cardPath)).IsFalse()
            .Because("A card made from a poster that is gone describes nothing.");
    }

    [Test]
    public async Task GetDisplayFileNameAsync_LandscapePosterReplacedByAPortraitOne_GetsACard()
    {
        // A landscape poster is remembered as needing no card. That memory must not outlive the
        // poster it was formed about.
        using var fixture = new TestDirectoryFixture();
        const string poster = "g_now_portrait.png";
        string posterPath = Path.Combine(fixture.DirectoryPath, poster);
        await SavePngAsync(posterPath, Solid(200, 100, Orange), 200, 100);
        ArtworkService service = CreateService(fixture.DirectoryPath);
        await Assert.That(await service.GetDisplayFileNameAsync(poster)).IsEqualTo(poster);

        await SavePortraitPosterAsync(posterPath, Orange, Magenta);
        File.SetLastWriteTimeUtc(posterPath, DateTime.UtcNow.AddMinutes(1));

        string? display = await service.GetDisplayFileNameAsync(poster);

        await Assert.That(display).IsEqualTo(poster + ".card.jpg");
    }

    // ------------------------------------------------------------------
    // Concurrency
    // ------------------------------------------------------------------

    [Test]
    public async Task GetDisplayFileNameAsync_ManyRequestsForOneCardAtOnce_ShareASingleComposition()
    {
        // Tiles re-realise on scroll, so the same card is asked for again while it is still being
        // made. Everyone must be handed the one result; none may start a composition of their own to
        // race the first onto the same file.
        using var fixture = new TestDirectoryFixture();
        const string poster = "g_concurrent.png";
        await SavePortraitPosterAsync(Path.Combine(fixture.DirectoryPath, poster), Orange, Magenta);
        ArtworkService service = CreateService(fixture.DirectoryPath);

        string?[] results = await Task.WhenAll(
            Enumerable.Range(0, 8).Select(_ => service.GetDisplayFileNameAsync(poster)));

        await Assert.That(results.All(name => name == poster + ".card.jpg")).IsTrue();
        await Assert.That(service.CardsComposed).IsEqualTo(1);
        await Assert.That(CardFilesIn(fixture.DirectoryPath)).Count().IsEqualTo(1);
    }

    // ------------------------------------------------------------------
    // Lifecycle: a card never outlives its poster
    // ------------------------------------------------------------------

    private static async Task<(DatabaseService Db, ArtworkService Service)> CreateServiceWithDatabaseAsync(
        string databaseDirectory, string artworkDirectory)
    {
        var db = new DatabaseService(NullLogger<DatabaseService>.Instance) { DbFolderPath = databaseDirectory };
        await db.InitializeAsync();
        return (db, new ArtworkService(db, NullLogger<ArtworkService>.Instance) { ArtworkFolderPath = artworkDirectory });
    }

    [Test]
    public async Task RemoveManualPosterAsync_DeletesTheCardWithThePoster()
    {
        using var databaseFixture = new TestDirectoryFixture();
        using var artworkFixture = new TestDirectoryFixture();
        (DatabaseService db, ArtworkService service) = await CreateServiceWithDatabaseAsync(
            databaseFixture.DirectoryPath, artworkFixture.DirectoryPath);
        const string path = @"D:\Anime\Dan Da Dan";
        const string poster = "m_custom_image.png";
        await SavePortraitPosterAsync(Path.Combine(artworkFixture.DirectoryPath, poster), Orange, Magenta);
        await db.SetFolderPosterAsync(path, poster, PosterSource.Manual);
        await service.GetDisplayFileNameAsync(poster);
        await Assert.That(CardFilesIn(artworkFixture.DirectoryPath)).Count().IsEqualTo(1);

        bool removed = await service.RemoveManualPosterAsync(path);

        await Assert.That(removed).IsTrue();
        await Assert.That(File.Exists(Path.Combine(artworkFixture.DirectoryPath, poster))).IsFalse();
        await Assert.That(CardFilesIn(artworkFixture.DirectoryPath)).IsEmpty();
    }

    [Test]
    public async Task TryWriteScrapedPosterRowAsync_SupersedingAPoster_DeletesItsCardToo()
    {
        using var databaseFixture = new TestDirectoryFixture();
        using var artworkFixture = new TestDirectoryFixture();
        (DatabaseService db, ArtworkService service) = await CreateServiceWithDatabaseAsync(
            databaseFixture.DirectoryPath, artworkFixture.DirectoryPath);
        const string path = @"D:\Anime\Frieren";
        const string oldPoster = "g_old_poster.png";
        await SavePortraitPosterAsync(Path.Combine(artworkFixture.DirectoryPath, oldPoster), Orange, Magenta);
        await db.SetFolderPosterAsync(path, oldPoster, PosterSource.Convention);
        await service.GetDisplayFileNameAsync(oldPoster);
        await Assert.That(CardFilesIn(artworkFixture.DirectoryPath)).Count().IsEqualTo(1);

        bool written = await service.TryWriteScrapedPosterRowAsync(path, "g_new_poster.png");

        await Assert.That(written).IsTrue();
        await Assert.That(File.Exists(Path.Combine(artworkFixture.DirectoryPath, oldPoster))).IsFalse();
        await Assert.That(CardFilesIn(artworkFixture.DirectoryPath)).IsEmpty();
    }

    [Test]
    public async Task PruneOrphanedArtworkAsync_FolderThatDisappeared_DeletesItsPosterCard()
    {
        using var databaseFixture = new TestDirectoryFixture();
        using var artworkFixture = new TestDirectoryFixture();
        using var libraryFixture = new TestDirectoryFixture();
        (DatabaseService db, ArtworkService service) = await CreateServiceWithDatabaseAsync(
            databaseFixture.DirectoryPath, artworkFixture.DirectoryPath);
        string goneFolder = Path.Combine(libraryFixture.DirectoryPath, "Deleted Show");
        const string poster = "g_pruned.png";
        await SavePortraitPosterAsync(Path.Combine(artworkFixture.DirectoryPath, poster), Orange, Magenta);
        await db.SetFolderPosterAsync(goneFolder, poster, PosterSource.Convention);
        await service.GetDisplayFileNameAsync(poster);
        await Assert.That(CardFilesIn(artworkFixture.DirectoryPath)).Count().IsEqualTo(1);

        await service.PruneOrphanedArtworkAsync([libraryFixture.DirectoryPath]);

        await Assert.That(File.Exists(Path.Combine(artworkFixture.DirectoryPath, poster))).IsFalse();
        await Assert.That(CardFilesIn(artworkFixture.DirectoryPath)).IsEmpty();
    }

    [Test]
    public async Task RemoveManualPosterAsync_CardWhosePosterIsAlreadyGone_IsStillDeleted()
    {
        // The orphan this exists to prevent: a card left from a poster that was removed some other
        // way. Deleting "the poster" must clear it even though there is no poster file left to delete.
        using var databaseFixture = new TestDirectoryFixture();
        using var artworkFixture = new TestDirectoryFixture();
        (DatabaseService db, ArtworkService service) = await CreateServiceWithDatabaseAsync(
            databaseFixture.DirectoryPath, artworkFixture.DirectoryPath);
        const string path = @"D:\Anime\Dan Da Dan";
        const string poster = "m_already_gone.png";
        await File.WriteAllBytesAsync(Path.Combine(artworkFixture.DirectoryPath, poster + ArtworkService.CardSuffix), [1, 2, 3]);
        await db.SetFolderPosterAsync(path, poster, PosterSource.Manual);

        await service.RemoveManualPosterAsync(path);

        await Assert.That(CardFilesIn(artworkFixture.DirectoryPath)).IsEmpty();
    }
}
