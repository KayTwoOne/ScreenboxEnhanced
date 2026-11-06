using System.Text;
using Microsoft.Extensions.Logging;
using Screenbox.Core.Contexts;
using Screenbox.Core.Coordinators;
using Screenbox.Core.Helpers;
using Screenbox.Core.Messages;
using Screenbox.Core.Models;
using Screenbox.Core.Services;
using Screenbox.Core.Tests.Helpers;

namespace Screenbox.Core.Tests.Coordinators;

public sealed class SubtitleResizeCoordinatorTests
{
    private const string StyleFormat =
        "Format: Name, Fontname, Fontsize, PrimaryColour, SecondaryColour, OutlineColour, BackColour, Bold, Italic, Underline, StrikeOut, ScaleX, ScaleY, Spacing, Angle, BorderStyle, Outline, Shadow, Alignment, MarginL, MarginR, MarginV, Encoding";

    private static string AssHeader(int fontSize = 60) =>
        "[Script Info]\nScriptType: v4.00+\n\n[V4+ Styles]\n" + StyleFormat + "\n" +
        $"Style: Main,Arial,{fontSize},&H00FFFFFF,&H000000FF,&H00000000,&H80000000,0,0,0,0,100,100,0,0,1,3,1,2,40,40,40,1\n\n" +
        "[Events]\nFormat: Layer, Start, End, Style, Name, MarginL, MarginR, MarginV, Effect, Text\n";

    /// <summary>TrackNumbers 2 and 3 are ASS, 4 is SRT, 5 is PGS. ES ids 0 to 3 in that order.</summary>
    private static byte[] SampleFile() => new MatroskaTestFile()
        .AddTrack(2, "S_TEXT/ASS", codecPrivate: AssHeader(), name: "Dialogue")
        .AddTrack(3, "S_TEXT/ASS", codecPrivate: AssHeader(40), name: "Signs")
        .AddTrack(4, "S_TEXT/UTF8", name: "SRT")
        .AddTrack(5, "S_HDMV/PGS", name: "Images")
        .AddCluster(0,
            MatroskaTestFile.BlockGroup(2, 0, "0,0,Main,,0,0,0,,dialogue line", 1000),
            MatroskaTestFile.BlockGroup(3, 0, "0,0,Main,,0,0,0,,sign line", 1000),
            MatroskaTestFile.BlockGroup(4, 0, "srt line", 1000),
            MatroskaTestFile.BlockGroup(5, 0, new byte[4], 1000))
        .Build();

    private static readonly SubtitleTrackInfo Dialogue = new(0, SubtitleTrackInfo.AssCodec, "Dialogue", "eng");
    private static readonly SubtitleTrackInfo Signs = new(1, SubtitleTrackInfo.AssCodec, "Signs", "eng");
    private static readonly SubtitleTrackInfo Srt = new(2, SubtitleTrackInfo.SrtCodec, "SRT", "eng");
    private static readonly SubtitleTrackInfo Images = new(3, "pgss", "Images", "eng");

    private static FakeTarget Target(SubtitleTrackInfo? selected = null, byte[]? file = null) =>
        new(file ?? SampleFile()) { SelectedTrack = selected ?? Dialogue };

    private static SubtitleResizeCoordinator Coordinator(
        TestSettingsService settings, FakeStore store, TimeSpan? delay = null, RecordingLogger? logger = null) =>
        new(settings, store, new PlayerContext(), logger ?? new RecordingLogger(), delay ?? TimeSpan.Zero);

    private static SettingsChangedMessage ScaleChanged() => new(nameof(ISettingsService.SubtitleScale), typeof(SubtitleResizeCoordinatorTests));

    [Test]
    public async Task AtOneHundredPercent_NothingIsReadAndTheOriginalStaysSelected()
    {
        TestSettingsService settings = new() { SubtitleScale = 100 };
        FakeStore store = new();
        FakeTarget target = Target();
        SubtitleResizeCoordinator coordinator = Coordinator(settings, store);

        coordinator.AttachTarget(target);
        await coordinator.PendingUpdate;

        await Assert.That(target.SourceRequests).IsEqualTo(0);
        await Assert.That(target.OpenCount).IsEqualTo(0);
        await Assert.That(store.Written.Count).IsEqualTo(0);
        await Assert.That(target.ShownResized.Count).IsEqualTo(0);
        await Assert.That(target.ShownOriginal.Count).IsEqualTo(0);
    }

