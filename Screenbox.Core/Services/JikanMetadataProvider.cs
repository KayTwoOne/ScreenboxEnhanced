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
/// <see cref="IMetadataProvider"/> backed by Jikan v4, the unofficial MyAnimeList API. No API key
/// is required. Intended as a fallback source when <see cref="AniListMetadataProvider"/> yields no
/// results.
/// </summary>
public sealed class JikanMetadataProvider : IMetadataProvider
{
    private const string Endpoint = "https://api.jikan.moe/v4/anime";
    private const string UserAgent = "Screenbox/1.0 (+https://github.com/screenbox)";

    private static readonly TimeSpan DefaultRequestTimeout = TimeSpan.FromSeconds(15);

    private readonly HttpClient _client;
    private readonly ILogger<JikanMetadataProvider> _logger;
    private readonly TimeSpan _requestTimeout;

    /// <inheritdoc />
    public string Name => "jikan";

    public JikanMetadataProvider(
        HttpClient client,
        ILogger<JikanMetadataProvider>? logger = null,
        TimeSpan? requestTimeout = null)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
        _logger = logger ?? NullLogger<JikanMetadataProvider>.Instance;
        _requestTimeout = requestTimeout ?? DefaultRequestTimeout;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<MetadataMatch>> SearchAsync(string query, CancellationToken ct)
    {
        using CancellationTokenSource timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(_requestTimeout);

        try
        {
            string url = $"{Endpoint}?q={Uri.EscapeDataString(query)}&limit=5";
            using HttpRequestMessage request = new(HttpMethod.Get, url);
            request.Headers.UserAgent.ParseAdd(UserAgent);

            using HttpResponseMessage response = await _client
                .SendAsync(request, timeoutCts.Token)
                .ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning(
                    "Jikan search for '{Query}' failed with status code {StatusCode}.",
                    query, (int)response.StatusCode);
                return Array.Empty<MetadataMatch>();
            }

            string responseBody = await response.Content.ReadAsStringAsync(timeoutCts.Token).ConfigureAwait(false);

            JikanResponse? envelope;
            try
            {
                envelope = JsonSerializer.Deserialize(responseBody, MetadataJsonContext.Default.JikanResponse);
            }
            catch (JsonException e)
            {
                _logger.LogWarning(e, "Jikan search for '{Query}' returned a malformed response body.", query);
                return Array.Empty<MetadataMatch>();
            }

            List<JikanAnime>? data = envelope?.Data;
            if (data is null || data.Count == 0)
            {
                return Array.Empty<MetadataMatch>();
            }

            List<MetadataMatch> matches = new(data.Count);
            foreach (JikanAnime item in data)
            {
                string? title = !string.IsNullOrWhiteSpace(item.TitleEnglish)
                    ? item.TitleEnglish
                    : item.Title;

                if (string.IsNullOrWhiteSpace(title))
                {
                    continue;
                }

                matches.Add(new MetadataMatch(
                    item.MalId.ToString(CultureInfo.InvariantCulture),
                    title!,
                    item.Synopsis,
                    item.Year,
                    item.Images?.Jpg?.LargeImageUrl));
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
            _logger.LogWarning(e, "Jikan search for '{Query}' timed out.", query);
            return Array.Empty<MetadataMatch>();
        }
        catch (Exception e)
        {
            _logger.LogError(e, "Jikan search for '{Query}' failed unexpectedly.", query);
            return Array.Empty<MetadataMatch>();
        }
    }
}
