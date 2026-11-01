using System;
using System.Collections.Generic;
using System.Globalization;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Screenbox.Core.Models;
using Screenbox.Core.Models.Serialization;

namespace Screenbox.Core.Services;

/// <summary>
/// <see cref="IMetadataProvider"/> backed by TMDB's <c>/search/tv</c> endpoint. Requires an API
/// key the user supplies from their own TMDB account - never hardcoded or committed. When the key
/// is null or empty, <see cref="SearchAsync"/> returns an empty result immediately and issues no
/// HTTP request, since a request without a key is destined to fail with 401.
/// </summary>
public sealed class TmdbMetadataProvider : IMetadataProvider
{
    private const string Endpoint = "https://api.themoviedb.org/3/search/tv";
    private const string PosterBaseUrl = "https://image.tmdb.org/t/p/w500";

    private static readonly TimeSpan DefaultRequestTimeout = TimeSpan.FromSeconds(15);

    private readonly HttpClient _client;
    private readonly string? _apiKey;
    private readonly ILogger<TmdbMetadataProvider> _logger;
    private readonly TimeSpan _requestTimeout;

    /// <inheritdoc />
    public string Name => "tmdb";

    public TmdbMetadataProvider(
        HttpClient client,
        string? apiKey,
        ILogger<TmdbMetadataProvider>? logger = null,
        TimeSpan? requestTimeout = null)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
        _apiKey = apiKey;
        _logger = logger ?? NullLogger<TmdbMetadataProvider>.Instance;
        _requestTimeout = requestTimeout ?? DefaultRequestTimeout;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<MetadataMatch>> SearchAsync(string query, CancellationToken ct)
    {
        // A request without a key is destined for a 401 - don't send it.
        if (string.IsNullOrEmpty(_apiKey))
        {
            return Array.Empty<MetadataMatch>();
        }

        using CancellationTokenSource timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(_requestTimeout);

        try
        {
            // _apiKey ?? string.Empty: a null key must not itself divert control flow here via an
            // exception - the guard above is what must stop the request. Without that guard, this
            // still builds a URL and sends it, so a deleted guard shows up as a real (unwanted)
            // HTTP call rather than an incidentally-empty result from a thrown exception.
            string url = $"{Endpoint}?query={Uri.EscapeDataString(query)}&api_key={Uri.EscapeDataString(_apiKey ?? string.Empty)}";

            using HttpResponseMessage response = await _client
                .GetAsync(url, timeoutCts.Token)
                .ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning(
                    "TMDB search for '{Query}' failed with status code {StatusCode}.",
                    query, (int)response.StatusCode);
                return Array.Empty<MetadataMatch>();
            }

            string responseBody = await response.Content.ReadAsStringAsync(timeoutCts.Token).ConfigureAwait(false);

            TmdbSearchResponse? envelope;
            try
            {
                envelope = JsonSerializer.Deserialize(responseBody, MetadataJsonContext.Default.TmdbSearchResponse);
            }
            catch (JsonException e)
            {
                _logger.LogWarning(e, "TMDB search for '{Query}' returned a malformed response body.", query);
                return Array.Empty<MetadataMatch>();
            }

            List<TmdbTvResult>? results = envelope?.Results;
            if (results is null || results.Count == 0)
            {
                return Array.Empty<MetadataMatch>();
            }

            List<MetadataMatch> matches = new(results.Count);
            foreach (TmdbTvResult item in results)
            {
                if (string.IsNullOrWhiteSpace(item.Name))
                {
                    continue;
                }

                matches.Add(new MetadataMatch(
                    item.Id.ToString(CultureInfo.InvariantCulture),
                    item.Name!,
                    item.Overview,
                    ParseYear(item.FirstAirDate),
                    item.PosterPath is { Length: > 0 } path ? PosterBaseUrl + path : null));
            }

            return matches;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // The caller cancelled - propagate rather than swallowing it.
            throw;
        }
        catch (OperationCanceledException e)
        {
            // Our own request timeout tripped, not the caller's token - treat as a failed search.
            _logger.LogWarning(e, "TMDB search for '{Query}' timed out.", query);
            return Array.Empty<MetadataMatch>();
        }
        catch (Exception e)
        {
            _logger.LogError(e, "TMDB search for '{Query}' failed unexpectedly.", query);
            return Array.Empty<MetadataMatch>();
        }
    }

    /// <summary>
    /// Parses TMDB's <c>first_air_date</c> ("YYYY-MM-DD", possibly an empty string) into a
    /// nullable year. Never trusts the field to be a well-formed date - an empty string or a
    /// too-short value both yield null rather than throwing.
    /// </summary>
    private static int? ParseYear(string? firstAirDate)
    {
        if (string.IsNullOrEmpty(firstAirDate) || firstAirDate.Length < 4)
        {
            return null;
        }

        return int.TryParse(
            firstAirDate.AsSpan(0, 4),
            NumberStyles.Integer,
            CultureInfo.InvariantCulture,
            out int year)
            ? year
            : null;
    }
}
