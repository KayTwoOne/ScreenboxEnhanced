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
/// <param name="Poster">The chosen image, or null when the poster is unchanged.</param>
public sealed record EditFolderResult(string Title, StorageFile? Poster);

public sealed partial class EditFolderDialog : ContentDialog
{
    private readonly IFilesService _filesService;
    private readonly ILogger<EditFolderDialog> _logger;
    private StorageFile? _chosenImage;

    [DynamicWindowsRuntimeCast(typeof(FrameworkElement))]
    public EditFolderDialog(string currentTitle)
    {
        _filesService = Ioc.Default.GetRequiredService<IFilesService>();
        _logger = DefaultLogging.CreateLogger<EditFolderDialog>();
        this.DefaultStyleKey = typeof(ContentDialog);
        this.InitializeComponent();
        FlowDirection = GlobalizationHelper.GetFlowDirection();
        RequestedTheme = ((FrameworkElement)Window.Current.Content).RequestedTheme;
        TitleTextBox.Text = currentTitle;
        TitleTextBox.SelectAll();
    }

    /// <summary>Shows the dialog and returns the result, or null when cancelled.</summary>
    public async Task<EditFolderResult?> GetResultAsync()
    {
        ContentDialogResult result = await ShowAsync();
        if (result != ContentDialogResult.Primary) return null;
        return new EditFolderResult(TitleTextBox.Text.Trim(), _chosenImage);
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

            _chosenImage = file;

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
