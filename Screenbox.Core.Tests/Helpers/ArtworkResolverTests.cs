using Screenbox.Core.Enums;
using Screenbox.Core.Helpers;
using Screenbox.Core.Models;

namespace Screenbox.Core.Tests.Helpers;

public sealed class ArtworkResolverTests
{
    [Test]
    public async Task Resolve_PrefersManualOverEverythingElse()
    {
        var metadata = new FolderMetadataDto
        {
            Path = @"D:\x", PosterFile = "manual.jpg", PosterSource = PosterSource.Manual
        };

        ArtworkDecision decision = ArtworkResolver.Resolve(metadata, hasConventionFile: true, hasCachedFrame: true);

        await Assert.That(decision.Source).IsEqualTo(PosterSource.Manual);
        await Assert.That(decision.FileName).IsEqualTo("manual.jpg");
        await Assert.That(decision.NeedsGeneration).IsFalse();
    }

    [Test]
    public async Task Resolve_PrefersScrapedOverConvention()
    {
        var metadata = new FolderMetadataDto
        {
            Path = @"D:\x", PosterFile = "scraped.jpg", PosterSource = PosterSource.Scraped
        };

        ArtworkDecision decision = ArtworkResolver.Resolve(metadata, hasConventionFile: true, hasCachedFrame: true);

        await Assert.That(decision.Source).IsEqualTo(PosterSource.Scraped);
        await Assert.That(decision.FileName).IsEqualTo("scraped.jpg");
    }

    [Test]
    public async Task Resolve_UsesConventionWhenNoStoredPoster()
    {
        ArtworkDecision decision = ArtworkResolver.Resolve(metadata: null, hasConventionFile: true, hasCachedFrame: true);

        await Assert.That(decision.Source).IsEqualTo(PosterSource.Convention);
        await Assert.That(decision.NeedsGeneration).IsFalse();
    }

    [Test]
    public async Task Resolve_RequestsGenerationWhenNothingAvailable()
    {
        ArtworkDecision decision = ArtworkResolver.Resolve(metadata: null, hasConventionFile: false, hasCachedFrame: false);

        await Assert.That(decision.Source).IsEqualTo(PosterSource.None);
        await Assert.That(decision.FileName).IsNull();
        await Assert.That(decision.NeedsGeneration).IsTrue();
    }

    [Test]
    public async Task Resolve_UsesCachedFrameWithoutRegenerating()
    {
        var metadata = new FolderMetadataDto
        {
            Path = @"D:\x", PosterFile = "frame.jpg", PosterSource = PosterSource.AutoFrame
        };

        ArtworkDecision decision = ArtworkResolver.Resolve(metadata, hasConventionFile: false, hasCachedFrame: true);

        await Assert.That(decision.Source).IsEqualTo(PosterSource.AutoFrame);
        await Assert.That(decision.NeedsGeneration).IsFalse();
    }

    [Test]
    public async Task Resolve_RegeneratesWhenStoredFrameFileIsMissing()
    {
        var metadata = new FolderMetadataDto
        {
            Path = @"D:\x", PosterFile = "gone.jpg", PosterSource = PosterSource.AutoFrame
        };

        ArtworkDecision decision = ArtworkResolver.Resolve(metadata, hasConventionFile: false, hasCachedFrame: false);

        await Assert.That(decision.NeedsGeneration).IsTrue();
    }

    [Test]
    public async Task Resolve_KeepsManualEvenWhenFileMissingSoUserChoiceIsNotSilentlyLost()
    {
        var metadata = new FolderMetadataDto
        {
            Path = @"D:\x", PosterFile = "manual.jpg", PosterSource = PosterSource.Manual
        };

        ArtworkDecision decision = ArtworkResolver.Resolve(metadata, hasConventionFile: true, hasCachedFrame: false);

        await Assert.That(decision.Source).IsEqualTo(PosterSource.Manual);
    }

    [Test]
    public async Task Resolve_PrefersConventionOverCachedFrame()
    {
        var metadata = new FolderMetadataDto
        {
            Path = @"D:\x", PosterFile = "frame.jpg", PosterSource = PosterSource.AutoFrame
        };

        ArtworkDecision decision = ArtworkResolver.Resolve(metadata, hasConventionFile: true, hasCachedFrame: true);

        await Assert.That(decision.Source).IsEqualTo(PosterSource.Convention);
    }
}
