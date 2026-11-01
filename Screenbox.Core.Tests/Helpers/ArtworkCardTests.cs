using Screenbox.Core.Helpers;
using static Screenbox.Core.Tests.Helpers.TestImages;

namespace Screenbox.Core.Tests.Helpers;

/// <summary>
/// The pixel and layout maths behind the artwork card, tested directly: no codec, no files.
/// </summary>
public sealed class ArtworkCardTests
{
    private const int W = ArtworkCard.Width;
    private const int H = ArtworkCard.Height;
    private const int SW = ArtworkCard.WashSampleWidth;
    private const int SH = ArtworkCard.WashSampleHeight;

    /// <summary>Largest difference between neighbouring pixels (blue channel), across every row and column.</summary>
    private static int MaxAdjacentStep(byte[] pixels, int width, int height)
    {
        int max = 0;
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                int i = (y * width + x) * 4;
                if (x + 1 < width) max = Math.Max(max, Math.Abs(pixels[i] - pixels[i + 4]));
                if (y + 1 < height) max = Math.Max(max, Math.Abs(pixels[i] - pixels[i + width * 4]));
            }
        }

        return max;
    }

    /// <summary>
    /// Largest change in slope across a window of pixels along each row: zero on a straight ramp,
    /// large at the crease where two ramps meet. A window of 1 is the finest measure.
    /// </summary>
    private static int MaxKink(byte[] pixels, int width, int height, int window = 1)
    {
        int max = 0;
        for (int y = 0; y < height; y++)
        {
            for (int x = window; x < width - window; x++)
            {
                int i = (y * width + x) * 4;
                max = Math.Max(max, Math.Abs(pixels[i - 4 * window] - 2 * pixels[i] + pixels[i + 4 * window]));
            }
        }

        return max;
    }

    /// <summary>A wash sample that is black but for one white pixel: the harshest thing it can be handed.</summary>
    private static byte[] SingleBrightSpot()
    {
        byte[] sample = Solid(SW, SH, new Rgb(0, 0, 0));
        Fill(sample, SW, 16, 9, 17, 10, new Rgb(255, 255, 255));
        return sample;
    }

    /// <summary>A wash sample that is white on its left half and black on its right.</summary>
    private static byte[] HardVerticalEdge()
    {
        byte[] sample = Solid(SW, SH, new Rgb(0, 0, 0));
        Fill(sample, SW, 0, 0, SW / 2, SH, new Rgb(255, 255, 255));
        return sample;
    }

    // ------------------------------------------------------------------
    // Layout
    // ------------------------------------------------------------------

    [Test]
    [Arguments(230, 325, true)]    // the AniList large cover that started this
    [Arguments(2000, 3000, true)]  // 2:3 key art
    [Arguments(1000, 1000, true)]  // square album art
    [Arguments(139, 100, true)]    // just under the threshold
    [Arguments(140, 100, false)]   // exactly at it: wide enough to be shown as it is
    [Arguments(1280, 720, false)]  // an auto-generated video frame
    [Arguments(0, 100, false)]     // not an image
    [Arguments(100, 0, false)]
    public async Task IsPortraitish_SplitsAtOnePointFourToOne(int width, int height, bool expected)
    {
        await Assert.That(ArtworkCard.IsPortraitish(width, height)).IsEqualTo(expected);
    }

    [Test]
    public async Task FitInside_PortraitSource_FillsTheCardHeightAndKeepsTheWholeImage()
    {
        (int width, int height) = ArtworkCard.FitInside(400, 600, W, H);

        await Assert.That(height).IsEqualTo(H);
        await Assert.That(width).IsEqualTo(341); // 400 * 512 / 600, rounded
        await Assert.That(width).IsLessThanOrEqualTo(W);
    }

    [Test]
    [Arguments(400, 600)]
    [Arguments(2000, 500)]
    [Arguments(500, 500)]
    [Arguments(1, 1000)]
    [Arguments(1000, 1)]
    public async Task FitInside_NeverExceedsTheBoxAndTouchesItOnOneAxis(int sourceWidth, int sourceHeight)
    {
        (int width, int height) = ArtworkCard.FitInside(sourceWidth, sourceHeight, W, H);

        await Assert.That(width).IsGreaterThanOrEqualTo(1);
        await Assert.That(height).IsGreaterThanOrEqualTo(1);
        await Assert.That(width).IsLessThanOrEqualTo(W);
        await Assert.That(height).IsLessThanOrEqualTo(H);
        await Assert.That(width == W || height == H).IsTrue();
    }

    [Test]
    public async Task CoverCrop_PortraitSource_KeepsTheMiddleBand()
    {
        // 400x600 covering 32x18: scaled to 32 wide (so 48 tall), keeping rows 15..33 of it.
        (int scaledWidth, int scaledHeight, int cropX, int cropY) = ArtworkCard.CoverCrop(400, 600, SW, SH);

        await Assert.That(scaledWidth).IsEqualTo(32);
        await Assert.That(scaledHeight).IsEqualTo(48);
        await Assert.That(cropX).IsEqualTo(0);
        await Assert.That(cropY).IsEqualTo(15);
    }

    [Test]
    public async Task CoverCrop_VeryWideSource_KeepsTheMiddleColumns()
    {
        // 3000x900 covering 32x18: scaled to 18 tall (so 60 wide), keeping columns 14..46 of it.
        (int scaledWidth, int scaledHeight, int cropX, int cropY) = ArtworkCard.CoverCrop(3000, 900, SW, SH);

        await Assert.That(scaledWidth).IsEqualTo(60);
        await Assert.That(scaledHeight).IsEqualTo(18);
        await Assert.That(cropX).IsEqualTo(14);
        await Assert.That(cropY).IsEqualTo(0);
    }

    // ------------------------------------------------------------------
    // Resampling
    // ------------------------------------------------------------------

    [Test]
    public async Task ResizeBilinear_SolidColour_StaysThatColour()
    {
        byte[] source = Solid(3, 2, new Rgb(10, 200, 90));

        byte[] result = ArtworkCard.ResizeBilinear(source, 3, 2, 17, 11);

        await Assert.That(result.Length).IsEqualTo(17 * 11 * 4);
        for (int i = 0; i < result.Length; i += 4)
        {
            await Assert.That(new Rgb(result[i + 2], result[i + 1], result[i]).DistanceTo(new Rgb(10, 200, 90))).IsLessThanOrEqualTo(1);
            await Assert.That((int)result[i + 3]).IsEqualTo(255);
        }
    }

    [Test]
    public async Task ResizeBilinear_TwoPixelsStretchedToFour_InterpolatesBetweenThemAndClampsAtTheEnds()
    {
        byte[] source = Solid(2, 1, new Rgb(0, 0, 0));
        Fill(source, 2, 1, 0, 2, 1, new Rgb(200, 200, 200));

        byte[] result = ArtworkCard.ResizeBilinear(source, 2, 1, 4, 1);

        // Sample positions are 0 (clamped), 0.25, 0.75 and 1 (clamped) of the way from black to 200.
        await Assert.That((int)result[0]).IsEqualTo(0);
        await Assert.That(Math.Abs(result[4] - 50)).IsLessThanOrEqualTo(1);
        await Assert.That(Math.Abs(result[8] - 150)).IsLessThanOrEqualTo(1);
        await Assert.That((int)result[12]).IsEqualTo(200);
    }

    [Test]
    public async Task ResizeBilinearStaged_ReachesTheTargetSizeFromATinySource()
    {
        byte[] result = ArtworkCard.ResizeBilinearStaged(Solid(SW, SH, new Rgb(1, 2, 3)), SW, SH, W, H);

        await Assert.That(result.Length).IsEqualTo(W * H * 4);
        await Assert.That(PixelAt(result, W, 0, 0)).IsEqualTo(new Rgb(1, 2, 3));
        await Assert.That(PixelAt(result, W, W - 1, H - 1)).IsEqualTo(new Rgb(1, 2, 3));
    }

    [Test]
    public async Task ResizeBilinearStaged_RoundsOffTheCreasesAOneStepUpscaleLeaves()
    {
        // One bright pixel in a black field. Stretched to card size in a single bilinear step it is
        // a pyramid with a sharp crease at every sample edge - the "blocky steps" in the wash.
        // Stretched in doublings, each step rounds the previous one's creases off.
        byte[] sample = SingleBrightSpot();

        byte[] oneStep = ArtworkCard.ResizeBilinear(sample, SW, SH, W, H);
        byte[] staged = ArtworkCard.ResizeBilinearStaged(sample, SW, SH, W, H);

        await Assert.That(MaxKink(oneStep, W, H)).IsGreaterThan(5)
            .Because("The comparison only means something if a single step really does leave creases.");
        await Assert.That(MaxKink(staged, W, H)).IsLessThanOrEqualTo(3);
    }

    [Test]
    public async Task BoxBlur_SpreadsABrightPixelToItsNeighbours()
    {
        byte[] pixels = Solid(5, 5, new Rgb(0, 0, 0));
        Fill(pixels, 5, 2, 2, 3, 3, new Rgb(180, 180, 180));

        ArtworkCard.BoxBlur(pixels, 5, 5, radius: 1, passes: 1);

        // A 3x3 box over a single 180 pixel: every pixel of that 3x3 block becomes 180 / 9 = 20.
        await Assert.That(PixelAt(pixels, 5, 2, 2).R).IsEqualTo((byte)20);
        await Assert.That(PixelAt(pixels, 5, 1, 1).R).IsEqualTo((byte)20);
        await Assert.That(PixelAt(pixels, 5, 3, 2).R).IsEqualTo((byte)20);
        await Assert.That(PixelAt(pixels, 5, 0, 0).R).IsEqualTo((byte)0);
    }

    [Test]
    public async Task BoxBlur_ReplicatesTheEdgeInsteadOfFadingToBlack()
    {
        // A bright first column over black. With the border replicated its blurred value is
        // (255 + 255 + 0) / 3 = 170; fading to black at the border would give (0 + 255 + 0) / 3 = 85.
        byte[] pixels = Solid(3, 1, new Rgb(0, 0, 0));
        Fill(pixels, 3, 0, 0, 1, 1, new Rgb(255, 255, 255));

        ArtworkCard.BoxBlur(pixels, 3, 1, radius: 1, passes: 1);

        await Assert.That(PixelAt(pixels, 3, 0, 0).R).IsEqualTo((byte)170);
    }

    // ------------------------------------------------------------------
    // Darkening and compositing
    // ------------------------------------------------------------------

    [Test]
    public async Task Darken_HalvesTheColourAndMakesThePixelOpaque()
    {
        byte[] pixels = [50, 100, 200, 255, 50, 100, 200, 90]; // BGRA: an opaque pixel and a translucent one

        ArtworkCard.Darken(pixels, 0.5);

        for (int i = 0; i < 8; i += 4)
        {
            await Assert.That((int)pixels[i]).IsEqualTo(25);
            await Assert.That((int)pixels[i + 1]).IsEqualTo(50);
            await Assert.That((int)pixels[i + 2]).IsEqualTo(100);
            await Assert.That((int)pixels[i + 3]).IsEqualTo(255);
        }
    }

    [Test]
    public async Task BlitOver_OpaqueSource_ReplacesExactlyItsOwnFootprint()
    {
        byte[] destination = Solid(10, 10, new Rgb(0, 0, 255));
        byte[] source = Solid(4, 3, new Rgb(255, 0, 0));

        ArtworkCard.BlitOver(destination, 10, 10, source, 4, 3, x: 3, y: 5);

        await Assert.That(PixelAt(destination, 10, 3, 5)).IsEqualTo(new Rgb(255, 0, 0));
        await Assert.That(PixelAt(destination, 10, 6, 7)).IsEqualTo(new Rgb(255, 0, 0));
        await Assert.That(PixelAt(destination, 10, 2, 5)).IsEqualTo(new Rgb(0, 0, 255));
        await Assert.That(PixelAt(destination, 10, 7, 5)).IsEqualTo(new Rgb(0, 0, 255));
        await Assert.That(PixelAt(destination, 10, 3, 4)).IsEqualTo(new Rgb(0, 0, 255));
        await Assert.That(PixelAt(destination, 10, 3, 8)).IsEqualTo(new Rgb(0, 0, 255));
    }

    [Test]
    public async Task BlitOver_SourceHangingOffTheDestination_IsClippedNotThrown()
    {
        byte[] destination = Solid(10, 10, new Rgb(0, 0, 255));
        byte[] source = Solid(4, 4, new Rgb(255, 0, 0));

        ArtworkCard.BlitOver(destination, 10, 10, source, 4, 4, x: -2, y: 8);

        await Assert.That(PixelAt(destination, 10, 0, 8)).IsEqualTo(new Rgb(255, 0, 0));
        await Assert.That(PixelAt(destination, 10, 1, 9)).IsEqualTo(new Rgb(255, 0, 0));
        await Assert.That(PixelAt(destination, 10, 2, 8)).IsEqualTo(new Rgb(0, 0, 255));
    }

    [Test]
    public async Task BlitOver_TranslucentSource_LetsTheDestinationShowThrough()
    {
        // Premultiplied: a source that is half red (R 100 at alpha 128) over solid blue (B 200).
        byte[] destination = Solid(1, 1, new Rgb(0, 0, 200));
        byte[] source = [0, 0, 100, 128];

        ArtworkCard.BlitOver(destination, 1, 1, source, 1, 1, x: 0, y: 0);

        Rgb result = PixelAt(destination, 1, 0, 0);
        await Assert.That(result.R).IsEqualTo((byte)100);
        await Assert.That(Math.Abs(result.B - 100)).IsLessThanOrEqualTo(1);
        await Assert.That((int)destination[3]).IsEqualTo(255);
    }

    [Test]
    public async Task BlitOver_FullyTransparentSource_ChangesNothing()
    {
        byte[] destination = Solid(1, 1, new Rgb(7, 8, 9));

        ArtworkCard.BlitOver(destination, 1, 1, [0, 0, 0, 0], 1, 1, x: 0, y: 0);

        await Assert.That(PixelAt(destination, 1, 0, 0)).IsEqualTo(new Rgb(7, 8, 9));
    }

    // ------------------------------------------------------------------
    // The wash
    // ------------------------------------------------------------------

    [Test]
    public async Task BuildWash_IsTheSourceColourAtHalfBrightnessAcrossTheWholeCard()
    {
        byte[] wash = ArtworkCard.BuildWash(Solid(SW, SH, new Rgb(200, 100, 50)), SW, SH);

        await Assert.That(wash.Length).IsEqualTo(W * H * 4);
        foreach ((int x, int y) in new[] { (0, 0), (W - 1, 0), (0, H - 1), (W - 1, H - 1), (W / 2, H / 2) })
        {
            await Assert.That(PixelAt(wash, W, x, y).DistanceTo(new Rgb(100, 50, 25))).IsLessThanOrEqualTo(1);
            await Assert.That((int)wash[(y * W + x) * 4 + 3]).IsEqualTo(255);
        }
    }

    [Test]
    [Arguments("hard edge")]
    [Arguments("single bright spot")]
    public async Task BuildWash_HighContrastSample_HasNoHardStepsAnywhere(string sampleKind)
    {
        // The harshest samples there are: a hard edge across the middle and a lone bright pixel.
        // Neighbouring card pixels may differ only slightly - no visible grid or banding. A
        // single-step, unblurred upscale of the same samples steps by 5 per pixel and more.
        byte[] sample = sampleKind == "hard edge" ? HardVerticalEdge() : SingleBrightSpot();

        byte[] wash = ArtworkCard.BuildWash(sample, SW, SH);

        await Assert.That(MaxAdjacentStep(wash, W, H)).IsLessThanOrEqualTo(2);
        await Assert.That(MaxKink(wash, W, H)).IsLessThanOrEqualTo(2);

        // The same, measured across 8 pixels, where it tells a wash upscaled in doublings from one
        // upscaled in a single step (4 on both samples) after the same blur.
        await Assert.That(MaxKink(wash, W, H, window: 8)).IsLessThanOrEqualTo(3);
    }

    [Test]
    public async Task BuildWash_DoesNotModifyItsSample()
    {
        byte[] sample = HardVerticalEdge();
        byte[] before = (byte[])sample.Clone();

        ArtworkCard.BuildWash(sample, SW, SH);

        await Assert.That(sample.AsSpan().SequenceEqual(before)).IsTrue();
    }

    // ------------------------------------------------------------------
    // The finished card
    // ------------------------------------------------------------------

    [Test]
    public async Task SampleWash_AveragesTheImageDownToTheSampleSize()
    {
        // Red on the left half, blue on the right: every sample row must read red then blue.
        byte[] image = Solid(100, 150, new Rgb(255, 0, 0));
        Fill(image, 100, 50, 0, 100, 150, new Rgb(0, 0, 255));

        byte[] sample = ArtworkCard.SampleWash(image, 100, 150);

        await Assert.That(sample.Length).IsEqualTo(SW * SH * 4);
        foreach (int y in new[] { 0, SH / 2, SH - 1 })
        {
            await Assert.That(PixelAt(sample, SW, 0, y)).IsEqualTo(new Rgb(255, 0, 0));
            await Assert.That(PixelAt(sample, SW, SW / 2 - 1, y)).IsEqualTo(new Rgb(255, 0, 0));
            await Assert.That(PixelAt(sample, SW, SW / 2, y)).IsEqualTo(new Rgb(0, 0, 255));
            await Assert.That(PixelAt(sample, SW, SW - 1, y)).IsEqualTo(new Rgb(0, 0, 255));
        }
    }

    [Test]
    public async Task SampleWash_PortraitImage_SamplesOnlyItsMiddleBand()
    {
        // 100x150 covering the sample area keeps the middle 37.5% of its height (31% to 69%). The
        // image is green above a yellow band spanning 25% to 75% and white below, so a sample taken
        // from anywhere but the middle - or from the whole image squashed down - would not be yellow.
        byte[] image = Solid(100, 150, new Rgb(0, 200, 0));
        Fill(image, 100, 0, 37, 100, 113, new Rgb(255, 255, 0));
        Fill(image, 100, 0, 113, 100, 150, new Rgb(255, 255, 255));

        byte[] sample = ArtworkCard.SampleWash(image, 100, 150);

        for (int y = 0; y < SH; y++)
        {
            for (int x = 0; x < SW; x++)
            {
                await Assert.That(PixelAt(sample, SW, x, y)).IsEqualTo(new Rgb(255, 255, 0));
            }
        }
    }

    [Test]
    public async Task SampleWash_ImageSmallerThanTheSample_StillFillsEverySample()
    {
        byte[] sample = ArtworkCard.SampleWash(Solid(3, 5, new Rgb(9, 9, 9)), 3, 5);

        await Assert.That(sample.Length).IsEqualTo(SW * SH * 4);
        await Assert.That(PixelAt(sample, SW, SW - 1, SH - 1)).IsEqualTo(new Rgb(9, 9, 9));
    }

    // 3 wide by 2 tall, one byte value per pixel in the blue channel, row by row: A B C / D E F.
    private static byte[] Labelled() =>
    [
        1, 0, 0, 255, 2, 0, 0, 255, 3, 0, 0, 255,
        4, 0, 0, 255, 5, 0, 0, 255, 6, 0, 0, 255
    ];

    private static int[] Values(byte[] pixels) =>
        [.. Enumerable.Range(0, pixels.Length / 4).Select(i => (int)pixels[i * 4])];

    [Test]
    [Arguments(1, 3, 2, new[] { 1, 2, 3, 4, 5, 6 })]   // as stored:                A B C / D E F
    [Arguments(2, 3, 2, new[] { 3, 2, 1, 6, 5, 4 })]   // mirrored left to right:   C B A / F E D
    [Arguments(3, 3, 2, new[] { 6, 5, 4, 3, 2, 1 })]   // upside down:              F E D / C B A
    [Arguments(4, 3, 2, new[] { 4, 5, 6, 1, 2, 3 })]   // mirrored top to bottom:   D E F / A B C
    [Arguments(5, 2, 3, new[] { 1, 4, 2, 5, 3, 6 })]   // transposed:               A D / B E / C F
    [Arguments(6, 2, 3, new[] { 4, 1, 5, 2, 6, 3 })]   // quarter turn clockwise:   D A / E B / F C
    [Arguments(7, 2, 3, new[] { 6, 3, 5, 2, 4, 1 })]   // transversed:              F C / E B / D A
    [Arguments(8, 2, 3, new[] { 3, 6, 2, 5, 1, 4 })]   // quarter turn anticlockwise: C F / B E / A D
    [Arguments(0, 3, 2, new[] { 1, 2, 3, 4, 5, 6 })]   // not an EXIF value: left as stored
    [Arguments(9, 3, 2, new[] { 1, 2, 3, 4, 5, 6 })]
    public async Task Orient_TurnsAStoredPictureUprightForEachExifOrientation(
        int orientation, int expectedWidth, int expectedHeight, int[] expected)
    {
        (byte[] pixels, int width, int height) = ArtworkCard.Orient(Labelled(), 3, 2, orientation);

        await Assert.That(width).IsEqualTo(expectedWidth);
        await Assert.That(height).IsEqualTo(expectedHeight);
        await Assert.That(Values(pixels).SequenceEqual(expected)).IsTrue();
        await Assert.That(Enumerable.Range(0, pixels.Length / 4).All(i => pixels[i * 4 + 3] == 255)).IsTrue();
    }

    [Test]
    public async Task Orient_DoesNotModifyItsSource()
    {
        byte[] source = Labelled();

        ArtworkCard.Orient(source, 3, 2, 6);

        await Assert.That(source.AsSpan().SequenceEqual(Labelled())).IsTrue();
    }

    [Test]
    public async Task Compose_PutsTheWholeForegroundInTheMiddleOfTheWashAndNothingElse()
    {
        // The wash is drawn from the foreground itself, so a solid foreground gives a solid wash of
        // its own colour at half brightness - told apart from the foreground by being darker.
        Rgb foregroundColour = new(200, 100, 250);
        Rgb darkWash = new(100, 50, 125);
        (int fw, int fh) = ArtworkCard.FitInside(400, 600, W, H);
        byte[] foreground = Solid(fw, fh, foregroundColour);

        byte[] card = ArtworkCard.Compose(foreground, fw, fh);

        int left = (W - fw) / 2;
        int right = left + fw - 1;

        await Assert.That(card.Length).IsEqualTo(W * H * 4);

        // Every corner of the foreground is there - nothing is cut off - and so is the centre.
        await Assert.That(PixelAt(card, W, left, 0)).IsEqualTo(foregroundColour);
        await Assert.That(PixelAt(card, W, right, 0)).IsEqualTo(foregroundColour);
        await Assert.That(PixelAt(card, W, left, H - 1)).IsEqualTo(foregroundColour);
        await Assert.That(PixelAt(card, W, right, H - 1)).IsEqualTo(foregroundColour);
        await Assert.That(PixelAt(card, W, W / 2, H / 2)).IsEqualTo(foregroundColour);

        // Centred: the margins either side are within a pixel of each other.
        await Assert.That(Math.Abs(left - (W - 1 - right))).IsLessThanOrEqualTo(1);

        // Immediately outside the foreground, and at the far edges, it is the darkened wash.
        foreach (int x in new[] { 0, left - 1, right + 1, W - 1 })
        {
            await Assert.That(PixelAt(card, W, x, H / 2).DistanceTo(darkWash)).IsLessThanOrEqualTo(1);
        }
    }
}
