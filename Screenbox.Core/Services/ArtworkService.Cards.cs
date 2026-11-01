using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Screenbox.Core.Helpers;
using Windows.Foundation;
using Windows.Graphics.Imaging;
using Windows.Storage;
using Windows.Storage.Streams;

namespace Screenbox.Core.Services;

// The display half of ArtworkService: derives the wide "card" a folder tile draws from a stored
// poster. The pixel maths lives in ArtworkCard; this file owns the codec calls and the card's
// files. The stored poster is never altered.
public sealed partial class ArtworkService
{
    /// <summary>Appended to a poster's file name to name the display card derived from it.</summary>
    public const string CardSuffix = ".card.jpg";

    private const float CardJpegQuality = 0.9f;

    /// <summary>
    /// Compositions running right now, by poster file name. Tiles re-realise on scroll, so the same
    /// card is often asked for twice at once; everyone asking shares one composition rather than
    /// racing each other onto the card file.
    /// </summary>
    private readonly ConcurrentDictionary<string, Lazy<Task<string>>> _cardsInFlight = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Posters already examined and found not to need a card, with the modification time they had
    /// then. Without it every realisation of a landscape poster would open and decode it again just
    /// to learn that again. A poster modified since is looked at afresh.
    /// </summary>
    private readonly ConcurrentDictionary<string, DateTime> _landscapeSources = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Bounds how many cards are composed at once. Each holds a few megabytes of pixels while it is
    /// built, and a page of never-before-seen folders would otherwise build them all in parallel.
    /// </summary>
    private readonly SemaphoreSlim _compositionGate = new(2);

    private int _cardsComposed;

    /// <summary>
    /// How many cards have been composed over this service's life. Lets tests tell one composition
    /// shared by many requests from many, and a card reused from one rebuilt - the name that comes
    /// back is the same either way.
    /// </summary>
    internal int CardsComposed => Volatile.Read(ref _cardsComposed);

    /// <summary>The name of the display card derived from a poster file.</summary>
    public static string GetCardFileName(string posterFileName) => posterFileName + CardSuffix;

    /// <inheritdoc/>
    public async Task<string?> GetDisplayFileNameAsync(string? posterFileName)
    {
        if (posterFileName is not { Length: > 0 }) return posterFileName;

        Lazy<Task<string>> composition = _cardsInFlight.GetOrAdd(
            posterFileName,
            static (name, service) => new Lazy<Task<string>>(() => service.ResolveDisplayFileAsync(name)),
            this);
        try
        {
            return await composition.Value;
        }
        finally
        {
            // Only this exact composition is removed: a later request may already have started a
            // newer one under the same name.
            _cardsInFlight.TryRemove(new KeyValuePair<string, Lazy<Task<string>>>(posterFileName, composition));
        }
    }

    /// <summary>
    /// Settles which file to draw for a poster: its up-to-date card, a card built now, or the
    /// poster itself. Catches everything - falling back to the poster is always better than losing
    /// the artwork - so the shared task every caller awaits cannot fault.
    /// </summary>
    private async Task<string> ResolveDisplayFileAsync(string posterFileName)
    {
        try
        {
            StorageFolder artworkFolder = await GetArtworkFolderAsync();
            string posterPath = System.IO.Path.Combine(artworkFolder.Path, posterFileName);
            if (!System.IO.File.Exists(posterPath)) return posterFileName;

            // Plain file-system probes rather than WinRT calls: this runs for every tile on every
            // realisation, and the answer is almost always "the card is current".
            DateTime posterModified = System.IO.File.GetLastWriteTimeUtc(posterPath);
            if (_landscapeSources.TryGetValue(posterFileName, out DateTime examined) && examined == posterModified)
            {
                return posterFileName;
            }

            string cardFileName = GetCardFileName(posterFileName);
            string cardPath = System.IO.Path.Combine(artworkFolder.Path, cardFileName);
            if (System.IO.File.Exists(cardPath) && System.IO.File.GetLastWriteTimeUtc(cardPath) >= posterModified)
            {
                return cardFileName;
            }

            // Missing, or older than the poster it was made from (the poster was replaced): build it.
            await _compositionGate.WaitAsync();
            try
            {
                CardInputs? inputs = await DecodeCardInputsAsync(posterPath);
                if (inputs is not { } decoded)
                {
                    // Landscape: shown as it is. A card left from an earlier, portrait poster of the
                    // same name would be stale, so it goes.
                    _landscapeSources[posterFileName] = posterModified;
                    await TryDeleteCardAsync(artworkFolder, posterFileName);
                    return posterFileName;
                }

                Interlocked.Increment(ref _cardsComposed);
                byte[] card = await Task.Run(() => ArtworkCard.Compose(
                    decoded.Foreground, decoded.ForegroundWidth, decoded.ForegroundHeight));

                await WriteCardAsync(artworkFolder, cardFileName, card);

                // A poster dated in the future (a wrong clock, a copied timestamp) would otherwise
                // always look newer than the card just written and be rebuilt on every request.
                if (System.IO.File.GetLastWriteTimeUtc(cardPath) < posterModified)
                {
                    System.IO.File.SetLastWriteTimeUtc(cardPath, posterModified);
                }

                return cardFileName;
            }
            finally
            {
                _compositionGate.Release();
            }
        }
        catch (Exception e)
        {
            _logger.LogWarning(e, "Could not build a display card for artwork '{Name}'; showing the original image.", posterFileName);
            return posterFileName;
        }
    }

