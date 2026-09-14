using Screenbox.Core.Services;

namespace Screenbox.Core.Tests.Services;

public sealed class ArtworkServiceTests
{
    [Test]
    public async Task MaxRecursionDepth_IsAtLeastThreeToCoverNestedSeasonFolders()
    {
        // Real libraries nest as Series/Season/Extras, so anything below 3 would
        // fail to find a video for the series-level folder that most needs artwork.
        await Assert.That(ArtworkService.MaxRecursionDepth).IsGreaterThanOrEqualTo(3);
    }

    [Test]
    public async Task ArtworkFolderName_IsStableAndRelative()
    {
        await Assert.That(ArtworkService.ArtworkFolderName).IsEqualTo("Artwork");
    }

    [Test]
    public async Task IsConventionFileName_MatchesKnownConventionsCaseInsensitively()
    {
        await Assert.That(ArtworkService.IsConventionFileName("poster.jpg")).IsTrue();
        await Assert.That(ArtworkService.IsConventionFileName("Folder.JPG")).IsTrue();
        await Assert.That(ArtworkService.IsConventionFileName("poster.png")).IsTrue();
        await Assert.That(ArtworkService.IsConventionFileName("cover.jpg")).IsTrue();
        await Assert.That(ArtworkService.IsConventionFileName("episode01.jpg")).IsFalse();
        await Assert.That(ArtworkService.IsConventionFileName("poster.txt")).IsFalse();
    }
}
