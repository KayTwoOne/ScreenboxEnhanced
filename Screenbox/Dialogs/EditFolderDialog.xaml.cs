using System;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.DependencyInjection;
using Microsoft.Extensions.Logging;
using Screenbox.Core.Enums;
using Screenbox.Core.Helpers;
using Screenbox.Core.Models;
using Screenbox.Core.Services;
using Screenbox.Helpers;
using Windows.Storage;
using Windows.Storage.Streams;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Media.Imaging;

namespace Screenbox.Dialogs;

/// <summary>The outcome of editing a folder's display title and poster.</summary>
/// <param name="Title">The trimmed display title. Empty means clear the custom title.</param>
/// <param name="Poster">
/// The chosen image, or null when no new image was chosen. It is a file whether the user picked it
/// from disk or pasted a link to it: a fetched image arrives as a temporary file, so both go through
/// the same path to become the folder's poster.
/// </param>
/// <param name="RemovePoster">
/// True when the user asked to remove the folder's custom image. Never true together with a
/// <paramref name="Poster"/>: choosing an image takes the request back.
/// </param>
public sealed record EditFolderResult(string Title, StorageFile? Poster, bool RemovePoster);

public sealed partial class EditFolderDialog : ContentDialog
{
    /// <summary>
    /// How long typing must pause before a pasted link is fetched, so a fetch is not started for
    /// every keystroke. Short enough that a pasted link still feels immediate.
    /// </summary>
    private static readonly TimeSpan LinkFetchDebounce = TimeSpan.FromMilliseconds(500);

    /// <summary>
    /// The preview is shown in a box 200 DIPs tall, so decoding a pasted image (which may be many
    /// thousands of pixels across) at its full size would only cost memory.
    /// </summary>
    private const int PreviewDecodePixelHeight = 480;

    /// <summary>Which of the two ways of choosing an image currently decides the poster.</summary>
    private enum ImageSource
    {
        None,
        File,
        Link
    }

    private readonly IFilesService _filesService;
    private readonly IArtworkService _artworkService;
    private readonly ILogger<EditFolderDialog> _logger;
    private readonly bool _hasCustomImage;

    // The poster is whichever of these was chosen LAST. Picking a file after pasting a link makes
    // the file the poster, and pasting or editing a link after picking a file makes the link the
    // poster; the caption under the preview always says which one it is. Nothing is discarded:
    // clearing the link box hands the poster back to a file chosen earlier.
    private ImageSource _activeSource;
    private StorageFile? _chosenImage;
    private bool _removeImage;

    // The image fetched for the link currently in the box. Null while there is none yet, and reset
    // by every edit to the box, so it can never belong to an older version of the text.
    private StorageFile? _linkImage;
    private CancellationTokenSource? _linkFetchCts;
    private Task<bool>? _linkFetch;

    // True once the typing pause is over and the request is actually under way. Until then Save
    // need not wait out the pause: it can fetch immediately.
    private bool _linkFetchStarted;

    // Set when the user pastes into the link box, so the edit that follows is fetched at once. The
    // typing pause exists to avoid a request per keystroke; a paste is one deliberate action.
    private bool _linkPasted;

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
        _artworkService = Ioc.Default.GetRequiredService<IArtworkService>();
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

