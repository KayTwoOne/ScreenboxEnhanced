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
    /// (files/navigation/factory) are never exercised and are safely left null.
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
            databaseService: databaseService);
    }

    [Test]
    public async Task ApplyFolderEditAsync_WithEmptyTitle_StoresNullCustomTitle()
    {
        using var fixture = new TestDirectoryFixture();
        DatabaseService db = await CreateDatabaseAsync(fixture.DirectoryPath);
        StorageFolder folder = await StorageFolder.GetFolderFromPathAsync(fixture.DirectoryPath);
        FolderViewPageViewModel vm = CreateViewModel(db);

        await vm.ApplyFolderEditAsync(folder, string.Empty, poster: null);

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
        await vm.ApplyFolderEditAsync(folder, folder.Name, poster: null);

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

        await vm.ApplyFolderEditAsync(folder, "Dandadan", poster: null);

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
        await vm.ApplyFolderEditAsync(folder, "New Title", poster: null);

        FolderMetadataDto? loaded = await db.LoadFolderMetadataAsync(folder.Path);
        await Assert.That(loaded).IsNotNull();
        await Assert.That(loaded!.CustomTitle).IsEqualTo("New Title");
        await Assert.That(loaded.PosterFile).IsEqualTo("a1b2c3.jpg");
        await Assert.That(loaded.PosterSource).IsEqualTo(PosterSource.Manual);
        await Assert.That(loaded.ProviderPin).IsEqualTo("anilist:171018");
        await Assert.That(loaded.SortOrder).IsEqualTo(3);
    }
}
