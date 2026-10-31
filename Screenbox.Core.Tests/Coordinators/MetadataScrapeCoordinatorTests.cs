using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using Screenbox.Core.Coordinators;
using Screenbox.Core.Enums;
using Screenbox.Core.Models;
using Screenbox.Core.Services;
using Screenbox.Core.Tests.Helpers;

namespace Screenbox.Core.Tests.Coordinators;

public sealed class MetadataScrapeCoordinatorTests
{
    // Zero by default so every test using this helper runs at full speed - the throttle itself is
    // exercised only by the tests below that construct a coordinator directly with an explicit
    // delay, precisely so the rest of the suite never has to sleep for it. A FakeArtworkService
    // keeps a fresh match's poster download from ever reaching a real HttpClient - see its own
    // doc comment. The one test that must prove the real Manual-poster guard end-to-end
    // (ScrapeFolderAsync_NeverOverwritesAManualPosterOrCustomTitle) builds its own coordinator with
    // a real ArtworkService instead of using this helper.
    private static MetadataScrapeCoordinator CreateCoordinator(
        TestSettingsService settings, IMetadataProviderFactory factory, IDatabaseService database) =>
        new(factory, settings, database, new FakeArtworkService(), NullLogger<MetadataScrapeCoordinator>.Instance, TimeSpan.Zero);

    // Matches the brief's illustrative test exactly: a coordinator built from a single provider,
    // backed by a database that throws if touched at all - the disabled path must never reach it.
    private static MetadataScrapeCoordinator CreateCoordinator(TestSettingsService settings, IMetadataProvider provider) =>
        CreateCoordinator(settings, new FakeMetadataProviderFactory(provider), new ThrowingDatabaseService());

    private static MetadataScrapeCoordinator CreateCoordinatorWithFactory(TestSettingsService settings, IMetadataProviderFactory factory) =>
        CreateCoordinator(settings, factory, new ThrowingDatabaseService());

    private static async Task<DatabaseService> CreateDatabaseAsync(string dir)
    {
        var db = new DatabaseService(NullLogger<DatabaseService>.Instance) { DbFolderPath = dir };
        await db.InitializeAsync();
        return db;
    }

    // ------------------------------------------------------------------
    // The non-negotiable constraint: nothing network-capable while disabled.
    // ------------------------------------------------------------------

    [Test]
    public async Task ScrapeFolderAsync_MakesNoProviderCallWhenDisabled()
    {
        // The feature is opt-in. With the master toggle off, nothing may reach a provider.
        var provider = new FakeMetadataProvider("counting-fake");
        var settings = new TestSettingsService { MetadataEnabled = false };
        var coordinator = CreateCoordinator(settings, provider);

        MetadataMatch? result = await coordinator.ScrapeFolderAsync(@"D:\Anime\Dan Da Dan", "Dan Da Dan", default);

        await Assert.That(result).IsNull();
        await Assert.That(provider.CallCount).IsEqualTo(0);
    }

    [Test]
    public async Task ScrapeFolderAsync_DoesNotEvenConstructAProviderWhenDisabled()
    {
        // Not calling a provider is not enough. A provider's constructor allocates an HttpClient,
        // and the guarantee the user accepted is that nothing network-capable comes into existence
        // until they opt in. Providers are obtained lazily through a factory so construction itself
        // is gated - GetEnabledProviders() is the one call that (in the real factory) builds them.
        var factory = new FakeMetadataProviderFactory();
        var settings = new TestSettingsService { MetadataEnabled = false };
        var coordinator = CreateCoordinatorWithFactory(settings, factory);

        await coordinator.ScrapeFolderAsync(@"D:\Anime\Dan Da Dan", "Dan Da Dan", default);

        await Assert.That(factory.ConstructionCount).IsEqualTo(0);
    }

    [Test]
    public async Task ScrapeLibraryAsync_Disabled_MakesNoProviderCalls()
    {
        var factory = new FakeMetadataProviderFactory();
        var settings = new TestSettingsService { MetadataEnabled = false };
        var coordinator = CreateCoordinatorWithFactory(settings, factory);

        await coordinator.ScrapeLibraryAsync(null, default);

        await Assert.That(factory.ConstructionCount).IsEqualTo(0);
    }

