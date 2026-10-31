using Microsoft.Extensions.Logging.Abstractions;
using Screenbox.Core.Enums;
using Screenbox.Core.Models;
using Screenbox.Core.Services;
using Screenbox.Core.Tests.Helpers;
using Screenbox.Core.ViewModels;
using Windows.Storage;

namespace Screenbox.Core.Tests.ViewModels;

/// <summary>
/// Covers <see cref="FolderViewPageViewModel.ApplyFolderEditAsync"/>, the trickiest correctness
/// path in the folder-edit feature: deciding when a title is "real" custom metadata versus just
/// the folder's own on-disk name, and never clobbering unrelated poster metadata on a title-only
/// edit.
/// </summary>
public sealed class FolderViewPageViewModelTests
{
    private static async Task<DatabaseService> CreateDatabaseAsync(string dir)
    {
        var db = new DatabaseService(NullLogger<DatabaseService>.Instance) { DbFolderPath = dir };
        await db.InitializeAsync();
        return db;
    }

    /// <summary>
    /// Builds a real <see cref="FolderViewPageViewModel"/> backed by a real, isolated
    /// <see cref="DatabaseService"/>. <see cref="ApplyFolderEditAsync"/> only touches
    /// the database and artwork services, so the remaining constructor dependencies
    /// (files/navigation/factory/folder stats) are never exercised and are safely left null.
    /// </summary>
    /// <remarks>
    /// TUnit can resume a test on a different thread than the one <see cref="TestInitializer"/>
    /// ran on (e.g. after the <c>await</c>s in each test that precede this call), so the
    /// dispatcher queue that <see cref="FolderViewPageViewModel"/>'s constructor requires is not
    /// guaranteed to exist on the current thread by the time we get here. Re-ensuring it
    /// immediately before construction (idempotent, so cheap) is the reliable fix.
    /// </remarks>
    private static FolderViewPageViewModel CreateViewModel(IDatabaseService databaseService)
    {
        DispatcherQueueTestHelper.EnsureDispatcherQueue();
        return new FolderViewPageViewModel(
            filesService: null!,
            navigationService: null!,
            storageVmFactory: null!,
            artworkService: null!,
            databaseService: databaseService,
            folderStatsService: null!);
    }

    private static FolderViewPageViewModel CreateViewModel(IDatabaseService databaseService, IArtworkService artworkService)
    {
        DispatcherQueueTestHelper.EnsureDispatcherQueue();
        return new FolderViewPageViewModel(
            filesService: null!,
            navigationService: null!,
            storageVmFactory: null!,
            artworkService: artworkService,
            databaseService: databaseService,
            folderStatsService: null!);
    }

    /// <summary>
    /// Records which poster writes the view model asks the artwork service for. Every other member
    /// throws, so an edit that reaches for something it should not fails loudly.
    /// </summary>
    private sealed class RecordingArtworkService : IArtworkService
    {
        public List<string> RemovedFor { get; } = [];
        public List<string> SetFor { get; } = [];

        public Task<bool> RemoveManualPosterAsync(string folderPath)
        {
            RemovedFor.Add(folderPath);
            return Task.FromResult(true);
        }

        public Task<string?> SetManualPosterAsync(StorageFolder folder, StorageFile image)
        {
            SetFor.Add(folder.Path);
            return Task.FromResult<string?>("m_recorded.jpg");
        }

        public Task<string?> GetPosterFileNameAsync(StorageFolder folder, FolderMetadataDto? metadata) => throw new NotSupportedException();
        public Task PruneOrphanedArtworkAsync(IReadOnlyList<string> libraryFolderPaths) => throw new NotSupportedException();
        public Task<bool> SetScrapedPosterAsync(string folderPath, string posterUrl, CancellationToken ct) => throw new NotSupportedException();
    }

