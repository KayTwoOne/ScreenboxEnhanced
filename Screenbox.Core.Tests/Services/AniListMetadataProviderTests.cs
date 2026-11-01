using System;
using System.Net;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Threading;
using Screenbox.Core.Models;
using Screenbox.Core.Services;
using Screenbox.Core.Tests.Helpers;

namespace Screenbox.Core.Tests.Services;

public sealed class AniListMetadataProviderTests
{
    [Test]
    public async Task Name_IsAnilist()
    {
        AniListMetadataProvider provider = new(new HttpClient(new StubHttpMessageHandler(HttpStatusCode.OK, "{}")));

        await Assert.That(provider.Name).IsEqualTo("anilist");
    }

    [Test]
    public async Task SearchAsync_NormalResultSet_MapsEnglishTitleSynopsisYearAndPoster()
    {
        const string body = """
            {
              "data": {
                "Page": {
                  "media": [
                    {
                      "id": 101922,
                      "title": { "romaji": "Shingeki no Kyojin", "english": "Attack on Titan" },
                      "description": "Robots &amp; Humans fight.<br><br>(Source: Test)",
                      "startDate": { "year": 2013 },
                      "coverImage": {
                        "extraLarge": "https://s4.anilist.co/file/anilistcdn/media/anime/cover/large/bx101922-extra.jpg",
                        "large": "https://s4.anilist.co/file/anilistcdn/media/anime/cover/large/bx101922.jpg"
                      }
                    }
                  ]
                }
              }
            }
            """;
        StubHttpMessageHandler handler = new(HttpStatusCode.OK, body);
        AniListMetadataProvider provider = new(new HttpClient(handler));

        IReadOnlyList<MetadataMatch> matches = await provider.SearchAsync("Attack on Titan", CancellationToken.None);

        await Assert.That(matches).Count().IsEqualTo(1);
        MetadataMatch match = matches[0];
        await Assert.That(match.ProviderId).IsEqualTo("101922");
        await Assert.That(match.Title).IsEqualTo("Attack on Titan");
        await Assert.That(match.Synopsis).IsEqualTo("Robots & Humans fight.(Source: Test)");
        await Assert.That(match.Year).IsEqualTo(2013);
        await Assert.That(match.PosterUrl).IsEqualTo("https://s4.anilist.co/file/anilistcdn/media/anime/cover/large/bx101922-extra.jpg")
            .Because("The extra-large cover is preferred over the large one when AniList serves both.");
        await Assert.That(handler.CallCount).IsEqualTo(1);
    }

    [Test]
    [Arguments("""{ "extraLarge": null, "large": "https://s4.anilist.co/large.jpg" }""")]
    [Arguments("""{ "large": "https://s4.anilist.co/large.jpg" }""")]
    [Arguments("""{ "extraLarge": "  ", "large": "https://s4.anilist.co/large.jpg" }""")]
    public async Task SearchAsync_NoUsableExtraLargeCover_FallsBackToTheLargeOne(string coverImage)
    {
        string body = $$"""
            {
              "data": { "Page": { "media": [
                {
                  "id": 5,
                  "title": { "romaji": "Cowboy Bebop", "english": null },
                  "coverImage": {{coverImage}}
                }
              ] } }
            }
            """;
        AniListMetadataProvider provider = new(new HttpClient(new StubHttpMessageHandler(HttpStatusCode.OK, body)));

        IReadOnlyList<MetadataMatch> matches = await provider.SearchAsync("Cowboy Bebop", CancellationToken.None);

        await Assert.That(matches).Count().IsEqualTo(1);
        await Assert.That(matches[0].PosterUrl).IsEqualTo("https://s4.anilist.co/large.jpg");
    }

    [Test]
    public async Task SearchAsync_ExtraLargeOnly_UsesIt()
    {
        const string body = """
            {
              "data": { "Page": { "media": [
                {
                  "id": 6,
                  "title": { "romaji": "Cowboy Bebop", "english": null },
                  "coverImage": { "extraLarge": "https://s4.anilist.co/extra.jpg", "large": null }
                }
              ] } }
            }
            """;
        AniListMetadataProvider provider = new(new HttpClient(new StubHttpMessageHandler(HttpStatusCode.OK, body)));

        IReadOnlyList<MetadataMatch> matches = await provider.SearchAsync("Cowboy Bebop", CancellationToken.None);

        await Assert.That(matches[0].PosterUrl).IsEqualTo("https://s4.anilist.co/extra.jpg");
    }

    [Test]
    public async Task SearchAsync_TitleEnglishNull_FallsBackToRomaji()
    {
        const string body = """
            {
              "data": {
                "Page": {
                  "media": [
                    {
                      "id": 1,
                      "title": { "romaji": "Only Romaji Title", "english": null },
                      "description": null,
                      "startDate": { "year": null },
                      "coverImage": { "extraLarge": null, "large": null }
                    }
                  ]
                }
              }
            }
            """;
        AniListMetadataProvider provider = new(new HttpClient(new StubHttpMessageHandler(HttpStatusCode.OK, body)));

        IReadOnlyList<MetadataMatch> matches = await provider.SearchAsync("query", CancellationToken.None);

        await Assert.That(matches).Count().IsEqualTo(1);
        await Assert.That(matches[0].Title).IsEqualTo("Only Romaji Title");
        await Assert.That(matches[0].Synopsis).IsNull();
        await Assert.That(matches[0].Year).IsNull();
        await Assert.That(matches[0].PosterUrl).IsNull();
    }