    // ------------------------------------------------------------------
    // Cache short-circuit.
    // ------------------------------------------------------------------

    [Test]
    public async Task ScrapeFolderAsync_CachedResult_ShortCircuitsWithoutCallingAProvider()
    {
        using var fixture = new TestDirectoryFixture();
        DatabaseService db = await CreateDatabaseAsync(fixture.DirectoryPath);
        const string path = @"D:\Anime\Dan Da Dan";

        // Simulates a folder a previous (real) scrape already resolved.
        await db.SetFolderCustomTitleAsync(path, "Dan Da Dan");
        await db.SaveScrapeCacheAsync(new FolderScrapeCacheDto
        {
            Path = path,
            Provider = "anilist",
            ProviderId = "171018",
            Synopsis = "A high-school romantic comedy about the occult.",
            Year = 2024,
            LastScraped = DateTimeOffset.UtcNow
        });

        var provider = new FakeMetadataProvider("anilist", [new MetadataMatch("171018", "Dan Da Dan", null, 2024, null)]);
        var settings = new TestSettingsService { MetadataEnabled = true };
        var coordinator = CreateCoordinator(settings, new FakeMetadataProviderFactory(provider), db);

        MetadataMatch? result = await coordinator.ScrapeFolderAsync(path, "Dan Da Dan", CancellationToken.None);

        await Assert.That(provider.CallCount).IsEqualTo(0);
        await Assert.That(result).IsNotNull();
        await Assert.That(result!.ProviderId).IsEqualTo("171018");
        await Assert.That(result.Title).IsEqualTo("Dan Da Dan");
    }

    [Test]
    public async Task ScrapeFolderAsync_CachedNoMatch_ShortCircuitsAndStaysNull()
    {
        using var fixture = new TestDirectoryFixture();
        DatabaseService db = await CreateDatabaseAsync(fixture.DirectoryPath);
        const string path = @"D:\Anime\hayase-cache";

        // A prior scrape already checked this folder and found nothing worth keeping.
        await db.SaveScrapeCacheAsync(new FolderScrapeCacheDto { Path = path, LastScraped = DateTimeOffset.UtcNow });

        var provider = new FakeMetadataProvider("anilist", [new MetadataMatch("1", "Whatever", null, null, null)]);
        var settings = new TestSettingsService { MetadataEnabled = true };
        var coordinator = CreateCoordinator(settings, new FakeMetadataProviderFactory(provider), db);

        MetadataMatch? result = await coordinator.ScrapeFolderAsync(path, "hayase-cache", CancellationToken.None);

        await Assert.That(result).IsNull();
        await Assert.That(provider.CallCount).IsEqualTo(0);
    }

    // ------------------------------------------------------------------
    // Provider selection and ordering.
    // ------------------------------------------------------------------

    [Test]
    public async Task ScrapeFolderAsync_DisabledProviderIsSkippedWhileEnabledProviderStillRuns()
    {
        using var fixture = new TestDirectoryFixture();
        DatabaseService db = await CreateDatabaseAsync(fixture.DirectoryPath);
        const string path = @"D:\Anime\Dan Da Dan";

        // Stands in for a provider whose settings flag is off: the real factory would never include
        // it in the list it hands the coordinator in the first place.
        var disabledProvider = new FakeMetadataProvider("tmdb", [new MetadataMatch("9", "Dan Da Dan", null, 2024, null)]);
        var enabledProvider = new FakeMetadataProvider("anilist", [new MetadataMatch("1", "Dan Da Dan", null, 2024, null)]);
        var settings = new TestSettingsService { MetadataEnabled = true };
        var coordinator = CreateCoordinator(settings, new FakeMetadataProviderFactory(enabledProvider), db);

        MetadataMatch? result = await coordinator.ScrapeFolderAsync(path, "Dan Da Dan", CancellationToken.None);

        await Assert.That(disabledProvider.CallCount).IsEqualTo(0);
        await Assert.That(enabledProvider.CallCount).IsEqualTo(1);
        await Assert.That(result).IsNotNull();
    }