    [Test]
    public async Task AboveOneHundredPercent_PlaysAResizedFileBuiltFromTheSelectedTrack()
    {
        TestSettingsService settings = new() { SubtitleScale = 150 };
        FakeStore store = new();
        FakeTarget target = Target();
        SubtitleResizeCoordinator coordinator = Coordinator(settings, store);

        coordinator.AttachTarget(target);
        await coordinator.PendingUpdate;

        await Assert.That(store.Written.Count).IsEqualTo(1);
        await Assert.That(store.Written[0]).Contains("Style: Main,Arial,90,");
        await Assert.That(store.Written[0]).Contains("dialogue line");
        await Assert.That(store.Written[0]).DoesNotContain("sign line");
        await Assert.That(target.ShownResized).IsEquivalentTo(new[] { store.Mrls[0] });
    }

    [Test]
    public async Task ScaleChange_ReusesTheCachedTrackInsteadOfReadingTheFileAgain()
    {
        TestSettingsService settings = new() { SubtitleScale = 150 };
        FakeStore store = new();
        FakeTarget target = Target();
        SubtitleResizeCoordinator coordinator = Coordinator(settings, store);
        coordinator.AttachTarget(target);
        await coordinator.PendingUpdate;
        await Assert.That(target.OpenCount).IsEqualTo(1);

        settings.SubtitleScale = 200;
        coordinator.Receive(ScaleChanged());
        await coordinator.PendingUpdate;

        await Assert.That(target.OpenCount).IsEqualTo(1);
        await Assert.That(store.Written.Count).IsEqualTo(2);
        await Assert.That(store.Written[1]).Contains("Style: Main,Arial,120,");
        await Assert.That(target.ShownResized.Count).IsEqualTo(2);
        await Assert.That(target.ShownResized[1]).IsEqualTo(store.Mrls[1]);
    }

    [Test]
    public async Task ReturningToOneHundredPercent_SwitchesBackToTheEmbeddedTrack()
    {
        TestSettingsService settings = new() { SubtitleScale = 150 };
        FakeStore store = new();
        FakeTarget target = Target();
        SubtitleResizeCoordinator coordinator = Coordinator(settings, store);
        coordinator.AttachTarget(target);
        await coordinator.PendingUpdate;

        settings.SubtitleScale = 100;
        coordinator.Receive(ScaleChanged());
        await coordinator.PendingUpdate;

        await Assert.That(target.ShownOriginal).IsEquivalentTo(new[] { Dialogue });
        await Assert.That(store.Written.Count).IsEqualTo(1);
        await Assert.That(target.OpenCount).IsEqualTo(1);
    }

    [Test]
    public async Task OtherSettingsChanges_AreIgnored()
    {
        TestSettingsService settings = new() { SubtitleScale = 150 };
        FakeStore store = new();
        FakeTarget target = Target();
        SubtitleResizeCoordinator coordinator = Coordinator(settings, store);
        coordinator.AttachTarget(target);
        await coordinator.PendingUpdate;

        settings.SubtitleScale = 200;
        coordinator.Receive(new SettingsChangedMessage(nameof(ISettingsService.PlayerShowChapters), typeof(SubtitleResizeCoordinatorTests)));
        await coordinator.PendingUpdate;

        await Assert.That(store.Written.Count).IsEqualTo(1);
    }

    [Test]
    public async Task RapidScaleChanges_OnlyTheLastOneBuildsAFile()
    {
        // A slider drag sends a change per step. VLC cannot remove a track once it is added, so
        // each file that got as far as VLC would stay loaded until playback ends.
        // The track is cached first, so without the wait each change would run to the end at once.
        TestSettingsService settings = new() { SubtitleScale = 150 };
        FakeStore store = new();
        FakeTarget target = Target();
        SubtitleResizeCoordinator coordinator = Coordinator(settings, store, TimeSpan.FromMilliseconds(50));
        coordinator.AttachTarget(target);
        await coordinator.PendingUpdate;

        foreach (int scale in new[] { 110, 120, 130 })
        {
            settings.SubtitleScale = scale;
            coordinator.Receive(ScaleChanged());
        }

        await coordinator.PendingUpdate;

        await Assert.That(store.Written.Count).IsEqualTo(2);
        await Assert.That(store.Written[1]).Contains("Style: Main,Arial,78,");
    }

