using Screenbox.Core.Helpers;

namespace Screenbox.Core.Tests.Helpers;

public sealed class WatchThresholdTests
{
    [Test]
    public async Task DefaultPercent_IsNinetyPercent()
    {
        await Assert.That(WatchThreshold.DefaultPercent).IsEqualTo(0.9);
    }

    [Test]
    public async Task IsComplete_TrueAtThreshold()
    {
        var duration = TimeSpan.FromMinutes(20);
        await Assert.That(WatchThreshold.IsComplete(TimeSpan.FromMinutes(18), duration, 0.9)).IsTrue();
    }

    [Test]
    public async Task IsComplete_FalseJustBelowThreshold()
    {
        var duration = TimeSpan.FromMinutes(20);
        await Assert.That(WatchThreshold.IsComplete(TimeSpan.FromMinutes(17), duration, 0.9)).IsFalse();
    }

    [Test]
    public async Task IsComplete_FalseWhenDurationIsZeroOrNegative()
    {
        // A zero duration means the length is unknown. Treating that as complete would mark
        // everything watched the moment it opened.
        await Assert.That(WatchThreshold.IsComplete(TimeSpan.FromMinutes(5), TimeSpan.Zero, 0.9)).IsFalse();
        await Assert.That(WatchThreshold.IsComplete(TimeSpan.FromMinutes(5), TimeSpan.FromSeconds(-1), 0.9)).IsFalse();
    }

    [Test]
    public async Task IsComplete_TrueWhenPositionExceedsDuration()
    {
        var duration = TimeSpan.FromMinutes(20);
        await Assert.That(WatchThreshold.IsComplete(TimeSpan.FromMinutes(25), duration, 0.9)).IsTrue();
    }

    [Test]
    public async Task Progress_IsClampedToZeroAndOne()
    {
        var duration = TimeSpan.FromMinutes(10);
        await Assert.That(WatchThreshold.Progress(TimeSpan.FromMinutes(5), duration)).IsEqualTo(0.5);
        await Assert.That(WatchThreshold.Progress(TimeSpan.FromMinutes(-5), duration)).IsEqualTo(0d);
        await Assert.That(WatchThreshold.Progress(TimeSpan.FromMinutes(50), duration)).IsEqualTo(1d);
    }

    [Test]
    public async Task Progress_IsZeroWhenDurationUnknown()
    {
        await Assert.That(WatchThreshold.Progress(TimeSpan.FromMinutes(5), TimeSpan.Zero)).IsEqualTo(0d);
    }
}
