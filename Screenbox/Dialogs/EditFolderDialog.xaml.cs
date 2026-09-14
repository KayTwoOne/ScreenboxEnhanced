using System.Threading.Tasks;
using Screenbox.Helpers;
using Windows.Storage;
using Windows.Storage.Pickers;
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
    private StorageFile? _chosenImage;

    [DynamicWindowsRuntimeCast(typeof(FrameworkElement))]
    public EditFolderDialog(string currentTitle)
    {
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

    private async void ChooseImageButton_OnClick(object sender, RoutedEventArgs e)
    {
        var picker = new FileOpenPicker { ViewMode = PickerViewMode.Thumbnail };
        picker.FileTypeFilter.Add(".jpg");
        picker.FileTypeFilter.Add(".jpeg");
        picker.FileTypeFilter.Add(".png");

        StorageFile? file = await picker.PickSingleFileAsync();
        if (file is null) return;

        _chosenImage = file;

        using Windows.Storage.Streams.IRandomAccessStream stream =
            await file.OpenAsync(FileAccessMode.Read);
        var bitmap = new BitmapImage();
        await bitmap.SetSourceAsync(stream);
        PreviewImage.Source = bitmap;
        PreviewImage.Visibility = Visibility.Visible;
    }
}