    [Test]
    public async Task ScrapeFolderAsync_TriesProvidersInOrder_FirstAcceptedMatchWins()
    {
        using var fixture = new TestDirectoryFixture();
        DatabaseService db = await CreateDatabaseAsync(fixture.DirectoryPath);
        const string path = @"D:\Anime\Dan Da Dan";

        var firstProvider = new FakeMetadataProvider("anilist", [new MetadataMatch("1", "Dan Da Dan", null, 2024, null)]);
        var secondProvider = new FakeMetadataProvider("jikan", [new MetadataMatch("2", "Dan Da Dan", null, 2024, null)]);
        var settings = new TestSettingsService { MetadataEnabled = true };
        var coordinator = CreateCoordinator(settings, new FakeMetadataProviderFactory(firstProvider, secondProvider), db);

        MetadataMatch? result = await coordinator.ScrapeFolderAsync(path, "Dan Da Dan", CancellationToken.None);

        await Assert.That(firstProvider.CallCount).IsEqualTo(1);
        await Assert.That(secondProvider.CallCount).IsEqualTo(0)
            .Because("The first provider's candidate already cleared the acceptance threshold - a later provider is never consulted once one has been accepted.");
        await Assert.That(result!.ProviderId).IsEqualTo("1");
    }

    [Test]
    public async Task ScrapeFolderAsync_FirstProviderReturnsEmpty_FallsThroughToNextProvider()
    {
        using var fixture = new TestDirectoryFixture();
        DatabaseService db = await CreateDatabaseAsync(fixture.DirectoryPath);
        const string path = @"D:\Anime\Dan Da Dan";

        var firstProvider = new FakeMetadataProvider("anilist");
        var secondProvider = new FakeMetadataProvider("jikan", [new MetadataMatch("2", "Dan Da Dan", null, 2024, null)]);
        var settings = new TestSettingsService { MetadataEnabled = true };
        var coordinator = CreateCoordinator(settings, new FakeMetadataProviderFactory(firstProvider, secondProvider), db);

        MetadataMatch? result = await coordinator.ScrapeFolderAsync(path, "Dan Da Dan", CancellationToken.None);

        await Assert.That(firstProvider.CallCount).IsEqualTo(1);
        await Assert.That(secondProvider.CallCount).IsEqualTo(1);
        await Assert.That(result!.ProviderId).IsEqualTo("2");
    }

    [Test]
    public async Task ScrapeFolderAsync_FirstProviderOnlyWeakCandidates_FallsThroughToNextProvider()
    {
        // The behavioural fix this test locks in: a provider that answers with candidates - just
        // none of them good enough to accept - must be treated exactly like a provider that
        // answered with nothing at all. AniList is the primary source and Jikan exists precisely as
        // the fallback for when AniList returns nothing USEFUL, not just nothing. Five confidently
        // wrong AniList candidates must not permanently block Jikan from being tried.
        using var fixture = new TestDirectoryFixture();
        DatabaseService db = await CreateDatabaseAsync(fixture.DirectoryPath);
        const string path = @"D:\Anime\Dan Da Dan";

        // Every candidate here scores well below MetadataMatchScorer.AcceptThreshold against the
        // "Dan Da Dan" query (see ScrapeFolderAsync_NoGoodCandidate_RecordsNoMatchRatherThanThrowing
        // for the same pairing used to establish that).
        var weakProvider = new FakeMetadataProvider("anilist",
        [
            new MetadataMatch("101", "Completely Unrelated Show Title", null, null, null),
            new MetadataMatch("102", "Another Unrelated Title Entirely", null, null, null)
        ]);
        var goodProvider = new FakeMetadataProvider("jikan", [new MetadataMatch("2", "Dan Da Dan", null, 2024, null)]);
        var settings = new TestSettingsService { MetadataEnabled = true };
        var coordinator = CreateCoordinator(settings, new FakeMetadataProviderFactory(weakProvider, goodProvider), db);

        MetadataMatch? result = await coordinator.ScrapeFolderAsync(path, "Dan Da Dan", CancellationToken.None);

        await Assert.That(weakProvider.CallCount).IsEqualTo(1);
        await Assert.That(goodProvider.CallCount).IsEqualTo(1)
            .Because("AniList's only candidates scored below threshold - the same as an empty result set - so Jikan must still be consulted.");
        await Assert.That(result).IsNotNull();
        await Assert.That(result!.ProviderId).IsEqualTo("2")
            .Because("The first ACCEPTED match wins, not the first non-empty result set.");
    }

