using System.Net;
using System.Net.Http.Headers;
using System.Text;
using Microsoft.Extensions.Logging;
using Screenbox.Core.Enums;
using Screenbox.Core.Models;
using Screenbox.Core.Services;
using Screenbox.Core.Tests.Helpers;

namespace Screenbox.Core.Tests.Services;

/// <summary>
/// Covers <see cref="IArtworkService.FetchImageFromLinkAsync"/>: turning a pasted link into a
/// validated image file. Every request is answered from memory by a <see cref="StubHttpMessageHandler"/>;
/// no test here can reach the network. Each test records the URLs actually requested, because "was
/// anything else fetched" is half of what the guards promise.
/// </summary>
[Timeout(30_000)] // a fetch that hangs must fail its test, not stall the run
public sealed class ArtworkServiceImageLinkTests
{
    private const string PageUrl = "https://example.com/gallery/p";
    private const string ImageUrl = "https://i.example.com/a.png";

    /// <summary>
    /// An artwork service wired to a canned set of responses and a private temp folder. A URL with
    /// no route answers 404, and still shows up in <see cref="Requests"/>, so an unexpected extra
    /// request is visible rather than silently harmless.
    /// </summary>
    private sealed partial class Rig : IDisposable
    {
        private readonly TestDirectoryFixture _folder = new();
        private readonly Dictionary<string, Func<CancellationToken, Task<HttpResponseMessage>>> _routes = [];

        public Rig()
        {
            Handler = new StubHttpMessageHandler((request, ct) =>
            {
                string url = request.RequestUri!.ToString();
                Requests.Add(url);
                return _routes.TryGetValue(url, out Func<CancellationToken, Task<HttpResponseMessage>>? respond)
                    ? respond(ct)
                    : Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
            });
            Service = new ArtworkService(new ThrowingDatabaseService(), Log, Handler)
            {
                ImageLinkFolderPath = _folder.DirectoryPath
            };
        }

        public List<string> Requests { get; } = [];
        public CapturingLogger Log { get; } = new();
        public StubHttpMessageHandler Handler { get; }
        public ArtworkService Service { get; }
        public string TempFolderPath => _folder.DirectoryPath;

        public Rig Route(string url, Func<HttpResponseMessage> respond) =>
            RouteAsync(url, _ => Task.FromResult(respond()));

        public Rig RouteAsync(string url, Func<CancellationToken, Task<HttpResponseMessage>> respond)
        {
            _routes[url] = respond;
            return this;
        }

        public Rig Image(string url, byte[] bytes, string contentType = "image/png") =>
            Route(url, () => Bytes(bytes, contentType));

        public Rig Page(string url, string html) =>
            Route(url, () => Bytes(Encoding.UTF8.GetBytes(html), "text/html; charset=utf-8"));

        public Task<ImageLinkResult> FetchAsync(string url, CancellationToken ct = default) =>
            Service.FetchImageFromLinkAsync(url, ct);

        public string[] TempFiles() => Directory.GetFiles(TempFolderPath);

        public void Dispose()
        {
            Service.Dispose();
            _folder.Dispose();
        }
    }

    /// <summary>Renders a request order as one string, so a mismatch prints both sequences in full.</summary>
    private static string Order(IEnumerable<string> urls) => string.Join("  ->  ", urls);

    private static string Order(params string[] urls) => string.Join("  ->  ", urls);

    /// <summary>Keeps what the service logged, so a failing assertion can say why the service gave up.</summary>
    private sealed partial class CapturingLogger : ILogger<ArtworkService>
    {
        public List<string> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter) =>
            Entries.Add(formatter(state, exception) + (exception is null ? string.Empty : " :: " + exception));

