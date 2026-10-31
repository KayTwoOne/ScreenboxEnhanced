using Screenbox.Core.Services;

namespace Screenbox.Core.Tests.Services;

/// <summary>
/// Covers <see cref="LibraryService.CountUnderFolders"/>, the counting half of the check that
/// stops a stale library cache from being trusted when the change tracker reports no changes.
/// The count is compared against the library query's own item count, so it must include exactly
/// the files that query would return - no more, no fewer.
/// </summary>
public sealed class LibraryCacheFreshnessTests
{
    [Test]
    public async Task CountUnderFolders_CountsOnlyLocationsInsideTheLibraryFolders()
    {
        // Removable-storage items share the cache but not the library query.
        string[] folders = [@"D:\Media\Anime"];
        string[] locations =
        [
            @"D:\Media\Anime\Death Note\ep1.mkv",
            @"D:\Media\Anime\Death Note\ep2.mkv",
            @"E:\USB\holiday.mp4",
        ];

        await Assert.That(LibraryService.CountUnderFolders(folders, locations)).IsEqualTo(2);
    }

    [Test]
    public async Task CountUnderFolders_DoesNotLetAFolderClaimASiblingThatSharesItsPrefix()
    {
        // Without the trailing separator, D:\Media\Anime would claim everything in
        // D:\Media\Anime2, the count would never match the query, and every launch would rescan.
        string[] folders = [@"D:\Media\Anime"];
        string[] locations =
        [
            @"D:\Media\Anime\Death Note\ep1.mkv",
            @"D:\Media\Anime2\Other Show\ep1.mkv",
        ];

        await Assert.That(LibraryService.CountUnderFolders(folders, locations)).IsEqualTo(1);
    }

    [Test]
    public async Task CountUnderFolders_IgnoresCaseLikeWindowsPaths()
    {
        string[] folders = [@"D:\Media\Anime"];
        string[] locations = [@"d:\MEDIA\anime\Death Note\ep1.mkv"];

        await Assert.That(LibraryService.CountUnderFolders(folders, locations)).IsEqualTo(1);
    }

    [Test]
    public async Task CountUnderFolders_CountsAFileInNestedLibraryFoldersOnce()
    {
        // A real library on this machine includes both C:\Users\Kaylum\Music and its own
        // subfolder \tracks. The library query returns each file once, so the count must too.
        string[] folders = [@"C:\Users\Kaylum\Music", @"C:\Users\Kaylum\Music\tracks"];
        string[] locations =
        [
            @"C:\Users\Kaylum\Music\song.flac",
            @"C:\Users\Kaylum\Music\tracks\other.flac",
        ];

        await Assert.That(LibraryService.CountUnderFolders(folders, locations)).IsEqualTo(2);
    }

    [Test]
    public async Task CountUnderFolders_AcceptsAFolderPathThatAlreadyEndsInASeparator()
    {
        string[] folders = [@"D:\Media\Anime\"];
        string[] locations = [@"D:\Media\Anime\Death Note\ep1.mkv"];

        await Assert.That(LibraryService.CountUnderFolders(folders, locations)).IsEqualTo(1);
    }
}
