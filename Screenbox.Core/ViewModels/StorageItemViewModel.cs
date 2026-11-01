using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.Extensions.Logging;
using Screenbox.Core.Factories;
using Screenbox.Core.Helpers;
using Screenbox.Core.Models;
using Screenbox.Core.Services;
using Windows.Storage;
using Windows.UI.Xaml;
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

    /// <summary>
    /// <see langword="true"/> when this item is a folder; the complement of <see cref="IsFile"/>.
    /// Used by the view layer to show folder-only affordances such as the edit-folder context menu item.
    /// </summary>
    public bool IsFolder => !IsFile;

    [ObservableProperty] public partial string CaptionText { get; set; }
    [ObservableProperty] public partial uint ItemCount { get; set; }
    [ObservableProperty] public partial BitmapImage? Thumbnail { get; set; }
    [ObservableProperty] public partial string DisplayName { get; set; }
    [ObservableProperty] public partial FolderStats? Stats { get; set; }

    /// <summary>Decode width for a poster drawn as it is.</summary>
    private const int PosterDecodePixelWidth = 400;

    /// <summary>
    /// Decode width for a card: twice the 232 px wide tile, so it stays sharp at 200% display
    /// scaling. Not the card's full 928 px, which would cost four times the memory for pixels the
    /// tile cannot show.
    /// </summary>
    private const int CardDecodePixelWidth = 464;

    private readonly IFilesService _filesService;
    private readonly ILogger<StorageItemViewModel> _logger;
    private readonly IArtworkService _artworkService;
    private readonly IDatabaseService _databaseService;
    private readonly IFolderStatsService _folderStatsService;
    private bool _folderArtworkLoaded;
    private bool _folderStatsLoaded;

    [DynamicWindowsRuntimeCast(typeof(StorageFile))]
    public StorageItemViewModel(IFilesService filesService,
        MediaViewModelFactory mediaFactory,
        ILogger<StorageItemViewModel> logger,
        IStorageItem storageItem,
        IArtworkService artworkService,
        IDatabaseService databaseService,
        IFolderStatsService folderStatsService)
    {
        _filesService = filesService;
        _logger = logger;
        _artworkService = artworkService;
        _databaseService = databaseService;
        _folderStatsService = folderStatsService;
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
                    EpisodeInfo episode = EpisodeInfoParser.Parse(file.Name);
                    if (episode.Title is { Length: > 0 } episodeTitle)
                    {
                        CaptionText = episodeTitle;
                        break;
                    }

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
    /// Loads the display artwork for this item. For files, this copies the already-loaded
    /// <see cref="MediaViewModel.Thumbnail"/>; callers must load that first (see
    /// <see cref="MediaViewModel.LoadThumbnailAsync"/>). For folders, this loads the custom
    /// title and poster artwork.
    /// </summary>
    /// <param name="force">
    /// Reload even when this item's folder artwork was already resolved. Used after the user edits
    /// the folder, which is the only time the stored values can have changed under us.
    /// </param>
    [DynamicWindowsRuntimeCast(typeof(StorageFolder))]
    [DynamicWindowsRuntimeCast(typeof(StorageFile))]
    public async Task LoadArtworkAsync(bool force = false)
    {
        if (StorageItem is StorageFile)
        {
            Thumbnail = Media?.Thumbnail;
            return;
        }

        if (StorageItem is not StorageFolder folder || string.IsNullOrEmpty(folder.Path)) return;

        // Container realization fires on every scroll-back, and resolving folder artwork costs a
        // database read plus filesystem scans. Same early-out as MediaViewModel.LoadThumbnailAsync,
        // widened to a flag so a folder that resolved to no artwork is not re-resolved either.
        if (_folderArtworkLoaded && !force) return;

        try
        {
            FolderMetadataDto? metadata = await _databaseService.LoadFolderMetadataAsync(folder.Path);
            DisplayName = metadata?.CustomTitle is { Length: > 0 } title ? title : Name;

            // Hand over the row just read; the service would otherwise read it again.
            string? posterFile = await _artworkService.GetPosterFileNameAsync(folder, metadata);
            _folderArtworkLoaded = true;
            if (posterFile is not { Length: > 0 })
            {
                Thumbnail = null;
                return;
            }

            // What is drawn can differ from the stored poster: portrait artwork is drawn from a
            // derived wide card that shows it whole instead of centre-cropped. The service falls
            // back to the poster itself when it cannot build one, so this never costs the artwork.
            // No ConfigureAwait: Thumbnail is bound, and must be set back on the thread it always was.
            string displayFile = await _artworkService.GetDisplayFileNameAsync(posterFile) ?? posterFile;
            bool isCard = !string.Equals(displayFile, posterFile, StringComparison.OrdinalIgnoreCase);

            var uri = new Uri($"ms-appdata:///local/{ArtworkService.ArtworkFolderName}/{displayFile}");
            var image = new BitmapImage(uri) { DecodePixelWidth = isCard ? CardDecodePixelWidth : PosterDecodePixelWidth };

            // A poster file can still go missing or turn unreadable between the check and the
            // decode. Dropping the failed image restores the folder glyph rather than leaving a
            // blank tile; the stored choice in the database is untouched either way.
            image.ImageFailed += OnThumbnailFailed;
            Thumbnail = image;
        }
        catch (Exception e)
        {
            _logger.LogError(e, "Failed to load folder artwork for '{Path}'.", Path);
        }
    }

    /// <summary>
    /// Loads aggregate statistics (episode count, total duration, unwatched count) for this
    /// item. No-op for files; the caption for files comes from <see cref="UpdateCaptionAsync"/>.
    /// </summary>
    [DynamicWindowsRuntimeCast(typeof(StorageFolder))]
    public async Task LoadFolderStatsAsync()
    {
        if (StorageItem is not StorageFolder folder || string.IsNullOrEmpty(folder.Path)) return;

        // Container realization fires on every scroll-back. Same early-out as LoadArtworkAsync,
        // widened to a flag so a folder whose stats resolved to nothing is not re-resolved either.
        // This does not defeat IFolderStatsService.Invalidate(): every invalidation (see
        // FolderViewPageViewModel.Receive(RefreshFolderMessage)) is paired with a full items
        // rebuild that hands out brand-new StorageItemViewModel instances, so no instance keeps
        // this flag latched true across an invalidation - there is nothing to force-refresh here.
        if (_folderStatsLoaded) return;

        try
        {
            Stats = await _folderStatsService.GetAsync(folder.Path);
            _folderStatsLoaded = true;
        }
        catch (Exception e)
        {
            _logger.LogError(e, "Failed to load folder stats for '{Path}'.", Path);
        }
    }

    [DynamicWindowsRuntimeCast(typeof(BitmapImage))]
    private void OnThumbnailFailed(object sender, ExceptionRoutedEventArgs e)
    {
        if (sender is BitmapImage image)
        {
            image.ImageFailed -= OnThumbnailFailed;
            if (ReferenceEquals(Thumbnail, image)) Thumbnail = null;
        }

        _logger.LogWarning("Folder artwork failed to load for '{Path}': {Message}.", Path, e.ErrorMessage);
    }
}
