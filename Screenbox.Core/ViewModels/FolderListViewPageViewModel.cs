using Screenbox.Core.Factories;
using Screenbox.Core.Services;

namespace Screenbox.Core.ViewModels;

// To support navigation type matching
public sealed partial class FolderListViewPageViewModel : FolderViewPageViewModel
{
    private readonly INavigationService _navigationService;

    // artworkService and databaseService are unused here on purpose: this type exists only so the
    // navigation service can match on it, and every parameter below the navigation service is
    // forwarded straight to the base constructor, which owns the folder identity behaviour.
    public FolderListViewPageViewModel(IFilesService filesService,
        INavigationService navigationService,
        StorageItemViewModelFactory storageVmFactory,
        IArtworkService artworkService,
        IDatabaseService databaseService) :
        base(filesService, navigationService, storageVmFactory, artworkService, databaseService)
    {
        _navigationService = navigationService;
    }

    protected override void Navigate(object? parameter = null)
    {
        _navigationService.NavigateExisting(typeof(FolderListViewPageViewModel),
            new NavigationMetadata(NavData?.RootViewModelType ?? typeof(FolderListViewPageViewModel), parameter));
    }
}
