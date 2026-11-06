namespace Screenbox.Core.Coordinators;

/// <summary>
/// Applies the subtitle size setting to text subtitles embedded in local MKV files. LibVLC 3 cannot
/// resize them itself, so this reads the selected track out of the file, builds a resized ASS file,
/// and plays that in place of the embedded track. The subtitle menu keeps showing the original.
/// </summary>
/// <remarks>
/// There is nothing to call: the coordinator listens for the player, its current item, the selected
/// subtitle track and the setting from the moment it is constructed.
/// </remarks>
public interface ISubtitleResizeCoordinator
{
}
