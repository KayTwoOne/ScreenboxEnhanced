using Screenbox.Core.Helpers;
using Screenbox.Core.Models;

namespace Screenbox.Core.Tests.Helpers;

public sealed class MatroskaSubtitleTrackMapperTests
{
    private static MatroskaTrack Subtitle(ulong number, string codecId, string name = "", string language = "eng", string languageBcp47 = "") => new()
    {
        Number = number,
        Type = MatroskaTrack.SubtitleTrackType,
        CodecId = codecId,
        Name = name,
        Language = language,
        LanguageBcp47 = languageBcp47,
    };

    private static MatroskaTrack Video(ulong number) => new() { Number = number, Type = 1, CodecId = "V_MPEGH/ISO/HEVC" };

    private static MatroskaTrack Audio(ulong number) => new() { Number = number, Type = 2, CodecId = "A_AAC" };

    private static SubtitleTrackInfo Ass(int id, string description = "", string language = "") => new(id, SubtitleTrackInfo.AssCodec, description, language);

    private static SubtitleTrackInfo Srt(int id, string description = "", string language = "") => new(id, SubtitleTrackInfo.SrtCodec, description, language);

    [Test]
    public async Task Map_MatchesTheDeathNoteLayoutMeasuredAgainstLibVlc()
    {
        // Measured: Media.Tracks reports ES ids 3, 4, 5 for TrackNumbers 4, 5, 6. Neither the id
        // nor the id plus an offset is the rule; the position among subtitle tracks is.
        MatroskaTrack[] matroska =
        [
            Video(1), Audio(2), Audio(3),
            Subtitle(4, "S_TEXT/ASS", "Signs and Songs [FMA1394/Redc4t]", "eng", "en"),
            Subtitle(5, "S_TEXT/ASS", "English [FMA1394/Redc4t]", "eng", "en"),
            Subtitle(6, "S_TEXT/UTF8", "", "und", "und"),
        ];
        SubtitleTrackInfo[] vlc =
        [
            Ass(3, "Signs and Songs [FMA1394/Redc4t]", "English"),
            Ass(4, "English [FMA1394/Redc4t]", "English"),
            Srt(5, "", "und"),
        ];

        await Assert.That(MatroskaSubtitleTrackMapper.Map(vlc, vlc[0], matroska)?.Number).IsEqualTo(4UL);
        await Assert.That(MatroskaSubtitleTrackMapper.Map(vlc, vlc[1], matroska)?.Number).IsEqualTo(5UL);
        await Assert.That(MatroskaSubtitleTrackMapper.Map(vlc, vlc[2], matroska)?.Number).IsEqualTo(6UL);
    }

    [Test]
    public async Task Map_OrdersMatroskaTracksByTrackNumberNotFileOrder()
    {
        // Measured with a synthetic file: TrackEntries 9, 2, 5, 7 in that file order got ES ids in
        // ascending TrackNumber order, so 2 got id 0 and 9 got id 3.
        MatroskaTrack[] matroska =
        [
            Subtitle(9, "S_TEXT/UTF8", "Nine", "fre"),
            Subtitle(2, "S_TEXT/ASS", "Two", "eng"),
            Subtitle(5, "S_TEXT/UTF8", "Five", "spa"),
            Subtitle(7, "S_TEXT/UTF8", "Seven", "ger"),
        ];
        SubtitleTrackInfo[] vlc =
        [
            Ass(0, "Two", "eng"),
            Srt(1, "Five", "spa"),
            Srt(2, "Seven", "ger"),
            Srt(3, "Nine", "fre"),
        ];

        await Assert.That(MatroskaSubtitleTrackMapper.Map(vlc, vlc[0], matroska)?.Number).IsEqualTo(2UL);
        await Assert.That(MatroskaSubtitleTrackMapper.Map(vlc, vlc[3], matroska)?.Number).IsEqualTo(9UL);
    }

    [Test]
    public async Task Map_OrdersVlcTracksByEsIdWhateverOrderTheyAreListedIn()
    {
        MatroskaTrack[] matroska = [Subtitle(4, "S_TEXT/ASS"), Subtitle(6, "S_TEXT/UTF8")];
        SubtitleTrackInfo[] vlc = [Srt(5), Ass(3)];

        await Assert.That(MatroskaSubtitleTrackMapper.Map(vlc, vlc[0], matroska)?.Number).IsEqualTo(6UL);
    }

    [Test]
    public async Task Map_UsesThePositionAloneWhenTheTracksLookTheSame()
    {
        // No names and one codec: only the order can tell these apart. Listed out of order on both
        // sides, with a video and an audio track that must not count towards the position.
        MatroskaTrack[] matroska =
        [
            Video(1),
            Subtitle(9, "S_TEXT/UTF8", language: "und"),
            Audio(2),
            Subtitle(3, "S_TEXT/UTF8", language: "und"),
            Subtitle(5, "S_TEXT/UTF8", language: "und"),
        ];
        SubtitleTrackInfo[] vlc = [Srt(4, language: "und"), Srt(2, language: "und"), Srt(3, language: "und")];

        await Assert.That(MatroskaSubtitleTrackMapper.Map(vlc, vlc[1], matroska)?.Number).IsEqualTo(3UL);
        await Assert.That(MatroskaSubtitleTrackMapper.Map(vlc, vlc[2], matroska)?.Number).IsEqualTo(5UL);
        await Assert.That(MatroskaSubtitleTrackMapper.Map(vlc, vlc[0], matroska)?.Number).IsEqualTo(9UL);
    }

