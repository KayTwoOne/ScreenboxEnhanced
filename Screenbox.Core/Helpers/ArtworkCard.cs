using System;

namespace Screenbox.Core.Helpers;

/// <summary>
/// The pixel and layout maths behind the wide artwork "card": portrait key art shown whole, centred
/// on a soft, darkened wash of its own colours, so a 232x128 folder tile can show the whole poster
/// instead of a centre-cropped band of it. Pure functions over BGRA8 byte buffers with no codec and
/// no I/O, so every step can be tested without a UWP host; <see cref="Services.ArtworkService"/>
/// owns the Windows.Graphics.Imaging calls that feed these.
/// <para>
/// All pixel buffers are 4 bytes per pixel (B, G, R, A), row-major with no padding, and are
/// PREMULTIPLIED where alpha matters. The wash is always fully opaque.
/// </para>
/// </summary>
public static class ArtworkCard
{
    /// <summary>
    /// Card width. Exactly 4x the 232 px wide folder tile, so the tile's UniformToFill never crops
    /// the card further and the card stays sharp at 200% display scaling.
    /// </summary>
    public const int Width = 928;

    /// <summary>Card height, 4x the 128 px folder tile's thumbnail height.</summary>
    public const int Height = 512;

    /// <summary>
    /// Images narrower than this width-to-height ratio get a card. The tile is ~1.81:1, so anything
    /// below 1.4:1 would lose a large share of itself to UniformToFill; wider images (video frames,
    /// landscape stills) fit the tile well enough to be shown as they are.
    /// </summary>
    public const double PortraitAspectThreshold = 1.4;

    /// <summary>
    /// Size of the tiny image the wash is built from. Averaging the artwork down to this makes the
    /// wash independent of fine detail; 32 px wide is about 29 card pixels per sample.
    /// </summary>
    public const int WashSampleWidth = 32;

    /// <inheritdoc cref="WashSampleWidth"/>
    public const int WashSampleHeight = 18;

    /// <summary>Share of the artwork's own brightness the wash keeps, so the foreground stands out.</summary>
    public const double WashBrightness = 0.5;

    private const int BytesPerPixel = 4;

    /// <summary>True when an image of this size is portrait-ish and should be shown on a card.</summary>
    public static bool IsPortraitish(int width, int height) =>
        width > 0 && height > 0 && (double)width / height < PortraitAspectThreshold;

    /// <summary>
    /// The size of an image scaled uniformly to fit entirely inside a box, never cropped. One
    /// dimension equals the box's; the other is no larger.
    /// </summary>
    public static (int Width, int Height) FitInside(int sourceWidth, int sourceHeight, int boxWidth, int boxHeight)
    {
        if (sourceWidth <= 0 || sourceHeight <= 0) return (1, 1);

        // Compared by cross-multiplication so the choice of limiting dimension has no rounding.
        if ((long)sourceWidth * boxHeight <= (long)boxWidth * sourceHeight)
        {
            return (Math.Max(1, (int)Math.Round((double)sourceWidth * boxHeight / sourceHeight)), boxHeight);
        }

        return (boxWidth, Math.Max(1, (int)Math.Round((double)sourceHeight * boxWidth / sourceWidth)));
    }

    /// <summary>
    /// How to cover a box with an image: the size to scale it to (at least as large as the box in
    /// both dimensions) and the offset of the box-sized window, centred, to keep from that.
    /// </summary>
    public static (int ScaledWidth, int ScaledHeight, int CropX, int CropY) CoverCrop(
        int sourceWidth, int sourceHeight, int boxWidth, int boxHeight)
    {
        if (sourceWidth <= 0 || sourceHeight <= 0) return (boxWidth, boxHeight, 0, 0);

        int scaledWidth;
        int scaledHeight;
        if ((long)sourceWidth * boxHeight >= (long)boxWidth * sourceHeight)
        {
            scaledHeight = boxHeight;
            scaledWidth = Math.Max(boxWidth, (int)Math.Round((double)sourceWidth * boxHeight / sourceHeight));
        }
        else
        {
            scaledWidth = boxWidth;
            scaledHeight = Math.Max(boxHeight, (int)Math.Round((double)sourceHeight * boxWidth / sourceWidth));
        }

        return (scaledWidth, scaledHeight, (scaledWidth - boxWidth) / 2, (scaledHeight - boxHeight) / 2);
    }

