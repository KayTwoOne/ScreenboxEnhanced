using Screenbox.Core.Helpers;

namespace Screenbox.Core.Tests.Helpers;

public sealed class SubtitleScaleRangeTests
{
    [Test]
    public async Task Default_IsOneHundredPercent()
    {
        await Assert.That(SubtitleScaleRange.Default).IsEqualTo(100);
        await Assert.That(new TestSettingsService().SubtitleScale).IsEqualTo(SubtitleScaleRange.Default);
    }

    [Test]
    [Arguments(100, 100)]
    [Arguments(50, 50)]
    [Arguments(250, 250)]
    [Arguments(49, 50)]
    [Arguments(0, 50)]
    [Arguments(-100, 50)]
    [Arguments(251, 250)]
    [Arguments(int.MaxValue, 250)]
    public async Task Clamp_LimitsToTheSupportedRange(int value, int expected)
    {
        await Assert.That(SubtitleScaleRange.Clamp(value)).IsEqualTo(expected);
    }

    [Test]
    [Arguments(150d, 150)]
    [Arguments(149.6, 150)]
    [Arguments(150.4, 150)]
    [Arguments(10d, 50)]
    [Arguments(900d, 250)]
    public async Task FromSlider_RoundsAndClamps(double value, int expected)
    {
        await Assert.That(SubtitleScaleRange.FromSlider(value)).IsEqualTo(expected);
    }

    [Test]
    public async Task TestSettingsService_ClampsLikeTheRealService()
    {
        TestSettingsService settings = new() { SubtitleScale = 400 };
        await Assert.That(settings.SubtitleScale).IsEqualTo(250);
        settings.SubtitleScale = 10;
        await Assert.That(settings.SubtitleScale).IsEqualTo(50);
    }
}