    [Test]
    public async Task SelectingAnotherTrack_ResizesThatTrack()
    {
        TestSettingsService settings = new() { SubtitleScale = 150 };
        FakeStore store = new();
        FakeTarget target = Target();
        SubtitleResizeCoordinator coordinator = Coordinator(settings, store);
        coordinator.AttachTarget(target);
        await coordinator.PendingUpdate;

        target.SelectedTrack = Srt;
        coordinator.OnSelectionChanged();
        await coordinator.PendingUpdate;

        await Assert.That(store.Written[1]).Contains("srt line");
        await Assert.That(target.ShownResized.Count).IsEqualTo(2);
    }

    [Test]
    public async Task ReturningToATrack_ResizesItAgainFromTheCache()
    {
        TestSettingsService settings = new() { SubtitleScale = 150 };
        FakeStore store = new();
        FakeTarget target = Target();
        SubtitleResizeCoordinator coordinator = Coordinator(settings, store);
        coordinator.AttachTarget(target);
        await coordinator.PendingUpdate;
        target.SelectedTrack = Srt;
        coordinator.OnSelectionChanged();
        await coordinator.PendingUpdate;
        int opens = target.OpenCount;

        target.SelectedTrack = Dialogue;
        coordinator.OnSelectionChanged();
        await coordinator.PendingUpdate;

        // Selecting a track makes the player switch to that track's own ES, so even though this
        // track and size were shown earlier, the resized file has to be selected again.
        await Assert.That(target.OpenCount).IsEqualTo(opens);
        await Assert.That(target.ShownResized.Count).IsEqualTo(3);
    }

    [Test]
    public async Task ReturningToATrackBeforeTheOtherTrackWasRead_SelectsItsResizedFileAgain()
    {
        // The player switched to the other track's ES and back to this track's own ES, so the
        // resized file is no longer what is playing, even though nothing else was shown in between.
        TestSettingsService settings = new() { SubtitleScale = 150 };
        FakeStore store = new();
        FakeTarget target = Target();
        SubtitleResizeCoordinator coordinator = Coordinator(settings, store);
        coordinator.AttachTarget(target);
        await coordinator.PendingUpdate;

        TaskCompletionSource gate = new();
        target.OpenGate = gate.Task;
        target.SelectedTrack = Srt;
        coordinator.OnSelectionChanged();
        Task abandoned = coordinator.PendingUpdate;

        target.OpenGate = null;
        target.SelectedTrack = Dialogue;
        coordinator.OnSelectionChanged();
        await coordinator.PendingUpdate;
        gate.SetResult();
        await abandoned;

        await Assert.That(target.ShownResized.Count).IsEqualTo(2);
    }

    [Test]
    public async Task TurningSubtitlesOff_ShowsNothing()
    {
        TestSettingsService settings = new() { SubtitleScale = 150 };
        FakeStore store = new();
        FakeTarget target = Target();
        SubtitleResizeCoordinator coordinator = Coordinator(settings, store);
        coordinator.AttachTarget(target);
        await coordinator.PendingUpdate;

        target.SelectedTrack = null;
        coordinator.OnSelectionChanged();
        await coordinator.PendingUpdate;

        // The player has already turned subtitles off. Nothing may turn a track back on.
        await Assert.That(target.ShownResized.Count).IsEqualTo(1);
        await Assert.That(target.ShownOriginal.Count).IsEqualTo(0);
    }

    [Test]
    public async Task SelectingATrackAtOneHundredPercentAfterResizing_LeavesThePlayerToSwitch()
    {
        TestSettingsService settings = new() { SubtitleScale = 150 };
        FakeStore store = new();
        FakeTarget target = Target();
        SubtitleResizeCoordinator coordinator = Coordinator(settings, store);
        coordinator.AttachTarget(target);
        await coordinator.PendingUpdate;

        settings.SubtitleScale = 100;
        target.SelectedTrack = Srt;
        coordinator.OnSelectionChanged();
        await coordinator.PendingUpdate;

        await Assert.That(target.ShownOriginal.Count).IsEqualTo(0);
        await Assert.That(store.Written.Count).IsEqualTo(1);
    }

