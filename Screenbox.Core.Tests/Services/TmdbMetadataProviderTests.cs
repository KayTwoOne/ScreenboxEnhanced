using System.Net;
using System.Net.Http;
using System.Threading;
using Screenbox.Core.Models;
using Screenbox.Core.Services;
using Screenbox.Core.Tests.Helpers;

namespace Screenbox.Core.Tests.Services;

public sealed class TmdbMetadataProviderTests
{
    private const string FakeApiKey = "unit-test-fake-api-key";

    [Test]
    public async Task Name_IsTmdb()
    {
        TmdbMetadataProvider provider = new(new HttpClient(new StubHttpMessageHandler(HttpStatusCode.OK, "{}")), FakeApiKey);

        await Assert.That(provider.Name).IsEqualTo("tmdb");
    }

    [Test]
    public async Task SearchAsync_NormalResultSet_MapsTitleSynopsisYearAndPoster()
    {
        const string body = """
            {
              "page": 1,
              "results": [
                {
                  "id": 1396,
                  "name": "Breaking Bad",
                  "overview": "A high school chemistry teacher diagnosed with cancer turns to crime.",
                  "first_air_date": "2008-01-20",
                  "poster_path": "/ggFHVNu6YYI5L9pCfOacjizRGt.jpg"
                }
              ],
              "total_pages": 1,
              "total_results": 1
            }
            """;
        StubHttpMessageHandler handler = new(HttpStatusCode.OK, body);
        TmdbMetadataProvider provider = new(new HttpClient(handler), FakeApiKey);

        IReadOnlyList<MetadataMatch> matches = await provider.SearchAsync("Breaking Bad", CancellationToken.None);

        await Assert.That(matches).Count().IsEqualTo(1);
        MetadataMatch match = matches[0];
        await Assert.That(match.ProviderId).IsEqualTo("1396");
        await Assert.That(match.Title).IsEqualTo("Breaking Bad");
        await Assert.That(match.Synopsis).IsEqualTo("A high school chemistry teacher diagnosed with cancer turns to crime.");
        await Assert.That(match.Year).IsEqualTo(2008);
        await Assert.That(match.PosterUrl).IsEqualTo("https://image.tmdb.org/t/p/w500/ggFHVNu6YYI5L9pCfOacjizRGt.jpg");
        await Assert.That(handler.CallCount).IsEqualTo(1);
    }

    [Test]
    public async Task SearchAsync_EmptyFirstAirDateAndNullPosterPath_YieldsNullYearAndNullPoster()
    {
        // THE TRAP: first_air_date is a "YYYY-MM-DD" string that can be an empty string. A naive
        // int.Parse(date[..4]) or DateTime.Parse on "" throws FormatException. Only defensive
        // parsing catches this without an exception escaping SearchAsync.
        const string body = """
            {
              "results": [
                {
                  "id": 42,
                  "name": "Unreleased Show",
                  "overview": null,
                  "first_air_date": "",
                  "poster_path": null
                }
              ]
            }
            """;
        TmdbMetadataProvider provider = new(new HttpClient(new StubHttpMessageHandler(HttpStatusCode.OK, body)), FakeApiKey);

        IReadOnlyList<MetadataMatch> matches = await provider.SearchAsync("query", CancellationToken.None);

        await Assert.That(matches).Count().IsEqualTo(1);
        await Assert.That(matches[0].Title).IsEqualTo("Unreleased Show");
        await Assert.That(matches[0].Year).IsNull();
        await Assert.That(matches[0].PosterUrl).IsNull();
    }

    [Test]
    public async Task SearchAsync_EmptyResultSet_ReturnsEmptyListNotNull()
    {
        const string body = """{ "results": [] }""";
        TmdbMetadataProvider provider = new(new HttpClient(new StubHttpMessageHandler(HttpStatusCode.OK, body)), FakeApiKey);

        IReadOnlyList<MetadataMatch> matches = await provider.SearchAsync("nothing matches this", CancellationToken.None);

        await Assert.That(matches).IsNotNull();
        await Assert.That(matches).IsEmpty();
    }

    [Test]
    public async Task SearchAsync_MalformedJsonBody_ReturnsEmptyWithoutThrowing()
    {
        TmdbMetadataProvider provider = new(new HttpClient(
            new StubHttpMessageHandler(HttpStatusCode.OK, "{ this is not valid json ")), FakeApiKey);

        IReadOnlyList<MetadataMatch> matches = await provider.SearchAsync("query", CancellationToken.None);

        await Assert.That(matches).IsNotNull();
        await Assert.That(matches).IsEmpty();
    }

    [Test]
    public async Task SearchAsync_NonSuccessStatusCode_ReturnsEmptyWithoutThrowing()
    {
        TmdbMetadataProvider provider = new(new HttpClient(
            new StubHttpMessageHandler(HttpStatusCode.InternalServerError, "Internal Server Error")), FakeApiKey);

        IReadOnlyList<MetadataMatch> matches = await provider.SearchAsync("query", CancellationToken.None);

        await Assert.That(matches).IsNotNull();
        await Assert.That(matches).IsEmpty();
    }

    [Test]
    public async Task SearchAsync_CancelledToken_PropagatesCancellation()
    {
        StubHttpMessageHandler handler = new(HttpStatusCode.OK, """{ "results": [] }""");
        TmdbMetadataProvider provider = new(new HttpClient(handler), FakeApiKey);
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
                Content = new StringContent("""{ "results": [] }""")
            };
        });
        TmdbMetadataProvider provider = new(
            new HttpClient(handler),
            FakeApiKey,
            requestTimeout: TimeSpan.FromMilliseconds(20));

        IReadOnlyList<MetadataMatch> matches = await provider.SearchAsync("query", CancellationToken.None);

        await Assert.That(matches).IsNotNull();
        await Assert.That(matches).IsEmpty();
        await Assert.That(handler.CallCount).IsEqualTo(1);
    }

    [Test]
    public async Task SearchAsync_NullApiKey_ReturnsEmptyWithZeroHttpCalls()
    {
        StubHttpMessageHandler handler = new(HttpStatusCode.OK, """{ "results": [] }""");
        TmdbMetadataProvider provider = new(new HttpClient(handler), null);

        IReadOnlyList<MetadataMatch> matches = await provider.SearchAsync("query", CancellationToken.None);

        await Assert.That(matches).IsNotNull();
        await Assert.That(matches).IsEmpty();
        await Assert.That(handler.CallCount).IsEqualTo(0);
    }

    [Test]
    public async Task SearchAsync_EmptyApiKey_ReturnsEmptyWithZeroHttpCalls()
    {
        StubHttpMessageHandler handler = new(HttpStatusCode.OK, """{ "results": [] }""");
        TmdbMetadataProvider provider = new(new HttpClient(handler), string.Empty);

        IReadOnlyList<MetadataMatch> matches = await provider.SearchAsync("query", CancellationToken.None);

        await Assert.That(matches).IsNotNull();
        await Assert.That(matches).IsEmpty();
        await Assert.That(handler.CallCount).IsEqualTo(0);
    }
}
