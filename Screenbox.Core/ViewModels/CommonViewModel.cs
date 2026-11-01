using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using CommunityToolkit.Mvvm.Messaging.Messages;
using Microsoft.Extensions.Logging;
using Screenbox.Core.Contexts;
using Screenbox.Core.Enums;
using Screenbox.Core.Helpers;
using Screenbox.Core.Messages;
using Screenbox.Core.Services;
using Windows.Storage;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;

namespace Screenbox.Core.ViewModels;

public sealed partial class CommonViewModel : ObservableRecipient,
    IRecipient<SettingsChangedMessage>,
    IRecipient<PropertyChangedMessage<NavigationViewDisplayMode>>,
    IRecipient<PropertyChangedMessage<PlayerVisibilityState>>
{
    public Dictionary<Type, string> NavigationStates { get; }

    public bool IsAdvancedModeEnabled => _settingsService.AdvancedMode;

    [ObservableProperty] public partial NavigationViewDisplayMode NavigationViewDisplayMode { get; set; }
    [ObservableProperty] public partial Thickness ScrollBarMargin { get; set; }
    [ObservableProperty] public partial Thickness FooterBottomPaddingMargin { get; set; }
    [ObservableProperty] public partial double FooterBottomPaddingHeight { get; set; }

    private readonly INavigationService _navigationService;
    private readonly IFilesService _filesService;
    private readonly ISettingsService _settingsService;
    private readonly IPlaylistService _playlistService;
    private readonly PlaylistsContext _playlistsContext;
    private readonly WatchStateContext _watchStateContext;
    private readonly ILogger<CommonViewModel> _logger;
    private readonly Dictionary<string, object> _pageStates;

    public CommonViewModel(INavigationService navigationService,
        IFilesService filesService,
        ISettingsService settingsService,
        IPlaylistService playlistService,
        PlaylistsContext playlistsContext,
        WatchStateContext watchStateContext,
        ILogger<CommonViewModel> logger)
    {
        _navigationService = navigationService;
        _filesService = filesService;
        _settingsService = settingsService;
        _playlistService = playlistService;
        _playlistsContext = playlistsContext;
        _watchStateContext = watchStateContext;
        _logger = logger;
        NavigationViewDisplayMode = Messenger.Send<NavigationViewDisplayModeRequestMessage>();
        NavigationStates = new Dictionary<Type, string>();
        _pageStates = new Dictionary<string, object>();

        Messenger.Register<SettingsChangedMessage>(this);
        Messenger.Register<PropertyChangedMessage<NavigationViewDisplayMode>>(this);
        Messenger.Register<PropertyChangedMessage<PlayerVisibilityState>>(this);
    }

    [DynamicWindowsRuntimeCast(typeof(Frame))]
    public void Receive(SettingsChangedMessage message)
    {
        if (message.SettingsName == nameof(SettingsPageViewModel.Theme) &&
            Window.Current.Content is Frame rootFrame)
        {
            rootFrame.RequestedTheme = _settingsService.Theme.ToElementTheme();
        }
    }

    public void Receive(PropertyChangedMessage<NavigationViewDisplayMode> message)
    {
        this.NavigationViewDisplayMode = message.NewValue;
    }

    public void Receive(PropertyChangedMessage<PlayerVisibilityState> message)
    {
        ScrollBarMargin = message.NewValue == PlayerVisibilityState.Hidden
            ? new Thickness(0)
            : (Thickness)Application.Current.Resources["ContentPageScrollBarMargin"];

        FooterBottomPaddingMargin = message.NewValue == PlayerVisibilityState.Hidden
            ? new Thickness(0)
            : (Thickness)Application.Current.Resources["ContentPageBottomMargin"];

        FooterBottomPaddingHeight = message.NewValue == PlayerVisibilityState.Hidden
            ? 0
            : (double)Application.Current.Resources["ContentPageBottomPaddingHeight"];
    }

    public void SavePageState(object state, string pageTypeName, int backStackDepth)
    {
        _pageStates[pageTypeName + backStackDepth] = state;
    }

    public bool TryGetPageState(string pageTypeName, int backStackDepth, out object? state)
    {
        return _pageStates.TryGetValue(pageTypeName + backStackDepth, out state);
    }

    [RelayCommand]
    private void PlayNext(MediaViewModel media)
    {
        Messenger.SendPlayNext(media);
    }

    [RelayCommand]
    private void AddToQueue(MediaViewModel media)
    {
        Messenger.SendAddToQueue(media);
    }

    /// <summary>
    /// Marks the item watched, or unwatched when it already is. Every page's Mark as watched /
    /// unwatched menu item binds here, and the work is done by
    /// <see cref="WatchStateContext.SetWatchedAsync"/>, so the reaction to the change (the item, the
    /// folder counts, Continue Watching) lives in one place instead of in each page's view model.
    /// </summary>
    /// <param name="media">The item the menu was opened on.</param>
    [RelayCommand]
    private async Task ToggleWatchedAsync(MediaViewModel? media)
    {
        if (media is null) return;

        try
        {
            // The same property the menu item's label is bound to, so the two cannot disagree.
            await _watchStateContext.SetWatchedAsync(media, !media.IsWatched);
        }
        catch (Exception e)
        {
            // Raised from a menu item, where nothing awaits this: nothing may escape.
            _logger.LogError(e, "Failed to change the watched state of '{Location}'.", media.Location);
        }
    }

    [RelayCommand]
    private void OpenAlbum(AlbumViewModel? album)
    {
        if (album == null) return;
        _navigationService.Navigate(typeof(AlbumDetailsPageViewModel),
            new NavigationMetadata(typeof(MusicPageViewModel), album));
    }

    [RelayCommand]
    private void OpenArtist(ArtistViewModel? artist)
    {
        if (artist == null) return;
        _navigationService.Navigate(typeof(ArtistDetailsPageViewModel),
            new NavigationMetadata(typeof(MusicPageViewModel), artist));
    }

    [RelayCommand]
    private void OpenPlaylist(PlaylistViewModel? playlist)
    {
        if (playlist == null) return;
        _navigationService.Navigate(typeof(PlaylistDetailsPageViewModel),
            new NavigationMetadata(typeof(PlaylistsPageViewModel), playlist));
    }

    /// <summary>
    /// Opens a file picker for the user to select one or more media files to play.
    /// Sends a <see cref="Core.Messages.FailedToOpenFilesNotificationMessage"/> on failure.
    /// </summary>
    [RelayCommand]
    private async Task OpenFilesAsync()
    {
        try
        {
            IReadOnlyList<StorageFile>? files = await _filesService.PickMultipleFilesAsync();
            if (files == null || files.Count == 0) return;
            Messenger.Send(new PlayMediaMessage(files));
        }
        catch (Exception e)
        {
            Messenger.Send(new FailedToOpenFilesNotificationMessage(e.Message));
        }
    }

}
