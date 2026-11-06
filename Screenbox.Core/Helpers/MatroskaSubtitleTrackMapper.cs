using System;
using System.Collections.Generic;
using System.Linq;
using Screenbox.Core.Models;

namespace Screenbox.Core.Helpers;

/// <summary>
/// Maps a subtitle track as VLC sees it to the Matroska track it came from.
/// </summary>
/// <remarks>
/// <para>
/// A VLC ES id is not a Matroska TrackNumber. Measured against LibVLC 3.0.23, the mkv demuxer adds
/// its tracks in ascending TrackNumber order, whatever their order in the file, and each gets the
/// next id from a counter that starts at 0. In a file with TrackNumbers 1 to 6, TrackNumber 4 has
/// ES id 3; in a file with TrackNumbers 2, 5, 7 and 9, TrackNumber 9 also has ES id 3.
/// </para>
/// <para>
/// That makes the position among subtitle tracks the reliable link: the nth text track by ES id is
/// the nth subtitle track by TrackNumber. The match is then checked against the codec, the name and
/// the language. If the position cannot be trusted, a track is only accepted if it is the one
/// subtitle track that fits. Otherwise there is no answer, because resizing the wrong track would
/// show the wrong subtitles, where keeping the original only means the size does not change.
/// </para>
/// </remarks>
public static class MatroskaSubtitleTrackMapper
{
    /// <summary>
    /// Finds the Matroska track for a VLC track.
    /// </summary>
    /// <param name="vlcTracks">Every embedded text track VLC reports.</param>
    /// <param name="selected">The track to find.</param>
    /// <param name="matroskaTracks">Every track in the file.</param>
    /// <returns>The matching track, or null when there is no safe answer.</returns>
    public static MatroskaTrack? Map(
        IReadOnlyList<SubtitleTrackInfo> vlcTracks,
        SubtitleTrackInfo selected,
        IReadOnlyList<MatroskaTrack> matroskaTracks)
    {
        MatroskaTrack[] subtitles = matroskaTracks.Where(track => track.IsSubtitle).OrderBy(track => track.Number).ToArray();
        SubtitleTrackInfo[] embedded = vlcTracks.OrderBy(track => track.VlcSpu).ToArray();

        int position = Array.FindIndex(embedded, track => track.VlcSpu == selected.VlcSpu);
        if (position >= 0 && embedded.Length == subtitles.Length && IsCompatible(selected, subtitles[position]))
        {
            return subtitles[position];
        }

        MatroskaTrack[] candidates = subtitles.Where(track => IsCompatible(selected, track)).ToArray();
        return candidates.Length == 1 ? candidates[0] : null;
    }

    private static bool IsCompatible(SubtitleTrackInfo vlcTrack, MatroskaTrack matroskaTrack) =>
        IsSameCodec(vlcTrack, matroskaTrack) && IsSameName(vlcTrack, matroskaTrack) && IsSameLanguage(vlcTrack, matroskaTrack);

    private static bool IsSameCodec(SubtitleTrackInfo vlcTrack, MatroskaTrack matroskaTrack) => vlcTrack.Codec switch
    {
        SubtitleTrackInfo.AssCodec => matroskaTrack.CodecId is "S_TEXT/ASS" or "S_TEXT/SSA" or "S_ASS" or "S_SSA",
        SubtitleTrackInfo.SrtCodec => matroskaTrack.CodecId is "S_TEXT/UTF8",
        _ => false,
    };

    private static bool IsSameName(SubtitleTrackInfo vlcTrack, MatroskaTrack matroskaTrack) =>
        vlcTrack.Description.Length == 0 || matroskaTrack.Name.Length == 0 ||
        vlcTrack.Description.Equals(matroskaTrack.Name, StringComparison.Ordinal);

    /// <summary>
    /// Compares languages only when VLC reports a code. It turns some codes into a language name,
    /// such as "English", and a name cannot be checked against a code without a table.
    /// </summary>
    private static bool IsSameLanguage(SubtitleTrackInfo vlcTrack, MatroskaTrack matroskaTrack)
    {
        string language = vlcTrack.Language;
        if (language.Equals("und", StringComparison.OrdinalIgnoreCase)) return true;

        return language.Length switch
        {
            3 => language.Equals(matroskaTrack.Language, StringComparison.OrdinalIgnoreCase),
            2 when matroskaTrack.LanguageBcp47.Length >= 2 =>
                language.AsSpan().Equals(matroskaTrack.LanguageBcp47.AsSpan(0, 2), StringComparison.OrdinalIgnoreCase),
            _ => true,
        };
    }
}
