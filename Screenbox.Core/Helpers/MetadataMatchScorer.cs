using System;

namespace Screenbox.Core.Helpers;

/// <summary>
/// Scores how closely a provider's candidate title matches the normalized search query, so
/// <see cref="Coordinators.MetadataScrapeCoordinator"/> can rank candidates from different
/// providers on one common measure. The three catalogs have no comparable notion of relevance to
/// each other - see the remarks on <see cref="Models.MetadataMatch.Confidence"/> - so this is the
/// only place <c>Confidence</c> is assigned, and it is applied identically regardless of which
/// provider produced the candidate.
/// </summary>
public static class MetadataMatchScorer
{
    /// <summary>
    /// The minimum score, out of 1, a candidate must reach to be accepted as a match. Below this,
    /// the caller must record a no-match rather than accepting the best of a bad set - a confident
    /// wrong match is worse than no match at all.
    /// <para>
    /// 0.6 was chosen by looking at both ends: real catalogs return titles that differ from the
    /// folder-derived query only in punctuation or spacing (a "Dan Da Dan" folder query against a
    /// provider title of "Dandadan" scores 0.8 under this measure - two inserted spaces over a
    /// 10-character string), which must clear the bar. A folder name that is not a series at all -
    /// "hayase-cache" is a real folder in this library - only shares a few incidental characters
    /// with whatever a fuzzy provider search happens to return for it, which must not.
    /// </para>
    /// </summary>
    public const double AcceptThreshold = 0.6;

    /// <summary>
    /// Returns a similarity score in [0, 1] between <paramref name="query"/> and
    /// <paramref name="candidateTitle"/>, based on normalized Levenshtein edit distance:
    /// <c>1 - distance / max(length)</c>. Comparison is case-insensitive and ignores
    /// leading/trailing whitespace. A null, empty, or whitespace-only argument on either side
    /// scores 0 rather than throwing.
    /// </summary>
    public static double Score(string? query, string? candidateTitle)
    {
        string a = Normalize(query);
        string b = Normalize(candidateTitle);

        if (a.Length == 0 || b.Length == 0)
        {
            return 0;
        }

        if (string.Equals(a, b, StringComparison.Ordinal))
        {
            return 1;
        }

        int distance = LevenshteinDistance(a, b);
        int maxLength = Math.Max(a.Length, b.Length);
        double similarity = 1.0 - (double)distance / maxLength;
        return Math.Clamp(similarity, 0, 1);
    }

    private static string Normalize(string? value) =>
        string.IsNullOrWhiteSpace(value) ? string.Empty : value.Trim().ToLowerInvariant();

    /// <summary>
    /// Classic Wagner-Fischer edit distance using a rolling two-row buffer - O(n*m) time,
    /// O(min(n,m)) space. Provider titles here are short (a handful of words), so no smarter
    /// algorithm is warranted.
    /// </summary>
    private static int LevenshteinDistance(string a, string b)
    {
        if (a.Length > b.Length)
        {
            (a, b) = (b, a);
        }

        int[] previousRow = new int[a.Length + 1];
        int[] currentRow = new int[a.Length + 1];

        for (int i = 0; i <= a.Length; i++)
        {
            previousRow[i] = i;
        }

        for (int j = 1; j <= b.Length; j++)
        {
            currentRow[0] = j;
            for (int i = 1; i <= a.Length; i++)
            {
                int substitutionCost = a[i - 1] == b[j - 1] ? 0 : 1;
                currentRow[i] = Math.Min(
                    Math.Min(currentRow[i - 1] + 1, previousRow[i] + 1),
                    previousRow[i - 1] + substitutionCost);
            }

            (previousRow, currentRow) = (currentRow, previousRow);
        }

        return previousRow[a.Length];
    }
}
