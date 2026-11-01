using System;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace Screenbox.Core.Tests.Helpers;

/// <summary>
/// An <see cref="HttpMessageHandler"/> stub that never touches the network. Every request is
/// answered from memory with a canned status code and body, or routed through a custom responder
/// for tests that need to simulate slow/cancellable responses or inspect the outgoing request.
/// Providers under test must be constructed with an <see cref="HttpClient"/> wrapping one of
/// these - no test in this suite may construct a real <see cref="HttpMessageHandler"/>.
/// </summary>
public sealed class StubHttpMessageHandler : HttpMessageHandler
{
    private readonly Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> _responder;

    /// <summary>Number of requests this handler has answered.</summary>
    public int CallCount { get; private set; }

    /// <summary>The most recently received request.</summary>
    public HttpRequestMessage? LastRequest { get; private set; }

    /// <summary>The most recently received request's body, captured before the responder runs.</summary>
    public string? LastRequestBody { get; private set; }

    /// <summary>Answers every request with a fixed status code and string body.</summary>
    public StubHttpMessageHandler(HttpStatusCode statusCode, string content)
        : this((_, _) => Task.FromResult(new HttpResponseMessage(statusCode)
        {
            Content = new StringContent(content)
        }))
    {
    }

    /// <summary>Answers every request via a custom responder, e.g. to simulate a slow response.</summary>
    public StubHttpMessageHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> responder)
    {
        _responder = responder ?? throw new ArgumentNullException(nameof(responder));
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        LastRequest = request;
        if (request.Content is not null)
        {
            LastRequestBody = await request.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        }

        // A pre-cancelled or cancelled-mid-flight token must surface as a cancellation, never
        // as a fabricated response - this is what lets tests prove cancellation propagates.
        cancellationToken.ThrowIfCancellationRequested();

        CallCount++;
        return await _responder(request, cancellationToken).ConfigureAwait(false);
    }
}