    // ------------------------------------------------------------------
    // Query resolution.
    // ------------------------------------------------------------------

    [Test]
    public async Task ScrapeFolderAsync_BareSeasonFolder_FallsBackToParentFolderName()
    {
        using var fixture = new TestDirectoryFixture();
        DatabaseService db = await CreateDatabaseAsync(fixture.DirectoryPath);
        const string path = @"D:\Anime\Jujutsu Kaisen\Season 1";

        var provider = new FakeMetadataProvider("anilist", [new MetadataMatch("1", "Jujutsu Kaisen", null, 2020, null)]);
        var settings = new TestSettingsService { MetadataEnabled = true };
        var coordinator = CreateCoordinator(settings, new FakeMetadataProviderFactory(provider), db);

        MetadataMatch? result = await coordinator.ScrapeFolderAsync(path, "Season 1", CancellationToken.None);

        await Assert.That(provider.LastQuery).IsEqualTo("Jujutsu Kaisen")
            .Because("\"Season 1\" normalizes to empty; the coordinator must search the parent folder's name instead.");
        await Assert.That(result).IsNotNull();
        await Assert.That(result!.Title).IsEqualTo("Jujutsu Kaisen");
    }

    // ------------------------------------------------------------------
    // No-match handling (the scoring threshold this whole system exists for).
    // ------------------------------------------------------------------

    [Test]
    public async Task ScrapeFolderAsync_NoGoodCandidate_RecordsNoMatchRatherThanThrowing()
    {
        using var fixture = new TestDirectoryFixture();
        DatabaseService db = await CreateDatabaseAsync(fixture.DirectoryPath);
        const string path = @"D:\Anime\hayase-cache";

        // hayase-cache is a real, non-series folder in this library. The provider still returns
        // something (a fuzzy search rarely returns literally nothing), but it must not be accepted.
        var provider = new FakeMetadataProvider("anilist",
            [new MetadataMatch("999", "Completely Unrelated Show Title", null, null, null)]);
        var settings = new TestSettingsService { MetadataEnabled = true };
        var coordinator = CreateCoordinator(settings, new FakeMetadataProviderFactory(provider), db);

        MetadataMatch? result = await coordinator.ScrapeFolderAsync(path, "hayase-cache", CancellationToken.None);

        await Assert.That(result).IsNull();

        FolderScrapeCacheDto? cache = await db.LoadScrapeCacheAsync(path);
        await Assert.That(cache).IsNotNull()
            .Because("A checked-and-rejected folder must still be recorded so it is not re-queried forever.");
        await Assert.That(cache!.Provider).IsNull();

        // The no-match must itself be cached: a second call must not hit the provider again.
        MetadataMatch? secondResult = await coordinator.ScrapeFolderAsync(path, "hayase-cache", CancellationToken.None);
        await Assert.That(secondResult).IsNull();
        await Assert.That(provider.CallCount).IsEqualTo(1);
    }

    // ------------------------------------------------------------------
    // folder_metadata guard - a scrape never writes the user's title and never clobbers a poster
    // choice. custom_title is durable user data (the Edit folder dialog writes it, and nothing
    // distinguishes a scraped value from a typed one), so the coordinator does not touch it at all.
    // ------------------------------------------------------------------

