using System;
using System.Collections.Generic;

namespace Screenbox.Core.Models;

/// <summary>
/// One <c>ContentEncoding</c> entry of a Matroska track: a compression or encryption step that
/// was applied to the track's frames (and possibly its CodecPrivate) when the file was muxed.
/// </summary>
/// <param name="Order">The <c>ContentEncodingOrder</c>. Decoding starts with the highest order.</param>
/// <param name="Scope">The <c>ContentEncodingScope</c> bit field: 1 = frames, 2 = CodecPrivate.</param>
/// <param name="Type">The <c>ContentEncodingType</c>: 0 = compression, 1 = encryption.</param>
/// <param name="CompressionAlgorithm">The <c>ContentCompAlgo</c>: 0 = zlib, 3 = header stripping.</param>
/// <param name="CompressionSettings">The <c>ContentCompSettings</c>; for header stripping, the stripped bytes.</param>
public sealed record MatroskaContentEncoding(
    ulong Order,
    ulong Scope,
    ulong Type,
    ulong CompressionAlgorithm,
    byte[] CompressionSettings)
{
    /// <summary>The <c>ContentEncodingType</c> value for compression.</summary>
    public const ulong TypeCompression = 0;

    /// <summary>The <c>ContentEncodingType</c> value for encryption.</summary>
    public const ulong TypeEncryption = 1;

    /// <summary>The <c>ContentCompAlgo</c> value for zlib.</summary>
    public const ulong AlgorithmZlib = 0;

    /// <summary>The <c>ContentCompAlgo</c> value for header stripping.</summary>
    public const ulong AlgorithmHeaderStripping = 3;

    /// <summary>Whether this encoding applies to the track's frames.</summary>
    public bool AppliesToFrames => (Scope & 1) != 0;

    /// <summary>Whether this encoding applies to the track's CodecPrivate.</summary>
    public bool AppliesToCodecPrivate => (Scope & 2) != 0;

    /// <summary>
    /// Whether the reader can undo this encoding: zlib and header stripping are supported,
    /// encryption and the other compression algorithms (bzlib, lzo1x) are not.
    /// </summary>
    public bool IsSupported => Type == TypeCompression &&
                               CompressionAlgorithm is AlgorithmZlib or AlgorithmHeaderStripping;
}

/// <summary>
/// The header of one Matroska <c>TrackEntry</c>, as read by
/// <see cref="Helpers.MatroskaSubtitleReader"/>.
/// </summary>
public sealed class MatroskaTrack
{
    /// <summary>The Matroska <c>TrackType</c> value for subtitle tracks.</summary>
    public const ulong SubtitleTrackType = 0x11;

    /// <summary>Gets the <c>TrackNumber</c>, which is what blocks refer to the track by.</summary>
    public ulong Number { get; init; }

    /// <summary>Gets the <c>TrackType</c>; <see cref="SubtitleTrackType"/> for subtitles.</summary>
    public ulong Type { get; init; }

    /// <summary>Gets the <c>CodecID</c>, for example <c>S_TEXT/ASS</c>.</summary>
    public string CodecId { get; init; } = string.Empty;

    /// <summary>
    /// Gets the <c>CodecPrivate</c> after any content encoding scoped to it has been undone.
    /// For ASS and SSA tracks this is the script header.
    /// </summary>
    public byte[] CodecPrivate { get; init; } = Array.Empty<byte>();

    /// <summary>Gets the legacy <c>Language</c> code. Matroska's default is <c>eng</c>.</summary>
    public string Language { get; init; } = "eng";

    /// <summary>Gets the <c>LanguageBCP47</c> tag, or an empty string when the file has none.</summary>
    public string LanguageBcp47 { get; init; } = string.Empty;

    /// <summary>Gets the track <c>Name</c>, or an empty string when the file has none.</summary>
    public string Name { get; init; } = string.Empty;

    /// <summary>Gets the <c>FlagDefault</c> value. Matroska's default is set.</summary>
    public bool IsDefault { get; init; } = true;

    /// <summary>Gets the <c>DefaultDuration</c> in nanoseconds, or 0 when the file has none.</summary>
    public long DefaultDurationNs { get; init; }

    /// <summary>Gets the content encodings, in file order.</summary>
    public IReadOnlyList<MatroskaContentEncoding> ContentEncodings { get; init; } = Array.Empty<MatroskaContentEncoding>();

    /// <summary>Gets a value that indicates whether this is a subtitle track.</summary>
    public bool IsSubtitle => Type == SubtitleTrackType;

    /// <summary>
    /// Gets a value that indicates whether every content encoding on this track can be undone.
    /// An encrypted track, or one using an unsupported compression algorithm, cannot be read.
    /// </summary>
    public bool HasSupportedEncodings
    {
        get
        {
            foreach (MatroskaContentEncoding encoding in ContentEncodings)
            {
                if (!encoding.IsSupported) return false;
            }

            return true;
        }
    }
}
