using CommunityToolkit.Mvvm.DependencyInjection;
using CommunityToolkit.WinUI;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Xaml.Controls;
using Screenbox.Behaviors;
using Screenbox.Core.Helpers;
using Screenbox.Core.ViewModels;
using Screenbox.Dialogs;
using Windows.Storage;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Navigation;

// The Blank Page item template is documented at https://go.microsoft.com/fwlink/?LinkId=234238

namespace Screenbox.Pages;

/// <summary>
/// An empty page that can be used on its own or navigated to within a Frame.
/// </summary>
public sealed partial class FolderViewPage : Page
{
    internal FolderViewPageViewModel ViewModel => (FolderViewPageViewModel)DataContext;

    internal CommonViewModel Common { get; }

    public Visibility HeaderVisibility { get; private set; }

    private readonly ILogger<FolderViewPage> _logger;
    private double _contentVerticalOffset;
    private ScrollViewer? _scrollViewer;
    private Windows.UI.Xaml.Controls.MenuFlyout? _folderFlyout;

    public FolderViewPage()
    {
        this.InitializeComponent();
        _logger = DefaultLogging.CreateLogger<FolderViewPage>();
        DataContext = Ioc.Default.GetRequiredService<FolderViewPageViewModel>();
        Common = Ioc.Default.GetRequiredService<CommonViewModel>();
        FolderView.ChoosingItemContainer += FolderViewOnChoosingItemContainer;
    }

    private void FolderViewOnChoosingItemContainer(ListViewBase sender, ChoosingItemContainerEventArgs args)
    {
        FolderView.ChoosingItemContainer -= FolderViewOnChoosingItemContainer;
    }

    protected override async void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        HeaderVisibility = e.Parameter is "VideosLibrary" ? Visibility.Collapsed : Visibility.Visible;
        TitleText.Visibility = HeaderVisibility;
        BreadcrumbBar.Visibility = HeaderVisibility;
        if (e.NavigationMode == NavigationMode.Back
            && Common.TryGetPageState(nameof(FolderViewPage), Frame.BackStackDepth, out var state)
            && state is double verticalOffset)
        {
            _contentVerticalOffset = verticalOffset;
        }

        // async void: an exception escaping here ends the process with no crash record. Loading a
        // folder enumerates the file system, which fails when a folder is deleted, a drive is
        // unplugged or access is revoked - that must leave an empty page, not a closed app.
        try
        {
            await ViewModel.OnNavigatedTo(e.Parameter);
            RestoreScrollVerticalOffset();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load the folder view.");
        }
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        base.OnNavigatedFrom(e);
        ViewModel.OnNavigatedFrom();
    }

    private void RestoreScrollVerticalOffset()
    {
        if (_scrollViewer == null) return;
        if (_contentVerticalOffset > 0 && _scrollViewer.VerticalOffset == 0)
        {
            _scrollViewer.ChangeView(null, _contentVerticalOffset, null, true);
        }
    }

    // The file menu comes out of Resources as object and is cast to a WinRT runtime class. Under
    // trimming (release builds publish with AOT) that cast can fail unless its metadata is kept.
    [DynamicWindowsRuntimeCast(typeof(Windows.UI.Xaml.Controls.MenuFlyout))]
    private void FolderView_OnItemContextRequested(ListViewContextBehavior sender, ListViewContextRequestedEventArgs e)
    {
        if (e.Item.Content is not StorageItemViewModel content)
        {
            e.Handled = true;
            return;
        }

        // Swap the flyout before the behavior shows it. Every item in ItemFlyout binds to
        // ContextItem.Media, which is null for a folder, so folders get their own menu instead
        // of the file menu with most of its entries hidden.
        sender.Flyout = content.IsFolder
            ? _folderFlyout ??= BuildFolderFlyout()
            : (Windows.UI.Xaml.Controls.MenuFlyout)Resources["ItemFlyout"];
    }

    /// <summary>
    /// Builds the folder context menu in code rather than as a XAML resource. A MenuFlyout
    /// resource that nothing in the markup references never gets its compiled bindings
    /// connected, so declaring it with x:Bind is not viable here.
    /// </summary>
    private Windows.UI.Xaml.Controls.MenuFlyout BuildFolderFlyout()
    {
        var editItem = new Windows.UI.Xaml.Controls.MenuFlyoutItem
        {
            Text = Strings.Resources.EditFolder
        };
        editItem.Click += EditFolder_OnClick;

        var flyout = new Windows.UI.Xaml.Controls.MenuFlyout();
        flyout.Items.Add(editItem);
        return flyout;
    }

    private void BreadcrumbBar_OnItemClicked(BreadcrumbBar sender, BreadcrumbBarItemClickedEventArgs args)
    {
        ViewModel.OnBreadcrumbBarItemClicked(args.Index);
    }

    private void FolderView_OnLoaded(object sender, RoutedEventArgs e)
    {
        _scrollViewer = FolderView.FindDescendant<ScrollViewer>();
        if (_scrollViewer == null) return;
        _scrollViewer.ViewChanging += ScrollViewerOnViewChanging;
    }

    private void ScrollViewerOnViewChanging(object? sender, ScrollViewerViewChangingEventArgs e)
    {
        Common.SavePageState(e.NextView.VerticalOffset, nameof(FolderViewPage), Frame.BackStackDepth);
    }

    // An exception escaping an async void handler terminates the app with no crash record, and
    // there is plenty here that can throw: ContentDialog.ShowAsync throws when another dialog is
    // already open, and the edit writes to the database and the artwork folder. A failed edit must
    // only leave the folder as it was.
    [DynamicWindowsRuntimeCast(typeof(StorageFolder))]
    private async void EditFolder_OnClick(object sender, RoutedEventArgs e)
    {
        try
        {
            StorageItemViewModel? item = ViewModel.ContextItem;
            if (item?.StorageItem is not StorageFolder folder) return;

            // The dialog offers to remove a custom image only if there is one, so it has to be told.
            bool hasCustomImage = await ViewModel.HasManualPosterAsync(folder);
            var dialog = new EditFolderDialog(item.DisplayName, hasCustomImage);
            EditFolderResult? result = await dialog.GetResultAsync();
            if (result is null) return;

            await ViewModel.ApplyFolderEditAsync(folder, result.Title, result.Poster, result.RemovePoster);

            // The stored title and poster just changed, so bypass the per-item early-out. This also
            // re-resolves artwork after a removal, so the folder falls back to automatic artwork.
            await item.LoadArtworkAsync(force: true);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to edit the folder.");
        }
    }
}
