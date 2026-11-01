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

    [Test]
    public async Task ResolveFromMetadata_SettlesPinnedChoicesWithoutInspectingTheFolder()
    {
        // Callers rely on this to skip the convention-file scan and the recursive video search,
        // which otherwise run on every container realization.
        var manual = new FolderMetadataDto
        {
            Path = @"D:\x", PosterFile = "manual.jpg", PosterSource = PosterSource.Manual
        };
        var scraped = new FolderMetadataDto
        {
            Path = @"D:\x", PosterFile = "scraped.jpg", PosterSource = PosterSource.Scraped
        };

        await Assert.That(ArtworkResolver.ResolveFromMetadata(manual)?.Source).IsEqualTo(PosterSource.Manual);
        await Assert.That(ArtworkResolver.ResolveFromMetadata(scraped)?.Source).IsEqualTo(PosterSource.Scraped);
    }

    [Test]
    public async Task ResolveFromMetadata_DefersWhenTheFolderStillHasToBeInspected()
    {
        var cachedFrame = new FolderMetadataDto
        {
            Path = @"D:\x", PosterFile = "frame.jpg", PosterSource = PosterSource.AutoFrame
        };

        // A convention file inside the folder outranks a cached frame, so these cases cannot be
        // decided from the row alone.
        await Assert.That(ArtworkResolver.ResolveFromMetadata(cachedFrame)).IsNull();
        await Assert.That(ArtworkResolver.ResolveFromMetadata(null)).IsNull();
        await Assert.That(ArtworkResolver.ResolveFromMetadata(new FolderMetadataDto
        {
            Path = @"D:\x", PosterSource = PosterSource.Manual
        })).IsNull().Because("A manual source with no stored file name decides nothing.");
    }
}
