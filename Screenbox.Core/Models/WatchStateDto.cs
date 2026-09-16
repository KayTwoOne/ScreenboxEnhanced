using System;

namespace Screenbox.Core.Models;

/// <summary>
/// Durable record of whether a media item has been watched to completion.
/// Unlike <see cref="MediaPlaybackProgress"/>, which is a bounded resume cache,
/// this is kept for every item the user has played.
/// </summary>
public sealed class WatchStateDto
{
    /// <summary>Media location. Primary key. Upper-invariant normalized for cache/database identity.</summary>
    public string Location { get; set; } = string.Empty;

    /// <summary>Whether playback passed the completion threshold.</summary>
    public bool Completed { get; set; }

    /// <summary>When the item was last played, or null when never recorded.</summary>
    public DateTimeOffset? LastPlayed { get; set; }

    /// <summary>Total duration, used to render progress without reopening the file.</summary>
    public TimeSpan? Duration { get; set; }

    /// <summary>
    /// Last recorded playback position. Stored here rather than read from
    /// <see cref="MediaPlaybackProgress"/> because that list holds only the 64 most recent
    /// items, so progress bars would vanish from older episodes.
    /// </summary>
    public TimeSpan LastPosition { get; set; }

    /// <summary>
    /// Original (un-normalized) media location, used for file resolution.
    /// Null for rows written before this column was added. StorageFile.GetFileFromPathAsync
    /// is case-sensitive and respects UWP's granted scopes, so the uppercase normalized
    /// Location can fail to resolve even when the file exists. OriginalLocation preserves
    /// the casing as received from the app, allowing successful resolution.
    /// </summary>
    public string? OriginalLocation { get; set; }
}