        // Save does not close the dialog until a pasted link has been turned into an image (see
        // EditFolderDialog_OnPrimaryButtonClick), so a link that decides the poster has one by now.
        StorageFile? poster = _removeImage
            ? null
            : _activeSource switch
            {
                ImageSource.Link => _linkImage,
                ImageSource.File => _chosenImage,
                _ => null
            };
        return new EditFolderResult(TitleTextBox.Text.Trim(), poster, _removeImage);
    }

    // Applied on Save like every other change in the dialog, so Cancel still leaves the folder
    // exactly as it was. Removing also drops any image chosen earlier in this same dialog, whether
    // picked or pasted: the user now wants none.
    private void RemoveImageButton_OnClick(object sender, RoutedEventArgs e)
    {
        try
        {
            _removeImage = true;
            _chosenImage = null;
            _activeSource = ImageSource.None;
            CancelLinkFetch();
            _linkImage = null;
            ImageLinkTextBox.Text = string.Empty;
            HideLinkStatus();
            HidePreview();
            RemoveImageButton.Visibility = Visibility.Collapsed;
            RemoveImageNote.Visibility = Visibility.Visible;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to remove the folder poster image.");
        }
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

            // The file is now the most recent choice, so it outranks any pasted link: stop fetching
            // the link and stop showing its problems, as it no longer decides anything.
            _chosenImage = file;
            _activeSource = ImageSource.File;
            CancelLinkFetch();
            HideLinkStatus();
            TakeBackRemoval();

            BitmapImage? bitmap = await LoadPreviewAsync(file);
            if (bitmap is not null && IsChosenFileStillActive(file))
            {
                ShowPreview(bitmap, ImageSource.File);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to choose a folder poster image.");
        }
    }

    private void ImageLinkTextBox_OnPaste(object sender, TextControlPasteEventArgs e)
    {
        _linkPasted = true;
    }

    // Runs on every edit of the link box, including the programmatic clear done by the remove
    // button. Each edit throws away whatever was fetched or being fetched for the old text and
    // starts again, so nothing stale can ever become the poster. Not async: starting the fetch does
    // not wait for it. The fetch's own task never throws (see FetchLinkImageAsync).
    private void ImageLinkTextBox_OnTextChanged(object sender, TextChangedEventArgs e)
    {
        try
        {
            string text = ImageLinkTextBox.Text.Trim();
            bool pasted = _linkPasted;
            _linkPasted = false;
            CancelLinkFetch();
            _linkImage = null;
            HideLinkStatus();

            if (text.Length == 0)
            {
                // No link any more, so a file chosen earlier is the poster again.
                if (_activeSource == ImageSource.Link)
                {
                    _activeSource = _chosenImage is null ? ImageSource.None : ImageSource.File;
                }

                _ = RestorePreviewForActiveSourceAsync();
                return;
            }

            _activeSource = ImageSource.Link;
            TakeBackRemoval();

            // Until the link yields an image there is nothing to show for it. The previous preview
            // belonged to the previous text.
            HidePreview();

            // Only something shaped like an address is worth a request. Anything else is left
            // alone while it is typed and is reported when Save is pressed.
            if (Uri.TryCreate(text, UriKind.Absolute, out _))
            {
                BeginLinkFetch(text, debounce: !pasted);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to react to an edit of the image link.");
        }
    }

    // Save must not close having done nothing. When a pasted link is what will become the poster
    // and no image has been fetched for it yet (still in the typing pause, still downloading, or
    // failed earlier), the dialog is held open with the button's deferral until the fetch has an
    // answer; a failure keeps the dialog open with the reason shown beside the link box. As an async
    // void handler, nothing may escape it: that would terminate the app.
    private async void EditFolderDialog_OnPrimaryButtonClick(ContentDialog sender, ContentDialogButtonClickEventArgs args)
    {
        ContentDialogButtonClickDeferral deferral = args.GetDeferral();
        try
        {
            if (!await EnsureLinkImageAsync())
            {
                args.Cancel = true;
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to prepare the pasted image link for saving.");
            ShowLinkError(ImageLinkFailure.DownloadFailed);
            args.Cancel = true;
        }
        finally
        {
            deferral.Complete();
        }
    }

    private void EditFolderDialog_OnClosed(ContentDialog sender, ContentDialogClosedEventArgs args)
    {
        // Nobody is waiting for a fetch any more once the dialog has gone.
        CancelLinkFetch();
    }

    /// <summary>
    /// Returns true when Save can go ahead: no pasted link is deciding the poster, or its image is
    /// ready. Otherwise fetches it now and says whether that worked.
    /// </summary>
    private async Task<bool> EnsureLinkImageAsync()
    {
        if (_removeImage || _activeSource != ImageSource.Link || _linkImage is not null)
        {
            return true;
        }

        Task<bool>? pending = _linkFetch;
        if (pending is null || !_linkFetchStarted || pending.IsCompleted)
        {
            // Nothing is under way: still in the typing pause (Save means now, so do not wait it
            // out), never started because the text was not yet an address, or an earlier attempt
            // that failed (which may have been a passing network problem, so try again).
            CancelLinkFetch();
            HideLinkStatus();
            pending = BeginLinkFetch(ImageLinkTextBox.Text.Trim(), debounce: false);
        }

        return await pending && _linkImage is not null && _activeSource == ImageSource.Link;
    }

    private Task<bool> BeginLinkFetch(string text, bool debounce)
    {
        _linkFetchCts = new CancellationTokenSource();
        _linkFetchStarted = false;
        _linkFetch = FetchLinkImageAsync(text, _linkFetchCts.Token, debounce);
        return _linkFetch;
    }

    private void CancelLinkFetch()
    {
        if (_linkFetchCts is { } cts)
        {
            cts.Cancel();
            cts.Dispose();
        }

        _linkFetchCts = null;
        _linkFetch = null;
        _linkFetchStarted = false;
        SetLinkBusy(false);
    }

    /// <summary>
    /// Fetches the image behind the link, shows it in the preview, and keeps it as the link's image.
    /// Never throws, so it is safe to leave running unobserved: every way it can end is reported in
    /// the dialog, or is silent because the text moved on. The UI is touched only after each await
    /// has resumed on the UI thread, and only while <paramref name="ct"/> says this is still the
    /// fetch the dialog is waiting for.
    /// </summary>
    /// <returns>True when the link's image is ready and shown.</returns>
    private async Task<bool> FetchLinkImageAsync(string text, CancellationToken ct, bool debounce)
    {
        try
        {
            if (debounce)
            {
                await Task.Delay(LinkFetchDebounce, ct);
            }

            _linkFetchStarted = true;
            SetLinkBusy(true);
            ImageLinkResult result = await _artworkService.FetchImageFromLinkAsync(text, ct);
            ct.ThrowIfCancellationRequested();
            if (!result.IsSuccess)
            {
                ShowLinkError(result.Failure.Value);
                return false;
            }

            BitmapImage? bitmap = await LoadPreviewAsync(result.Image);
            ct.ThrowIfCancellationRequested();
            if (bitmap is null)
            {
                ShowLinkError(ImageLinkFailure.NotAnImage);
                return false;
            }

            _linkImage = result.Image;
            ShowPreview(bitmap, ImageSource.Link);
            return true;
        }
        catch (OperationCanceledException)
        {
            // The text changed or the dialog went away; whoever cancelled has moved on.
            return false;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to fetch the image behind the pasted link.");
            if (!ct.IsCancellationRequested)
            {
                ShowLinkError(ImageLinkFailure.DownloadFailed);
            }

            return false;
        }
        finally
        {
            // A cancelled fetch leaves the indicator to whoever cancelled it, which may already
            // have started the next fetch.
            if (!ct.IsCancellationRequested)
            {
                SetLinkBusy(false);
            }
        }
    }

    /// <summary>Decodes a file for the preview, or returns null (after logging) when it cannot be shown.</summary>
    private async Task<BitmapImage?> LoadPreviewAsync(StorageFile file)
    {
        try
        {
            using IRandomAccessStream stream = await file.OpenAsync(FileAccessMode.Read);
            var bitmap = new BitmapImage { DecodePixelHeight = PreviewDecodePixelHeight };
            await bitmap.SetSourceAsync(stream);
            return bitmap;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to load the folder poster preview.");
            return null;
        }
    }

    private bool IsChosenFileStillActive(StorageFile file) =>
        _activeSource == ImageSource.File && ReferenceEquals(_chosenImage, file);

    // Shows whatever will be the poster now that the link box has been emptied. Never throws, so
    // the task is safe to leave unobserved.
    private async Task RestorePreviewForActiveSourceAsync()
    {
        try
        {
            if (_activeSource != ImageSource.File || _chosenImage is not { } file)
            {
                HidePreview();
                return;
            }

            BitmapImage? bitmap = await LoadPreviewAsync(file);
            if (bitmap is not null && IsChosenFileStillActive(file))
            {
                ShowPreview(bitmap, ImageSource.File);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to restore the folder poster preview.");
        }
    }

    // Choosing an image replaces the old one anyway, so it takes a pending removal back.
    private void TakeBackRemoval()
    {
        _removeImage = false;
        RemoveImageNote.Visibility = Visibility.Collapsed;
        RemoveImageButton.Visibility = _hasCustomImage ? Visibility.Visible : Visibility.Collapsed;
    }

    private void ShowPreview(BitmapImage bitmap, ImageSource source)
    {
        PreviewImage.Source = bitmap;
        PreviewImage.Visibility = Visibility.Visible;
        PreviewSourceNote.Text = source == ImageSource.Link
            ? Strings.Resources.PosterPreviewFromLink
            : Strings.Resources.PosterPreviewFromFile;
        PreviewSourceNote.Visibility = Visibility.Visible;
    }

    private void HidePreview()
    {
        PreviewImage.Source = null;
        PreviewImage.Visibility = Visibility.Collapsed;
        PreviewSourceNote.Visibility = Visibility.Collapsed;
    }

    private void SetLinkBusy(bool busy)
    {
        ImageLinkProgress.IsActive = busy;
        ImageLinkProgress.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        if (busy)
        {
            ImageLinkError.Visibility = Visibility.Collapsed;
            ImageLinkStatus.Visibility = Visibility.Visible;
        }
        else if (ImageLinkError.Visibility == Visibility.Collapsed)
        {
            ImageLinkStatus.Visibility = Visibility.Collapsed;
        }
    }

    private void ShowLinkError(ImageLinkFailure failure)
    {
        ImageLinkError.Text = failure switch
        {
            ImageLinkFailure.NotHttps => Strings.Resources.ImageLinkNotHttps,
            ImageLinkFailure.NotAnImage => Strings.Resources.ImageLinkNotAnImage,
            ImageLinkFailure.TooLarge => Strings.Resources.ImageLinkTooLarge,
            ImageLinkFailure.NoImageOnPage => Strings.Resources.ImageLinkNoImageOnPage,
            ImageLinkFailure.TimedOut => Strings.Resources.ImageLinkTimedOut,
            _ => Strings.Resources.ImageLinkDownloadFailed
        };
        SetLinkBusy(false);
        ImageLinkError.Visibility = Visibility.Visible;
        ImageLinkStatus.Visibility = Visibility.Visible;
    }

    private void HideLinkStatus()
    {
        ImageLinkError.Visibility = Visibility.Collapsed;
        ImageLinkProgress.IsActive = false;
        ImageLinkProgress.Visibility = Visibility.Collapsed;
        ImageLinkStatus.Visibility = Visibility.Collapsed;
    }
}
