using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using CommunityToolkit.WinUI;
using Microsoft.Extensions.Logging;
using Screenbox.Core.Contexts;
using Screenbox.Core.Coordinators;
using Screenbox.Core.Enums;
using Screenbox.Core.Helpers;
using Screenbox.Core.Messages;
using Screenbox.Core.Models;
using Screenbox.Core.Services;
using Windows.Devices.Enumeration;
using Windows.Globalization;
using Windows.Storage;
using Windows.Storage.AccessCache;
using Windows.System;

namespace Screenbox.Core.ViewModels;

public sealed partial class SettingsPageViewModel : ObservableRecipient
{
    [ObservableProperty] public partial int PlayerAutoResize { get; set; }
    [ObservableProperty] public partial int PlayerRewindStep { get; set; }
    [ObservableProperty] public partial int PlayerFastForwardStep { get; set; }
    [ObservableProperty] public partial PlaybackActionKind PlayerGestureTap { get; set; }
    [ObservableProperty] public partial PlaybackActionKind PlayerGestureSwipeUp { get; set; }
    [ObservableProperty] public partial PlaybackActionKind PlayerGestureSwipeDown { get; set; }
    [ObservableProperty] public partial PlaybackActionKind PlayerGestureSwipeLeft { get; set; }
    [ObservableProperty] public partial PlaybackActionKind PlayerGestureSwipeRight { get; set; }
    [ObservableProperty] public partial bool PlayerGestureSlideVertical { get; set; }
    [ObservableProperty] public partial bool PlayerGestureSlideHorizontal { get; set; }
    [ObservableProperty] public partial bool PlayerGesturePressAndHold { get; set; }
    [ObservableProperty] public partial bool PlayerShowControls { get; set; }
    [ObservableProperty] public partial bool PlayerShowChapters { get; set; }
    [ObservableProperty] public partial int PlayerControlsHideDelay { get; set; }
    [ObservableProperty] public partial int VolumeBoost { get; set; }
    [ObservableProperty] public partial bool UseIndexer { get; set; }
    [ObservableProperty] public partial bool ShowRecent { get; set; }
    [ObservableProperty] public partial int Theme { get; set; }
    [ObservableProperty] public partial bool EnqueueAllFilesInFolder { get; set; }
    [ObservableProperty] public partial bool RestorePlaybackPosition { get; set; }
    [ObservableProperty] public partial bool SearchRemovableStorage { get; set; }
    [ObservableProperty] public partial bool AdvancedMode { get; set; }
    [ObservableProperty] public partial int VideoUpscaling { get; set; }
    [ObservableProperty] public partial bool UseMultipleInstances { get; set; }
    [ObservableProperty] public partial string GlobalArguments { get; set; }
    [ObservableProperty] public partial bool IsRelaunchRequired { get; set; }
    [ObservableProperty] public partial int SelectedLanguage { get; set; }
    [ObservableProperty] public partial bool PersistPlaybackPosition { get; set; }
    [ObservableProperty] public partial StorageFolder? FrameCaptureFolder { get; set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ScanLibraryMetadataCommand))]
    public partial bool MetadataEnabled { get; set; }

    [ObservableProperty] public partial bool MetadataUseAniList { get; set; }
    [ObservableProperty] public partial bool MetadataUseJikan { get; set; }
    [ObservableProperty] public partial bool MetadataUseTmdb { get; set; }

    // Verified against this project's SDK target: unlike classic UWP, PasswordBox.Password here is
    // a real DependencyProperty (PasswordProperty), so it binds directly via {x:Bind, Mode=TwoWay}
    // like every other control on this page - no PasswordChanged code-behind handler needed.
    [ObservableProperty] public partial string TmdbApiKey { get; set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ScanLibraryMetadataCommand))]
    public partial bool IsScanningLibraryMetadata { get; set; }

    [ObservableProperty] public partial int MetadataScanProgress { get; set; }

    public ObservableCollection<StorageFolder> MusicLocations { get; }

    public ObservableCollection<StorageFolder> VideoLocations { get; }

    public ObservableCollection<StorageFolder> RemovableStorageFolders { get; }

    public List<LanguageInfo> AvailableLanguages { get; }

    public IReadOnlyList<int> PlayerSeekStepOptions { get; } = new[] { 5, 10, 15, 20, 30 };

    public IReadOnlyList<int> PlayerControlsHideDelayOptions { get; } = new[] { 1, 2, 3, 4, 5 };

    public IReadOnlyList<PlaybackActionKind> GestureOptions { get; } = Enum.GetValues<PlaybackActionKind>();

    private readonly ISettingsService _settingsService;
    private readonly IFilesService _filesService;
    private readonly LibraryContext _libraryContext;
    private readonly RecentContext _recentContext;
    private readonly ILibraryCoordinator _libraryCoordinator;
    private readonly IMetadataScrapeCoordinator _metadataScrapeCoordinator;
    private readonly IArtworkService _artworkService;
    private readonly IDatabaseService _databaseService;
    private readonly DispatcherQueue _dispatcherQueue;
    private readonly DispatcherQueueTimer _storageDeviceRefreshTimer;
    private readonly DeviceWatcher? _portableStorageDeviceWatcher;
    private readonly IPlaybackProgressTracker _playbackProgressTracker;
    private readonly ILogger<SettingsPageViewModel> _logger;
    private static InitialValues? _initialValues;
    private StorageLibrary? _videosLibrary;
    private StorageLibrary? _musicLibrary;
    private CancellationTokenSource? _metadataScanCts;

    private record InitialValues(string GlobalArguments, bool AdvancedMode, int VideoUpscaling, int Language)
    {
        public string GlobalArguments { get; } = GlobalArguments;
        public bool AdvancedMode { get; } = AdvancedMode;
        public int VideoUpscaling { get; } = VideoUpscaling;
        public int Language { get; } = Language;
    }

    public SettingsPageViewModel(
        ISettingsService settingsService,
        IFilesService filesService,
        LibraryContext libraryContext,
        RecentContext recentContext,
        ILibraryCoordinator libraryCoordinator,
        IMetadataScrapeCoordinator metadataScrapeCoordinator,
        IArtworkService artworkService,
        IDatabaseService databaseService,
        IPlaybackProgressTracker playbackProgressTracker,
        ILogger<SettingsPageViewModel> logger)
    {
        _settingsService = settingsService;
        _filesService = filesService;
        _libraryContext = libraryContext;
        _recentContext = recentContext;
        _libraryCoordinator = libraryCoordinator;
        _metadataScrapeCoordinator = metadataScrapeCoordinator;
        _artworkService = artworkService;
        _databaseService = databaseService;
        _playbackProgressTracker = playbackProgressTracker;
        _logger = logger;
        _dispatcherQueue = DispatcherQueue.GetForCurrentThread();
        _storageDeviceRefreshTimer = _dispatcherQueue.CreateTimer();
        MusicLocations = new ObservableCollection<StorageFolder>();
        VideoLocations = new ObservableCollection<StorageFolder>();
        RemovableStorageFolders = new ObservableCollection<StorageFolder>();

        IEnumerable<Language> manifestLanguages = ApplicationLanguages.ManifestLanguages.Select(l => new Language(l));
        AvailableLanguages = manifestLanguages.Select(l => new LanguageInfo(l.NativeName, l.LanguageTag))
            .OrderBy(l => l.NativeName, StringComparer.CurrentCultureIgnoreCase)
            .Prepend(new LanguageInfo(string.Empty, string.Empty))
            .ToList();

        if (SystemInformation.IsXbox)
        {
            _portableStorageDeviceWatcher = DeviceInformation.CreateWatcher(DeviceClass.PortableStorageDevice);
            _portableStorageDeviceWatcher.Updated += OnPortableStorageDeviceChanged;
            _portableStorageDeviceWatcher.Removed += OnPortableStorageDeviceChanged;
            _portableStorageDeviceWatcher.Start();
        }

        // Load values
        PlayerAutoResize = (int)_settingsService.PlayerAutoResize;
        PlayerRewindStep = _settingsService.PlayerRewindStep;
        PlayerFastForwardStep = _settingsService.PlayerFastForwardStep;
        PlayerGestureTap = _settingsService.PlayerGestureTap;
        PlayerGestureSwipeUp = _settingsService.PlayerGestureSwipeUp;
        PlayerGestureSwipeDown = _settingsService.PlayerGestureSwipeDown;
        PlayerGestureSwipeLeft = _settingsService.PlayerGestureSwipeLeft;
        PlayerGestureSwipeRight = _settingsService.PlayerGestureSwipeRight;
        PlayerGestureSlideVertical = _settingsService.PlayerGestureSlideVertical;
        PlayerGestureSlideHorizontal = _settingsService.PlayerGestureSlideHorizontal;
        PlayerGesturePressAndHold = _settingsService.PlayerGesturePressAndHold;
        PlayerShowControls = _settingsService.PlayerShowControls;
        PlayerShowChapters = _settingsService.PlayerShowChapters;
        PlayerControlsHideDelay = _settingsService.PlayerControlsHideDelay;
        UseIndexer = _settingsService.UseIndexer;
        ShowRecent = _settingsService.ShowRecent;
        PersistPlaybackPosition = _settingsService.PersistPlaybackPosition;
        Theme = ((int)_settingsService.Theme + 2) % 3;
        EnqueueAllFilesInFolder = _settingsService.EnqueueAllFilesInFolder;
        RestorePlaybackPosition = _settingsService.RestorePlaybackPosition;
        SearchRemovableStorage = _settingsService.SearchRemovableStorage;
        AdvancedMode = _settingsService.AdvancedMode;
        UseMultipleInstances = _settingsService.UseMultipleInstances;
        VideoUpscaling = (int)_settingsService.VideoUpscale;
        GlobalArguments = _settingsService.GlobalArguments;
        MetadataEnabled = _settingsService.MetadataEnabled;
        MetadataUseAniList = _settingsService.MetadataUseAniList;
        MetadataUseJikan = _settingsService.MetadataUseJikan;
        MetadataUseTmdb = _settingsService.MetadataUseTmdb;
        TmdbApiKey = _settingsService.TmdbApiKey;
        int maxVolume = _settingsService.MaxVolume;
        VolumeBoost = maxVolume switch
        {
            >= 200 => 3,
            >= 150 => 2,
            >= 125 => 1,
            _ => 0
        };

        string currentLanguage = ApplicationLanguages.PrimaryLanguageOverride;
        SelectedLanguage = AvailableLanguages.FindIndex(l => l.LanguageTag.Equals(currentLanguage));

        // Setting initial values for relaunch check
        _initialValues ??= new InitialValues(GlobalArguments, AdvancedMode, VideoUpscaling, SelectedLanguage);
        CheckForRelaunch();
    }

    partial void OnThemeChanged(int value)
    {
        // The recommended theme option order is Light, Dark, System
        // So we need to map the value to the correct ThemeOption
        _settingsService.Theme = (ThemeOption)((value + 1) % 3);
        Messenger.Send(new SettingsChangedMessage(nameof(Theme), typeof(SettingsPageViewModel)));
    }

    partial void OnSelectedLanguageChanged(int value)
    {
        if (value <= 0)
        {
            ApplicationLanguages.PrimaryLanguageOverride = string.Empty;
            CheckForRelaunch();
            return;
        }

        // If the value is out of bounds, do nothing
        if (value >= AvailableLanguages.Count) return;
        ApplicationLanguages.PrimaryLanguageOverride = AvailableLanguages[value].LanguageTag;
        CheckForRelaunch();
    }

    partial void OnPlayerAutoResizeChanged(int value)
    {
        _settingsService.PlayerAutoResize = (PlayerAutoResizeOption)value;
        Messenger.Send(new SettingsChangedMessage(nameof(PlayerAutoResize), typeof(SettingsPageViewModel)));
    }

    partial void OnPlayerRewindStepChanged(int value)
    {
        _settingsService.PlayerRewindStep = value;
        Messenger.Send(new SettingsChangedMessage(nameof(PlayerRewindStep), typeof(SettingsPageViewModel)));
    }

    partial void OnPlayerFastForwardStepChanged(int value)
    {
        _settingsService.PlayerFastForwardStep = value;
        Messenger.Send(new SettingsChangedMessage(nameof(PlayerFastForwardStep), typeof(SettingsPageViewModel)));
    }

    partial void OnPlayerGestureTapChanged(PlaybackActionKind value)
    {
        _settingsService.PlayerGestureTap = value;
        Messenger.Send(new SettingsChangedMessage(nameof(PlayerGestureTap), typeof(SettingsPageViewModel)));
    }

    partial void OnPlayerGestureSwipeUpChanged(PlaybackActionKind value)
    {
        _settingsService.PlayerGestureSwipeUp = value;
        Messenger.Send(new SettingsChangedMessage(nameof(PlayerGestureSwipeUp), typeof(SettingsPageViewModel)));
    }

    partial void OnPlayerGestureSwipeDownChanged(PlaybackActionKind value)
    {
        _settingsService.PlayerGestureSwipeDown = value;
        Messenger.Send(new SettingsChangedMessage(nameof(PlayerGestureSwipeDown), typeof(SettingsPageViewModel)));
    }

    partial void OnPlayerGestureSwipeLeftChanged(PlaybackActionKind value)
    {
        _settingsService.PlayerGestureSwipeLeft = value;
        Messenger.Send(new SettingsChangedMessage(nameof(PlayerGestureSwipeLeft), typeof(SettingsPageViewModel)));
    }

    partial void OnPlayerGestureSwipeRightChanged(PlaybackActionKind value)
    {
        _settingsService.PlayerGestureSwipeRight = value;
        Messenger.Send(new SettingsChangedMessage(nameof(PlayerGestureSwipeRight), typeof(SettingsPageViewModel)));
    }

    partial void OnPlayerGestureSlideVerticalChanged(bool value)
    {
        _settingsService.PlayerGestureSlideVertical = value;
        Messenger.Send(new SettingsChangedMessage(nameof(PlayerGestureSlideVertical), typeof(SettingsPageViewModel)));
    }

    partial void OnPlayerGestureSlideHorizontalChanged(bool value)
    {
        _settingsService.PlayerGestureSlideHorizontal = value;
        Messenger.Send(new SettingsChangedMessage(nameof(PlayerGestureSlideHorizontal), typeof(SettingsPageViewModel)));
    }

    partial void OnPlayerGesturePressAndHoldChanged(bool value)
    {
        _settingsService.PlayerGesturePressAndHold = value;
        Messenger.Send(new SettingsChangedMessage(nameof(PlayerGesturePressAndHold), typeof(SettingsPageViewModel)));
    }

    partial void OnPlayerShowControlsChanged(bool value)
    {
        _settingsService.PlayerShowControls = value;
        Messenger.Send(new SettingsChangedMessage(nameof(PlayerShowControls), typeof(SettingsPageViewModel)));
    }

    partial void OnPlayerShowChaptersChanged(bool value)
    {
        _settingsService.PlayerShowChapters = value;
        Messenger.Send(new SettingsChangedMessage(nameof(PlayerShowChapters), typeof(SettingsPageViewModel)));
    }

    partial void OnPlayerControlsHideDelayChanged(int value)
    {
        _settingsService.PlayerControlsHideDelay = value;
        Messenger.Send(new SettingsChangedMessage(nameof(PlayerControlsHideDelay), typeof(SettingsPageViewModel)));
    }

    partial void OnUseIndexerChanged(bool value)
    {
        _settingsService.UseIndexer = value;
        Messenger.Send(new SettingsChangedMessage(nameof(UseIndexer), typeof(SettingsPageViewModel)));

        _dispatcherQueue.TryEnqueue(RefreshWatchersAsync);

        async void RefreshWatchersAsync()
        {
            try
            {
                await _libraryCoordinator.RefreshWatchersAsync();
            }
            catch (Exception e)
            {
                _logger.LogError(e, "Failed to refresh library watchers after updating the indexer setting.");
            }
        }
    }

    partial void OnShowRecentChanged(bool value)
    {
        _settingsService.ShowRecent = value;
        if (!value)
        {
            ClearRecentContext();
        }

        Messenger.Send(new SettingsChangedMessage(nameof(ShowRecent), typeof(SettingsPageViewModel)));
    }

    partial void OnEnqueueAllFilesInFolderChanged(bool value)
    {
        _settingsService.EnqueueAllFilesInFolder = value;
        Messenger.Send(new SettingsChangedMessage(nameof(EnqueueAllFilesInFolder), typeof(SettingsPageViewModel)));
    }

    partial void OnRestorePlaybackPositionChanged(bool value)
    {
        _settingsService.RestorePlaybackPosition = value;
        Messenger.Send(new SettingsChangedMessage(nameof(RestorePlaybackPosition), typeof(SettingsPageViewModel)));
    }

    async partial void OnSearchRemovableStorageChanged(bool value)
    {
        _settingsService.SearchRemovableStorage = value;
        Messenger.Send(new SettingsChangedMessage(nameof(SearchRemovableStorage), typeof(SettingsPageViewModel)));

        if (SystemInformation.IsXbox && RemovableStorageFolders.Count > 0)
        {
            await RefreshLibrariesAsync();
        }
    }

    partial void OnVolumeBoostChanged(int value)
    {
        _settingsService.MaxVolume = value switch
        {
            3 => 200,
            2 => 150,
            1 => 125,
            _ => 100
        };
        Messenger.Send(new SettingsChangedMessage(nameof(VolumeBoost), typeof(SettingsPageViewModel)));
    }

    partial void OnAdvancedModeChanged(bool value)
    {
        _settingsService.AdvancedMode = value;
        Messenger.Send(new SettingsChangedMessage(nameof(AdvancedMode), typeof(SettingsPageViewModel)));
        CheckForRelaunch();
    }

    partial void OnVideoUpscalingChanged(int value)
    {
        _settingsService.VideoUpscale = (VideoUpscaleOption)value;
        Messenger.Send(new SettingsChangedMessage(nameof(VideoUpscaling), typeof(SettingsPageViewModel)));
        CheckForRelaunch();
    }

    partial void OnUseMultipleInstancesChanged(bool value)
    {
        _settingsService.UseMultipleInstances = value;
        Messenger.Send(new SettingsChangedMessage(nameof(UseMultipleInstances), typeof(SettingsPageViewModel)));
    }

    partial void OnGlobalArgumentsChanged(string value)
    {
        // No need to broadcast SettingsChangedMessage for this option
        if (value != _settingsService.GlobalArguments)
        {
            _settingsService.GlobalArguments = value;
        }

        GlobalArguments = _settingsService.GlobalArguments;
        CheckForRelaunch();
    }

    partial void OnPersistPlaybackPositionChanged(bool value)
    {
        _settingsService.PersistPlaybackPosition = value;
        Messenger.Send(new SettingsChangedMessage(nameof(PersistPlaybackPosition), typeof(SettingsPageViewModel)));
    }

    partial void OnMetadataEnabledChanged(bool value)
    {
        _settingsService.MetadataEnabled = value;
        Messenger.Send(new SettingsChangedMessage(nameof(MetadataEnabled), typeof(SettingsPageViewModel)));
    }

    partial void OnMetadataUseAniListChanged(bool value)
    {
        _settingsService.MetadataUseAniList = value;
        Messenger.Send(new SettingsChangedMessage(nameof(MetadataUseAniList), typeof(SettingsPageViewModel)));
    }

    partial void OnMetadataUseJikanChanged(bool value)
    {
        _settingsService.MetadataUseJikan = value;
        Messenger.Send(new SettingsChangedMessage(nameof(MetadataUseJikan), typeof(SettingsPageViewModel)));
    }

    partial void OnMetadataUseTmdbChanged(bool value)
    {
        _settingsService.MetadataUseTmdb = value;
        Messenger.Send(new SettingsChangedMessage(nameof(MetadataUseTmdb), typeof(SettingsPageViewModel)));
    }

    partial void OnTmdbApiKeyChanged(string value)
    {
        // Stored in unencrypted LocalSettings by design (a read-only TMDB key), but still never
        // logged and never included in an exception message - see SettingsService.TmdbApiKey.
        _settingsService.TmdbApiKey = value;
        Messenger.Send(new SettingsChangedMessage(nameof(TmdbApiKey), typeof(SettingsPageViewModel)));
    }

    [RelayCommand]
    private async Task RefreshLibrariesAsync()
    {
        await Task.WhenAll(RefreshMusicLibrary(), RefreshVideosLibrary());
    }

    [RelayCommand]
    private async Task AddVideosFolderAsync()
    {
        if (_videosLibrary == null) return;
        await _videosLibrary.RequestAddFolderAsync();
    }

    [RelayCommand]
    private async Task RemoveVideosFolderAsync(StorageFolder folder)
    {
        if (_videosLibrary == null) return;
        try
        {
            await _videosLibrary.RequestRemoveFolderAsync(folder);
        }
        catch (Exception)
        {
            // System.Exception: The remote procedure call was cancelled.
            // pass
        }
    }

    [RelayCommand]
    private async Task AddMusicFolderAsync()
    {
        if (_musicLibrary == null) return;
        await _musicLibrary.RequestAddFolderAsync();
    }

    [RelayCommand]
    private async Task RemoveMusicFolderAsync(StorageFolder folder)
    {
        if (_musicLibrary == null) return;
        try
        {
            await _musicLibrary.RequestRemoveFolderAsync(folder);
        }
        catch (Exception)
        {
            // System.Exception: The remote procedure call was cancelled.
            // pass
        }
    }

    [RelayCommand]
    private async Task ChangeFrameCaptureLocationAsync()
    {
        StorageFolder? folder = await _filesService.PickFolderAsync();
        if (folder is null) return;

        // Release the previous entry so the FutureAccessList (capped at 1000 items) doesn't accumulate stale folders.
        string oldToken = _settingsService.FrameCaptureFolderToken;
        if (!string.IsNullOrEmpty(oldToken) && StorageApplicationPermissions.FutureAccessList.ContainsItem(oldToken))
        {
            StorageApplicationPermissions.FutureAccessList.Remove(oldToken);
        }

        string newToken = StorageApplicationPermissions.FutureAccessList.Add(folder, "frame-capture");
        _settingsService.FrameCaptureFolderToken = newToken;
        FrameCaptureFolder = folder;
    }

    [RelayCommand]
    private async Task OpenFrameCaptureFolderAsync()
    {
        try
        {
            await LoadFrameCaptureFolderAsync();
            await Launcher.LaunchFolderAsync(FrameCaptureFolder);
        }
        catch (Exception e)
        {
            _logger.LogError(e, "Failed to open the frame capture folder.");
        }
    }

    [RelayCommand]
    private void ClearRecentHistory()
    {
        ClearRecentContext();
        StorageApplicationPermissions.MostRecentlyUsedList.Clear();
    }

    private void ClearRecentContext()
    {
        _recentContext.Recent.Clear();
        _recentContext.PathToMruMappings.Clear();
        _recentContext.TokenToMediaMappings.Clear();
        _recentContext.IsLoaded = true;
    }

    [RelayCommand]
    private async Task ClearPlaybackPositionHistoryAsync()
    {
        try
        {
            _playbackProgressTracker.ClearAll();
            await _playbackProgressTracker.SaveToDiskAsync();
        }
        catch (Exception)
        {
            // pass
        }
    }

    private bool CanScanLibraryMetadata() => MetadataEnabled && !IsScanningLibraryMetadata;

    [RelayCommand(CanExecute = nameof(CanScanLibraryMetadata))]
    private async Task ScanLibraryMetadataAsync()
    {
        // Nothing from this command may escape to the caller: it is invoked from a Button.Command,
        // and any unhandled exception on that path is unhandled on the UI thread with no crash log.
        IsScanningLibraryMetadata = true;
        MetadataScanProgress = 0;
        _metadataScanCts = new CancellationTokenSource();

        try
        {
            await ScrapeLibraryAndDownloadPostersAsync(
                new Progress<int>(value => MetadataScanProgress = value), _metadataScanCts.Token);
        }
        catch (OperationCanceledException)
        {
            // Navigating away (see OnNavigatedFrom) or app teardown cancelled the scan - nothing to
            // report.
        }
        catch (Exception e)
        {
            _logger.LogError(e, "Online metadata library scan failed.");
        }
        finally
        {
            IsScanningLibraryMetadata = false;
            MetadataScanProgress = 0;
            _metadataScanCts?.Dispose();
            _metadataScanCts = null;
        }
    }

    // Mirrors MetadataScrapeCoordinator's own DefaultInterFolderDelay: AniList documents 90
    // requests/minute plus a stricter burst limiter, Jikan documents 60/minute. This loop cannot
    // see whether a given ScrapeFolderAsync call actually reached a provider (that distinction is
    // internal to the coordinator, which paces its own ScrapeLibraryAsync loop the same way) or
    // short-circuited from cache, so it paces unconditionally rather than risk tripping either
    // limit during a first scan of a fresh cache - at the cost of a rescan running slower than it
    // strictly needs to.
    private static readonly TimeSpan InterFolderDelay = TimeSpan.FromMilliseconds(1500);

    /// <summary>
    /// Scrapes every known library folder and, for the ones that yield a fresh match with a poster
    /// URL, downloads and stores that poster. The poster URL only ever exists on a fresh (uncached)
    /// <see cref="IMetadataScrapeCoordinator.ScrapeFolderAsync"/> result - a cached reconstruction
    /// never carries one - so this loop calls that per-folder method directly rather than
    /// <see cref="IMetadataScrapeCoordinator.ScrapeLibraryAsync"/>, which discards the match. One
    /// folder's failure is logged and does not abort the rest, mirroring the coordinator's own
    /// whole-library loop.
    /// </summary>
    private async Task ScrapeLibraryAndDownloadPostersAsync(IProgress<int> progress, CancellationToken ct)
    {
        List<FolderMetadataDto> folders = await _databaseService.ListFolderMetadataAsync();
        if (folders.Count == 0)
        {
            progress.Report(100);
            return;
        }

        for (int i = 0; i < folders.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            FolderMetadataDto folder = folders[i];
            string folderName = Path.GetFileName(folder.Path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));

            try
            {
                MetadataMatch? match = await _metadataScrapeCoordinator.ScrapeFolderAsync(folder.Path, folderName, ct);
                if (match?.PosterUrl is { Length: > 0 } posterUrl)
                {
                    await _artworkService.SetScrapedPosterAsync(folder.Path, posterUrl, ct);
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception e)
            {
                _logger.LogError(e, "Failed to scrape online metadata for a library folder.");
            }

            progress.Report((int)((i + 1) * 100.0 / folders.Count));

            bool isLastFolder = i == folders.Count - 1;
            if (!isLastFolder)
            {
                await Task.Delay(InterFolderDelay, ct);
            }
        }
    }

    public void OnNavigatedFrom()
    {
        if (SystemInformation.IsXbox)
            _portableStorageDeviceWatcher?.Stop();

        // A scan mid-flight when the user leaves the page should stop rather than keep running
        // (and reporting progress) against a view model nothing is observing anymore.
        _metadataScanCts?.Cancel();
    }

    public async Task LoadLibraryLocations()
    {
        if (_videosLibrary == null)
        {
            _videosLibrary = _libraryContext.VideosStorageLibrary;
            if (_videosLibrary != null)
            {
                _videosLibrary.DefinitionChanged += LibraryOnDefinitionChanged;
            }
        }

        if (_musicLibrary == null)
        {
            _musicLibrary = _libraryContext.MusicStorageLibrary;
            if (_musicLibrary != null)
            {
                _musicLibrary.DefinitionChanged += LibraryOnDefinitionChanged;
            }
        }

        UpdateLibraryLocations();
        await UpdateRemovableStorageFoldersAsync();
    }

    public async Task LoadFrameCaptureFolderAsync()
    {
        try
        {
            FrameCaptureFolder = await _filesService.GetFrameCaptureFolderAsync(_settingsService.FrameCaptureFolderToken);
        }
        catch (Exception e)
        {
            _logger.LogError(e, "Failed to load the frame capture folder.");
        }
    }

    private void LibraryOnDefinitionChanged(StorageLibrary sender, object args)
    {
        _dispatcherQueue.TryEnqueue(UpdateLibraryLocations);
    }

    private void OnPortableStorageDeviceChanged(DeviceWatcher sender, DeviceInformationUpdate args)
    {
        async void RefreshAction() => await UpdateRemovableStorageFoldersAsync();
        _storageDeviceRefreshTimer.Debounce(RefreshAction, TimeSpan.FromMilliseconds(500));
    }

    private void UpdateLibraryLocations()
    {
        if (_videosLibrary != null)
        {
            VideoLocations.Clear();
            foreach (StorageFolder folder in _videosLibrary.Folders)
            {
                VideoLocations.Add(folder);
            }
        }

        if (_musicLibrary != null)
        {
            MusicLocations.Clear();

            foreach (StorageFolder folder in _musicLibrary.Folders)
            {
                MusicLocations.Add(folder);
            }
        }
    }

    private async Task UpdateRemovableStorageFoldersAsync()
    {
        if (SystemInformation.IsXbox)
        {
            RemovableStorageFolders.Clear();
            var accessStatus = await KnownFolders.RequestAccessAsync(KnownFolderId.RemovableDevices);
            if (accessStatus != KnownFoldersAccessStatus.Allowed)
                return;

            foreach (StorageFolder folder in await KnownFolders.RemovableDevices.GetFoldersAsync())
            {
                RemovableStorageFolders.Add(folder);
            }
        }
    }

    private async Task RefreshMusicLibrary()
    {
        try
        {
            await _libraryCoordinator.FetchMusicAsync(false);
        }
        catch (UnauthorizedAccessException)
        {
            Messenger.Send(new RaiseLibraryAccessDeniedNotificationMessage(KnownLibraryId.Music));
        }
        catch (Exception e)
        {
            Messenger.Send(new ErrorMessage(null, e.Message));
            _logger.LogError(e, "Failed to refresh the music library from settings.");
        }
    }

    private async Task RefreshVideosLibrary()
    {
        try
        {
            await _libraryCoordinator.FetchVideosAsync(false);
        }
        catch (UnauthorizedAccessException)
        {
            Messenger.Send(new RaiseLibraryAccessDeniedNotificationMessage(KnownLibraryId.Videos));
        }
        catch (Exception e)
        {
            Messenger.Send(new ErrorMessage(null, e.Message));
            _logger.LogError(e, "Failed to refresh the videos library from settings.");
        }
    }

    private void CheckForRelaunch()
    {
        if (_initialValues == null) return;

        // Check if upscaling mode has been changed
        bool upscalingChanged = _initialValues.VideoUpscaling != VideoUpscaling;

        // Check if app language has been changed
        bool languageChanged = _initialValues.Language != SelectedLanguage;

        // Check if global arguments have been changed
        bool argsChanged = _initialValues.GlobalArguments != _settingsService.GlobalArguments;

        // Check if advanced mode has been changed
        bool modeChanged = _initialValues.AdvancedMode != AdvancedMode;

        // Check if there are any global arguments set
        bool hasArgs = _settingsService.GlobalArguments.Length > 0;

        // Check if advanced mode is on, and if global arguments are set
        bool whenOn = modeChanged && AdvancedMode && hasArgs;

        // Check if advanced mode is off, and if global arguments are set or have been removed
        bool whenOff = modeChanged && !AdvancedMode && ((!hasArgs && argsChanged) || hasArgs);

        // Require relaunch when advanced mode is on and global arguments have been changed
        bool whenOnAndChanged = AdvancedMode && argsChanged;

        // Combine everything
        IsRelaunchRequired = upscalingChanged || languageChanged || whenOn || whenOff || whenOnAndChanged;
    }
}