    [Test]
    public async Task ScrapeFolderAsync_NoExistingTitle_LeavesCustomTitleUntouched()
    {
        using var fixture = new TestDirectoryFixture();
        DatabaseService db = await CreateDatabaseAsync(fixture.DirectoryPath);
        const string path = @"D:\Anime\Dan Da Dan";

        // The provider's title differs from the folder name in the ways real ones do: stylised
        // capitals and a trailing full stop. Writing it into custom_title would turn a lookup into
        // a permanent rename the user never asked for.
        var provider = new FakeMetadataProvider("anilist",
            [new MetadataMatch("1", "DAN DA DAN.", "synopsis", 2024, "https://example.com/poster.jpg")]);
        var settings = new TestSettingsService { MetadataEnabled = true };
        var coordinator = CreateCoordinator(settings, new FakeMetadataProviderFactory(provider), db);

        MetadataMatch? result = await coordinator.ScrapeFolderAsync(path, "Dan Da Dan", CancellationToken.None);

        await Assert.That(result).IsNotNull()
            .Because("The scrape must genuinely have matched, otherwise an untouched title proves nothing.");

        FolderMetadataDto? metadata = await db.LoadFolderMetadataAsync(path);
        await Assert.That(metadata?.CustomTitle).IsNull();

        // What a scrape does keep lives in the disposable cache, where cache data belongs.
        FolderScrapeCacheDto? cache = await db.LoadScrapeCacheAsync(path);
        await Assert.That(cache).IsNotNull();
        await Assert.That(cache!.Synopsis).IsEqualTo("synopsis");
        await Assert.That(cache.Year).IsEqualTo(2024);
    }

    [Test]
    public async Task ScrapeFolderAsync_SeasonFolderSearchedViaParent_KeepsItsOwnNameButReceivesThePoster()
    {
        // "Season 1" has no searchable name of its own, so it is searched under its parent's name.
        // The series result must reach the season folder only as artwork - never as a title, or
        // Season 1, Season 2 and Season 3 all end up renamed to the series and indistinguishable.
        using var fixture = new TestDirectoryFixture();
        DatabaseService db = await CreateDatabaseAsync(fixture.DirectoryPath);
        const string path = @"D:\Anime\Jujutsu Kaisen\Season 1";

        var provider = new FakeMetadataProvider("anilist",
            [new MetadataMatch("1", "JUJUTSU KAISEN", "synopsis", 2020, "https://example.com/jjk.jpg")]);
        var settings = new TestSettingsService { MetadataEnabled = true };
        var artwork = new FakeArtworkService();
        var coordinator = new MetadataScrapeCoordinator(
            new FakeMetadataProviderFactory(provider), settings, db, artwork,
            NullLogger<MetadataScrapeCoordinator>.Instance, TimeSpan.Zero);

        MetadataMatch? result = await coordinator.ScrapeFolderAsync(path, "Season 1", CancellationToken.None);

        await Assert.That(result).IsNotNull();
        await Assert.That(provider.LastQuery).IsEqualTo("Jujutsu Kaisen")
            .Because("A bare season folder is still searched, under its parent's name.");
        await Assert.That(artwork.LastFolderPath).IsEqualTo(path)
            .Because("The season folder may still receive the series poster.");
        await Assert.That(artwork.LastPosterUrl).IsEqualTo("https://example.com/jjk.jpg");

        FolderMetadataDto? metadata = await db.LoadFolderMetadataAsync(path);
        await Assert.That(metadata?.CustomTitle).IsNull()
            .Because("The season folder keeps its own name; the series title is never written into it.");
    }