    [Test]
    public async Task ApplyFolderEditAsync_WithEmptyTitle_StoresNullCustomTitle()
    {
        using var fixture = new TestDirectoryFixture();
        DatabaseService db = await CreateDatabaseAsync(fixture.DirectoryPath);
        StorageFolder folder = await StorageFolder.GetFolderFromPathAsync(fixture.DirectoryPath);
        FolderViewPageViewModel vm = CreateViewModel(db);

        await vm.ApplyFolderEditAsync(folder, string.Empty, poster: null, removePoster: false);

        FolderMetadataDto? loaded = await db.LoadFolderMetadataAsync(folder.Path);
        await Assert.That(loaded).IsNotNull();
        await Assert.That(loaded!.CustomTitle).IsNull();
    }

    [Test]
    public async Task ApplyFolderEditAsync_WithTitleMatchingFolderName_StoresNullCustomTitle()
    {
        using var fixture = new TestDirectoryFixture();
        DatabaseService db = await CreateDatabaseAsync(fixture.DirectoryPath);
        StorageFolder folder = await StorageFolder.GetFolderFromPathAsync(fixture.DirectoryPath);
        FolderViewPageViewModel vm = CreateViewModel(db);

        // The dialog pre-fills with the current DisplayName, which for a folder with no custom
        // title is the on-disk name. Saving unedited must not pin that name as an explicit title.
        await vm.ApplyFolderEditAsync(folder, folder.Name, poster: null, removePoster: false);

        FolderMetadataDto? loaded = await db.LoadFolderMetadataAsync(folder.Path);
        await Assert.That(loaded).IsNotNull();
        await Assert.That(loaded!.CustomTitle).IsNull();
    }

    [Test]
    public async Task ApplyFolderEditAsync_WithGenuinelyCustomTitle_StoresIt()
    {
        using var fixture = new TestDirectoryFixture();
        DatabaseService db = await CreateDatabaseAsync(fixture.DirectoryPath);
        StorageFolder folder = await StorageFolder.GetFolderFromPathAsync(fixture.DirectoryPath);
        FolderViewPageViewModel vm = CreateViewModel(db);

        await vm.ApplyFolderEditAsync(folder, "Dandadan", poster: null, removePoster: false);

        FolderMetadataDto? loaded = await db.LoadFolderMetadataAsync(folder.Path);
        await Assert.That(loaded).IsNotNull();
        await Assert.That(loaded!.CustomTitle).IsEqualTo("Dandadan");
    }

    [Test]
    public async Task ApplyFolderEditAsync_TitleOnlyEdit_PreservesExistingPosterFields()
    {
        using var fixture = new TestDirectoryFixture();
        DatabaseService db = await CreateDatabaseAsync(fixture.DirectoryPath);
        StorageFolder folder = await StorageFolder.GetFolderFromPathAsync(fixture.DirectoryPath);
        FolderViewPageViewModel vm = CreateViewModel(db);

        await db.SaveFolderMetadataAsync(new FolderMetadataDto
        {
            Path = folder.Path,
            PosterFile = "a1b2c3.jpg",
            PosterSource = PosterSource.Manual,
            ProviderPin = "anilist:171018",
            SortOrder = 3
        });

        // No poster supplied: this is a title-only edit and must not disturb the existing
        // poster metadata written by a previous edit or by automatic artwork resolution.
        await vm.ApplyFolderEditAsync(folder, "New Title", poster: null, removePoster: false);

        FolderMetadataDto? loaded = await db.LoadFolderMetadataAsync(folder.Path);
        await Assert.That(loaded).IsNotNull();
        await Assert.That(loaded!.CustomTitle).IsEqualTo("New Title");
        await Assert.That(loaded.PosterFile).IsEqualTo("a1b2c3.jpg");
        await Assert.That(loaded.PosterSource).IsEqualTo(PosterSource.Manual);
        await Assert.That(loaded.ProviderPin).IsEqualTo("anilist:171018");
        await Assert.That(loaded.SortOrder).IsEqualTo(3);
    }
    [Test]
    public async Task HasManualPosterAsync_IsTrueOnlyWhenTheStoredPosterIsAManualOne()
    {
        // The edit dialog offers "Remove custom image" only for a poster the user chose, because
        // that is the only kind removal applies to. So this answer has to be exactly "is Manual".
        using var fixture = new TestDirectoryFixture();
        DatabaseService db = await CreateDatabaseAsync(fixture.DirectoryPath);
        StorageFolder folder = await StorageFolder.GetFolderFromPathAsync(fixture.DirectoryPath);
        FolderViewPageViewModel vm = CreateViewModel(db);

        await Assert.That(await vm.HasManualPosterAsync(folder)).IsFalse()
            .Because("A folder with no stored row has no poster of any kind.");

        foreach (PosterSource source in new[] { PosterSource.AutoFrame, PosterSource.Convention, PosterSource.Scraped })
        {
            await db.SetFolderPosterAsync(folder.Path, "g_poster.jpg", source);
            await Assert.That(await vm.HasManualPosterAsync(folder)).IsFalse().Because($"{source} is not a manual poster.");
        }

        await db.SetFolderPosterAsync(folder.Path, "m_poster.jpg", PosterSource.Manual);
        await Assert.That(await vm.HasManualPosterAsync(folder)).IsTrue();
    }

