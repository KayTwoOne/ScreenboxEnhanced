using Screenbox.Core.Enums;
using Screenbox.Core.Models;

namespace Screenbox.Core.Helpers;

/// <summary>
/// The outcome of resolving which artwork a folder should display.
/// </summary>
/// <param name="Source">The winning artwork source.</param>
/// <param name="FileName">File name within the local Artwork folder, or null when none applies.</param>
/// <param name="NeedsGeneration">True when a frame must be captured to satisfy the request.</param>
public readonly record struct ArtworkDecision(PosterSource Source, string? FileName, bool NeedsGeneration);

/// <summary>
/// Decides which artwork source wins for a folder. Pure logic with no I/O so the
/// priority order can be verified without a UWP host.
/// </summary>
public static class ArtworkResolver
{
    /// <summary>
    /// Resolves the artwork to display.
    /// Priority: manual, then scraped, then convention file, then a cached or newly generated frame.
    /// </summary>
    /// <param name="metadata">Stored metadata for the folder, or null when it has none.</param>
    /// <param name="hasConventionFile">Whether poster.jpg or folder.jpg exists inside the folder.</param>
    /// <param name="hasCachedFrame">Whether the stored poster file is present on disk.</param>
    public static ArtworkDecision Resolve(FolderMetadataDto? metadata, bool hasConventionFile, bool hasCachedFrame)
    {
        if (ResolveFromMetadata(metadata) is { } pinned)
        {
            return pinned;
        }

        if (hasConventionFile)
        {
            return new ArtworkDecision(PosterSource.Convention, metadata?.PosterFile, NeedsGeneration: false);
        }

        if (metadata is { PosterSource: PosterSource.AutoFrame, PosterFile: { Length: > 0 } frame } && hasCachedFrame)
        {
            return new ArtworkDecision(PosterSource.AutoFrame, frame, NeedsGeneration: false);
        }

        return new ArtworkDecision(PosterSource.None, FileName: null, NeedsGeneration: true);
    }

    /// <summary>
    /// Resolves the decision that stored metadata settles on its own, or null when the folder's
    /// contents still have to be inspected. Callers use this to skip the convention-file scan and
    /// the recursive video search entirely for a folder whose artwork is already pinned.
    /// </summary>
    /// <param name="metadata">Stored metadata for the folder, or null when it has none.</param>
    public static ArtworkDecision? ResolveFromMetadata(FolderMetadataDto? metadata)
    {
        // A manual choice always wins and is never silently replaced, even when the
        // file is temporarily missing. Losing it would discard deliberate user work.
        if (metadata is { PosterSource: PosterSource.Manual, PosterFile: { Length: > 0 } manual })
        {
            return new ArtworkDecision(PosterSource.Manual, manual, NeedsGeneration: false);
        }

        if (metadata is { PosterSource: PosterSource.Scraped, PosterFile: { Length: > 0 } scraped })
        {
            return new ArtworkDecision(PosterSource.Scraped, scraped, NeedsGeneration: false);
        }

        return null;
    }
}