    [Test]
    public async Task ImageSubtitles_AreNeverRead()
    {
        TestSettingsService settings = new() { SubtitleScale = 150 };
        FakeStore store = new();
        FakeTarget target = Target(Images);
        SubtitleResizeCoordinator coordinator = Coordinator(settings, store);

        coordinator.AttachTarget(target);
        await coordinator.PendingUpdate;

        await Assert.That(target.OpenCount).IsEqualTo(0);
        await Assert.That(target.ShownResized.Count).IsEqualTo(0);
    }

    [Test]
    public async Task ASourceThatIsNotALocalMkv_DoesNothing()
    {
        TestSettingsService settings = new() { SubtitleScale = 150 };
        FakeStore store = new();
        FakeTarget target = Target();
        target.HasSource = false;
        RecordingLogger logger = new();
        SubtitleResizeCoordinator coordinator = Coordinator(settings, store, logger: logger);

        coordinator.AttachTarget(target);
        await coordinator.PendingUpdate;

        await Assert.That(target.OpenCount).IsEqualTo(0);
        await Assert.That(store.Written.Count).IsEqualTo(0);
        await Assert.That(target.ShownResized.Count).IsEqualTo(0);

        // This is an ordinary case, so it must not show up as a failure in the logs.
        await Assert.That(logger.ErrorCount).IsEqualTo(0);
    }

    [Test]
    public async Task ATrackThatCannotBeMatchedToTheFile_KeepsTheOriginal()
    {
        // VLC lists one track where the file has two that look the same. Picking one could show
        // the wrong subtitles.
        byte[] file = new MatroskaTestFile()
            .AddTrack(2, "S_TEXT/ASS", codecPrivate: AssHeader())
            .AddTrack(3, "S_TEXT/ASS", codecPrivate: AssHeader())
            .AddCluster(0,
                MatroskaTestFile.BlockGroup(2, 0, "0,0,Main,,0,0,0,,one", 1000),
                MatroskaTestFile.BlockGroup(3, 0, "0,0,Main,,0,0,0,,two", 1000))
            .Build();
        SubtitleTrackInfo track = new(0, SubtitleTrackInfo.AssCodec, string.Empty, string.Empty);
        TestSettingsService settings = new() { SubtitleScale = 150 };
        FakeStore store = new();
        FakeTarget target = Target(track, file);
        target.EmbeddedTracks = new[] { track };
        SubtitleResizeCoordinator coordinator = Coordinator(settings, store);

        coordinator.AttachTarget(target);
        await coordinator.PendingUpdate;

        await Assert.That(store.Written.Count).IsEqualTo(0);
        await Assert.That(target.ShownResized.Count).IsEqualTo(0);
    }

    [Test]
    public async Task AnEncryptedTrack_KeepsTheOriginal()
    {
        byte[] file = new MatroskaTestFile()
            .AddTrack(2, "S_TEXT/ASS", contentEncodings: MatroskaTestFile.Encryption())
            .AddCluster(0, MatroskaTestFile.BlockGroup(2, 0, "secret", 1000))
            .Build();
        TestSettingsService settings = new() { SubtitleScale = 150 };
        FakeStore store = new();
        FakeTarget target = Target(new SubtitleTrackInfo(0, SubtitleTrackInfo.AssCodec, string.Empty, string.Empty), file);
        target.EmbeddedTracks = [target.SelectedTrack!];
        SubtitleResizeCoordinator coordinator = Coordinator(settings, store);

        coordinator.AttachTarget(target);
        await coordinator.PendingUpdate;

        await Assert.That(store.Written.Count).IsEqualTo(0);
        await Assert.That(target.ShownResized.Count).IsEqualTo(0);
    }

