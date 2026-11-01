using System.Net;
using System.Net.Http;
using System.Threading;
using Screenbox.Core.Models;
using Screenbox.Core.Services;
using Screenbox.Core.Tests.Helpers;

namespace Screenbox.Core.Tests.Services;

public sealed class JikanMetadataProviderTests
{
    [Test]
    public async Task Name_IsJikan()
    {
        JikanMetadataProvider provider = new(new HttpClient(new StubHttpMessageHandler(HttpStatusCode.OK, "{}")));

        await Assert.That(provider.Name).IsEqualTo("jikan");
    }

    [Test]
    public async Task SearchAsync_NormalResultSet_MapsTitleSynopsisYearAndPoster()
    {
        const string body = """
            {
              "pagination": { "last_visible_page": 1 },
              "data": [
                {
                  "mal_id": 1,
                  "title": "Cowboy Bebop",
                  "title_english": "Cowboy Bebop",
                  "synopsis": "In the year 2071, humanity has colonized the solar system.",
                  "year": 1998,
                  "images": {
                    "jpg": {
                      "image_url": "https://cdn.myanimelist.net/images/anime/4/19644.jpg",
                      "small_image_url": "https://cdn.myanimelist.net/images/anime/4/19644t.jpg",
                      "large_image_url": "https://cdn.myanimelist.net/images/anime/4/19644l.jpg"
                    },
                    "webp": {
                      "image_url": "https://cdn.myanimelist.net/images/anime/4/19644.webp"
                    }
                  }
                }
              ]
            }
            """;
        StubHttpMessageHandler handler = new(HttpStatusCode.OK, body);
        JikanMetadataProvider provider = new(new HttpClient(handler));

        IReadOnlyList<MetadataMatch> matches = await provider.SearchAsync("Cowboy Bebop", CancellationToken.None);

        await Assert.That(matches).Count().IsEqualTo(1);
        MetadataMatch match = matches[0];
        await Assert.That(match.ProviderId).IsEqualTo("1");
        await Assert.That(match.Title).IsEqualTo("Cowboy Bebop");
        await Assert.That(match.Synopsis).IsEqualTo("In the year 2071, humanity has colonized the solar system.");
        await Assert.That(match.Year).IsEqualTo(1998);
        await Assert.That(match.PosterUrl).IsEqualTo("https://cdn.myanimelist.net/images/anime/4/19644l.jpg");
        await Assert.That(handler.CallCount).IsEqualTo(1);
    }

    [Test]
    public async Task SearchAsync_TitleEnglishAndYearNull_FallsBackToTitleAndNullYear()
    {
        const string body = """
            {
              "data": [
                {
                  "mal_id": 5,
                  "title": "Only Native Title",
                  "title_english": null,
                  "synopsis": null,
                  "year": null,
                  "images": { "jpg": { "large_image_url": null } }
                }
              ]
            }
            """;
        JikanMetadataProvider provider = new(new HttpClient(new StubHttpMessageHandler(HttpStatusCode.OK, body)));

        IReadOnlyList<MetadataMatch> matches = await provider.SearchAsync("query", CancellationToken.None);

        await Assert.That(matches).Count().IsEqualTo(1);
        await Assert.That(matches[0].Title).IsEqualTo("Only Native Title");
        await Assert.That(matches[0].Synopsis).IsNull();
        await Assert.That(matches[0].Year).IsNull();
        await Assert.That(matches[0].PosterUrl).IsNull();
    }

    [Test]
    public async Task SearchAsync_EmptyResultSet_ReturnsEmptyListNotNull()
    {
        const string body = """{ "data": [] }""";
        JikanMetadataProvider provider = new(new HttpClient(new StubHttpMessageHandler(HttpStatusCode.OK, body)));

        IReadOnlyList<MetadataMatch> matches = await provider.SearchAsync("nothing matches this", CancellationToken.None);

        await Assert.That(matches).IsNotNull();
        await Assert.That(matches).IsEmpty();
    }

    [Test]
    public async Task SearchAsync_MalformedJsonBody_ReturnsEmptyWithoutThrowing()
    {
        JikanMetadataProvider provider = new(new HttpClient(
            new StubHttpMessageHandler(HttpStatusCode.OK, "{ this is not valid json ")));

        IReadOnlyList<MetadataMatch> matches = await provider.SearchAsync("query", CancellationToken.None);

        await Assert.That(matches).IsNotNull();
        await Assert.That(matches).IsEmpty();
    }

    [Test]
    public async Task SearchAsync_NonSuccessStatusCode_ReturnsEmptyWithoutThrowing()
    {
        JikanMetadataProvider provider = new(new HttpClient(
            new StubHttpMessageHandler(HttpStatusCode.InternalServerError, "Internal Server Error")));

        IReadOnlyList<MetadataMatch> matches = await provider.SearchAsync("query", CancellationToken.None);

        await Assert.That(matches).IsNotNull();
        await Assert.That(matches).IsEmpty();
    }

    [Test]
    public async Task SearchAsync_CancelledToken_PropagatesCancellation()
    {
        StubHttpMessageHandler handler = new(HttpStatusCode.OK, """{ "data": [] }""");
        JikanMetadataProvider provider = new(new HttpClient(handler));
        using CancellationTokenSource cts = new();
        await cts.CancelAsync();

        await Assert.That(async () => await provider.SearchAsync("query", cts.Token))
            .Throws<OperationCanceledException>();

        // The stub must never have produced a fabricated response for a cancelled call.
        await Assert.That(handler.CallCount).IsEqualTo(0);
    }

    [Test]
    public async Task SearchAsync_InternalRequestTimeoutElapses_ReturnsEmptyListWithoutThrowing()
    {
        // Distinct from the caller-cancellation test above: here the CALLER's token
        // (CancellationToken.None) is never cancelled. Only the provider's own internal
        // request-timeout budget expires mid-flight, so this must land in the "our own timeout
        // tripped" branch and be swallowed into an empty list rather than rethrown. The request
        // actually starts (CallCount reaches 1), unlike the pre-cancelled-caller case.
        StubHttpMessageHandler handler = new(async (_, ct) =>
        {
            await Task.Delay(TimeSpan.FromSeconds(5), ct);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""{ "data": [] }""")
            };
        });
        JikanMetadataProvider provider = new(
            new HttpClient(handler),
            requestTimeout: TimeSpan.FromMilliseconds(250));

        IReadOnlyList<MetadataMatch> matches = await provider.SearchAsync("query", CancellationToken.None);

        await Assert.That(matches).IsNotNull();
        await Assert.That(matches).IsEmpty();
        await Assert.That(handler.CallCount).IsEqualTo(1);
    }
}
