namespace Screenbox.Core.Enums;

/// <summary>
/// Identifies where a folder's poster image came from, so a refresh can regenerate
/// derived artwork without discarding a manual choice made by the user.
/// </summary>
public enum PosterSource
{
    /// <summary>No poster has been resolved.</summary>
    None = 0,

    /// <summary>Generated from a video frame inside the folder.</summary>
    AutoFrame = 1,

    /// <summary>Explicitly chosen by the user.</summary>
    Manual = 2,

    /// <summary>Found as poster.jpg or folder.jpg inside the folder.</summary>
    Convention = 3,

    /// <summary>Downloaded from an online metadata provider.</summary>
    Scraped = 4
}