    [Test]
    public async Task ACorruptFile_KeepsTheOriginalAndIsNotReadAgainOnEveryUpdate()
    {
        TestSettingsService settings = new() { SubtitleScale = 150 };
        FakeStore store = new();
        FakeTarget target = Target(file: Encoding.UTF8.GetBytes("this is not a matroska file at all"));
        SubtitleResizeCoordinator coordinator = Coordinator(settings, store);

        coordinator.AttachTarget(target);
        await coordinator.PendingUpdate;
        coordinator.OnPlaybackResumed();
        await coordinator.PendingUpdate;

        await Assert.That(store.Written.Count).IsEqualTo(0);
        await Assert.That(target.ShownResized.Count).IsEqualTo(0);
        await Assert.That(target.OpenCount).IsEqualTo(1);
    }

    [Test]
    public async Task ATrackWithNoBlocks_KeepsTheOriginal()
    {
        byte[] file = new MatroskaTestFile()
            .AddTrack(2, "S_TEXT/ASS", codecPrivate: AssHeader(), name: "Dialogue")
            .AddCluster(0, MatroskaTestFile.BlockGroup(9, 0, "another track", 1000))
            .Build();
        TestSettingsService settings = new() { SubtitleScale = 150 };
        FakeStore store = new();
        FakeTarget target = Target(file: file);
        target.EmbeddedTracks = [Dialogue];
        SubtitleResizeCoordinator coordinator = Coordinator(settings, store);

        coordinator.AttachTarget(target);
        await coordinator.PendingUpdate;

        await Assert.That(target.ShownResized.Count).IsEqualTo(0);
    }

    [Test]
    public async Task WhileThePlayerIsNotReady_NothingHappensUntilPlaybackStarts()
    {
        // A generated ES added while the input is still opening could be mistaken for an embedded one.
        TestSettingsService settings = new() { SubtitleScale = 150 };
        FakeStore store = new();
        FakeTarget target = Target();
        target.IsReady = false;
        SubtitleResizeCoordinator coordinator = Coordinator(settings, store);

        coordinator.AttachTarget(target);
        await coordinator.PendingUpdate;
        await Assert.That(target.OpenCount).IsEqualTo(0);

        target.IsReady = true;
        coordinator.OnPlaybackResumed();
        await coordinator.PendingUpdate;

        await Assert.That(target.ShownResized.Count).IsEqualTo(1);
    }

    [Test]
    public async Task PlaybackResuming_DoesNotAddTheSameFileAgain()
    {
        TestSettingsService settings = new() { SubtitleScale = 150 };
        FakeStore store = new();
        FakeTarget target = Target();
        SubtitleResizeCoordinator coordinator = Coordinator(settings, store);
        coordinator.AttachTarget(target);
        await coordinator.PendingUpdate;

        coordinator.OnPlaybackResumed();
        await coordinator.PendingUpdate;

        await Assert.That(store.Written.Count).IsEqualTo(1);
        await Assert.That(target.ShownResized.Count).IsEqualTo(1);
    }

    [Test]
    public async Task InputRestart_ReleasesTheFilesAndResizesAgainFromTheCache()
    {
        // VLC drops slaves when the input restarts, for example on a replay or when looping.
        TestSettingsService settings = new() { SubtitleScale = 150 };
        FakeStore store = new();
        FakeTarget target = Target();
        SubtitleResizeCoordinator coordinator = Coordinator(settings, store);
        coordinator.AttachTarget(target);
        await coordinator.PendingUpdate;

        coordinator.OnInputRestarted();
        await coordinator.PendingRelease;
        await Assert.That(store.Released).IsEquivalentTo(new[] { store.Mrls[0] });

        coordinator.OnPlaybackResumed();
        await coordinator.PendingUpdate;

        await Assert.That(target.OpenCount).IsEqualTo(1);
        await Assert.That(target.ShownResized.Count).IsEqualTo(2);
    }

    [Test]
    public async Task PlaybackEnding_ReleasesTheGeneratedFiles()
    {
        TestSettingsService settings = new() { SubtitleScale = 150 };
        FakeStore store = new();
        FakeTarget target = Target();
        SubtitleResizeCoordinator coordinator = Coordinator(settings, store);
        coordinator.AttachTarget(target);
        await coordinator.PendingUpdate;
        settings.SubtitleScale = 200;
        coordinator.Receive(ScaleChanged());
        await coordinator.PendingUpdate;

        coordinator.OnPlaybackEnded();
        await coordinator.PendingRelease;

        await Assert.That(store.Released).IsEquivalentTo(store.Mrls);
    }

