using System.Collections.Generic;
using System.Threading.Tasks;
using Screenbox.Core.Models;

namespace Screenbox.Core.Coordinators;

/// <summary>
/// What the subtitle resizer needs from the item being played. The production implementation
/// wraps LibVLC and a <see cref="Windows.Storage.StorageFile"/>; keeping them behind this interface
/// is what lets the resizer's decisions be tested without either.
/// </summary>
internal interface ISubtitleResizeTarget
{
    /// <summary>Gets a value that indicates whether the player can take a new track yet.</summary>
    bool IsReady { get; }

    /// <summary>Gets the selected embedded track, or null when subtitles are off or the track is external.</summary>
    SubtitleTrackInfo? SelectedTrack { get; }

    /// <summary>Gets every embedded subtitle track, used to find the matching Matroska track.</summary>
    IReadOnlyList<SubtitleTrackInfo> EmbeddedTracks { get; }

    /// <summary>Gets the file to read from, or null when the item is not a local MKV.</summary>
    Task<SubtitleSourceFile?> GetSourceFileAsync();

    /// <summary>Loads a generated file and plays it in place of the current track.</summary>
    /// <returns>False when the player refused the file.</returns>
    bool ShowResized(string mrl);

    /// <summary>Switches back to a track's own embedded ES.</summary>
    void ShowOriginal(SubtitleTrackInfo track);
}
