using Screenbox.Core.Helpers;
using Screenbox.Core.Models;

namespace Screenbox.Core.Tests.Helpers;

public sealed class EpisodeInfoParserTests
{
    [Test]
    public async Task Parse_SeasonXEpisodeFormat()
    {
        EpisodeInfo info = EpisodeInfoParser.Parse("Death Note - 01x02.mkv");
        await Assert.That(info.Season).IsEqualTo(1);
        await Assert.That(info.Episode).IsEqualTo(2);
    }

    [Test]
    public async Task Parse_SxxExxFormatWithGroupAndTags()
    {
        EpisodeInfo info = EpisodeInfoParser.Parse(
            "[Starbez] Gachiakuta - S01E02 (BD 1080p HEVC Opus) [Dual Audio] [03153BDA].mkv");
        await Assert.That(info.Season).IsEqualTo(1);
        await Assert.That(info.Episode).IsEqualTo(2);
        await Assert.That(info.IsSpecial).IsFalse();
    }

    [Test]
    public async Task Parse_BareNumberWithVersionSuffix()
    {
        EpisodeInfo info = EpisodeInfoParser.Parse("[sam] Chainsaw Man - 02v2 [BD 1080p FLAC] [0BB54194].mkv");
        await Assert.That(info.Episode).IsEqualTo(2);
        await Assert.That(info.Version).IsEqualTo(2);
    }

    [Test]
    public async Task Parse_BareNumberWithGroupPrefix()
    {
        EpisodeInfo info = EpisodeInfoParser.Parse("[Anime Time] Attack on Titan - 01.mkv");
        await Assert.That(info.Episode).IsEqualTo(1);
    }

    [Test]
    public async Task Parse_PaddedNumberWithEpisodeTitle()
    {
        EpisodeInfo info = EpisodeInfoParser.Parse("Jujutsu Kaisen - 001 - Ryoumen Sukuna.mkv");
        await Assert.That(info.Episode).IsEqualTo(1);
        await Assert.That(info.Title).IsEqualTo("Ryoumen Sukuna");
    }

    [Test]
    public async Task Parse_MovieWithUnderscoresAndNoEpisodeNumber()
    {
        EpisodeInfo info = EpisodeInfoParser.Parse("[DB]Kimi no Na wa._-_(Dual Audio_10bit_BD1080p_x265).mkv");
        await Assert.That(info.Episode).IsNull();
        await Assert.That(info.IsMovie).IsTrue();
    }

    [Test]
    public async Task Parse_MovieWithEnDashSeparator()
    {
        EpisodeInfo info = EpisodeInfoParser.Parse("[Judas] Chainsaw Man – The Movie Reze Arc.mkv");
        await Assert.That(info.Episode).IsNull();
        await Assert.That(info.IsMovie).IsTrue();
    }

    [Test]
    public async Task Parse_NonCreditEndingIsSpecialNotEpisodeOne()
    {
        EpisodeInfo info = EpisodeInfoParser.Parse("[sam] Chainsaw Man - NCED 01 [BD 1080p FLAC] [0719C366].mkv");
        await Assert.That(info.IsSpecial).IsTrue();
    }

    [Test]
    public async Task Parse_SeriesNameEndingInNumberIsNotAnEpisode()
    {
        // "Mob Psycho 100" — the 100 belongs to the title. No hyphen separator, so no episode.
        EpisodeInfo info = EpisodeInfoParser.Parse("Mob Psycho 100.mkv");
        await Assert.That(info.Episode).IsNull();
    }

    [Test]
    public async Task Parse_FilmNamedWithATrailingZeroIsNotEpisodeZero()
    {
        // "Jujutsu Kaisen 0" is a film.
        EpisodeInfo info = EpisodeInfoParser.Parse("Jujutsu Kaisen 0.mkv");
        await Assert.That(info.Episode).IsNull();
        await Assert.That(info.IsMovie).IsTrue();
    }

    [Test]
    public async Task Parse_ParentFolderMarksItemAsSpecial()
    {
        // A file inside Extras is a special regardless of its own name.
        EpisodeInfo info = EpisodeInfoParser.Parse("Chainsaw Man - 01.mkv", parentFolderName: "Extras");
        await Assert.That(info.IsSpecial).IsTrue();
    }

    [Test]
    public async Task Parse_ParentFolderNamesCoveringKnownSpecialsFolders()
    {
        foreach (string folder in new[] { "Extras", "Specials", "NCOP & NCED", "OAD" })
        {
            EpisodeInfo info = EpisodeInfoParser.Parse("Show - 03.mkv", parentFolderName: folder);
            await Assert.That(info.IsSpecial).IsTrue();
        }
    }

    [Test]
    public async Task Parse_OrdinaryParentFolderDoesNotMarkSpecial()
    {
        EpisodeInfo info = EpisodeInfoParser.Parse("Show - 03.mkv", parentFolderName: "Season 1");
        await Assert.That(info.IsSpecial).IsFalse();
        await Assert.That(info.Episode).IsEqualTo(3);
    }

    [Test]
    public async Task Parse_CrcHashIsNotMistakenForAnEpisodeNumber()
    {
        // An 8-character hex CRC can be all digits, e.g. [03153BDA] vs [12345678].
        EpisodeInfo info = EpisodeInfoParser.Parse("[grp] Show - 04 [1080p] [12345678].mkv");
        await Assert.That(info.Episode).IsEqualTo(4);
    }

    [Test]
    public async Task Parse_EmptyOrExtensionOnlyInputDoesNotThrow()
    {
        await Assert.That(EpisodeInfoParser.Parse("").Episode).IsNull();
        await Assert.That(EpisodeInfoParser.Parse(".mkv").Episode).IsNull();
    }
}