    [Test]
    public async Task MovingToAnotherItem_ReleasesTheFilesOfThePreviousOne()
    {
        TestSettingsService settings = new() { SubtitleScale = 150 };
        FakeStore store = new();
        FakeTarget first = Target();
        FakeTarget second = Target();
        second.Path = @"D:\Media\second.mkv";
        SubtitleResizeCoordinator coordinator = Coordinator(settings, store);
        coordinator.AttachTarget(first);
        await coordinator.PendingUpdate;

        coordinator.AttachTarget(second);
        await coordinator.PendingRelease;
        await coordinator.PendingUpdate;

        await Assert.That(store.Released).IsEquivalentTo(new[] { store.Mrls[0] });
        await Assert.That(second.OpenCount).IsEqualTo(1);
        await Assert.That(second.ShownResized.Count).IsEqualTo(1);
    }

    [Test]
    public async Task AFileThatChangedOnDisk_IsReadAgain()
    {
        TestSettingsService settings = new() { SubtitleScale = 150 };
        FakeStore store = new();
        FakeTarget target = Target();
        SubtitleResizeCoordinator coordinator = Coordinator(settings, store);
        coordinator.AttachTarget(target);
        await coordinator.PendingUpdate;

        target.Modified = target.Modified.AddMinutes(1);
        settings.SubtitleScale = 200;
        coordinator.Receive(ScaleChanged());
        await coordinator.PendingUpdate;

        await Assert.That(target.OpenCount).IsEqualTo(2);
    }

    [Test]
    public async Task TheCacheKeepsAFewTracksAndEvictsTheOldest()
    {
        // Six ASS tracks: selecting them in turn pushes the first out of a cache of four.
        MatroskaTestFile builder = new();
        List<SubtitleTrackInfo> tracks = [];
        for (ulong number = 1; number <= 6; number++)
        {
            builder.AddTrack(number, "S_TEXT/ASS", codecPrivate: AssHeader(), name: $"Track {number}");
            builder.AddCluster(number * 1000, MatroskaTestFile.BlockGroup(number, 0, $"0,0,Main,,0,0,0,,line {number}", 100));
            tracks.Add(new SubtitleTrackInfo((int)number - 1, SubtitleTrackInfo.AssCodec, $"Track {number}", "eng"));
        }

        TestSettingsService settings = new() { SubtitleScale = 150 };
        FakeStore store = new();
        FakeTarget target = Target(tracks[0], builder.Build());
        target.EmbeddedTracks = tracks;
        SubtitleResizeCoordinator coordinator = Coordinator(settings, store);
        coordinator.AttachTarget(target);
        await coordinator.PendingUpdate;
        foreach (SubtitleTrackInfo track in tracks.Skip(1))
        {
            target.SelectedTrack = track;
            coordinator.OnSelectionChanged();
            await coordinator.PendingUpdate;
        }

        int opens = target.OpenCount;

        target.SelectedTrack = tracks[5];
        coordinator.OnSelectionChanged();
        await coordinator.PendingUpdate;
        await Assert.That(target.OpenCount).IsEqualTo(opens);

        target.SelectedTrack = tracks[0];
        coordinator.OnSelectionChanged();
        await coordinator.PendingUpdate;
        await Assert.That(target.OpenCount).IsEqualTo(opens + 1);
    }

    [Test]
    public async Task ASelectionChangeDuringARead_DiscardsTheStaleResult()
    {
        TestSettingsService settings = new() { SubtitleScale = 150 };
        FakeStore store = new();
        FakeTarget target = Target();
        TaskCompletionSource gate = new();
        target.OpenGate = gate.Task;
        SubtitleResizeCoordinator coordinator = Coordinator(settings, store);
        coordinator.AttachTarget(target);
        Task stale = coordinator.PendingUpdate;

        target.OpenGate = null;
        target.SelectedTrack = Srt;
        coordinator.OnSelectionChanged();
        Task current = coordinator.PendingUpdate;
        gate.SetResult();
        await stale;
        await current;

        await Assert.That(store.Written.Count).IsEqualTo(1);
        await Assert.That(store.Written[0]).Contains("srt line");
        await Assert.That(target.ShownResized.Count).IsEqualTo(1);
    }

