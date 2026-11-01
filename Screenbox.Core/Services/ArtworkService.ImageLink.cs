using System;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Screenbox.Core.Enums;
using Screenbox.Core.Helpers;
using Screenbox.Core.Models;
using Windows.Graphics.Imaging;
using Windows.Storage;
using Windows.Storage.Streams;

namespace Screenbox.Core.Services;

// The pasted-link half of ArtworkService: turns a link the user pasted into a validated image
// file that the ordinary manual-poster path can apply. Everything that can go wrong with a
// stranger's web server is handled here, so the dialog only ever sees a file or a reason.
public sealed partial class ArtworkService
{
    /// <summary>Folder inside the app's temporary storage where fetched link images are written.</summary>
    public const string ImageLinkFolderName = "ImageLinks";

    /// <summary>
    /// How many bytes of a web page are scanned for its image tag. The tags live in the head, so
    /// this is generous; a page is never refused for being heavy, only read near its top.
    /// </summary>
    private const int PageScanLimit = 512 * 1024;

    private const int ReadChunkSize = 80 * 1024;

    // Identifies the app. Several image hosts turn away a request that says nothing about its sender.
    private const string ImageLinkUserAgent = "ScreenboxEnhanced/1.0";

    // Longer than any fetch plausibly takes, short enough that nothing is still around a day later.
    private static readonly TimeSpan StaleImageLinkAge = TimeSpan.FromHours(1);

    /// <summary>Directory fetched link images are written to; empty means the app's temporary folder.</summary>
    internal string ImageLinkFolderPath { get; set; } = string.Empty;

