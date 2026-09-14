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
        const string conventionExtension = ".jpg";
        FolderMetadataDto recorded = new()
        {
            Path = "C:\\Library\\Show",
            PosterSource = PosterSource.Convention,
            PosterFile = "g_deadbeef_8f2c1a.jpg"
        };

        // Already copied and recorded: skip the redundant copy and database write. This is the
        // hot path exercised on every folder navigation once a convention poster is established.
        await Assert.That(ArtworkService.ShouldSkipConventionCopy(recorded, conventionExtension, recordedFileExists: true))
            .IsTrue();

        // Metadata claims the file exists but it is missing from local storage: must (re)copy.
        await Assert.That(ArtworkService.ShouldSkipConventionCopy(recorded, conventionExtension, recordedFileExists: false))
            .IsFalse();

        // No metadata at all: first time seeing this folder, must copy.
        await Assert.That(ArtworkService.ShouldSkipConventionCopy(null, conventionExtension, recordedFileExists: true))
            .IsFalse();

        // Metadata records a different source (e.g. an AutoFrame fallback happens to reuse the
        // same file name): must not mistake that for a recorded convention copy.
        FolderMetadataDto differentSource = new()
        {
            Path = recorded.Path,
            PosterSource = PosterSource.AutoFrame,
            PosterFile = recorded.PosterFile
        };
        await Assert.That(ArtworkService.ShouldSkipConventionCopy(differentSource, conventionExtension, recordedFileExists: true))
            .IsFalse();

        // The convention file's extension changed from .png to .jpg: must copy the new one rather
        // than keeping the stale reference.
        FolderMetadataDto staleFileName = new()
        {
            Path = recorded.Path,
            PosterSource = PosterSource.Convention,
            PosterFile = "g_00000000_4b1e.png"
        };
        await Assert.That(ArtworkService.ShouldSkipConventionCopy(staleFileName, conventionExtension, recordedFileExists: true))
            .IsFalse();
    }

    [Test]
    public async Task IsOrphanedFolderPath_EvictsOnlyFoldersThatGenuinelyDisappeared()
    {
        string[] roots = [@"D:\Media\Anime", @"E:\Archive"];
        // E: is an unplugged drive: its root reads as missing, and so does everything beneath it.
        HashSet<string> existing = new(StringComparer.OrdinalIgnoreCase)
        {
            @"D:\Media\Anime",
            @"D:\Media\Anime\Frieren"
        };
        bool DirectoryExists(string path) => existing.Contains(path);

        // Gone from a library root that is readable right now: safe to evict.
        await Assert.That(ArtworkService.IsOrphanedFolderPath(@"D:\Media\Anime\Deleted Show", roots, DirectoryExists))
            .IsTrue();

        // Still on disk: keep the row, including a manual poster.
        await Assert.That(ArtworkService.IsOrphanedFolderPath(@"D:\Media\Anime\Frieren", roots, DirectoryExists))
            .IsFalse();

        // Under a root that is itself unreachable (offline drive or network share): everything
        // there looks missing, so evicting would destroy data that is merely not plugged in.
        await Assert.That(ArtworkService.IsOrphanedFolderPath(@"E:\Archive\Old Show", roots, DirectoryExists))
            .IsFalse();

        // Outside every scanned root: this scan says nothing about it, so leave it alone.
        await Assert.That(ArtworkService.IsOrphanedFolderPath(@"C:\Elsewhere\Show", roots, DirectoryExists))
            .IsFalse();

        // A sibling whose name merely starts with a root's name is not inside that root.
        await Assert.That(ArtworkService.IsOrphanedFolderPath(@"D:\Media\AnimeMovies\Show", roots, DirectoryExists))
            .IsFalse();
    }
}