    [Test]
    public async Task Map_FallsBackToAUniqueMatchWhenVlcListsFewerTracks()
    {
        // VLC has no entry for one of the subtitle tracks, so positions no longer line up.
        MatroskaTrack[] matroska =
        [
            Subtitle(3, "S_HDMV/PGS", "Full"),
            Subtitle(4, "S_TEXT/ASS", "Signs"),
            Subtitle(5, "S_TEXT/ASS", "Dialogue"),
        ];
        SubtitleTrackInfo[] vlc = [Ass(1, "Signs"), Ass(2, "Dialogue")];

        await Assert.That(MatroskaSubtitleTrackMapper.Map(vlc, vlc[1], matroska)?.Number).IsEqualTo(5UL);
    }

    [Test]
    public async Task Map_FallsBackToAUniqueMatchWhenThePositionHasTheWrongCodec()
    {
        // Same counts, but the track at this position is SRT where VLC says ASS.
        MatroskaTrack[] matroska = [Subtitle(4, "S_TEXT/UTF8"), Subtitle(5, "S_TEXT/ASS")];
        SubtitleTrackInfo[] vlc = [Ass(3), Srt(4)];

        await Assert.That(MatroskaSubtitleTrackMapper.Map(vlc, vlc[0], matroska)?.Number).IsEqualTo(5UL);
    }

    [Test]
    public async Task Map_ReturnsNullWhenThePositionCannotBeTrustedAndTheFallbackIsAmbiguous()
    {
        // Counts differ and two tracks look the same. Resizing the wrong track would show the
        // wrong subtitles, so the answer has to be "keep the original".
        MatroskaTrack[] matroska = [Subtitle(4, "S_TEXT/ASS"), Subtitle(5, "S_TEXT/ASS"), Subtitle(6, "S_TEXT/ASS")];
        SubtitleTrackInfo[] vlc = [Ass(3), Ass(4)];

        await Assert.That(MatroskaSubtitleTrackMapper.Map(vlc, vlc[0], matroska)).IsNull();
    }

    [Test]
    public async Task Map_RejectsThePositionWhenTheNameDiffers()
    {
        MatroskaTrack[] matroska = [Subtitle(4, "S_TEXT/ASS", "First"), Subtitle(5, "S_TEXT/ASS", "Second")];
        SubtitleTrackInfo[] vlc = [Ass(3, "Second"), Ass(4, "First")];

        await Assert.That(MatroskaSubtitleTrackMapper.Map(vlc, vlc[0], matroska)?.Number).IsEqualTo(5UL);
    }

    [Test]
    public async Task Map_RejectsThePositionWhenLanguageCodesDiffer()
    {
        MatroskaTrack[] matroska = [Subtitle(4, "S_TEXT/UTF8", language: "jpn"), Subtitle(5, "S_TEXT/UTF8", language: "eng")];
        SubtitleTrackInfo[] vlc = [Srt(3, language: "eng"), Srt(4, language: "jpn")];

        await Assert.That(MatroskaSubtitleTrackMapper.Map(vlc, vlc[0], matroska)?.Number).IsEqualTo(5UL);
    }

    [Test]
    [Arguments("English")]     // VLC turns some codes into a language name. Not a code, so no check.
    [Arguments("eng")]
    [Arguments("en")]
    [Arguments("und")]
    [Arguments("")]
    public async Task Map_AcceptsEveryWayVlcReportsAMatchingLanguage(string vlcLanguage)
    {
        MatroskaTrack[] matroska = [Subtitle(4, "S_TEXT/UTF8", language: "eng", languageBcp47: "en")];
        SubtitleTrackInfo[] vlc = [Srt(3, language: vlcLanguage)];

        await Assert.That(MatroskaSubtitleTrackMapper.Map(vlc, vlc[0], matroska)?.Number).IsEqualTo(4UL);
    }

    [Test]
    public async Task Map_ComparesTheVlcCodecWithEveryAssCodecId()
    {
        foreach (string codecId in new[] { "S_TEXT/ASS", "S_TEXT/SSA", "S_ASS", "S_SSA" })
        {
            MatroskaTrack[] matroska = [Subtitle(4, codecId)];
            SubtitleTrackInfo[] vlc = [Ass(3)];
            await Assert.That(MatroskaSubtitleTrackMapper.Map(vlc, vlc[0], matroska)?.Number).IsEqualTo(4UL);
        }
    }

    [Test]
    public async Task Map_ReturnsNullWhenNoSubtitleTrackFits()
    {
        SubtitleTrackInfo[] vlc = [Ass(3)];

        await Assert.That(MatroskaSubtitleTrackMapper.Map(vlc, vlc[0], [Video(1), Audio(2)])).IsNull();
        await Assert.That(MatroskaSubtitleTrackMapper.Map(vlc, vlc[0], [Subtitle(4, "S_HDMV/PGS")])).IsNull();
    }
}
