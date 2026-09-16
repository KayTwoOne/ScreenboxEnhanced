using System;
using System.Collections.Generic;
using Screenbox.Core.Models;

namespace Screenbox.Core.Helpers;

/// <summary>
/// Orders media filenames by parsed season and episode, so episodes read 1, 2, … 10, 11
/// rather than the lexical 1, 10, 11, 2. Specials sort after regular episodes; anything
/// unparsable falls back to an ordinal name comparison.
/// </summary>
public sealed class EpisodeComparer : IComparer<string>
{
    /// <summary>Shared instance. The comparer holds no state.</summary>
    public static readonly EpisodeComparer Instance = new();

    /// <inheritdoc/>
    public int Compare(string? x, string? y)
    {
        if (ReferenceEquals(x, y)) return 0;
        if (x is null) return -1;
        if (y is null) return 1;

        EpisodeInfo left = EpisodeInfoParser.Parse(x);
        EpisodeInfo right = EpisodeInfoParser.Parse(y);

        // Specials always trail regular episodes.
        if (left.IsSpecial != right.IsSpecial) return left.IsSpecial ? 1 : -1;

        // A name with no season is treated as season 1 so "Show - 02" and "Show - S01E03"
        // order sensibly against each other inside the same folder.
        int season = (left.Season ?? 1).CompareTo(right.Season ?? 1);
        if (season != 0) return season;

        if (left.Episode is { } le && right.Episode is { } re)
        {
            int episode = le.CompareTo(re);
            if (episode != 0) return episode;
        }
        else if (left.Episode is not null)
        {
            return -1;
        }
        else if (right.Episode is not null)
        {
            return 1;
        }

        return string.Compare(x, y, StringComparison.OrdinalIgnoreCase);
    }
}