    [Test]
    public async Task SearchAsync_EmptyResultSet_ReturnsEmptyListNotNull()
    {
        const string body = """{ "data": { "Page": { "media": [] } } }""";
        AniListMetadataProvider provider = new(new HttpClient(new StubHttpMessageHandler(HttpStatusCode.OK, body)));

        IReadOnlyList<MetadataMatch> matches = await provider.SearchAsync("nothing matches this", CancellationToken.None);

        await Assert.That(matches).IsNotNull();
        await Assert.That(matches).IsEmpty();
    }

    [Test]
    public async Task SearchAsync_MalformedJsonBody_ReturnsEmptyWithoutThrowing()
    {
        AniListMetadataProvider provider = new(new HttpClient(
            new StubHttpMessageHandler(HttpStatusCode.OK, "{ this is not valid json ")));

        IReadOnlyList<MetadataMatch> matches = await provider.SearchAsync("query", CancellationToken.None);

        await Assert.That(matches).IsNotNull();
        await Assert.That(matches).IsEmpty();
    }

    [Test]
    public async Task SearchAsync_NonSuccessStatusCode_ReturnsEmptyWithoutThrowing()
    {
        AniListMetadataProvider provider = new(new HttpClient(
            new StubHttpMessageHandler(HttpStatusCode.InternalServerError, "Internal Server Error")));

        IReadOnlyList<MetadataMatch> matches = await provider.SearchAsync("query", CancellationToken.None);

        await Assert.That(matches).IsNotNull();
        await Assert.That(matches).IsEmpty();
    }

    [Test]
    public async Task SearchAsync_HttpOkWithErrorsArray_ReturnsEmptyWithoutThrowing()
    {
        // THE TRAP: AniList answers a failed GraphQL query with HTTP 200 and an "errors" array.
        // "data" here is deliberately POPULATED with a spurious match - a provider that only
        // checks IsSuccessStatusCode (or only guards against null "data") would happily map and
        // return that match instead of honouring the error. Only an explicit errors check catches
        // this; this is what distinguishes the check from mere null-safety on the data chain.
        const string body = """
            {
              "data": {
                "Page": {
                  "media": [
                    {
                      "id": 999,
                      "title": { "romaji": "Should Not Appear", "english": null },
                      "description": null,
                      "startDate": { "year": null },
                      "coverImage": { "extraLarge": null, "large": null }
                    }
                  ]
                }
              },
              "errors": [
                { "message": "Validation error." }
              ]
            }
            """;
        AniListMetadataProvider provider = new(new HttpClient(new StubHttpMessageHandler(HttpStatusCode.OK, body)));

        IReadOnlyList<MetadataMatch> matches = await provider.SearchAsync("query", CancellationToken.None);

        await Assert.That(matches).IsNotNull();
        await Assert.That(matches).IsEmpty();
    }

    [Test]
    public async Task SearchAsync_CancelledToken_PropagatesCancellation()
    {
        StubHttpMessageHandler handler = new(HttpStatusCode.OK, """{ "data": { "Page": { "media": [] } } }""");
        AniListMetadataProvider provider = new(new HttpClient(handler));
        using CancellationTokenSource cts = new();
        await cts.CancelAsync();

        await Assert.That(async () => await provider.SearchAsync("query", cts.Token))
            .Throws<OperationCanceledException>();

        // The stub must never have produced a fabricated response for a cancelled call.
        await Assert.That(handler.CallCount).IsEqualTo(0);
    }

    [Test]
    public async Task SearchAsync_SendsGraphQlQueryWithSearchTermAsVariable()
    {
        StubHttpMessageHandler handler = new(HttpStatusCode.OK, """{ "data": { "Page": { "media": [] } } }""");
        AniListMetadataProvider provider = new(new HttpClient(handler));

        await provider.SearchAsync("Cowboy Bebop", CancellationToken.None);

        await Assert.That(handler.LastRequestBody).IsNotNull();
        await Assert.That(handler.LastRequestBody!).Contains("Cowboy Bebop");
        await Assert.That(handler.LastRequestBody!).Contains("ANIME");
        await Assert.That(handler.CallCount).IsEqualTo(1);

        // The extra-large cover is only ever returned if the query asks for it, and the large one
        // stays as the fallback. The stubbed response above would satisfy any query.
        await Assert.That(Regex.IsMatch(handler.LastRequestBody!, @"coverImage\s*\{[^}]*extraLarge[^}]*large[^}]*\}")).IsTrue();
    }

    [Test]
    public async Task SearchAsync_InternalRequestTimeoutElapses_ReturnsEmptyListWithoutThrowing()
    {
        // Distinct from the caller-cancellation test above: here the CALLER's token
        // (CancellationToken.None) is never cancelled. Only the provider's own internal
        // request-timeout budget expires mid-flight, so this must land in the "our own timeout
        // tripped" branch and be swallowed into an empty list rather than rethrown. Both paths
        // surface as OperationCanceledException, so without this test only the rethrowing half
        // was covered. The request actually starts (CallCount reaches 1), unlike the
        // pre-cancelled-caller case.
        StubHttpMessageHandler handler = new(async (_, ct) =>
        {
            await Task.Delay(TimeSpan.FromSeconds(5), ct);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""{ "data": { "Page": { "media": [] } } }""")
            };
        });
        AniListMetadataProvider provider = new(
            new HttpClient(handler),
            requestTimeout: TimeSpan.FromMilliseconds(250));

        IReadOnlyList<MetadataMatch> matches = await provider.SearchAsync("query", CancellationToken.None);

        await Assert.That(matches).IsNotNull();
        await Assert.That(matches).IsEmpty();
        await Assert.That(handler.CallCount).IsEqualTo(1);
    }
}
