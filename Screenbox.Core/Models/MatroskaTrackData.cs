using System;
using System.Collections.Generic;

namespace Screenbox.Core.Models;

/// <summary>
/// One frame of a Matroska track, with its timing already resolved to nanoseconds.
/// </summary>
/// <param name="StartNs">The absolute start time: (Cluster timestamp + block offset) × TimestampScale.</param>
/// <param name="DurationNs">
/// The duration: BlockDuration × TimestampScale, or the track's DefaultDuration for a block
/// without one, or 0 when neither is known.
/// </param>
/// <param name="Payload">The frame data, with the track's content encodings already undone.</param>
public sealed record MatroskaBlock(long StartNs, long DurationNs, byte[] Payload);

/// <summary>
/// The segment-level information the subtitle reader needs: the timestamp scale and every
/// track header. Read from the start of the file only, so it is cheap.
/// </summary>
public sealed class MatroskaHeader
{
    /// <summary>The default <c>TimestampScale</c>: one millisecond, in nanoseconds.</summary>
    public const long DefaultTimestampScale = 1_000_000;

    /// <summary>Gets the <c>TimestampScale</c> in nanoseconds per timestamp unit.</summary>
    public long TimestampScale { get; init; } = DefaultTimestampScale;

    /// <summary>Gets every track header, in file order.</summary>
    public IReadOnlyList<MatroskaTrack> Tracks { get; init; } = Array.Empty<MatroskaTrack>();
}

/// <summary>
/// The result of reading one track out of a Matroska file.
/// </summary>
public sealed class MatroskaTrackData
{
    /// <summary>Gets the segment header the track was read with.</summary>
    public required MatroskaHeader Header { get; init; }

    /// <summary>Gets the header of the requested track.</summary>
    public required MatroskaTrack Track { get; init; }

    /// <summary>Gets the track's blocks, in file order.</summary>
    public required IReadOnlyList<MatroskaBlock> Blocks { get; init; }

    /// <summary>
    /// Gets a value that indicates whether the whole file was read. <see langword="false"/> means the
    /// file was truncated or corrupt part-way and <see cref="Blocks"/> holds what came before that.
    /// </summary>
    public bool IsComplete { get; init; }
}
