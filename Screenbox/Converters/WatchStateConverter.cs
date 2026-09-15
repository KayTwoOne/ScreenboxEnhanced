using Windows.UI.Xaml;

namespace Screenbox.Converters;

/// <summary>
/// Provides <see langword="static"/> methods to convert watch state values for tile rendering.
/// </summary>
public static partial class WatchStateConverter
{
    /// <summary>
    /// Determines whether the in-progress indicator should be shown for a tile.
    /// </summary>
    /// <param name="isWatched">A <see cref="bool"/> indicating whether the item is fully watched.</param>
    /// <param name="progress">A <see cref="double"/> from 0 to 1 indicating playback progress.</param>
    /// <returns>
    /// <see cref="Visibility.Visible"/> if <paramref name="progress"/> is above zero and
    /// <paramref name="isWatched"/> is <see langword="false"/>; otherwise, <see cref="Visibility.Collapsed"/>.
    /// </returns>
    public static Visibility ShouldShowProgress(bool isWatched, double progress)
    {
        return !isWatched && progress > 0 ? Visibility.Visible : Visibility.Collapsed;
    }
}