    /// <summary>
    /// The longest the whole fetch may take, across both hops and the body reads. Without it a
    /// server that sends headers and then stalls would hold the dialog's Save button forever.
    /// </summary>
    internal TimeSpan ImageLinkTimeout { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>The most bytes an image may have.</summary>
    internal long MaxImageLinkBytes { get; set; } = 20L * 1024 * 1024;

    /// <inheritdoc/>
    public async Task<ImageLinkResult> FetchImageFromLinkAsync(string url, CancellationToken ct)
    {
        // HTTPS only, and checked before anything network-capable is created, exactly as the
        // scraped-poster download does: accepting plain HTTP would add nothing but a window for a
        // tampered image to become a folder's poster.
        if (!Uri.TryCreate(url.Trim(), UriKind.Absolute, out Uri? uri) || uri.Scheme != Uri.UriSchemeHttps)
        {
            return ImageLinkResult.Failed(ImageLinkFailure.NotHttps);
        }

        using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(ImageLinkTimeout);
        try
        {
            return await FetchLinkedImageAsync(uri, allowPage: true, timeout.Token);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            // Not the caller's doing, so the clock ran out: the fetch's own timeout, or the
            // HttpClient's per-request one.
            _logger.LogWarning("Fetching an image from '{Host}' timed out.", uri.Host);
            return ImageLinkResult.Failed(ImageLinkFailure.TimedOut);
        }
        catch (Exception e)
        {
            // Only the host is logged: the rest of a pasted link can carry a token.
            _logger.LogWarning(e, "Failed to fetch an image from '{Host}'.", uri.Host);
            return ImageLinkResult.Failed(ImageLinkFailure.DownloadFailed);
        }
    }

    /// <summary>
    /// Fetches one address. An image is validated and saved; a page (only when
    /// <paramref name="allowPage"/>) is read for the image it names, which is fetched in turn with
    /// pages no longer allowed - so a link is followed exactly one hop, never a chain.
    /// </summary>
    private async Task<ImageLinkResult> FetchLinkedImageAsync(Uri uri, bool allowPage, CancellationToken ct)
    {
        HttpClient client = GetOrCreateHttpClient();
        using HttpRequestMessage request = new(HttpMethod.Get, uri);
        request.Headers.UserAgent.ParseAdd(ImageLinkUserAgent);
        request.Headers.Accept.ParseAdd(allowPage ? "image/*, text/html;q=0.9" : "image/*");
        using HttpResponseMessage response =
            await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        if (!response.IsSuccessStatusCode)
        {
            return ImageLinkResult.Failed(ImageLinkFailure.DownloadFailed);
        }

        // Whatever finally answered, after any redirects, must itself be https. HttpClient does not
        // follow an https-to-http redirect, but this guard should not hinge on that.
        Uri answeredBy = response.RequestMessage?.RequestUri ?? uri;
        if (answeredBy.Scheme != Uri.UriSchemeHttps)
        {
            return ImageLinkResult.Failed(ImageLinkFailure.NotHttps);
        }

        string? mediaType = response.Content.Headers.ContentType?.MediaType;
        if (mediaType is not null && mediaType.StartsWith("image/", StringComparison.OrdinalIgnoreCase))
        {
            return await ReadAndSaveImageAsync(response.Content, ct);
        }

        bool isPage = string.Equals(mediaType, "text/html", StringComparison.OrdinalIgnoreCase)
            || string.Equals(mediaType, "application/xhtml+xml", StringComparison.OrdinalIgnoreCase);
        if (!allowPage || !isPage)
        {
            return ImageLinkResult.Failed(ImageLinkFailure.NotAnImage);
        }

        (byte[] head, _) = await ReadUpToAsync(response.Content, PageScanLimit, ct);
        string? advertised = OpenGraphImage.Find(Encoding.UTF8.GetString(head));
        if (advertised is null || !Uri.TryCreate(answeredBy, advertised, out Uri? imageUri))
        {
            return ImageLinkResult.Failed(ImageLinkFailure.NoImageOnPage);
        }

        // Checked on the resolved address, before it is requested: a plain-http or data: image
        // tag costs no further request at all.
        if (imageUri.Scheme != Uri.UriSchemeHttps)
        {
            return ImageLinkResult.Failed(ImageLinkFailure.NotHttps);
        }

        return await FetchLinkedImageAsync(imageUri, allowPage: false, ct);
    }

    /// <summary>
    /// Reads an image response within the size cap, checks that its bytes really decode as an
    /// image, and saves them to a temporary file named for the format they turned out to be.
    /// </summary>
    private async Task<ImageLinkResult> ReadAndSaveImageAsync(HttpContent content, CancellationToken ct)
    {
        long limit = MaxImageLinkBytes;

        // A declared length over the cap is answered from the headers alone; the body is never touched.
        if (content.Headers.ContentLength > limit)
        {
            return ImageLinkResult.Failed(ImageLinkFailure.TooLarge);
        }

        (byte[] bytes, bool exceeded) = await ReadUpToAsync(content, limit, ct);
        if (exceeded)
        {
            return ImageLinkResult.Failed(ImageLinkFailure.TooLarge);
        }

        // A Content-Type header is only a claim. The bytes have to decode.
        string? extension = await TryGetImageExtensionAsync(bytes);
        if (extension is null)
        {
            return ImageLinkResult.Failed(ImageLinkFailure.NotAnImage);
        }

        ct.ThrowIfCancellationRequested();
        string folderPath = PrepareImageLinkFolder();

        // The extension comes from the decoded format, not the address or the header, because the
        // manual-poster path stores the image under its source file's extension. The name is a
        // fresh Guid, so no two fetches can ever share a file.
        //
        // Written with System.IO rather than StorageFolder.CreateFileAsync on purpose: with test
        // classes running in parallel, concurrent CreateFileAsync calls made unrelated
        // BitmapDecoder.CreateAsync calls fail intermittently with E_INVALIDARG - a valid pasted
        // image reported as "not an image". Plain file I/O does not.
        string path = Path.Combine(folderPath, $"link_{Guid.NewGuid():N}{extension}");
        await File.WriteAllBytesAsync(path, bytes, ct);
        return ImageLinkResult.Succeeded(await StorageFile.GetFileFromPathAsync(path));
    }

    /// <summary>
    /// Reads at most <paramref name="limit"/> bytes plus one, which is just enough to know the body
    /// is longer than the limit; nothing past that is pulled from the connection, so a hostile
    /// endless response is cut off rather than buffered.
    /// </summary>
    /// <returns>The bytes read, and whether the body went past <paramref name="limit"/>.</returns>
    private static async Task<(byte[] Bytes, bool Exceeded)> ReadUpToAsync(
        HttpContent content, long limit, CancellationToken ct)
    {
        await using Stream stream = await content.ReadAsStreamAsync(ct);
        using MemoryStream buffer = new();
        byte[] chunk = new byte[ReadChunkSize];
        while (true)
        {
            int wanted = (int)Math.Min(chunk.Length, limit - buffer.Length + 1);
            int read = await stream.ReadAsync(chunk.AsMemory(0, wanted), ct);
            if (read == 0) return (buffer.ToArray(), false);

            buffer.Write(chunk, 0, read);
            if (buffer.Length > limit) return (buffer.ToArray(), true);
        }
    }

    /// <summary>
    /// Decodes the bytes' header with the Windows imaging codecs and returns the file extension of
    /// the format found, or null when they are not an image any installed codec can read.
    /// </summary>
    private async Task<string?> TryGetImageExtensionAsync(byte[] bytes)
    {
        try
        {
            using InMemoryRandomAccessStream stream = new();
            using (DataWriter writer = new(stream))
            {
                writer.WriteBytes(bytes);
                await writer.StoreAsync();
                writer.DetachStream();
            }

            stream.Seek(0);
            BitmapDecoder decoder = await BitmapDecoder.CreateAsync(stream);
            if (decoder.PixelWidth == 0 || decoder.PixelHeight == 0) return null;

            Guid codec = decoder.DecoderInformation.CodecId;
            if (codec == BitmapDecoder.PngDecoderId) return ".png";
            if (codec == BitmapDecoder.JpegDecoderId) return ".jpg";
            if (codec == BitmapDecoder.GifDecoderId) return ".gif";
            if (codec == BitmapDecoder.BmpDecoderId) return ".bmp";
            return decoder.DecoderInformation.FileExtensions.FirstOrDefault() ?? ".img";
        }
        catch (Exception e)
        {
            // The codec throws for anything it cannot read: a COM error for an unknown format,
            // others for a damaged header. All of them mean the same thing here.
            _logger.LogDebug(e, "Downloaded bytes did not decode as an image.");
            return null;
        }
    }

    /// <summary>
    /// Returns the folder fetched link images are written to, creating it if needed, after removing
    /// any left by earlier fetches that are old enough to be certain nothing is using them. Each
    /// fetch writes a file of its own that the dialog hands on and never deletes, so this is what
    /// stops them piling up.
    /// </summary>
    private string PrepareImageLinkFolder()
    {
        string folderPath = string.IsNullOrEmpty(ImageLinkFolderPath)
            ? Path.Combine(ApplicationData.Current.TemporaryFolder.Path, ImageLinkFolderName)
            : ImageLinkFolderPath;
        Directory.CreateDirectory(folderPath);

        DateTime cutoff = DateTime.UtcNow - StaleImageLinkAge;
        try
        {
            foreach (string file in Directory.EnumerateFiles(folderPath))
            {
                // One file that cannot be removed now must not spare the rest; a later fetch tries again.
                try
                {
                    if (File.GetCreationTimeUtc(file) < cutoff) File.Delete(file);
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                {
                    _logger.LogWarning(e, "Failed to delete a stale fetched link image.");
                }
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(e, "Failed to clear stale fetched link images.");
        }

        return folderPath;
    }
}
