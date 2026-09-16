using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Screenbox.Core.Models;
using Screenbox.Core.Models.Serialization;

namespace Screenbox.Core.Services;

/// <summary>
/// <see cref="IMetadataProvider"/> backed by AniList's public GraphQL endpoint. No API key is
/// required and rates are unauthenticated. AniList reports a failed query as HTTP 200 with a
/// populated "errors" array rather than a non-2xx status, so success is determined from the
/// response body, never from the status line alone.
/// </summary>
public sealed class AniListMetadataProvider : IMetadataProvider
{
    private const string Endpoint = "https://graphql.anilist.co";

    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(15);

    private const string SearchQuery =
        """
        query ($q: String) {
          Page(perPage: 5) {
            media(search: $q, type: ANIME) {
              id
              title { romaji english }
              description
              startDate { year }
              coverImage { large }
            }
          }
        }
        """;

    private static readonly Regex HtmlTagRegex = new(
        "<[^>]*>", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private readonly HttpClient _client;
    private readonly ILogger<AniListMetadataProvider> _logger;

    /// <inheritdoc />
    public string Name => "anilist";

    public AniListMetadataProvider(HttpClient client, ILogger<AniListMetadataProvider>? logger = null)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
        _logger = logger ?? NullLogger<AniListMetadataProvider>.Instance;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<MetadataMatch>> SearchAsync(string query, CancellationToken ct)
    {
        using CancellationTokenSource timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(RequestTimeout);

        try
        {
            AniListRequest requestBody = new()
            {
                Query = SearchQuery,
                Variables = new AniListVariables { Q = query }
            };

            string requestJson = JsonSerializer.Serialize(requestBody, MetadataJsonContext.Default.AniListRequest);
            using StringContent content = new(requestJson, Encoding.UTF8, "application/json");

            using HttpResponseMessage response = await _client
                .PostAsync(Endpoint, content, timeoutCts.Token)
                .ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning(
                    "AniList search for '{Query}' failed with status code {StatusCode}.",
                    query, (int)response.StatusCode);
                return Array.Empty<MetadataMatch>();
            }

            string responseBody = await response.Content.ReadAsStringAsync(timeoutCts.Token).ConfigureAwait(false);

            AniListResponse? envelope;
            try
            {
                envelope = JsonSerializer.Deserialize(responseBody, MetadataJsonContext.Default.AniListResponse);
            }
            catch (JsonException e)
            {
                _logger.LogWarning(e, "AniList search for '{Query}' returned a malformed response body.", query);
                return Array.Empty<MetadataMatch>();
            }

            // The trap: AniList answers a failed GraphQL query with HTTP 200 and an "errors"
            // array, with "data" absent or null. Check for that independently of the status code.
            if (envelope?.Errors is { Count: > 0 } errors)
            {
                string messages = string.Join("; ", errors.Select(e => e.Message ?? "unknown error"));
                _logger.LogWarning("AniList search for '{Query}' returned errors: {Errors}", query, messages);
                return Array.Empty<MetadataMatch>();
            }

            List<AniListMedia>? media = envelope?.Data?.Page?.Media;
            if (media is null || media.Count == 0)
            {
                return Array.Empty<MetadataMatch>();
            }

            List<MetadataMatch> matches = new(media.Count);
            foreach (AniListMedia item in media)
            {
                string? title = !string.IsNullOrWhiteSpace(item.Title?.English)
                    ? item.Title!.English
                    : item.Title?.Romaji;

                if (string.IsNullOrWhiteSpace(title))
                {
                    continue;
                }

                matches.Add(new MetadataMatch(
                    item.Id.ToString(CultureInfo.InvariantCulture),
                    title!,
                    StripHtml(item.Description),
                    item.StartDate?.Year,
                    item.CoverImage?.Large,
                    1.0));
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
            _logger.LogWarning(e, "AniList search for '{Query}' timed out.", query);
            return Array.Empty<MetadataMatch>();
        }
        catch (Exception e)
        {
            _logger.LogError(e, "AniList search for '{Query}' failed unexpectedly.", query);
            return Array.Empty<MetadataMatch>();
        }
    }

    private static string? StripHtml(string? html)
    {
        if (string.IsNullOrEmpty(html))
        {
            return html;
        }

        string withoutTags = HtmlTagRegex.Replace(html, string.Empty);
        return WebUtility.HtmlDecode(withoutTags).Trim();
    }
}