    [Test]
    public async Task ASelectionChangeAfterTheFileIsWritten_DoesNotShowTheOldTrack()
    {
        // The selection can change on VLC's thread at any moment, so it is checked one last time.
        TestSettingsService settings = new() { SubtitleScale = 150 };
        FakeStore store = new();
        FakeTarget target = Target();
        store.OnWrite = () => target.SelectedTrack = Srt;
        SubtitleResizeCoordinator coordinator = Coordinator(settings, store);

        coordinator.AttachTarget(target);
        await coordinator.PendingUpdate;

        await Assert.That(target.ShownResized.Count).IsEqualTo(0);
    }

    [Test]
    public async Task AFailureWhileReading_IsCaughtAndTheOriginalKeepsPlaying()
    {
        TestSettingsService settings = new() { SubtitleScale = 150 };
        FakeStore store = new();
        FakeTarget target = Target();
        target.OpenException = new IOException("disk removed");
        RecordingLogger logger = new();
        SubtitleResizeCoordinator coordinator = Coordinator(settings, store, logger: logger);

        coordinator.AttachTarget(target);
        await coordinator.PendingUpdate;

        await Assert.That(target.ShownResized.Count).IsEqualTo(0);
        await Assert.That(target.ShownOriginal.Count).IsEqualTo(0);
        await Assert.That(logger.ErrorCount).IsEqualTo(1);
    }

    [Test]
    public async Task AFailureWhileWriting_IsCaughtAndCanBeRetried()
    {
        TestSettingsService settings = new() { SubtitleScale = 150 };
        FakeStore store = new() { WriteException = new IOException("disk full") };
        FakeTarget target = Target();
        SubtitleResizeCoordinator coordinator = Coordinator(settings, store);

        coordinator.AttachTarget(target);
        await coordinator.PendingUpdate;
        await Assert.That(target.ShownResized.Count).IsEqualTo(0);

        store.WriteException = null;
        coordinator.OnPlaybackResumed();
        await coordinator.PendingUpdate;

        await Assert.That(target.ShownResized.Count).IsEqualTo(1);
    }

    [Test]
    public async Task AFailureWhileReleasing_IsCaught()
    {
        TestSettingsService settings = new() { SubtitleScale = 150 };
        FakeStore store = new() { ReleaseException = new IOException("file in use") };
        FakeTarget target = Target();
        SubtitleResizeCoordinator coordinator = Coordinator(settings, store);
        coordinator.AttachTarget(target);
        await coordinator.PendingUpdate;

        coordinator.OnPlaybackEnded();
        await coordinator.PendingRelease;

        await Assert.That(store.ReleaseAttempts).IsEqualTo(1);
    }

    [Test]
    public async Task APlayerThatRejectsTheFile_IsRetriedOnTheNextUpdate()
    {
        TestSettingsService settings = new() { SubtitleScale = 150 };
        FakeStore store = new();
        FakeTarget target = Target();
        target.AcceptsFiles = false;
        SubtitleResizeCoordinator coordinator = Coordinator(settings, store);
        coordinator.AttachTarget(target);
        await coordinator.PendingUpdate;

        target.AcceptsFiles = true;
        coordinator.OnPlaybackResumed();
        await coordinator.PendingUpdate;

        await Assert.That(target.ShownResized.Count).IsEqualTo(2);
    }

    [Test]
    public async Task Startup_DeletesFilesLeftByAnEarlierSession()
    {
        TestSettingsService settings = new();
        FakeStore store = new();

        SubtitleResizeCoordinator coordinator = Coordinator(settings, store);
        await coordinator.StartupCleanup;

        await Assert.That(store.DeleteAllCount).IsEqualTo(1);
    }

