using Screenbox.Core.Services;
using Windows.Storage;
using Microsoft.Extensions.Logging;
using StorageItemViewModel = Screenbox.Core.ViewModels.StorageItemViewModel;

namespace Screenbox.Core.Factories;

public sealed class StorageItemViewModelFactory
{
    private readonly IFilesService _filesService;
    private readonly MediaViewModelFactory _mediaFactory;
    private readonly ILogger<StorageItemViewModel> _logger;
    private readonly IArtworkService _artworkService;
    private readonly IDatabaseService _databaseService;
    private readonly IFolderStatsService _folderStatsService;

    public StorageItemViewModelFactory(
        IFilesService filesService,
        MediaViewModelFactory mediaFactory,
        ILogger<StorageItemViewModel> logger,
        IArtworkService artworkService,
        IDatabaseService databaseService,
        IFolderStatsService folderStatsService)
    {
        _filesService = filesService;
        _mediaFactory = mediaFactory;
        _logger = logger;
        _artworkService = artworkService;
        _databaseService = databaseService;
        _folderStatsService = folderStatsService;
    }

    public StorageItemViewModel GetInstance(IStorageItem storageItem)
    {
        return new StorageItemViewModel(
            _filesService, _mediaFactory, _logger, storageItem, _artworkService, _databaseService, _folderStatsService);
    }
}
