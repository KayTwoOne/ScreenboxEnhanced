using Windows.Foundation;
using Windows.Graphics.Imaging;
using Windows.Storage;
using Windows.Storage.Streams;

namespace Screenbox.Core.Tests.Helpers;

/// <summary>
/// Builds and reads small real image files for tests that exercise the Windows imaging codecs.
/// Pixel buffers are BGRA8, row-major, matching <see cref="Screenbox.Core.Helpers.ArtworkCard"/>.
/// </summary>
internal static class TestImages
{
    public readonly record struct Rgb(byte R, byte G, byte B)
    {
        /// <summary>Largest per-channel difference from <paramref name="other"/>.</summary>
        public int DistanceTo(Rgb other) =>
            Math.Max(Math.Abs(R - other.R), Math.Max(Math.Abs(G - other.G), Math.Abs(B - other.B)));
    }

    public static byte[] Solid(int width, int height, Rgb colour)
    {
        byte[] pixels = new byte[width * height * 4];
        Fill(pixels, width, 0, 0, width, height, colour);
        return pixels;
    }

    /// <summary>Fills the half-open rectangle [x0, x1) by [y0, y1) with an opaque colour.</summary>
    public static void Fill(byte[] pixels, int width, int x0, int y0, int x1, int y1, Rgb colour)
    {
        for (int y = y0; y < y1; y++)
        {
            for (int x = x0; x < x1; x++)
            {
                int i = (y * width + x) * 4;
                pixels[i] = colour.B;
                pixels[i + 1] = colour.G;
                pixels[i + 2] = colour.R;
                pixels[i + 3] = 255;
            }
        }
    }

    public static Rgb PixelAt(byte[] pixels, int width, int x, int y)
    {
        int i = (y * width + x) * 4;
        return new Rgb(pixels[i + 2], pixels[i + 1], pixels[i]);
    }

    public static Task SavePngAsync(string path, byte[] pixels, int width, int height) =>
        SaveAsync(path, BitmapEncoder.PngEncoderId, pixels, width, height, orientation: null);

    /// <summary>
    /// Saves a JPEG whose EXIF orientation tag is set, so the stored pixels and the image as
    /// displayed differ (orientation 6 stores the picture rotated a quarter turn).
    /// </summary>
    public static Task SaveJpegAsync(string path, byte[] pixels, int width, int height, ushort orientation) =>
        SaveAsync(path, BitmapEncoder.JpegEncoderId, pixels, width, height, orientation);

    /// <summary>A real, valid PNG file's bytes: a small solid-colour image, encoded by the Windows codec.</summary>
    public static Task<byte[]> TinyPngAsync(int width = 4, int height = 4) =>
        EncodeAsync(BitmapEncoder.PngEncoderId, Solid(width, height, new Rgb(200, 30, 30)), width, height, orientation: null);

    /// <summary>A real, valid JPEG file's bytes: a small solid-colour image, encoded by the Windows codec.</summary>
    public static Task<byte[]> TinyJpegAsync(int width = 8, int height = 8) =>
        EncodeAsync(BitmapEncoder.JpegEncoderId, Solid(width, height, new Rgb(30, 30, 200)), width, height, orientation: null);

    private static async Task SaveAsync(string path, Guid encoderId, byte[] pixels, int width, int height, ushort? orientation)
    {
        byte[] bytes = await EncodeAsync(encoderId, pixels, width, height, orientation);
        await File.WriteAllBytesAsync(path, bytes);
    }

    private static async Task<byte[]> EncodeAsync(Guid encoderId, byte[] pixels, int width, int height, ushort? orientation)
    {
        using InMemoryRandomAccessStream stream = new();
        BitmapEncoder encoder = await BitmapEncoder.CreateAsync(encoderId, stream);
        encoder.SetPixelData(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Ignore, (uint)width, (uint)height, 96, 96, pixels);
        if (orientation is { } value)
        {
            await encoder.BitmapProperties.SetPropertiesAsync(new BitmapPropertySet
            {
                { "System.Photo.Orientation", new BitmapTypedValue(value, PropertyType.UInt16) }
            });
        }

        await encoder.FlushAsync();

        stream.Seek(0);
        using DataReader reader = new(stream.GetInputStreamAt(0));
        await reader.LoadAsync((uint)stream.Size);
        byte[] bytes = new byte[stream.Size];
        reader.ReadBytes(bytes);
        return bytes;
    }

    /// <summary>Decodes an image file as stored: no EXIF orientation, no colour management.</summary>
    public static async Task<(byte[] Pixels, int Width, int Height)> ReadAsync(string path)
    {
        StorageFile file = await StorageFile.GetFileFromPathAsync(path);
        using IRandomAccessStream stream = await file.OpenReadAsync();
        BitmapDecoder decoder = await BitmapDecoder.CreateAsync(stream);
        PixelDataProvider provider = await decoder.GetPixelDataAsync(
            BitmapPixelFormat.Bgra8, BitmapAlphaMode.Ignore, new BitmapTransform(),
            ExifOrientationMode.IgnoreExifOrientation, ColorManagementMode.DoNotColorManage);
        return (provider.DetachPixelData(), (int)decoder.PixelWidth, (int)decoder.PixelHeight);
    }
}
