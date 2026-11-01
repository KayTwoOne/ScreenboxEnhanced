using System.Diagnostics.CodeAnalysis;
using Screenbox.Core.Enums;
using Windows.Storage;

namespace Screenbox.Core.Models;

/// <summary>
/// The outcome of fetching an image from a pasted link: either a validated image saved as a
/// temporary file, or the reason it could not be used. Never both.
/// </summary>
public sealed class ImageLinkResult
{
    private ImageLinkResult(StorageFile? image, ImageLinkFailure? failure)
    {
        Image = image;
        Failure = failure;
    }

    /// <summary>
    /// The downloaded image, saved to a temporary file named for its real format and ready to be
    /// applied like any file the user picked. Null when the fetch failed.
    /// </summary>
    public StorageFile? Image { get; }

    /// <summary>Why the fetch failed, or null when it succeeded.</summary>
    public ImageLinkFailure? Failure { get; }

    /// <summary>True when <see cref="Image"/> holds a validated image.</summary>
    [MemberNotNullWhen(true, nameof(Image))]
    [MemberNotNullWhen(false, nameof(Failure))]
    public bool IsSuccess => Image is not null;

    /// <summary>A successful result carrying the saved image.</summary>
    public static ImageLinkResult Succeeded(StorageFile image) => new(image, null);

    /// <summary>A failed result carrying the reason.</summary>
    public static ImageLinkResult Failed(ImageLinkFailure failure) => new(null, failure);
}
