using System;

namespace Screenbox.Core.Helpers;

/// <summary>
/// Decides when playback counts as finished. Pure logic so the rule can be verified
/// without a player, a media file, or a UWP host.
/// </summary>
public static class WatchThreshold
{
    /// <summary>Fraction of the runtime that must be reached to count as watched.</summary>
    public const double DefaultPercent = 0.9;

    /// <summary>
    /// Returns true when <paramref name="position"/> has reached the completion threshold.
    /// A non-positive duration means the length is unknown and never counts as complete —
    /// otherwise every item would be marked watched the moment it was opened.
    /// </summary>
    public static bool IsComplete(TimeSpan position, TimeSpan duration, double thresholdPercent)
    {
        if (duration <= TimeSpan.Zero) return false;
        return position.Ticks >= duration.Ticks * thresholdPercent;
    }

    /// <summary>
    /// Returns playback progress as a fraction in the range 0 to 1, or 0 when the duration
    /// is unknown.
    /// </summary>
    public static double Progress(TimeSpan position, TimeSpan duration)
    {
        if (duration <= TimeSpan.Zero) return 0d;
        double raw = (double)position.Ticks / duration.Ticks;
        return raw < 0d ? 0d : raw > 1d ? 1d : raw;
    }
}
