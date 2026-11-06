using System;
using CommunityToolkit.Diagnostics;
using LibVLCSharp.Shared;
using Screenbox.Core.Models;
using Windows.Media.Core;

namespace Screenbox.Core.Playback;

public sealed partial class SubtitleTrack : MediaTrack
{
    internal int VlcSpu { get; set; }

    /// <summary>
    /// Gets what VLC reports about this track when it is embedded in the media file, or null for a
    /// subtitle file the user added. The subtitle resizer only ever works on embedded tracks.
    /// </summary>
    internal SubtitleTrackInfo? EmbeddedInfo { get; }

    public SubtitleTrack(string language = "") : base(MediaTrackKind.TimedMetadata, language)
    {
    }

    public SubtitleTrack(LibVLCSharp.Shared.MediaTrack textTrack) : base(textTrack)
    {
        Guard.IsTrue(textTrack.TrackType == TrackType.Text, nameof(textTrack.TrackType));
        VlcSpu = textTrack.Id;
        EmbeddedInfo = new SubtitleTrackInfo(
            textTrack.Id,
            FormatFourCc(textTrack.Codec),
            textTrack.Description ?? string.Empty,
            textTrack.Language ?? string.Empty);
    }

    public SubtitleTrack(TimedMetadataTrack track) : base(track)
    {
    }

    private static string FormatFourCc(uint fourCc) => string.Create(4, fourCc, static (characters, code) =>
    {
        for (int i = 0; i < characters.Length; i++)
        {
            characters[i] = (char)((code >> (8 * i)) & 0xFF);
        }
    });
}
