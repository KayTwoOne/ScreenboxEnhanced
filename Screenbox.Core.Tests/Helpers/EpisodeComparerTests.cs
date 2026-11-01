using Screenbox.Core.Helpers;

namespace Screenbox.Core.Tests.Helpers;

public sealed class EpisodeComparerTests
{
    [Test]
    public async Task Sort_OrdersEpisodesNumericallyNotLexically()
    {
        string[] names =
        [
            "Show - 10.mkv",
            "Show - 2.mkv",
            "Show - 1.mkv",
            "Show - 11.mkv"
        ];

        Array.Sort(names, EpisodeComparer.Instance);

        await Assert.That(names[0]).IsEqualTo("Show - 1.mkv");
        await Assert.That(names[1]).IsEqualTo("Show - 2.mkv");
        await Assert.That(names[2]).IsEqualTo("Show - 10.mkv");
        await Assert.That(names[3]).IsEqualTo("Show - 11.mkv");
    }

    [Test]
    public async Task Sort_OrdersBySeasonThenEpisode()
    {
        string[] names = ["Show - S02E01.mkv", "Show - S01E09.mkv", "Show - S01E10.mkv"];

        Array.Sort(names, EpisodeComparer.Instance);

        await Assert.That(names[0]).IsEqualTo("Show - S01E09.mkv");
        await Assert.That(names[1]).IsEqualTo("Show - S01E10.mkv");
        await Assert.That(names[2]).IsEqualTo("Show - S02E01.mkv");
    }

    [Test]
    public async Task Sort_PlacesSpecialsAfterRegularEpisodes()
    {
        string[] names = ["Show - NCED 01.mkv", "Show - 02.mkv", "Show - 01.mkv"];

        Array.Sort(names, EpisodeComparer.Instance);

        await Assert.That(names[0]).IsEqualTo("Show - 01.mkv");
        await Assert.That(names[1]).IsEqualTo("Show - 02.mkv");
        await Assert.That(names[2]).IsEqualTo("Show - NCED 01.mkv");
    }

    [Test]
    public async Task Sort_FallsBackToNameForUnparsableItems()
    {
        string[] names = ["Beta.mkv", "Alpha.mkv"];

        Array.Sort(names, EpisodeComparer.Instance);

        await Assert.That(names[0]).IsEqualTo("Alpha.mkv");
        await Assert.That(names[1]).IsEqualTo("Beta.mkv");
    }

    [Test]
    public async Task Sort_IsStableForIdenticalEpisodeNumbers()
    {
        // Two versions of the same episode must not be reordered arbitrarily.
        await Assert.That(EpisodeComparer.Instance.Compare("Show - 01.mkv", "Show - 01.mkv")).IsEqualTo(0);
    }
}