    /// <summary>
    /// Turns a buffer stored with an EXIF orientation into the picture as it is meant to be seen.
    /// Orientations 1 to 8 are the EXIF values: 1 is as stored, 2 mirrored left to right, 3 turned
    /// upside down, 4 mirrored top to bottom, 5 transposed, 6 turned a quarter clockwise, 7
    /// transversed, 8 turned a quarter anticlockwise. 5 to 8 swap width and height. Any other value
    /// is treated as 1. Done here rather than by the codec because the codec's own handling of
    /// orientation, scaling and cropping together is not something to build on.
    /// </summary>
    public static (byte[] Pixels, int Width, int Height) Orient(byte[] source, int width, int height, int orientation)
    {
        if (orientation is < 2 or > 8) return (source, width, height);

        bool swaps = orientation >= 5;
        int targetWidth = swaps ? height : width;
        int targetHeight = swaps ? width : height;
        byte[] target = new byte[source.Length];

        for (int y = 0; y < targetHeight; y++)
        {
            for (int x = 0; x < targetWidth; x++)
            {
                (int sourceX, int sourceY) = orientation switch
                {
                    2 => (width - 1 - x, y),
                    3 => (width - 1 - x, height - 1 - y),
                    4 => (x, height - 1 - y),
                    5 => (y, x),
                    6 => (y, height - 1 - x),
                    7 => (width - 1 - y, height - 1 - x),
                    _ => (width - 1 - y, x)
                };

                Buffer.BlockCopy(source, (sourceY * width + sourceX) * BytesPerPixel,
                    target, (y * targetWidth + x) * BytesPerPixel, BytesPerPixel);
            }
        }

        return (target, targetWidth, targetHeight);
    }

    /// <summary>
    /// Averages an image, covering a <see cref="WashSampleWidth"/> by <see cref="WashSampleHeight"/>
    /// area, down to exactly that size: the middle of whatever overflows is kept, the rest is
    /// dropped. The result is the colours the wash is made of.
    /// </summary>
    public static byte[] SampleWash(byte[] image, int width, int height)
    {
        (int coverWidth, int coverHeight, int cropX, int cropY) = CoverCrop(width, height, WashSampleWidth, WashSampleHeight);
        byte[] sample = new byte[WashSampleWidth * WashSampleHeight * BytesPerPixel];

        for (int sy = 0; sy < WashSampleHeight; sy++)
        {
            // The image's own rows behind this sample row; always at least one.
            int top = (cropY + sy) * height / coverHeight;
            int bottom = Math.Min(height, Math.Max(top + 1, (cropY + sy + 1) * height / coverHeight));
            for (int sx = 0; sx < WashSampleWidth; sx++)
            {
                int left = (cropX + sx) * width / coverWidth;
                int right = Math.Min(width, Math.Max(left + 1, (cropX + sx + 1) * width / coverWidth));

                long blue = 0, green = 0, red = 0, alpha = 0;
                for (int y = top; y < bottom; y++)
                {
                    int index = (y * width + left) * BytesPerPixel;
                    for (int x = left; x < right; x++, index += BytesPerPixel)
                    {
                        blue += image[index];
                        green += image[index + 1];
                        red += image[index + 2];
                        alpha += image[index + 3];
                    }
                }

                int count = (bottom - top) * (right - left);
                int target = (sy * WashSampleWidth + sx) * BytesPerPixel;
                sample[target] = (byte)(blue / count);
                sample[target + 1] = (byte)(green / count);
                sample[target + 2] = (byte)(red / count);
                sample[target + 3] = (byte)(alpha / count);
            }
        }

        return sample;
    }

