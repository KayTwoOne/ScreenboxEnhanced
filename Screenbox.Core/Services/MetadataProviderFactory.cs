using System;
using System.Collections.Generic;
using System.Net.Http;
using Microsoft.Extensions.Logging;

namespace Screenbox.Core.Services;

/// <inheritdoc cref="IMetadataProviderFactory"/>
public sealed partial class MetadataProviderFactory : IMetadataProviderFactory
{
    /// <summary>
    /// Identifies this app to metadata providers. Jikan asks callers to identify themselves;
    /// AniList and TMDB do not require it, but sending one is good API etiquette. The URL is
    /// where an API operator can reach whoever maintains this build, so it must point at this
    /// fork - not at the original Screenbox project, which does not send these requests.
    /// </summary>
    internal const string UserAgent = "ScreenboxEnhanced/1.0 (+https://github.com/KayTwoOne/ScreenboxEnhanced)";

    private static readonly TimeSpan ClientTimeout = TimeSpan.FromSeconds(30);

    private readonly ISettingsService _settingsService;
    private readonly ILoggerFactory _loggerFactory;
    private readonly object _lock = new();

    private HttpClient? _client;
    private bool _disposed;

    public MetadataProviderFactory(ISettingsService settingsService, ILoggerFactory loggerFactory)
    {
        _settingsService = settingsService;
        _loggerFactory = loggerFactory;
    }

    /// <inheritdoc/>
    public IReadOnlyList<IMetadataProvider> GetEnabledProviders()
    {
        bool useAniList = _settingsService.MetadataUseAniList;
        bool useJikan = _settingsService.MetadataUseJikan;
        bool useTmdb = _settingsService.MetadataUseTmdb;

        if (!useAniList && !useJikan && !useTmdb)
        {
            // Nothing enabled - not even the shared HttpClient gets created.
            return Array.Empty<IMetadataProvider>();
        }

        HttpClient client = GetOrCreateClient();
        List<IMetadataProvider> providers = new(3);

        if (useAniList)
        {
            providers.Add(new AniListMetadataProvider(client, _loggerFactory.CreateLogger<AniListMetadataProvider>()));
        }

        if (useJikan)
        {
            providers.Add(new JikanMetadataProvider(client, _loggerFactory.CreateLogger<JikanMetadataProvider>()));
        }

        if (useTmdb)
        {
            providers.Add(new TmdbMetadataProvider(
                client, _settingsService.TmdbApiKey, _loggerFactory.CreateLogger<TmdbMetadataProvider>()));
        }

        return providers;
    }

    private HttpClient GetOrCreateClient()
    {
        if (_client is { } existing)
        {
            return existing;
        }

        lock (_lock)
        {
            if (_client is null)
            {
                HttpClient client = new() { Timeout = ClientTimeout };
                client.DefaultRequestHeaders.UserAgent.ParseAdd(UserAgent);
                _client = client;
            }

            return _client;
        }
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _client?.Dispose();
    }
}