    [Test]
    public async Task ScrapeFolderAsync_SpecialsFolder_MakesNoProviderCallAndRecordsNothing()
    {
        // A specials folder is not a series, and SeriesNameNormalizer says so by normalizing it to
        // empty. Falling back to the parent's name for it would apply the parent series' result to
        // a folder that is not that series. The provider is primed with a perfect match for the
        // parent so a fall-back to the parent name could not go unnoticed.
        using var fixture = new TestDirectoryFixture();
        DatabaseService db = await CreateDatabaseAsync(fixture.DirectoryPath);

        var provider = new FakeMetadataProvider("anilist",
            [new MetadataMatch("1", "Jujutsu Kaisen", "synopsis", 2020, "https://example.com/jjk.jpg")]);
        var factory = new FakeMetadataProviderFactory(provider);
        var settings = new TestSettingsService { MetadataEnabled = true };
        var artwork = new FakeArtworkService();
        var coordinator = new MetadataScrapeCoordinator(
            factory, settings, db, artwork, NullLogger<MetadataScrapeCoordinator>.Instance, TimeSpan.Zero);

        foreach (string name in new[] { "NCOP & NCED", "Extras", "Specials", "NCOP", "NCED", "OAD", "OVA", "Bonus" })
        {
            string path = @"D:\Anime\Jujutsu Kaisen\" + name;

            MetadataMatch? result = await coordinator.ScrapeFolderAsync(path, name, CancellationToken.None);

            await Assert.That(result).IsNull();
            await Assert.That(await db.LoadScrapeCacheAsync(path)).IsNull()
                .Because($"'{name}' must not even be recorded as a no-match: nothing about it was checked.");
        }

        await Assert.That(provider.CallCount).IsEqualTo(0);
        await Assert.That(factory.ConstructionCount).IsEqualTo(0)
            .Because("Not even a provider lookup - the factory is where providers are constructed.");
        await Assert.That(artwork.SetScrapedPosterCallCount).IsEqualTo(0);
    }

    [Test]
    public async Task ScrapeFolderAsync_SpecialsFolderWithLeftoverCacheRow_IsStillNotScraped()
    {
        // An earlier build did fall back to the parent for specials folders, so a library scanned
        // by it holds a matched cache row for "NCOP & NCED". That row must not resurface as a match
        // for a folder that is not a series.
        using var fixture = new TestDirectoryFixture();
        DatabaseService db = await CreateDatabaseAsync(fixture.DirectoryPath);
        const string path = @"D:\Anime\Jujutsu Kaisen\NCOP & NCED";

        await db.SaveScrapeCacheAsync(new FolderScrapeCacheDto
        {
            Path = path, Provider = "anilist", ProviderId = "113415", Synopsis = "x", LastScraped = DateTimeOffset.UtcNow
        });

        var provider = new FakeMetadataProvider("anilist");
        var settings = new TestSettingsService { MetadataEnabled = true };
        var coordinator = CreateCoordinator(settings, new FakeMetadataProviderFactory(provider), db);

        MetadataMatch? result = await coordinator.ScrapeFolderAsync(path, "NCOP & NCED", CancellationToken.None);

        await Assert.That(result).IsNull();
    }

    [Test]
    public async Task ScrapeLibraryAsync_SkipsSpecialsFoldersButStillScrapesTheirSiblings()
    {
        // The whole-library path reaches the same gate: the name it passes down is the last path
        // segment, so a specials subfolder is skipped while the season beside it is still searched.
        using var fixture = new TestDirectoryFixture();
        DatabaseService db = await CreateDatabaseAsync(fixture.DirectoryPath);
        const string specialsPath = @"D:\Anime\Jujutsu Kaisen\NCOP & NCED";
        const string seasonPath = @"D:\Anime\Jujutsu Kaisen\Season 1";
        await db.SetFolderCustomTitleAsync(specialsPath, null);
        await db.SetFolderCustomTitleAsync(seasonPath, null);

        var provider = new FakeMetadataProvider("anilist", [new MetadataMatch("1", "Jujutsu Kaisen", null, null, null)]);
        var settings = new TestSettingsService { MetadataEnabled = true };
        var coordinator = CreateCoordinator(settings, new FakeMetadataProviderFactory(provider), db);

        await coordinator.ScrapeLibraryAsync(null, CancellationToken.None);

        await Assert.That(provider.CallCount).IsEqualTo(1);
        await Assert.That(await db.LoadScrapeCacheAsync(specialsPath)).IsNull();
        await Assert.That(await db.LoadScrapeCacheAsync(seasonPath)).IsNotNull();
    }

