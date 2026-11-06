using System;
using System.IO;
using System.Threading.Tasks;

namespace Screenbox.Core.Models;

/// <summary>
/// A subtitle track as LibVLC reports it. <see cref="VlcSpu"/> is the ES id that selects the track.
/// </summary>
/// <param name="VlcSpu">The ES id.</param>
/// <param name="Codec">The codec as a four-character code, for example <c>"ssa "</c> or <c>"subt"</c>.</param>
/// <param name="Description">The track name, or an empty string.</param>
/// <param name="Language">The language as VLC reports it: a code, a language name, or an empty string.</param>
public sealed record SubtitleTrackInfo(int VlcSpu, string Codec, string Description, string Language)
{
    /// <summary>The four-character code VLC uses for ASS and SSA tracks.</summary>
    public const string AssCodec = "ssa ";

    /// <summary>The four-character code VLC uses for SRT tracks.</summary>
    public const string SrtCodec = "subt";

    /// <summary>
    /// Gets a value that indicates whether the track is in a text format the resizer supports.
    /// Checking this first means image subtitles never cost a read of the file.
    /// </summary>
    public bool IsResizableCodec => Codec is AssCodec or SrtCodec;
}

/// <summary>
/// The local file a subtitle track is read from. Its path, size and modified time identify the
/// parsed track in the resizer's cache, so a file that changes on disk is read again.
/// </summary>
/// <param name="Path">The file's path.</param>
/// <param name="Size">The file's size in bytes.</param>
/// <param name="Modified">The file's last modified time.</param>
/// <param name="OpenReadAsync">Opens a new seekable stream over the file.</param>
public sealed record SubtitleSourceFile(string Path, long Size, DateTimeOffset Modified, Func<Task<Stream>> OpenReadAsync);