    [Test]
    public async Task Writing_WaitsForTheStartupCleanup()
    {
        // Otherwise the cleanup could delete a file generated moments after launch.
        TestSettingsService settings = new() { SubtitleScale = 150 };
        TaskCompletionSource gate = new();
        FakeStore store = new() { DeleteAllGate = gate.Task };
        FakeTarget target = Target();
        SubtitleResizeCoordinator coordinator = Coordinator(settings, store);

        coordinator.AttachTarget(target);
        Task update = coordinator.PendingUpdate;
        await Task.Delay(50);
        await Assert.That(store.Written.Count).IsEqualTo(0);

        gate.SetResult();
        await update;

        await Assert.That(store.Written.Count).IsEqualTo(1);
    }

    [Test]
    public async Task AFailedStartupCleanup_DoesNotStopResizing()
    {
        TestSettingsService settings = new() { SubtitleScale = 150 };
        FakeStore store = new() { DeleteAllException = new IOException("in use") };
        FakeTarget target = Target();

        SubtitleResizeCoordinator coordinator = Coordinator(settings, store);
        await coordinator.StartupCleanup;
        coordinator.AttachTarget(target);
        await coordinator.PendingUpdate;

        await Assert.That(target.ShownResized.Count).IsEqualTo(1);
    }

    private sealed class RecordingLogger : ILogger<SubtitleResizeCoordinator>
    {
        public int ErrorCount { get; private set; }

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (logLevel >= LogLevel.Error) ErrorCount++;
        }
    }

    /// <summary>A playback target with no VLC behind it, reading from a byte array.</summary>
    private sealed class FakeTarget(byte[] file) : ISubtitleResizeTarget
    {
        public string Path { get; set; } = @"D:\Media\file.mkv";
        public DateTimeOffset Modified { get; set; } = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        public bool HasSource { get; set; } = true;
        public bool AcceptsFiles { get; set; } = true;
        public Task? OpenGate { get; set; }
        public Exception? OpenException { get; set; }
        public int SourceRequests { get; private set; }
        public int OpenCount { get; private set; }
        public List<string> ShownResized { get; } = [];
        public List<SubtitleTrackInfo> ShownOriginal { get; } = [];

        public bool IsReady { get; set; } = true;
        public SubtitleTrackInfo? SelectedTrack { get; set; }
        public IReadOnlyList<SubtitleTrackInfo> EmbeddedTracks { get; set; } = [Dialogue, Signs, Srt, Images];

        public Task<SubtitleSourceFile?> GetSourceFileAsync()
        {
            SourceRequests++;
            return Task.FromResult(HasSource ? new SubtitleSourceFile(Path, file.Length, Modified, OpenAsync) : null);
        }

        public bool ShowResized(string mrl)
        {
            ShownResized.Add(mrl);
            return AcceptsFiles;
        }

        public void ShowOriginal(SubtitleTrackInfo track) => ShownOriginal.Add(track);

        private async Task<Stream> OpenAsync()
        {
            OpenCount++;
            if (OpenGate is { } gate) await gate;
            if (OpenException is { } exception) throw exception;
            return new MemoryStream(file, writable: false);
        }
    }

    /// <summary>Keeps generated files in memory and hands out a made-up MRL for each.</summary>
    private sealed class FakeStore : IResizedSubtitleStore
    {
        public List<string> Written { get; } = [];
        public List<string> Mrls { get; } = [];
        public List<string> Released { get; } = [];
        public int DeleteAllCount { get; private set; }
        public int ReleaseAttempts { get; private set; }
        public Action? OnWrite { get; set; }
        public Exception? WriteException { get; set; }
        public Exception? ReleaseException { get; set; }
        public Exception? DeleteAllException { get; set; }
        public Task? DeleteAllGate { get; set; }

        public Task<string> WriteAsync(string content, CancellationToken cancellationToken)
        {
            if (WriteException is { } exception) throw exception;
            OnWrite?.Invoke();
            Written.Add(content);
            string mrl = $"fake://{Mrls.Count}";
            Mrls.Add(mrl);
            return Task.FromResult(mrl);
        }

        public Task ReleaseAsync(IReadOnlyCollection<string> mrls)
        {
            ReleaseAttempts++;
            if (ReleaseException is { } exception) throw exception;
            Released.AddRange(mrls);
            return Task.CompletedTask;
        }

        public async Task DeleteAllAsync()
        {
            DeleteAllCount++;
            if (DeleteAllGate is { } gate) await gate;
            if (DeleteAllException is { } exception) throw exception;
        }
    }
}
