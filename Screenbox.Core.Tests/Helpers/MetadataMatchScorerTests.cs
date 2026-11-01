using Screenbox.Core.Helpers;

namespace Screenbox.Core.Tests.Helpers;

/// <summary>
/// Exercises <see cref="MetadataMatchScorer"/> as a pure function, independently of any provider or
/// coordinator - the brief for this scoring function requires exactly that isolation, since it is
/// what lets the coordinator's accept/reject line be verified without a network stub in sight.
/// </summary>
public sealed class MetadataMatchScorerTests
{
    [Test]
    public async Task Score_IdenticalStrings_Returns1()
    {
        await Assert.That(MetadataMatchScorer.Score("Dan Da Dan", "Dan Da Dan")).IsEqualTo(1.0);
    }

    [Test]
    public async Task Score_DifferingOnlyByCase_Returns1()
    {
        // Comparison must be case-insensitive - a provider's canonical casing should never be
        // treated as a mismatch against a folder-derived query.
        await Assert.That(MetadataMatchScorer.Score("attack on titan", "ATTACK ON TITAN")).IsEqualTo(1.0);
    }

    [Test]
    public async Task Score_DifferingOnlyByLeadingOrTrailingWhitespace_Returns1()
    {
        await Assert.That(MetadataMatchScorer.Score("  Frieren  ", "Frieren")).IsEqualTo(1.0);
    }

    [Test]
    public async Task Score_MinorPunctuationAndSpacingDrift_ScoresAboveTheAcceptThreshold()
    {
        // "Dandadan" is a real catalog title for a "Dan Da Dan" folder query - two inserted spaces
        // over a 10-character string, so this must clear the bar and be accepted as a match.
        double score = MetadataMatchScorer.Score("Dan Da Dan", "Dandadan");

        await Assert.That(score).IsEqualTo(0.8);
        await Assert.That(score).IsGreaterThanOrEqualTo(MetadataMatchScorer.AcceptThreshold);
    }

    [Test]
    public async Task Score_UnrelatedStrings_ScoresBelowTheAcceptThreshold()
    {
        // "hayase-cache" is a real folder in this library that is not a series at all. Whatever a
        // fuzzy provider search returns for it must not clear the bar, or the coordinator would
        // record a confident wrong match instead of a no-match.
        double score = MetadataMatchScorer.Score("hayase-cache", "Completely Unrelated Show Title");

        await Assert.That(score).IsLessThan(MetadataMatchScorer.AcceptThreshold);
    }

    [Test]
    public async Task Score_EmptyQuery_ReturnsZeroRatherThanThrowing()
    {
        await Assert.That(MetadataMatchScorer.Score(string.Empty, "Some Title")).IsEqualTo(0.0);
    }

    [Test]
    public async Task Score_EmptyCandidateTitle_ReturnsZeroRatherThanThrowing()
    {
        await Assert.That(MetadataMatchScorer.Score("Some Query", string.Empty)).IsEqualTo(0.0);
    }

    [Test]
    public async Task Score_NullArguments_ReturnZeroRatherThanThrowing()
    {
        await Assert.That(MetadataMatchScorer.Score(null, null)).IsEqualTo(0.0);
        await Assert.That(MetadataMatchScorer.Score(null, "Title")).IsEqualTo(0.0);
        await Assert.That(MetadataMatchScorer.Score("Query", null)).IsEqualTo(0.0);
    }

    [Test]
    public async Task Score_IsSymmetric()
    {
        double forward = MetadataMatchScorer.Score("Cowboy Bebop", "Cowboy Bebop: Remastered");
        double backward = MetadataMatchScorer.Score("Cowboy Bebop: Remastered", "Cowboy Bebop");

        await Assert.That(forward).IsEqualTo(backward);
    }

    [Test]
    public async Task AcceptThreshold_IsWithinTheDocumentedZeroToOneRange()
    {
        await Assert.That(MetadataMatchScorer.AcceptThreshold).IsGreaterThan(0.0);
        await Assert.That(MetadataMatchScorer.AcceptThreshold).IsLessThanOrEqualTo(1.0);
    }
}