    /// <summary>
    /// Resamples a buffer with bilinear interpolation, mapping pixel centres to pixel centres and
    /// clamping at the edges.
    /// </summary>
    public static byte[] ResizeBilinear(byte[] source, int sourceWidth, int sourceHeight, int targetWidth, int targetHeight)
    {
        byte[] target = new byte[targetWidth * targetHeight * BytesPerPixel];

        // The horizontal sample positions repeat on every row, so they are worked out once.
        int[] left = new int[targetWidth];
        int[] right = new int[targetWidth];
        int[] weightRight = new int[targetWidth];
        for (int x = 0; x < targetWidth; x++)
        {
            double position = Math.Clamp((x + 0.5) * sourceWidth / targetWidth - 0.5, 0, sourceWidth - 1);
            int x0 = (int)position;
            left[x] = x0 * BytesPerPixel;
            right[x] = Math.Min(x0 + 1, sourceWidth - 1) * BytesPerPixel;
            weightRight[x] = (int)((position - x0) * 256);
        }

        for (int y = 0; y < targetHeight; y++)
        {
            double position = Math.Clamp((y + 0.5) * sourceHeight / targetHeight - 0.5, 0, sourceHeight - 1);
            int y0 = (int)position;
            int rowTop = y0 * sourceWidth * BytesPerPixel;
            int rowBottom = Math.Min(y0 + 1, sourceHeight - 1) * sourceWidth * BytesPerPixel;
            int weightBottom = (int)((position - y0) * 256);
            int weightTop = 256 - weightBottom;
            int targetRow = y * targetWidth * BytesPerPixel;

            for (int x = 0; x < targetWidth; x++)
            {
                int wRight = weightRight[x];
                int wLeft = 256 - wRight;
                int targetIndex = targetRow + x * BytesPerPixel;
                for (int c = 0; c < BytesPerPixel; c++)
                {
                    int topLeft = source[rowTop + left[x] + c];
                    int topRight = source[rowTop + right[x] + c];
                    int bottomLeft = source[rowBottom + left[x] + c];
                    int bottomRight = source[rowBottom + right[x] + c];
                    int value = (topLeft * wLeft + topRight * wRight) * weightTop
                        + (bottomLeft * wLeft + bottomRight * wRight) * weightBottom;
                    target[targetIndex + c] = (byte)((value + 32768) >> 16);
                }
            }
        }

        return target;
    }

    /// <summary>
    /// Resamples a buffer up to a size at most twice as large at a time, bilinear at every step. One
    /// bilinear jump from a tiny image to a large one is piecewise linear, with a visible crease at
    /// every source pixel; each further step rounds those creases off, which is what turns a
    /// handful of sample colours into a smooth gradient.
    /// </summary>
    public static byte[] ResizeBilinearStaged(byte[] source, int sourceWidth, int sourceHeight, int targetWidth, int targetHeight)
    {
        byte[] current = source;
        int width = sourceWidth;
        int height = sourceHeight;
        while (width != targetWidth || height != targetHeight)
        {
            int nextWidth = Math.Min(width * 2, targetWidth);
            int nextHeight = Math.Min(height * 2, targetHeight);
            current = ResizeBilinear(current, width, height, nextWidth, nextHeight);
            width = nextWidth;
            height = nextHeight;
        }

        return current;
    }

    /// <summary>
    /// Blurs a buffer in place with a separable box filter, repeated for the given number of
    /// passes. Edge pixels are replicated, so the colours at the border do not fade. Meant for the
    /// tiny wash sample, where it costs nothing.
    /// </summary>
    public static void BoxBlur(byte[] pixels, int width, int height, int radius, int passes)
    {
        if (radius <= 0 || passes <= 0) return;

        byte[] scratch = new byte[pixels.Length];
        for (int pass = 0; pass < passes; pass++)
        {
            BlurAxis(pixels, scratch, width, height, radius, horizontal: true);
            BlurAxis(scratch, pixels, width, height, radius, horizontal: false);
        }
    }

    private static void BlurAxis(byte[] from, byte[] to, int width, int height, int radius, bool horizontal)
    {
        int lineCount = horizontal ? height : width;
        int lineLength = horizontal ? width : height;
        int step = (horizontal ? 1 : width) * BytesPerPixel;
        int windowSize = radius * 2 + 1;

        for (int line = 0; line < lineCount; line++)
        {
            int lineStart = horizontal ? line * width * BytesPerPixel : line * BytesPerPixel;
            for (int i = 0; i < lineLength; i++)
            {
                for (int c = 0; c < BytesPerPixel; c++)
                {
                    int sum = 0;
                    for (int k = -radius; k <= radius; k++)
                    {
                        int index = Math.Clamp(i + k, 0, lineLength - 1);
                        sum += from[lineStart + index * step + c];
                    }

                    to[lineStart + i * step + c] = (byte)((sum + windowSize / 2) / windowSize);
                }
            }
        }
    }

