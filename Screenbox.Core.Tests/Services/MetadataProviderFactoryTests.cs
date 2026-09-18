using System.Collections.Generic;
using System.Linq;
using Microsoft.Extensions.Logging.Abstractions;
using Screenbox.Core.Services;
using Screenbox.Core.Tests.Helpers;

namespace Screenbox.Core.Tests.Services;

/// <summary>
/// Exercises the REAL <see cref="MetadataProviderFactory"/> - unlike
/// <see cref="Coordinators.MetadataScrapeCoordinatorTests"/>, which drives the coordinator against a
/// fake factory. These tests never call <see cref="IMetadataProvider.SearchAsync"/>, so constructing
/// a real provider (and the real <see cref="System.Net.Http.HttpClient"/> behind it) here issues no
/// HTTP request - only calling SearchAsync would, and nothing here does.
/// </summary>
public sealed class MetadataProviderFactoryTests
{
    [Test]
    public async Task GetEnabledProviders_NothingEnabled_ReturnsEmptyList()
    {
        var settings = new TestSettingsService();
        using var factory = new MetadataProviderFactory(settings, NullLoggerFactory.Instance);

        IReadOnlyList<IMetadataProvider> providers = factory.GetEnabledProviders();

        await Assert.That(providers).IsEmpty();
    }

    [Test]
    public async Task GetEnabledProviders_OnlyJikanEnabled_ReturnsOnlyJikan()
    {
        var settings = new TestSettingsService { MetadataUseJikan = true };
        using var factory = new MetadataProviderFactory(settings, NullLoggerFactory.Instance);

        IReadOnlyList<IMetadataProvider> providers = factory.GetEnabledProviders();

        await Assert.That(providers).Count().IsEqualTo(1);
        await Assert.That(providers[0].Name).IsEqualTo("jikan");
    }

    [Test]
    public async Task GetEnabledProviders_AllEnabled_ReturnsAllThreeInAFixedOrder()
    {
        var settings = new TestSettingsService
        {
            MetadataUseAniList = true,
            MetadataUseJikan = true,
            MetadataUseTmdb = true,
            TmdbApiKey = "fake-key-for-construction-only"
        };
        using var factory = new MetadataProviderFactory(settings, NullLoggerFactory.Instance);

        IReadOnlyList<IMetadataProvider> providers = factory.GetEnabledProviders();

        await Assert.That(providers).Count().IsEqualTo(3);
        await Assert.That(providers[0].Name).IsEqualTo("anilist");
        await Assert.That(providers[1].Name).IsEqualTo("jikan");
        await Assert.That(providers[2].Name).IsEqualTo("tmdb");
    }

    [Test]
    public async Task GetEnabledProviders_DisabledProviderIsAbsent_EnabledOneIsPresent()
    {
        var settings = new TestSettingsService { MetadataUseAniList = true, MetadataUseTmdb = false, MetadataUseJikan = false };
        using var factory = new MetadataProviderFactory(settings, NullLoggerFactory.Instance);

        IReadOnlyList<IMetadataProvider> providers = factory.GetEnabledProviders();

        await Assert.That(providers.Any(p => p.Name == "anilist")).IsTrue();
        await Assert.That(providers.Any(p => p.Name is "jikan" or "tmdb")).IsFalse();
    }

    [Test]
    public async Task GetEnabledProviders_CalledTwice_SucceedsBothTimesWithFreshInstances()
    {
        // The coordinator asks anew on every scrape. Each call must construct fresh provider
        // instances - only the shared HttpClient behind them is reused, which is an implementation
        // detail this test cannot observe directly, but a second call must at least succeed cleanly.
        var settings = new TestSettingsService { MetadataUseAniList = true };
        using var factory = new MetadataProviderFactory(settings, NullLoggerFactory.Instance);

        IReadOnlyList<IMetadataProvider> first = factory.GetEnabledProviders();
        IReadOnlyList<IMetadataProvider> second = factory.GetEnabledProviders();

        await Assert.That(first).Count().IsEqualTo(1);
        await Assert.That(second).Count().IsEqualTo(1);
        await Assert.That(ReferenceEquals(first[0], second[0])).IsFalse();
    }

    [Test]
    public async Task Dispose_WhenNothingWasEverConstructed_DoesNotThrow()
    {
        var settings = new TestSettingsService();
        var factory = new MetadataProviderFactory(settings, NullLoggerFactory.Instance);

        factory.Dispose();

        // Reaching this line without an exception from Dispose() above is the actual assertion.
        await Assert.That(true).IsTrue();
    }

    [Test]
    public async Task Dispose_AfterConstructingProviders_DoesNotThrow()
    {
        var settings = new TestSettingsService { MetadataUseAniList = true };
        var factory = new MetadataProviderFactory(settings, NullLoggerFactory.Instance);
        factory.GetEnabledProviders();

        factory.Dispose();

        await Assert.That(true).IsTrue();
    }
}
