using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.Extensions.Logging;
using Screenbox.Core.Factories;
using Screenbox.Core.Models;
using Screenbox.Core.Services;
using Windows.Storage;
using Windows.UI.Xaml.Media.Imaging;

namespace Screenbox.Core.ViewModels;

public sealed partial class StorageItemViewModel : ObservableObject
{
    public string Name { get; }

    public string Path { get; }

    public DateTimeOffset DateCreated { get; }

    public IStorageItem StorageItem { get; }

    public MediaViewModel? Media { get; }

    public bool IsFile { get; }

    [ObservableProperty] public partial string CaptionText { get; set; }
    [ObservableProperty] public partial uint ItemCount { get; set; }
    [ObservableProperty] public partial BitmapImage? Thumbnail { get; set; }
    [ObservableProperty] public partial string DisplayName { get; set; }

    private readonly IFilesService _filesService;
    private readonly ILogger<StorageItemViewModel> _logger;
    private readonly IArtworkService _artworkService;
    private readonly IDatabaseService _databaseService;

    [DynamicWindowsRuntimeCast(typeof(StorageFile))]
    public StorageItemViewModel(IFilesService filesService,
        MediaViewModelFactory mediaFactory,
        ILogger<StorageItemViewModel> logger,
        IStorageItem storageItem,
        IArtworkService artworkService,
        IDatabaseService databaseService)
    {
        _filesService = filesService;
        _logger = logger;
        _artworkService = artworkService;
        _databaseService = databaseService;
        StorageItem = storageItem;
        CaptionText = string.Empty;
        DateCreated = storageItem.DateCreated;

        if (storageItem is StorageFile file)
        {
            IsFile = true;
            Media = mediaFactory.GetOrCreate(file);
            Name = Media.Name;
            Path = Media.Location;
        }
        else
        {
            Name = storageItem.Name;
            Path = storageItem.Path;
        }

        DisplayName = Name;
    }

    [DynamicWindowsRuntimeCast(typeof(StorageFolder))]
    [DynamicWindowsRuntimeCast(typeof(StorageFile))]
    public async Task UpdateCaptionAsync()
    {
        try
        {
            switch (StorageItem)
            {
                case StorageFolder folder when !string.IsNullOrEmpty(folder.Path):
                    ItemCount = await _filesService.GetSupportedItemCountAsync(folder);
                    break;
                case StorageFile file:
                    if (!string.IsNullOrEmpty(Media?.Caption))
                    {
                        CaptionText = Media?.Caption ?? string.Empty;
                    }
                    else
                    {
                        string[] additionalPropertyKeys =
                        {
                            SystemProperties.Music.Artist,
                            SystemProperties.Media.Duration
                        };

                        IDictionary<string, object> additionalProperties =
                            await file.Properties.RetrievePropertiesAsync(additionalPropertyKeys);

                        if (additionalProperties[SystemProperties.Music.Artist] is string[] { Length: > 0 } contributingArtists)
                        {
                            CaptionText = string.Join(", ", contributingArtists);
                        }
                        else if (additionalProperties[SystemProperties.Media.Duration] is ulong ticks and > 0)
                        {
                            TimeSpan duration = TimeSpan.FromTicks((long)ticks);
                            CaptionText = Humanizer.ToDuration(duration);
                        }
                    }
                    break;
            }
        }
        catch (Exception e)
        {
            _logger.LogError(e, "Failed to update the caption for storage item '{Path}'.", Path);
        }
    }

    /// <summary>
    /// Loads the custom title and poster artwork for a folder. Does nothing for files,
    /// whose artwork already comes from <see cref="MediaViewModel.Thumbnail"/>.
    /// </summary>
    [DynamicWindowsRuntimeCast(typeof(StorageFolder))]
    public async Task LoadFolderArtworkAsync()
    {
        if (StorageItem is not StorageFolder folder || string.IsNullOrEmpty(folder.Path)) return;

        try
        {
            FolderMetadataDto? metadata = await _databaseService.LoadFolderMetadataAsync(folder.Path);
            if (metadata?.CustomTitle is { Length: > 0 } title)
            {
                DisplayName = title;
            }

            string? posterFile = await _artworkService.GetPosterFileNameAsync(folder);
            if (posterFile is not { Length: > 0 }) return;

            var uri = new Uri($"ms-appdata:///local/{ArtworkService.ArtworkFolderName}/{posterFile}");
            Thumbnail = new BitmapImage(uri) { DecodePixelWidth = 400 };
        }
        catch (Exception e)
        {
            _logger.LogError(e, "Failed to load folder artwork for '{Path}'.", Path);
        }
    }
}