    [Test]
    public async Task ScrapeFolderAsync_NeverOverwritesAManualPosterOrCustomTitle()
    {
        // folder_metadata is durable user data. A scrape may fill blanks, never clobber choices.
        // This is the guarantee the whole phase rests on, so - unlike every other test in this
        // file - this one builds a REAL ArtworkService (backed by the same real database) instead
        // of using the CreateCoordinator helper's FakeArtworkService: the guarantee must be proven
        // against the actual write path the coordinator now drives, not a stand-in that would pass
        // even if the real guard were deleted. This stays network-free because the folder already
        // has a Manual poster before the scrape runs - ArtworkService.SetScrapedPosterAsync checks
        // that and returns before ever reaching the HTTP download.
        using var fixture = new TestDirectoryFixture();
        DatabaseService db = await CreateDatabaseAsync(fixture.DirectoryPath);
        const string path = @"D:\Anime\Dan Da Dan";

        // The user has already set both a custom title and a manual poster before any scrape runs.
        await db.SetFolderCustomTitleAsync(path, "My Custom Title");
        await db.SetFolderPosterAsync(path, "manual-poster.jpg", PosterSource.Manual);

        // The provider would happily supply a different title and a different poster.
        var provider = new FakeMetadataProvider("anilist",
            [new MetadataMatch("1", "Dan Da Dan", "A different synopsis.", 2024, "https://example.com/scraped-poster.jpg")]);
        var settings = new TestSettingsService { MetadataEnabled = true };
        var artworkService = new ArtworkService(db, NullLogger<ArtworkService>.Instance);
        var coordinator = new MetadataScrapeCoordinator(
            new FakeMetadataProviderFactory(provider), settings, db, artworkService,
            NullLogger<MetadataScrapeCoordinator>.Instance, TimeSpan.Zero);

        MetadataMatch? result = await coordinator.ScrapeFolderAsync(path, "Dan Da Dan", CancellationToken.None);

        await Assert.That(result).IsNotNull().Because("The scrape itself must still succeed and be cached.");
        await Assert.That(result!.PosterUrl).IsNotNull()
            .Because("The guard this test exercises only matters when a scrape genuinely found a poster to (almost) act on.");

        // The poster half of this test cannot be proven by the row alone: with the Manual guard
        // deleted, the download to this placeholder URL simply fails, the row is never written, and
        // the persisted assertions below pass anyway. Whether the guard actually fired is visible
        // from whether anything network-capable came into existence - it is only created on the way
        // to a download, which the guard exists to prevent.
        await Assert.That(artworkService.HasCreatedHttpClient).IsFalse()
            .Because("A folder with a Manual poster must be refused before any download is attempted.");

        // The assertion that matters: read the persisted row back from the database, not from
        // ArtworkResolver or any other reader. A resolver's read-time precedence cannot protect
        // data that a writer already clobbered.
        FolderMetadataDto? persisted = await db.LoadFolderMetadataAsync(path);
        await Assert.That(persisted).IsNotNull();
        await Assert.That(persisted!.CustomTitle).IsEqualTo("My Custom Title");
        await Assert.That(persisted.PosterFile).IsEqualTo("manual-poster.jpg");
        await Assert.That(persisted.PosterSource).IsEqualTo(PosterSource.Manual);
    }

    // ------------------------------------------------------------------
    // Whole-library scan.
    // ------------------------------------------------------------------

    [Test]
    public async Task ScrapeLibraryAsync_ScrapesEveryKnownFolder()
    {
        using var fixture = new TestDirectoryFixture();
        DatabaseService db = await CreateDatabaseAsync(fixture.DirectoryPath);

        // Two folders already known to folder_metadata (e.g. from artwork resolution), neither one
        // titled yet.
        await db.SetFolderCustomTitleAsync(@"D:\Anime\Dan Da Dan", null);
        await db.SetFolderPosterAsync(@"D:\Anime\Frieren", "g_cached_frame.jpg", PosterSource.AutoFrame);

        var provider = new FakeMetadataProvider("anilist", [new MetadataMatch("1", "Match", null, null, null)]);
        var settings = new TestSettingsService { MetadataEnabled = true };
        var coordinator = CreateCoordinator(settings, new FakeMetadataProviderFactory(provider), db);

        await coordinator.ScrapeLibraryAsync(null, CancellationToken.None);

        await Assert.That(provider.CallCount).IsEqualTo(2);
    }

