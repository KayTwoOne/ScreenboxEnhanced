namespace Screenbox.Core.Helpers;

/// <summary>Maps a row of the subtitle menu to an index in the subtitle track list.</summary>
public static class SubtitlePickerRow
{
    /// <summary>
    /// Converts a menu row to a track index, where row 0 is "Disable" and has track index -1.
    /// </summary>
    /// <param name="row">The row. A list being rebuilt reports a lost selection as -1.</param>
    /// <param name="trackIndex">The track index, when the row is a real one.</param>
    /// <returns>False when no row is selected, which says nothing about which track should play.</returns>
    public static bool TryGetTrackIndex(int row, out int trackIndex)
    {
        trackIndex = row - 1;
        return row >= 0;
    }
}
