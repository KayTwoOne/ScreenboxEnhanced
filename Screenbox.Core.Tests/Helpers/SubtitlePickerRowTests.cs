using Screenbox.Core.Helpers;

namespace Screenbox.Core.Tests.Helpers;

public sealed class SubtitlePickerRowTests
{
    [Test]
    public async Task ALostSelection_IsNotTheDisableRow()
    {
        // A ListView reports -1 while its items are being rebuilt. Treating that as "Disable" turned
        // subtitles off every time the menu opened on a new video.
        await Assert.That(SubtitlePickerRow.TryGetTrackIndex(-1, out _)).IsFalse();
    }

    [Test]
    public async Task TheFirstRow_TurnsSubtitlesOff()
    {
        await Assert.That(SubtitlePickerRow.TryGetTrackIndex(0, out int trackIndex)).IsTrue();
        await Assert.That(trackIndex).IsEqualTo(-1);
    }

    [Test]
    [Arguments(1, 0)]
    [Arguments(2, 1)]
    [Arguments(9, 8)]
    public async Task OtherRows_AreOffsetByTheDisableRow(int row, int expected)
    {
        await Assert.That(SubtitlePickerRow.TryGetTrackIndex(row, out int trackIndex)).IsTrue();
        await Assert.That(trackIndex).IsEqualTo(expected);
    }
}
