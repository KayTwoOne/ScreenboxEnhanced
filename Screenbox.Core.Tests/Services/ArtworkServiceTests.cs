using Screenbox.Core.Enums;
using Screenbox.Core.Models;
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

    [Test]
    public async Task ShouldSkipConventionCopy_SkipsOnlyWhenDestinationExistsAndMetadataAlreadyRecordsIt()
    {
        const string expectedFileName = "g_deadbeef.jpg";
        FolderMetadataDto recorded = new()
        {
            Path = "C:\\Library\\Show",
            PosterSource = PosterSource.Convention,
            PosterFile = expectedFileName
        };

        // Already copied and recorded: skip the redundant copy and database write. This is the
        // hot path exercised on every folder navigation once a convention poster is established.
        await Assert.That(ArtworkService.ShouldSkipConventionCopy(recorded, expectedFileName, destinationFileExists: true))
            .IsTrue();

        // Metadata claims the file exists but it is missing from local storage: must (re)copy.
        await Assert.That(ArtworkService.ShouldSkipConventionCopy(recorded, expectedFileName, destinationFileExists: false))
            .IsFalse();

        // No metadata at all: first time seeing this folder, must copy.
        await Assert.That(ArtworkService.ShouldSkipConventionCopy(null, expectedFileName, destinationFileExists: true))
            .IsFalse();

        // Metadata records a different source (e.g. an AutoFrame fallback happens to reuse the
        // same file name): must not mistake that for a recorded convention copy.
        FolderMetadataDto differentSource = new()
        {
            Path = recorded.Path,
            PosterSource = PosterSource.AutoFrame,
            PosterFile = expectedFileName
        };
        await Assert.That(ArtworkService.ShouldSkipConventionCopy(differentSource, expectedFileName, destinationFileExists: true))
            .IsFalse();

        // Metadata records a different file name (e.g. the convention file's extension changed
        // from .png to .jpg): must copy the new one rather than keeping the stale reference.
        FolderMetadataDto staleFileName = new()
        {
            Path = recorded.Path,
            PosterSource = PosterSource.Convention,
            PosterFile = "g_00000000.png"
        };
        await Assert.That(ArtworkService.ShouldSkipConventionCopy(staleFileName, expectedFileName, destinationFileExists: true))
            .IsFalse();
    }
}
