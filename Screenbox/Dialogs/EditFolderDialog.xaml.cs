using System;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.DependencyInjection;
using Microsoft.Extensions.Logging;
using Screenbox.Core.Helpers;
using Screenbox.Core.Services;
using Screenbox.Helpers;
using Windows.Storage;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Media.Imaging;

namespace Screenbox.Dialogs;

/// <summary>The outcome of editing a folder's display title and poster.</summary>
/// <param name="Title">The trimmed display title. Empty means clear the custom title.</param>
/// <param name="Poster">The chosen image, or null when no new image was chosen.</param>
/// <param name="RemovePoster">
/// True when the user asked to remove the folder's custom image. Never true together with a
/// <paramref name="Poster"/>: choosing an image takes the request back.
/// </param>
public sealed record EditFolderResult(string Title, StorageFile? Poster, bool RemovePoster);

public sealed partial class EditFolderDialog : ContentDialog
{
    private readonly IFilesService _filesService;
    private readonly ILogger<EditFolderDialog> _logger;
    private readonly bool _hasCustomImage;
    private StorageFile? _chosenImage;
    private bool _removeImage;

    /// <param name="currentTitle">The title to pre-fill.</param>
    /// <param name="hasCustomImage">
    /// Whether the folder currently has a poster the user chose. The remove button is shown only
    /// then: any other poster is not the user's to remove, and there is nothing to remove otherwise.
    /// </param>
    [DynamicWindowsRuntimeCast(typeof(FrameworkElement))]
    public EditFolderDialog(string currentTitle, bool hasCustomImage)
    {
        _hasCustomImage = hasCustomImage;
        _filesService = Ioc.Default.GetRequiredService<IFilesService>();
        _logger = DefaultLogging.CreateLogger<EditFolderDialog>();
        this.DefaultStyleKey = typeof(ContentDialog);
        this.InitializeComponent();
        FlowDirection = GlobalizationHelper.GetFlowDirection();
        RequestedTheme = ((FrameworkElement)Window.Current.Content).RequestedTheme;
        TitleTextBox.Text = currentTitle;
        TitleTextBox.SelectAll();
        RemoveImageButton.Visibility = hasCustomImage ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>Shows the dialog and returns the result, or null when cancelled.</summary>
    public async Task<EditFolderResult?> GetResultAsync()
    {
        ContentDialogResult result = await ShowAsync();
        if (result != ContentDialogResult.Primary) return null;
        return new EditFolderResult(TitleTextBox.Text.Trim(), _chosenImage, _removeImage);
    }

    // Applied on Save like every other change in the dialog, so Cancel still leaves the folder
    // exactly as it was. Removing also drops any image chosen earlier in this same dialog: the
    // user now wants none.
    private void RemoveImageButton_OnClick(object sender, RoutedEventArgs e)
    {
        _removeImage = true;
        _chosenImage = null;
        PreviewImage.Source = null;
        PreviewImage.Visibility = Visibility.Collapsed;
        RemoveImageButton.Visibility = Visibility.Collapsed;
        RemoveImageNote.Visibility = Visibility.Visible;
    }

    // An unhandled exception in an async void event handler terminates the app, and the file picker
    // throws for reasons outside this dialog's control: another picker already open, the dialog
    // dismissed mid-pick, or a file that cannot be read. Failing to pick an image must only leave
    // the poster unchanged.
    private async void ChooseImageButton_OnClick(object sender, RoutedEventArgs e)
    {
        try
        {
            // Reuses the shared picker so the dialog inherits the app's thumbnail view mode and
            // suggested start location instead of hand-rolling a picker that has neither.
            StorageFile? file = await _filesService.PickFileAsync(".jpg", ".jpeg", ".png");
            if (file is null) return;

            // Choosing an image replaces the old one anyway, so it takes a pending removal back.
            _chosenImage = file;
            _removeImage = false;
            RemoveImageNote.Visibility = Visibility.Collapsed;
            RemoveImageButton.Visibility = _hasCustomImage ? Visibility.Visible : Visibility.Collapsed;

            using Windows.Storage.Streams.IRandomAccessStream stream =
                await file.OpenAsync(FileAccessMode.Read);
            var bitmap = new BitmapImage();
            await bitmap.SetSourceAsync(stream);
            PreviewImage.Source = bitmap;
            PreviewImage.Visibility = Visibility.Visible;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to choose a folder poster image.");
        }
    }
}
