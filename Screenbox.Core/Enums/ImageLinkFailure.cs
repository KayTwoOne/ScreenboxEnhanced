namespace Screenbox.Core.Enums;

/// <summary>
/// Why a pasted image link could not be turned into a poster image. One reason per message the
/// user can be shown, so the caller maps each to its own localized text.
/// </summary>
public enum ImageLinkFailure
{
    /// <summary>The link, or the image a page pointed at, is not an https address.</summary>
    NotHttps = 1,

    /// <summary>The address answered with something that is not an image the app can decode.</summary>
    NotAnImage = 2,

    /// <summary>The image is larger than the allowed size.</summary>
    TooLarge = 3,

    /// <summary>The link is a web page that does not name an image.</summary>
    NoImageOnPage = 4,

    /// <summary>The address could not be reached or refused the request.</summary>
    DownloadFailed = 5,

    /// <summary>The address did not answer in time.</summary>
    TimedOut = 6
}