    // ------------------------------------------------------------------
    // Inter-folder throttle. A deliberately large delay (tens of seconds) is injected below - the
    // same way AniListMetadataProvider's tests inject requestTimeout - specifically so these tests
    // can prove the delay is skipped or cut short WITHOUT the suite ever sleeping for it: a test
    // that mistakenly waited the delay out would be caught by the elapsed-time assertion, not by
    // actually taking that long to run.
    // ------------------------------------------------------------------

    [Test]
    public async Task ScrapeLibraryAsync_AllFoldersCached_DoesNotThrottleBetweenThem()
    {
        using var fixture = new TestDirectoryFixture();
        DatabaseService db = await CreateDatabaseAsync(fixture.DirectoryPath);

        const string pathA = @"D:\Anime\Dan Da Dan";
        const string pathB = @"D:\Anime\Frieren";
        await db.SetFolderCustomTitleAsync(pathA, "Dan Da Dan");
        await db.SetFolderCustomTitleAsync(pathB, "Frieren");
        await db.SaveScrapeCacheAsync(new FolderScrapeCacheDto
        {
            Path = pathA, Provider = "anilist", ProviderId = "1", LastScraped = DateTimeOffset.UtcNow
        });
        await db.SaveScrapeCacheAsync(new FolderScrapeCacheDto
        {
            Path = pathB, Provider = "anilist", ProviderId = "2", LastScraped = DateTimeOffset.UtcNow
        });

        var provider = new FakeMetadataProvider("anilist");
        var settings = new TestSettingsService { MetadataEnabled = true };
        // 30s: if the coordinator mistakenly throttled between these cache-only folders, this test
        // would take over 30s to complete instead of the well-under-5s asserted below.
        var coordinator = new MetadataScrapeCoordinator(
            new FakeMetadataProviderFactory(provider), settings, db, new FakeArtworkService(),
            NullLogger<MetadataScrapeCoordinator>.Instance, TimeSpan.FromSeconds(30));

        Stopwatch stopwatch = Stopwatch.StartNew();
        await coordinator.ScrapeLibraryAsync(null, CancellationToken.None);
        stopwatch.Stop();

        await Assert.That(provider.CallCount).IsEqualTo(0)
            .Because("Both folders short-circuit from the scrape cache - no provider call, so no throttle applies.");
        await Assert.That(stopwatch.Elapsed).IsLessThan(TimeSpan.FromSeconds(5));
    }

    [Test]
    public async Task ScrapeLibraryAsync_CancelledDuringThrottleDelay_StopsPromptlyInsteadOfWaitingItOut()
    {
        using var fixture = new TestDirectoryFixture();
        DatabaseService db = await CreateDatabaseAsync(fixture.DirectoryPath);

        // Two folders that both require a real provider call (no cache), so the throttle between
        // them is actually exercised.
        await db.SetFolderCustomTitleAsync(@"D:\Anime\Dan Da Dan", null);
        await db.SetFolderCustomTitleAsync(@"D:\Anime\Frieren", null);

        var provider = new FakeMetadataProvider("anilist", [new MetadataMatch("1", "Match", null, null, null)]);
        var settings = new TestSettingsService { MetadataEnabled = true };
        // 30s: long enough that the test would be obviously, badly wrong if cancellation failed to
        // cut the delay short - without this test itself ever waiting anywhere near that long.
        var coordinator = new MetadataScrapeCoordinator(
            new FakeMetadataProviderFactory(provider), settings, db, new FakeArtworkService(),
            NullLogger<MetadataScrapeCoordinator>.Instance, TimeSpan.FromSeconds(30));

        using var cts = new CancellationTokenSource();
        cts.CancelAfter(TimeSpan.FromMilliseconds(200));

        Stopwatch stopwatch = Stopwatch.StartNew();
        await Assert.That(async () => await coordinator.ScrapeLibraryAsync(null, cts.Token))
            .Throws<OperationCanceledException>();
        stopwatch.Stop();

        await Assert.That(provider.CallCount).IsEqualTo(1)
            .Because("The first folder's provider call must complete before the throttle delay before the second folder begins.");
        await Assert.That(stopwatch.Elapsed).IsLessThan(TimeSpan.FromSeconds(10))
            .Because("The delay honours the CancellationToken, so cancellation cuts it short instead of sitting out the full 30s.");
    }
}
