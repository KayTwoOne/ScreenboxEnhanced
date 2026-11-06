using Screenbox.Core.Playback;

namespace Screenbox.Core.Tests.Helpers;

public sealed class GeneratedSubtitleTracksTests
{
    [Test]
    public async Task TryClaim_ClaimsNothingWhenNoFileIsExpected()
    {
        // A subtitle the user added must reach the track list as usual.
        GeneratedSubtitleTracks tracks = new();

        await Assert.That(tracks.TryClaim(6, isKnownTrack: false)).IsFalse();
        await Assert.That(tracks.Contains(6)).IsFalse();
    }

    [Test]
    public async Task TryClaim_ClaimsTheNewEsThatFollowsAnExpectedFile()
    {
        GeneratedSubtitleTracks tracks = new();
        tracks.Expect();

        await Assert.That(tracks.TryClaim(6, isKnownTrack: false)).IsTrue();
        await Assert.That(tracks.Contains(6)).IsTrue();
    }

    [Test]
    public async Task TryClaim_UsesUpTheExpectation()
    {
        GeneratedSubtitleTracks tracks = new();
        tracks.Expect();
        tracks.TryClaim(6, isKnownTrack: false);

        await Assert.That(tracks.TryClaim(7, isKnownTrack: false)).IsFalse();
        await Assert.That(tracks.Contains(7)).IsFalse();
    }

    [Test]
    public async Task TryClaim_MatchesEachExpectedFileToOneEs()
    {
        GeneratedSubtitleTracks tracks = new();
        tracks.Expect();
        tracks.Expect();

        await Assert.That(tracks.TryClaim(6, isKnownTrack: false)).IsTrue();
        await Assert.That(tracks.TryClaim(7, isKnownTrack: false)).IsTrue();
        await Assert.That(tracks.TryClaim(8, isKnownTrack: false)).IsFalse();
    }

    [Test]
    public async Task TryClaim_LeavesAKnownTrackAlone()
    {
        // An embedded track's ES appearing while a file is expected is not the generated one, and
        // must not use up the expectation.
        GeneratedSubtitleTracks tracks = new();
        tracks.Expect();

        await Assert.That(tracks.TryClaim(3, isKnownTrack: true)).IsFalse();
        await Assert.That(tracks.Contains(3)).IsFalse();
        await Assert.That(tracks.TryClaim(6, isKnownTrack: false)).IsTrue();
    }

    [Test]
    public async Task TryClaim_IsTrueAgainForAnEsAlreadyClaimed()
    {
        GeneratedSubtitleTracks tracks = new();
        tracks.Expect();
        tracks.TryClaim(6, isKnownTrack: false);

        await Assert.That(tracks.TryClaim(6, isKnownTrack: false)).IsTrue();
    }

    [Test]
    public async Task CancelExpected_RemovesAnExpectationThatWillNotArrive()
    {
        GeneratedSubtitleTracks tracks = new();
        tracks.Expect();
        tracks.CancelExpected();

        await Assert.That(tracks.TryClaim(6, isKnownTrack: false)).IsFalse();

        // With nothing expected, cancelling again must not build up credit.
        tracks.CancelExpected();
        tracks.Expect();
        await Assert.That(tracks.TryClaim(7, isKnownTrack: false)).IsTrue();
    }

    [Test]
    public async Task Reset_ForgetsEverything()
    {
        // VLC restarts its ES ids when the input restarts, so an old id could come back as a
        // subtitle the user added.
        GeneratedSubtitleTracks tracks = new();
        tracks.Expect();
        tracks.TryClaim(6, isKnownTrack: false);
        tracks.Expect();

        tracks.Reset();

        await Assert.That(tracks.Contains(6)).IsFalse();
        await Assert.That(tracks.TryClaim(6, isKnownTrack: false)).IsFalse();
    }
}
