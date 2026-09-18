using System;
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
    private static MetadataScrapeCoordinator CreateCoordinator(
        TestSettingsService settings, IMetadataProviderFactory factory, IDatabaseService database) =>
        new(factory, settings, database, NullLogger<MetadataScrapeCoordinator>.Instance);

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
    public async Task ScrapeFolderAsync_TriesProvidersInOrder_FirstNonEmptyResultWins()
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
            .Because("The first provider already answered - a later provider is never consulted, win or lose.");
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
    // folder_metadata guard - fills blanks, never clobbers a user's choice.
    // ------------------------------------------------------------------

    [Test]
    public async Task ScrapeFolderAsync_NoExistingTitle_FillsCustomTitleFromTheMatch()
    {
        using var fixture = new TestDirectoryFixture();
        DatabaseService db = await CreateDatabaseAsync(fixture.DirectoryPath);
        const string path = @"D:\Anime\Dan Da Dan";

        var provider = new FakeMetadataProvider("anilist",
            [new MetadataMatch("1", "Dan Da Dan", "synopsis", 2024, "https://example.com/poster.jpg")]);
        var settings = new TestSettingsService { MetadataEnabled = true };
        var coordinator = CreateCoordinator(settings, new FakeMetadataProviderFactory(provider), db);

        await coordinator.ScrapeFolderAsync(path, "Dan Da Dan", CancellationToken.None);

        FolderMetadataDto? metadata = await db.LoadFolderMetadataAsync(path);
        await Assert.That(metadata).IsNotNull();
        await Assert.That(metadata!.CustomTitle).IsEqualTo("Dan Da Dan");
    }

    [Test]
    public async Task ScrapeFolderAsync_NeverOverwritesAManualPosterOrCustomTitle()
    {
        // folder_metadata is durable user data. A scrape may fill blanks, never clobber choices.
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
        var coordinator = CreateCoordinator(settings, new FakeMetadataProviderFactory(provider), db);

        MetadataMatch? result = await coordinator.ScrapeFolderAsync(path, "Dan Da Dan", CancellationToken.None);

        await Assert.That(result).IsNotNull().Because("The scrape itself must still succeed and be cached.");

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
}
