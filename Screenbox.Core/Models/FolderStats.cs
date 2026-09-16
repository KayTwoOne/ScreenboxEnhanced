using System;

namespace Screenbox.Core.Models;

/// <summary>
/// Aggregate information about the media inside a library folder, derived from the
/// cached library records rather than the file system.
/// </summary>
/// <param name="EpisodeCount">Number of known media files at or below the folder.</param>
/// <param name="TotalDuration">Combined runtime of those files.</param>
/// <param name="UnwatchedCount">How many have not been watched to completion.</param>
public readonly record struct FolderStats(int EpisodeCount, TimeSpan TotalDuration, int UnwatchedCount);