    [Test]
    public async Task ApplyFolderEditAsync_RemovePoster_RemovesTheManualPosterAndStillSavesTheTitle()
    {
        using var fixture = new TestDirectoryFixture();
        DatabaseService db = await CreateDatabaseAsync(fixture.DirectoryPath);
        StorageFolder folder = await StorageFolder.GetFolderFromPathAsync(fixture.DirectoryPath);
        var artwork = new RecordingArtworkService();
        FolderViewPageViewModel vm = CreateViewModel(db, artwork);

        await vm.ApplyFolderEditAsync(folder, "Dandadan", poster: null, removePoster: true);

        await Assert.That(artwork.RemovedFor).IsEquivalentTo([folder.Path]);
        await Assert.That(artwork.SetFor).IsEmpty();
        FolderMetadataDto? loaded = await db.LoadFolderMetadataAsync(folder.Path);
        await Assert.That(loaded!.CustomTitle).IsEqualTo("Dandadan");
    }

    [Test]
    public async Task ApplyFolderEditAsync_NoRemovalRequested_NeverRemovesAPoster()
    {
        // A title-only edit must leave the poster alone.
        using var fixture = new TestDirectoryFixture();
        DatabaseService db = await CreateDatabaseAsync(fixture.DirectoryPath);
        StorageFolder folder = await StorageFolder.GetFolderFromPathAsync(fixture.DirectoryPath);
        var artwork = new RecordingArtworkService();
        FolderViewPageViewModel vm = CreateViewModel(db, artwork);

        await vm.ApplyFolderEditAsync(folder, "Dandadan", poster: null, removePoster: false);

        await Assert.That(artwork.RemovedFor).IsEmpty();
        await Assert.That(artwork.SetFor).IsEmpty();
    }

    [Test]
    public async Task ApplyFolderEditAsync_ChosenPosterWinsOverARemovalRequest()
    {
        // The dialog never sends both, but if it ever did, the image the user just picked is the
        // one they want: replacing the old poster already removes it, and removing first would only
        // briefly drop the folder back to automatic artwork.
        using var fixture = new TestDirectoryFixture();
        DatabaseService db = await CreateDatabaseAsync(fixture.DirectoryPath);
        StorageFolder folder = await StorageFolder.GetFolderFromPathAsync(fixture.DirectoryPath);
        string imagePath = Path.Combine(fixture.DirectoryPath, "chosen.jpg");
        await File.WriteAllBytesAsync(imagePath, [1, 2, 3]);
        StorageFile image = await StorageFile.GetFileFromPathAsync(imagePath);
        var artwork = new RecordingArtworkService();
        FolderViewPageViewModel vm = CreateViewModel(db, artwork);

        await vm.ApplyFolderEditAsync(folder, "Dandadan", poster: image, removePoster: true);

        await Assert.That(artwork.SetFor).IsEquivalentTo([folder.Path]);
        await Assert.That(artwork.RemovedFor).IsEmpty();
    }
}