        public override string ToString() => string.Join(" | ", Entries);
    }

    private static HttpResponseMessage Bytes(byte[] bytes, string? contentType)
    {
        ByteArrayContent content = new(bytes);
        if (contentType is not null)
        {
            content.Headers.ContentType = MediaTypeHeaderValue.Parse(contentType);
        }

        return new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
    }

    private static string PageWith(string headTags) =>
        $"<!doctype html><html><head><meta charset=\"utf-8\"><title>A picture</title>{headTags}</head><body><p>hi</p></body></html>";

    private static async Task<bool> SameBytesAsync(ImageLinkResult result, byte[] expected) =>
        result.Image is { } file && (await File.ReadAllBytesAsync(file.Path)).AsSpan().SequenceEqual(expected);

    /// <summary>
    /// A response body that claims a length but must never be read. Proves an oversized declaration
    /// is refused from the headers alone: a service that reads on anyway trips the exception.
    /// </summary>
    private sealed partial class UnreadableBodyContent : HttpContent
    {
        private readonly long _declaredLength;

        public UnreadableBodyContent(string contentType, long declaredLength)
        {
            _declaredLength = declaredLength;
            Headers.ContentType = MediaTypeHeaderValue.Parse(contentType);
        }

        protected override bool TryComputeLength(out long length)
        {
            length = _declaredLength;
            return true;
        }

        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) =>
            throw new InvalidOperationException("The body must not be read once its declared length is over the cap.");
    }

    /// <summary>
    /// A response body that declares no length and is handed out as a plain forward-only stream
    /// rather than being buffered first, like a chunked response. Counts the bytes actually pulled
    /// from it so a test can prove the reader stopped at the cap instead of draining the rest.
    /// </summary>
    private sealed partial class UnknownLengthContent : HttpContent
    {
        private readonly byte[] _bytes;
        private readonly CountingStream _stream;

        public UnknownLengthContent(string contentType, byte[] bytes)
        {
            _bytes = bytes;
            _stream = new CountingStream(bytes);
            Headers.ContentType = MediaTypeHeaderValue.Parse(contentType);
        }

        public long BytesServed => _stream.BytesServed;

        protected override bool TryComputeLength(out long length)
        {
            length = 0;
            return false;
        }

        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) =>
            stream.WriteAsync(_bytes, 0, _bytes.Length);

        protected override Task<Stream> CreateContentReadStreamAsync() => Task.FromResult<Stream>(_stream);
    }

    private sealed partial class CountingStream(byte[] bytes) : Stream
    {
        private int _position;

        public long BytesServed => _position;
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            int n = Math.Min(count, bytes.Length - _position);
            Array.Copy(bytes, _position, buffer, offset, n);
            _position += n;
            return n;
        }

        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    /// <summary>A body whose first read never completes until cancelled: a server that stalls mid-download.</summary>
    private sealed partial class StalledBodyContent : HttpContent
    {
        public StalledBodyContent(string contentType) => Headers.ContentType = MediaTypeHeaderValue.Parse(contentType);

        protected override bool TryComputeLength(out long length)
        {
            length = 0;
            return false;
        }

        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) => Task.Delay(Timeout.Infinite);

        protected override Task<Stream> CreateContentReadStreamAsync() => Task.FromResult<Stream>(new StalledStream());
    }

    private sealed partial class StalledStream : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            await Task.Delay(Timeout.Infinite, cancellationToken);
            return 0;
        }

        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    // ------------------------------------------------------------------
    // A link that is already an image.
    // ------------------------------------------------------------------

    [Test]
    public async Task DirectPng_IsAccepted_SavedAsAPngFileHoldingTheSameBytes()
    {
        using var rig = new Rig();
        byte[] png = await TestImages.TinyPngAsync();
        rig.Image(ImageUrl, png);

        ImageLinkResult result = await rig.FetchAsync(ImageUrl);

        await Assert.That(result.IsSuccess).IsTrue();
        await Assert.That(result.Image!.FileType).IsEqualTo(".png");
        await Assert.That(await SameBytesAsync(result, png)).IsTrue();
        await Assert.That(Order(rig.Requests)).IsEqualTo(Order(ImageUrl));
    }

    [Test]
    public async Task DirectJpeg_IsAccepted_SavedAsAJpegFile()
    {
        using var rig = new Rig();
        byte[] jpeg = await TestImages.TinyJpegAsync();
        rig.Image("https://i.example.com/a.jpg", jpeg, "image/jpeg");

        ImageLinkResult result = await rig.FetchAsync("https://i.example.com/a.jpg");

        await Assert.That(result.IsSuccess).IsTrue();
        await Assert.That(result.Image!.FileType).IsEqualTo(".jpg");
        await Assert.That(await SameBytesAsync(result, jpeg)).IsTrue();
    }

    [Test]
    public async Task SavedFileIsNamedForWhatTheBytesAre_NotForTheUrlOrTheHeader()
    {
        // The poster is stored under the extension of the file it was copied from, so a PNG served
        // from a ".jpg" address with an image/jpeg header must still land as a PNG.
        using var rig = new Rig();
        byte[] png = await TestImages.TinyPngAsync();
        rig.Image("https://i.example.com/liar.jpg", png, "image/jpeg");

        ImageLinkResult result = await rig.FetchAsync("https://i.example.com/liar.jpg");

        await Assert.That(result.IsSuccess).IsTrue();
        await Assert.That(result.Image!.FileType).IsEqualTo(".png");
        await Assert.That(Path.GetDirectoryName(result.Image.Path)).IsEqualTo(rig.TempFolderPath);
    }

    [Test]
    public async Task LinkIsTrimmed_BeforeItIsUsed()
    {
        // Copying a link off a web page often drags a non-breaking space or an em space along with
        // it. Uri parsing shrugs off ordinary spaces and line breaks by itself, so only whitespace
        // it does not recognise can show whether the link was really trimmed first.
        using var rig = new Rig();
        rig.Image(ImageUrl, await TestImages.TinyPngAsync());

        ImageLinkResult result = await rig.FetchAsync("  " + ImageUrl + " \r\n");

        await Assert.That(result.IsSuccess).IsTrue();
        await Assert.That(Order(rig.Requests)).IsEqualTo(Order(ImageUrl));
    }

    [Test]
    public async Task Request_IdentifiesItselfWithAUserAgent()
    {
        // Several image hosts turn away a request that says nothing about who is asking.
        using var rig = new Rig();
        rig.Image(ImageUrl, await TestImages.TinyPngAsync());

        await rig.FetchAsync(ImageUrl);

        await Assert.That(rig.Handler.LastRequest!.Headers.UserAgent.Count).IsGreaterThan(0);
    }

    // ------------------------------------------------------------------
    // A link that is a web page: exactly one hop to the image it names.
    // ------------------------------------------------------------------

    [Test]
    [Arguments("""<meta property="og:image" content="https://i.example.com/a.png">""", ImageUrl)]
    [Arguments("""<meta content="https://i.example.com/a.png" property="og:image">""", ImageUrl)] // content before property
    [Arguments("""<meta property='og:image' content='https://i.example.com/a.png'>""", ImageUrl)] // single quotes
    [Arguments("""<meta content='https://i.example.com/a.png' property='og:image' />""", ImageUrl)] // single quotes, reversed, self-closed
    [Arguments("""<meta property=og:image content=https://i.example.com/a.png>""", ImageUrl)] // unquoted
    [Arguments("""<META PROPERTY="OG:IMAGE" CONTENT="https://i.example.com/a.png">""", ImageUrl)] // shouting
    [Arguments("""<meta property="og:image" content="/img/a.png">""", "https://example.com/img/a.png")] // root-relative
    [Arguments("""<meta property="og:image" content="a.png">""", "https://example.com/gallery/a.png")] // path-relative
    [Arguments("""<meta property="og:image" content="//cdn.example.com/a.png">""", "https://cdn.example.com/a.png")] // scheme-relative
    [Arguments("""<meta property="og:image" content="https://i.example.com/a.png?x=1&amp;y=2">""", "https://i.example.com/a.png?x=1&y=2")] // encoded ampersand
    [Arguments("""<meta property="og:image:secure_url" content="https://i.example.com/a.png">""", ImageUrl)]
    [Arguments("""<meta name="twitter:image" content="https://i.example.com/a.png">""", ImageUrl)]
    [Arguments("""<meta property="twitter:image" content="https://i.example.com/a.png">""", ImageUrl)]
    public async Task HtmlPage_WithAnImageTag_IsFollowedOneHopToTheImage(string metaTag, string expectedImageUrl)
    {
        using var rig = new Rig();
        byte[] png = await TestImages.TinyPngAsync();
        rig.Page(PageUrl, PageWith(metaTag));
        rig.Image(expectedImageUrl, png);

        ImageLinkResult result = await rig.FetchAsync(PageUrl);

        await Assert.That(result.IsSuccess).IsTrue();
        await Assert.That(await SameBytesAsync(result, png)).IsTrue();
        await Assert.That(Order(rig.Requests)).IsEqualTo(Order(PageUrl, expectedImageUrl));
    }

    [Test]
    public async Task ImgurStylePage_PicksTheImageTag_NotItsNeighboursOrWiderMetadata()
    {
        // og:image:width and og:image:height merely start with "og:image"; the answer is the tag
        // whose property IS og:image, wherever it sits among the rest.
        using var rig = new Rig();
        byte[] png = await TestImages.TinyPngAsync();
        string head = """
            <meta property="og:title" content="Something &amp; more">
            <meta property="og:image:width" content="1200">
            <meta property="og:image:height" content="630">
            <meta property="og:description" content="Look at https://i.example.com/wrong.png">
            <meta property="og:image" content="https://i.imgur.test/abc123.png?fb" />
            <meta name="twitter:card" content="summary_large_image">
            """;
        rig.Page("https://imgur.test/abc123", PageWith(head));
        rig.Image("https://i.imgur.test/abc123.png?fb", png);

        ImageLinkResult result = await rig.FetchAsync("https://imgur.test/abc123");

        await Assert.That(result.IsSuccess).IsTrue();
        await Assert.That(Order(rig.Requests)).IsEqualTo(Order("https://imgur.test/abc123", "https://i.imgur.test/abc123.png?fb"));
    }

    [Test]
    public async Task HtmlPage_NamingSeveralImages_PrefersTheSecureUrlThenOgImageThenTwitter()
    {
        using var rig = new Rig();
        byte[] png = await TestImages.TinyPngAsync();
        // Document order is the reverse of the preference, so only the preference can pick the winner.
        string head = """
            <meta name="twitter:image" content="https://t.example.com/twitter.png">
            <meta property="og:image" content="https://o.example.com/og.png">
            <meta property="og:image:secure_url" content="https://s.example.com/secure.png">
            """;
        rig.Page(PageUrl, PageWith(head));
        rig.Image("https://s.example.com/secure.png", png);

        ImageLinkResult result = await rig.FetchAsync(PageUrl);

        await Assert.That(result.IsSuccess).IsTrue();
        await Assert.That(Order(rig.Requests)).IsEqualTo(Order(PageUrl, "https://s.example.com/secure.png"));
    }

    [Test]
    public async Task HtmlPage_WhoseImageTagIsPlainHttp_ButHasAnHttpsSecureUrl_UsesTheSecureUrl()
    {
        using var rig = new Rig();
        byte[] png = await TestImages.TinyPngAsync();
        rig.Page(PageUrl, PageWith("""
            <meta property="og:image" content="http://i.example.com/a.png">
            <meta property="og:image:secure_url" content="https://i.example.com/a.png">
            """));
        rig.Image(ImageUrl, png);

        ImageLinkResult result = await rig.FetchAsync(PageUrl);

        await Assert.That(result.IsSuccess).IsTrue();
        await Assert.That(Order(rig.Requests)).IsEqualTo(Order(PageUrl, ImageUrl));
    }

    [Test]
    public async Task HtmlPage_LargerThanTheImageCap_IsStillReadForItsImageTag()
    {
        // The cap is for images. A page is only scanned near its top, so a heavy page must not be
        // refused for its weight.
        using var rig = new Rig();
        rig.Service.MaxImageLinkBytes = 1024;
        byte[] png = await TestImages.TinyPngAsync();
        rig.Page(PageUrl, PageWith("""<meta property="og:image" content="https://i.example.com/a.png">""")
            + "<!--" + new string('x', 8000) + "-->");
        rig.Image(ImageUrl, png);

        ImageLinkResult result = await rig.FetchAsync(PageUrl);

        await Assert.That(result.IsSuccess).IsTrue();
    }

    [Test]
    public async Task SecondLevelPage_IsNotFollowedFurther()
    {
        // The page's image tag leads to ANOTHER page whose own tag leads to a real image. One hop
        // only: the answer is a failure, and the real image is never requested.
        using var rig = new Rig();
        byte[] png = await TestImages.TinyPngAsync();
        rig.Page(PageUrl, PageWith("""<meta property="og:image" content="https://other.example.com/second">"""));
        rig.Page("https://other.example.com/second", PageWith("""<meta property="og:image" content="https://i.example.com/a.png">"""));
        rig.Image(ImageUrl, png);

        ImageLinkResult result = await rig.FetchAsync(PageUrl);

        await Assert.That(result.IsSuccess).IsFalse();
        await Assert.That(result.Failure).IsEqualTo(ImageLinkFailure.NotAnImage);
        await Assert.That(Order(rig.Requests)).IsEqualTo(Order(PageUrl, "https://other.example.com/second"));
    }

    // ------------------------------------------------------------------
    // HTTPS only.
    // ------------------------------------------------------------------

    [Test]
    [Arguments("http://example.com/a.png")]
    [Arguments("ftp://example.com/a.png")]
    [Arguments("file:///C:/Users/me/a.png")]
    [Arguments("//example.com/a.png")]
    [Arguments("example.com/a.png")]
    [Arguments("not a link")]
    [Arguments("")]
    [Arguments("   ")]
    public async Task PastedLink_ThatIsNotAnAbsoluteHttpsAddress_IsRejectedBeforeAnyRequest(string link)
    {
        using var rig = new Rig();
        rig.Image(ImageUrl, await TestImages.TinyPngAsync());

        ImageLinkResult result = await rig.FetchAsync(link);

        await Assert.That(result.IsSuccess).IsFalse();
        await Assert.That(result.Failure).IsEqualTo(ImageLinkFailure.NotHttps);
        await Assert.That(rig.Handler.CallCount).IsEqualTo(0);
        await Assert.That(rig.Service.HasCreatedHttpClient).IsFalse()
            .Because("A refused link must be turned away before anything network-capable exists.");
    }

    [Test]
    public async Task PageWhoseImageTagIsPlainHttp_IsRejected_AndTheImageIsNeverRequested()
    {
        using var rig = new Rig();
        byte[] png = await TestImages.TinyPngAsync();
        rig.Page(PageUrl, PageWith("""<meta property="og:image" content="http://i.example.com/a.png">"""));
        // Were it fetched, this image would be accepted - so only the scheme check can reject it.
        rig.Image("http://i.example.com/a.png", png);

        ImageLinkResult result = await rig.FetchAsync(PageUrl);

        await Assert.That(result.IsSuccess).IsFalse();
        await Assert.That(result.Failure).IsEqualTo(ImageLinkFailure.NotHttps);
        await Assert.That(Order(rig.Requests)).IsEqualTo(Order(PageUrl));
        await Assert.That(rig.Handler.CallCount).IsEqualTo(1);
    }

    [Test]
    public async Task ImageTagWithADataUri_IsRejectedWithoutAnyFurtherRequest()
    {
        using var rig = new Rig();
        rig.Page(PageUrl, PageWith("""<meta property="og:image" content="data:image/png;base64,AAAA">"""));

        ImageLinkResult result = await rig.FetchAsync(PageUrl);

        await Assert.That(result.Failure).IsEqualTo(ImageLinkFailure.NotHttps);
        await Assert.That(rig.Handler.CallCount).IsEqualTo(1);
    }

    [Test]
    public async Task Response_ThatCameFromAPlainHttpAddress_AfterARedirect_IsRejected()
    {
        // HttpClient does not follow an https -> http redirect on its own, but the guard must not
        // depend on that: whatever address finally answered must itself be https.
        using var rig = new Rig();
        byte[] png = await TestImages.TinyPngAsync();
        rig.Route(ImageUrl, () =>
        {
            HttpResponseMessage response = Bytes(png, "image/png");
            response.RequestMessage = new HttpRequestMessage(HttpMethod.Get, "http://i.example.com/a.png");
            return response;
        });

        ImageLinkResult result = await rig.FetchAsync(ImageUrl);

        await Assert.That(result.IsSuccess).IsFalse();
        await Assert.That(result.Failure).IsEqualTo(ImageLinkFailure.NotHttps);
    }

    [Test]
    public async Task RelativeImageTag_IsResolvedAgainstThePageThatServedIt_NotTheLinkThatWasPasted()
    {
        // A redirect moved the page; its relative image belongs beside where it ended up.
        using var rig = new Rig();
        byte[] png = await TestImages.TinyPngAsync();
        rig.Route("https://short.example/x", () =>
        {
            HttpResponseMessage response = Bytes(
                Encoding.UTF8.GetBytes(PageWith("""<meta property="og:image" content="a.png">""")), "text/html");
            response.RequestMessage = new HttpRequestMessage(HttpMethod.Get, "https://long.example.com/gallery/page");
            return response;
        });
        rig.Image("https://long.example.com/gallery/a.png", png);

        ImageLinkResult result = await rig.FetchAsync("https://short.example/x");

        await Assert.That(result.IsSuccess).IsTrue();
    }

    // ------------------------------------------------------------------
    // Size cap.
    // ------------------------------------------------------------------

    [Test]
    public async Task Image_DeclaringALengthOverTheCap_IsRejectedWithoutReadingItsBody()
    {
        using var rig = new Rig();
        rig.Service.MaxImageLinkBytes = 4096;
        rig.Route(ImageUrl, () => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new UnreadableBodyContent("image/png", declaredLength: 50_000_000)
        });

        ImageLinkResult result = await rig.FetchAsync(ImageUrl);

        await Assert.That(result.IsSuccess).IsFalse();
        await Assert.That(result.Failure).IsEqualTo(ImageLinkFailure.TooLarge);
    }

    [Test]
    public async Task Image_WithNoDeclaredLength_IsRejectedOnceItPassesTheCap_WithoutDrainingTheRest()
    {
        using var rig = new Rig();
        rig.Service.MaxImageLinkBytes = 4096;
        UnknownLengthContent body = new("image/png", new byte[1_000_000]);
        rig.Route(ImageUrl, () => new HttpResponseMessage(HttpStatusCode.OK) { Content = body });

        ImageLinkResult result = await rig.FetchAsync(ImageUrl);

        await Assert.That(result.IsSuccess).IsFalse();
        await Assert.That(result.Failure).IsEqualTo(ImageLinkFailure.TooLarge);
        await Assert.That(body.BytesServed).IsLessThan(100_000)
            .Because("The reader must stop at the cap, not pull the whole megabyte into memory first.");
    }

    [Test]
    [Arguments(true)]
    [Arguments(false)]
    public async Task Image_ExactlyAtTheCap_IsAccepted_AndOneByteOverIsNot(bool declaresItsLength)
    {
        using var rig = new Rig();
        byte[] png = await TestImages.TinyPngAsync();
        rig.Route(ImageUrl, () => declaresItsLength
            ? Bytes(png, "image/png")
            : new HttpResponseMessage(HttpStatusCode.OK) { Content = new UnknownLengthContent("image/png", png) });

        rig.Service.MaxImageLinkBytes = png.Length;
        ImageLinkResult atCap = await rig.FetchAsync(ImageUrl);

        rig.Service.MaxImageLinkBytes = png.Length - 1;
        ImageLinkResult overCap = await rig.FetchAsync(ImageUrl);

        await Assert.That(atCap.IsSuccess).IsTrue();
        await Assert.That(overCap.IsSuccess).IsFalse();
        await Assert.That(overCap.Failure).IsEqualTo(ImageLinkFailure.TooLarge);
    }

    [Test]
    public async Task Image_AtTheDefaultCap_AllowsTwentyMegabytes()
    {
        // The cap is a product decision, not an implementation detail: a 20 MB poster is allowed.
        using var rig = new Rig();
        rig.Image(ImageUrl, new byte[20 * 1024 * 1024]);

        ImageLinkResult result = await rig.FetchAsync(ImageUrl);

        // The zeros are not an image, so the verdict is "not an image" - which is only reachable
        // when the size check let a body of exactly 20 MB through.
        await Assert.That(result.Failure).IsEqualTo(ImageLinkFailure.NotAnImage);
    }

    // ------------------------------------------------------------------
    // It must really be an image.
    // ------------------------------------------------------------------

    [Test]
    public async Task PngHeader_OnBytesThatAreNotAnImage_IsRejected_AndNothingIsSaved()
    {
        using var rig = new Rig();
        rig.Image(ImageUrl, Encoding.ASCII.GetBytes("<html><body>Sorry, this picture is private.</body></html>"), "image/png");

        ImageLinkResult result = await rig.FetchAsync(ImageUrl);

        await Assert.That(result.IsSuccess).IsFalse();
        await Assert.That(result.Failure).IsEqualTo(ImageLinkFailure.NotAnImage);
        await Assert.That(rig.TempFiles()).IsEmpty();
    }

    [Test]
    public async Task PngSignatureWithNothingAfterIt_IsRejected()
    {
        using var rig = new Rig();
        byte[] signatureOnly = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];
        rig.Image(ImageUrl, signatureOnly, "image/png");

        ImageLinkResult result = await rig.FetchAsync(ImageUrl);

        await Assert.That(result.Failure).IsEqualTo(ImageLinkFailure.NotAnImage);
    }

    [Test]
    public async Task EmptyImageBody_IsRejected()
    {
        using var rig = new Rig();
        rig.Image(ImageUrl, [], "image/png");

        ImageLinkResult result = await rig.FetchAsync(ImageUrl);

        await Assert.That(result.Failure).IsEqualTo(ImageLinkFailure.NotAnImage);
    }

    [Test]
    [Arguments("application/json")]
    [Arguments("application/octet-stream")]
    [Arguments("text/plain")]
    public async Task Response_ThatIsNeitherAnImageNorAPage_IsRejected(string contentType)
    {
        using var rig = new Rig();
        // Genuine PNG bytes: only the content type can be what turns this away.
        rig.Image(ImageUrl, await TestImages.TinyPngAsync(), contentType);

        ImageLinkResult result = await rig.FetchAsync(ImageUrl);

        await Assert.That(result.Failure).IsEqualTo(ImageLinkFailure.NotAnImage);
    }

    [Test]
    public async Task Response_WithNoContentType_IsRejected()
    {
        using var rig = new Rig();
        byte[] png = await TestImages.TinyPngAsync();
        rig.Route(ImageUrl, () => Bytes(png, contentType: null));

        ImageLinkResult result = await rig.FetchAsync(ImageUrl);

        await Assert.That(result.Failure).IsEqualTo(ImageLinkFailure.NotAnImage);
    }

    [Test]
    public async Task SvgImage_IsRejected_BecauseItCannotBeDecoded()
    {
        using var rig = new Rig();
        rig.Image(ImageUrl, Encoding.UTF8.GetBytes("<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"4\" height=\"4\"/>"), "image/svg+xml");

        ImageLinkResult result = await rig.FetchAsync(ImageUrl);

        await Assert.That(result.Failure).IsEqualTo(ImageLinkFailure.NotAnImage);
    }

    // ------------------------------------------------------------------
    // Failures that must come back as a reason, never as an exception.
    // ------------------------------------------------------------------

    [Test]
    [Arguments("""<meta property="og:title" content="No pictures here">""")]
    [Arguments("")]
    [Arguments("""<meta property="og:image" content="">""")]
    [Arguments("""<meta property="og:image" content="   ">""")]
    [Arguments("""<meta property="og:image:width" content="1200">""")]
    public async Task HtmlPage_NamingNoImage_FailsAsNoImageOnPage(string headTags)
    {
        using var rig = new Rig();
        rig.Page(PageUrl, PageWith(headTags));

        ImageLinkResult result = await rig.FetchAsync(PageUrl);

        await Assert.That(result.IsSuccess).IsFalse();
        await Assert.That(result.Failure).IsEqualTo(ImageLinkFailure.NoImageOnPage);
        await Assert.That(rig.Handler.CallCount).IsEqualTo(1);
    }

    [Test]
    public async Task NotFound_FailsAsDownloadFailed()
    {
        using var rig = new Rig();

        ImageLinkResult result = await rig.FetchAsync("https://i.example.com/missing.png");

        await Assert.That(result.IsSuccess).IsFalse();
        await Assert.That(result.Failure).IsEqualTo(ImageLinkFailure.DownloadFailed);
    }

    [Test]
    public async Task PageImage_ThatIsNotFound_FailsAsDownloadFailed()
    {
        using var rig = new Rig();
        rig.Page(PageUrl, PageWith("""<meta property="og:image" content="https://i.example.com/gone.png">"""));

        ImageLinkResult result = await rig.FetchAsync(PageUrl);

        await Assert.That(result.Failure).IsEqualTo(ImageLinkFailure.DownloadFailed);
        await Assert.That(rig.Handler.CallCount).IsEqualTo(2);
    }

    [Test]
    public async Task ConnectionFailure_FailsAsDownloadFailed()
    {
        using var rig = new Rig();
        rig.RouteAsync(ImageUrl, _ => throw new HttpRequestException("No such host is known."));

        ImageLinkResult result = await rig.FetchAsync(ImageUrl);

        await Assert.That(result.Failure).IsEqualTo(ImageLinkFailure.DownloadFailed);
    }

    [Test]
    public async Task ServerThatNeverAnswers_FailsAsTimedOut()
    {
        using var rig = new Rig();
        rig.Service.ImageLinkTimeout = TimeSpan.FromMilliseconds(150);
        rig.RouteAsync(ImageUrl, async ct =>
        {
            await Task.Delay(Timeout.Infinite, ct);
            return new HttpResponseMessage(HttpStatusCode.OK);
        });

        ImageLinkResult result = await rig.FetchAsync(ImageUrl);

        await Assert.That(result.IsSuccess).IsFalse();
        await Assert.That(result.Failure).IsEqualTo(ImageLinkFailure.TimedOut);
    }

    [Test]
    public async Task ServerThatStallsMidDownload_FailsAsTimedOut()
    {
        // The timeout covers the whole fetch, not just the wait for the first byte.
        using var rig = new Rig();
        rig.Service.ImageLinkTimeout = TimeSpan.FromMilliseconds(150);
        rig.Route(ImageUrl, () => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StalledBodyContent("image/png") });

        ImageLinkResult result = await rig.FetchAsync(ImageUrl);

        await Assert.That(result.Failure).IsEqualTo(ImageLinkFailure.TimedOut);
    }

    [Test]
    public async Task TimeoutOnThePagesImage_FailsAsTimedOut()
    {
        using var rig = new Rig();
        rig.Service.ImageLinkTimeout = TimeSpan.FromMilliseconds(150);
        rig.Page(PageUrl, PageWith("""<meta property="og:image" content="https://i.example.com/a.png">"""));
        rig.RouteAsync(ImageUrl, async ct =>
        {
            await Task.Delay(Timeout.Infinite, ct);
            return new HttpResponseMessage(HttpStatusCode.OK);
        });

        ImageLinkResult result = await rig.FetchAsync(PageUrl);

        await Assert.That(result.Failure).IsEqualTo(ImageLinkFailure.TimedOut);
    }

    [Test]
    public async Task CallerCancellation_Throws_AndIsNotMistakenForATimeout()
    {
        using var rig = new Rig();
        rig.RouteAsync(ImageUrl, async ct =>
        {
            await Task.Delay(Timeout.Infinite, ct);
            return new HttpResponseMessage(HttpStatusCode.OK);
        });
        using var cts = new CancellationTokenSource();
        cts.CancelAfter(TimeSpan.FromMilliseconds(100));

        await Assert.That(async () => await rig.FetchAsync(ImageUrl, cts.Token))
            .Throws<OperationCanceledException>();
        await Assert.That(rig.TempFiles()).IsEmpty();
    }

    [Test]
    public async Task CancellationDuringTheBodyRead_Throws()
    {
        using var rig = new Rig();
        rig.Route(ImageUrl, () => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StalledBodyContent("image/png") });
        using var cts = new CancellationTokenSource();
        cts.CancelAfter(TimeSpan.FromMilliseconds(100));

        await Assert.That(async () => await rig.FetchAsync(ImageUrl, cts.Token))
            .Throws<OperationCanceledException>();
    }

    // ------------------------------------------------------------------
    // Temporary files do not pile up.
    // ------------------------------------------------------------------

    [Test]
    public async Task EarlierTemporaryImages_AreClearedOut_OnceTheyAreStale_ButRecentOnesAreKept()
    {
        using var rig = new Rig();
        string stale = Path.Combine(rig.TempFolderPath, "link_stale.png");
        string recent = Path.Combine(rig.TempFolderPath, "link_recent.png");
        await File.WriteAllBytesAsync(stale, [1]);
        await File.WriteAllBytesAsync(recent, [1]);
        File.SetCreationTimeUtc(stale, DateTime.UtcNow.AddHours(-3));
        rig.Image(ImageUrl, await TestImages.TinyPngAsync());

        ImageLinkResult result = await rig.FetchAsync(ImageUrl);

        await Assert.That(result.IsSuccess).IsTrue();
        await Assert.That(File.Exists(stale)).IsFalse();
        await Assert.That(File.Exists(recent)).IsTrue();
        await Assert.That(File.Exists(result.Image!.Path)).IsTrue();
    }

    [Test]
    public async Task TwoFetchesOfTheSameLink_DoNotShareAFile()
    {
        // Each result is a file of its own: a later fetch must never overwrite the image an
        // earlier result is still pointing at.
        using var rig = new Rig();
        rig.Image(ImageUrl, await TestImages.TinyPngAsync());

        ImageLinkResult first = await rig.FetchAsync(ImageUrl);
        ImageLinkResult second = await rig.FetchAsync(ImageUrl);

        await Assert.That(first.IsSuccess).IsTrue().Because(rig.Log.ToString());
        await Assert.That(second.IsSuccess).IsTrue().Because(rig.Log.ToString());
        await Assert.That(first.Image!.Path).IsNotEqualTo(second.Image!.Path);
    }
}
