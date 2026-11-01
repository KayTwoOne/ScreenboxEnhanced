using Screenbox.Core.Helpers;

namespace Screenbox.Core.Tests.Helpers;

public sealed class SeriesNameNormalizerTests
{
    [Test]
    public async Task Normalize_PlainTitle_IsUnchanged()
    {
        await Assert.That(SeriesNameNormalizer.Normalize("Attack On Titan")).IsEqualTo("Attack On Titan");
    }

    [Test]
    public async Task Normalize_SeasonSuffix_StripsToBaseTitle()
    {
        await Assert.That(SeriesNameNormalizer.Normalize("Attack on Titan Season 1")).IsEqualTo("Attack on Titan");
    }

    [Test]
    public async Task Normalize_BareSeasonFolder_ReturnsEmpty()
    {
        // "Season 1" is a real Jujutsu Kaisen subfolder. It is not itself a series - the
        // caller must fall back to the parent folder name rather than search for "Season 1".
        await Assert.That(SeriesNameNormalizer.Normalize("Season 1")).IsEqualTo(string.Empty);
    }

    [Test]
    public async Task Normalize_AllBareSeasonFoldersInLibrary_ReturnEmpty()
    {
        // Jujutsu Kaisen has Season 1, Season 2 and Season 3 subfolders in the reference library.
        foreach (string folder in new[] { "Season 1", "Season 2", "Season 3" })
        {
            await Assert.That(SeriesNameNormalizer.Normalize(folder)).IsEqualTo(string.Empty);
        }
    }

    [Test]
    public async Task Normalize_TrailingNumberIsPartOfTitle_JujutsuKaisenZero()
    {
        // "Jujutsu Kaisen 0" is a real folder name - the film's title ends in "0". Stripping
        // trailing digits as a general rule would wrongly turn this into "Jujutsu Kaisen".
        await Assert.That(SeriesNameNormalizer.Normalize("Jujutsu Kaisen 0")).IsEqualTo("Jujutsu Kaisen 0");
    }

    [Test]
    public async Task Normalize_TrailingNumberIsPartOfTitle_MobPsycho100()
    {
        // "Mob Psycho 100" - the 100 belongs to the title, same trap as EpisodeInfoParser covers.
        await Assert.That(SeriesNameNormalizer.Normalize("Mob Psycho 100")).IsEqualTo("Mob Psycho 100");
    }

    [Test]
    public async Task Normalize_OrdinalSeasonSuffix_StripsToBaseTitle()
    {
        await Assert.That(SeriesNameNormalizer.Normalize("Dandadan 2nd Season")).IsEqualTo("Dandadan");
    }

    [Test]
    public async Task Normalize_UnknownFolderName_PassesThroughUnchanged()
    {
        // "hayase-cache" isn't a series and isn't a recognized specials folder either. It
        // passes through unchanged and will simply match nothing when searched - that is correct;
        // the normalizer's job is not to guess, only to avoid producing a misleading query.
        await Assert.That(SeriesNameNormalizer.Normalize("hayase-cache")).IsEqualTo("hayase-cache");
    }

    [Test]
    public async Task Normalize_SpecialsFolder_NcopAndNced_ReturnsEmpty()
    {
        await Assert.That(SeriesNameNormalizer.Normalize("NCOP & NCED")).IsEqualTo(string.Empty);
    }

    [Test]
    public async Task Normalize_SpecialsFolder_Extras_ReturnsEmpty()
    {
        await Assert.That(SeriesNameNormalizer.Normalize("Extras")).IsEqualTo(string.Empty);
    }

    [Test]
    public async Task IsNonSeriesFolder_EverySpecialsFolderName_IsTrue()
    {
        // The distinction Normalize collapses: both a specials folder and a bare season folder
        // normalize to empty, but only the specials folder is not a series at all.
        foreach (string folder in new[] { "NCOP & NCED", "Extras", "Specials", "NCOP", "NCED", "OAD", "OVA", "Bonus" })
        {
            await Assert.That(SeriesNameNormalizer.IsNonSeriesFolder(folder)).IsTrue()
                .Because($"'{folder}' is organizational, not a series.");
        }
    }

    [Test]
    public async Task IsNonSeriesFolder_IgnoresCaseAndStrayWhitespace()
    {
        await Assert.That(SeriesNameNormalizer.IsNonSeriesFolder("  ncop  &  nced ")).IsTrue();
        await Assert.That(SeriesNameNormalizer.IsNonSeriesFolder("EXTRAS")).IsTrue();
    }

    [Test]
    public async Task IsNonSeriesFolder_BareSeasonFolder_IsFalse()
    {
        // A season folder is part of a series: it may be searched via its parent. Calling it
        // non-series would stop it ever receiving the series poster.
        foreach (string folder in new[] { "Season 1", "Season 2", "2nd Season" })
        {
            await Assert.That(SeriesNameNormalizer.IsNonSeriesFolder(folder)).IsFalse()
                .Because($"'{folder}' normalizes to empty but belongs to a series.");
        }
    }

    [Test]
    public async Task IsNonSeriesFolder_OrdinarySeriesNameOrBlank_IsFalse()
    {
        await Assert.That(SeriesNameNormalizer.IsNonSeriesFolder("Jujutsu Kaisen")).IsFalse();
        await Assert.That(SeriesNameNormalizer.IsNonSeriesFolder("Extras Of The Gods")).IsFalse();
        await Assert.That(SeriesNameNormalizer.IsNonSeriesFolder("")).IsFalse();
        await Assert.That(SeriesNameNormalizer.IsNonSeriesFolder("   ")).IsFalse();
    }

    [Test]
    public async Task Normalize_EmptyInput_ReturnsEmpty()
    {
        await Assert.That(SeriesNameNormalizer.Normalize("")).IsEqualTo(string.Empty);
    }

    [Test]
    public async Task Normalize_WhitespaceOnlyInput_ReturnsEmpty()
    {
        await Assert.That(SeriesNameNormalizer.Normalize("   ")).IsEqualTo(string.Empty);
    }

    [Test]
    public async Task Normalize_CollapsesInternalWhitespace()
    {
        await Assert.That(SeriesNameNormalizer.Normalize("Attack  on   Titan")).IsEqualTo("Attack on Titan");
    }
}