    /// <summary>
    /// Scales the colour channels by <paramref name="brightness"/> (0 to 1) in place and makes every
    /// pixel fully opaque. For premultiplied input, opaque-with-scaled-colour is the artwork
    /// composited over black, which is what a wash drawn from a transparent image should be.
    /// </summary>
    public static void Darken(byte[] pixels, double brightness)
    {
        int factor = (int)Math.Round(Math.Clamp(brightness, 0, 1) * 256);
        for (int i = 0; i < pixels.Length; i += BytesPerPixel)
        {
            pixels[i] = (byte)(pixels[i] * factor >> 8);
            pixels[i + 1] = (byte)(pixels[i + 1] * factor >> 8);
            pixels[i + 2] = (byte)(pixels[i + 2] * factor >> 8);
            pixels[i + 3] = 255;
        }
    }

    /// <summary>
    /// Draws a premultiplied source over an opaque destination at the given position, in place.
    /// Anything outside the destination is clipped. Fully opaque source pixels replace the
    /// destination; partly transparent ones let it show through; the destination stays opaque.
    /// </summary>
    public static void BlitOver(byte[] destination, int destinationWidth, int destinationHeight,
        byte[] source, int sourceWidth, int sourceHeight, int x, int y)
    {
        for (int row = 0; row < sourceHeight; row++)
        {
            int destinationY = y + row;
            if (destinationY < 0 || destinationY >= destinationHeight) continue;

            for (int column = 0; column < sourceWidth; column++)
            {
                int destinationX = x + column;
                if (destinationX < 0 || destinationX >= destinationWidth) continue;

                int sourceIndex = (row * sourceWidth + column) * BytesPerPixel;
                int destinationIndex = (destinationY * destinationWidth + destinationX) * BytesPerPixel;
                int alpha = source[sourceIndex + 3];
                if (alpha == 0) continue;

                if (alpha == 255)
                {
                    Buffer.BlockCopy(source, sourceIndex, destination, destinationIndex, BytesPerPixel);
                    continue;
                }

                int inverse = 255 - alpha;
                for (int c = 0; c < 3; c++)
                {
                    int blended = source[sourceIndex + c] + (destination[destinationIndex + c] * inverse + 127) / 255;
                    destination[destinationIndex + c] = (byte)Math.Min(255, blended);
                }

                destination[destinationIndex + 3] = 255;
            }
        }
    }

    /// <summary>
    /// Builds the card's background from the tiny wash sample: blurred a little more, darkened, and
    /// stretched up to the full card.
    /// </summary>
    /// <param name="sample">
    /// The artwork covering a <see cref="WashSampleWidth"/> by <see cref="WashSampleHeight"/> area,
    /// premultiplied BGRA. Not modified.
    /// </param>
    public static byte[] BuildWash(byte[] sample, int sampleWidth, int sampleHeight)
    {
        byte[] small = (byte[])sample.Clone();
        BoxBlur(small, sampleWidth, sampleHeight, radius: 1, passes: 2);
        Darken(small, WashBrightness);
        return ResizeBilinearStaged(small, sampleWidth, sampleHeight, Width, Height);
    }

    /// <summary>
    /// Composes the finished card: a wash of the artwork's own colours with the whole artwork
    /// centred on it.
    /// </summary>
    /// <param name="foreground">
    /// The whole artwork, upright, already scaled to fit inside the card (see
    /// <see cref="FitInside"/>), premultiplied BGRA. Not modified.
    /// </param>
    /// <returns>A <see cref="Width"/> by <see cref="Height"/> buffer, fully opaque.</returns>
    public static byte[] Compose(byte[] foreground, int foregroundWidth, int foregroundHeight)
    {
        byte[] card = BuildWash(SampleWash(foreground, foregroundWidth, foregroundHeight), WashSampleWidth, WashSampleHeight);
        BlitOver(card, Width, Height, foreground, foregroundWidth, foregroundHeight,
            (Width - foregroundWidth) / 2, (Height - foregroundHeight) / 2);
        return card;
    }
}