    /// <summary>
    /// Decodes a poster into the upright, card-sized foreground the card is made from, or returns
    /// null when the image is not portrait-ish and needs no card. The EXIF orientation is read and
    /// applied here, so a photo stored sideways is judged, scaled and drawn the way it is meant to
    /// look.
    /// </summary>
    private static async Task<CardInputs?> DecodeCardInputsAsync(string posterPath)
    {
        // Decoded from a copy in memory, not from the file. A decoder keeps what it was given until
        // it is garbage collected, and one holding the poster file open would stop the poster being
        // replaced or deleted - which the rest of this service does as a matter of course.
        byte[] bytes = await System.IO.File.ReadAllBytesAsync(posterPath);
        using InMemoryRandomAccessStream input = new();
        using (DataWriter writer = new(input))
        {
            writer.WriteBytes(bytes);
            await writer.StoreAsync();
            writer.DetachStream();
        }

        input.Seek(0);
        BitmapDecoder decoder = await BitmapDecoder.CreateAsync(input);
        int orientation = await ReadOrientationAsync(decoder);
        bool swaps = orientation >= 5;
        int width = (int)(swaps ? decoder.PixelHeight : decoder.PixelWidth);
        int height = (int)(swaps ? decoder.PixelWidth : decoder.PixelHeight);
        if (!ArtworkCard.IsPortraitish(width, height)) return null;

        // The whole image, scaled to fit the card's height: nothing cropped. The codec is asked for
        // the pixels as stored - its own orientation, scaling and cropping do not combine
        // predictably - at the stored size that comes out the fitted size once turned upright.
        (int fitWidth, int fitHeight) = ArtworkCard.FitInside(width, height, ArtworkCard.Width, ArtworkCard.Height);
        BitmapTransform transform = new()
        {
            ScaledWidth = (uint)(swaps ? fitHeight : fitWidth),
            ScaledHeight = (uint)(swaps ? fitWidth : fitHeight),
            InterpolationMode = BitmapInterpolationMode.Fant
        };
        PixelDataProvider pixels = await decoder.GetPixelDataAsync(
            BitmapPixelFormat.Bgra8,
            BitmapAlphaMode.Premultiplied,
            transform,
            ExifOrientationMode.IgnoreExifOrientation,
            ColorManagementMode.ColorManageToSRgb);

        (byte[] upright, int uprightWidth, int uprightHeight) = ArtworkCard.Orient(
            pixels.DetachPixelData(), (int)transform.ScaledWidth, (int)transform.ScaledHeight, orientation);
        return new CardInputs(upright, uprightWidth, uprightHeight);
    }

    /// <summary>The image's EXIF orientation (1 to 8), or 1 when it has none or it cannot be read.</summary>
    private static async Task<int> ReadOrientationAsync(BitmapDecoder decoder)
    {
        const string orientationKey = "System.Photo.Orientation";
        try
        {
            BitmapPropertySet found = await decoder.BitmapProperties.GetPropertiesAsync(new string[] { orientationKey });
            return found.TryGetValue(orientationKey, out BitmapTypedValue? value) && value.Value is ushort orientation
                ? orientation
                : 1;
        }
        catch (Exception)
        {
            // Formats with no metadata at all can answer this with an error rather than nothing.
            return 1;
        }
    }

    /// <summary>
    /// Writes the card to a scratch file and moves it into place, so a card that is half written -
    /// the app closed mid-write - can never be mistaken for a finished one.
    /// </summary>
    private static async Task WriteCardAsync(StorageFolder artworkFolder, string cardFileName, byte[] pixels)
    {
        StorageFile scratch = await artworkFolder.CreateFileAsync(cardFileName + ".tmp", CreationCollisionOption.ReplaceExisting);
        using (IRandomAccessStream output = await scratch.OpenAsync(FileAccessMode.ReadWrite))
        {
            BitmapPropertySet options = new()
            {
                { "ImageQuality", new BitmapTypedValue(CardJpegQuality, PropertyType.Single) }
            };
            BitmapEncoder encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.JpegEncoderId, output, options);
            encoder.SetPixelData(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Ignore,
                ArtworkCard.Width, ArtworkCard.Height, 96, 96, pixels);
            await encoder.FlushAsync();
        }

        // Not StorageFile.MoveAsync: in testing, image encodes running at the same time as a move
        // through it failed with E_INVALIDARG. The file system call has no such side effect.
        System.IO.File.Move(scratch.Path, System.IO.Path.Combine(artworkFolder.Path, cardFileName), overwrite: true);
    }

    [DynamicWindowsRuntimeCast(typeof(StorageFile))]
    private async Task TryDeleteCardAsync(StorageFolder artworkFolder, string posterFileName)
    {
        string cardFileName = GetCardFileName(posterFileName);
        try
        {
            if (await artworkFolder.TryGetItemAsync(cardFileName) is StorageFile card)
            {
                await card.DeleteAsync(StorageDeleteOption.PermanentDelete);
            }
        }
        catch (Exception e)
        {
            _logger.LogWarning(e, "Failed to delete display card '{Name}'.", cardFileName);
        }
    }

    private readonly record struct CardInputs(byte[] Foreground, int ForegroundWidth, int ForegroundHeight);
}
