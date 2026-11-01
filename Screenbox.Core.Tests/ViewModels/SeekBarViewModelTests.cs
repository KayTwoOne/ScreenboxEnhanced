using Microsoft.Extensions.Logging.Abstractions;
using Screenbox.Core.Contexts;
using Screenbox.Core.Factories;
using Screenbox.Core.Messages;
using Screenbox.Core.Services;
using Screenbox.Core.Tests.Helpers;
using Screenbox.Core.ViewModels;

namespace Screenbox.Core.Tests.ViewModels;

/// <summary>
/// Covers how <see cref="SeekBarViewModel"/> hands playback progress to
/// <see cref="WatchStateContext"/>, the one place that reacts to a watch state changing.
/// </summary>
public sealed class SeekBarViewModelTests
{
    /// <summary>
    /// Records progress against a tracker that has never saved anything: every call is "due", and
    /// nothing is restored when an item becomes current.
    /// </summary>
    private sealed class NeverUpdatedTracker : IPlaybackProgressTracker
    {
        public bool IsLoaded => false;
        public DateTimeOffset LastUpdated => DateTimeOffset.MinValue;
        public void UpdateProgress(string location, TimeSpan position) { }
        public TimeSpan GetPosition(string location) => TimeSpan.Zero;
        public void RemovePosition(string location) { }
        public void ClearAll() { }
        public Task SaveToDiskAsync() => Task.CompletedTask;
        public Task LoadFromDiskAsync() => Task.CompletedTask;
    }

    [Test]
    public async Task UpdateProgress_RefreshesTheItemThatWasRecorded_NotTheOneCurrentWhenTheWriteFinishes()
    {
        // An episode can end, and the queue advance to the next one, while the write for the last
        // progress tick is still in flight. The tile that must change is the episode that was
        // recorded. Reading the current item again after the await would refresh the next episode
        // instead and leave the one just watched looking unwatched.
        using var fixture = new TestDirectoryFixture();
        var db = new DatabaseService(NullLogger<DatabaseService>.Instance) { DbFolderPath = fixture.DirectoryPath };
        await db.InitializeAsync();
        var inner = new WatchStateService(db, NullLogger<WatchStateService>.Instance);
        await inner.LoadAsync();

        var gate = new TaskCompletionSource();
        var service = new AsyncWatchStateService(inner) { Gate = gate.Task };
        var context = new WatchStateContext(
            service,
            new MediaViewModelFactory(new TestPlayerService(), new PlayerContext(), new LibraryContext()),
            new FakeFolderStatsService(),
            NullLogger<WatchStateContext>.Instance);

        var playerContext = new PlayerContext();
        MediaViewModel recorded = new(playerContext, new TestPlayerService(), new Uri(@"D:\Anime\ep1.mkv"));
        MediaViewModel next = new(playerContext, new TestPlayerService(), new Uri(@"D:\Anime\ep2.mkv"));

        DispatcherQueueTestHelper.EnsureDispatcherQueue();
        var seekBar = new SeekBarViewModel(
            new TestSettingsService { PersistPlaybackPosition = true },
            new NeverUpdatedTracker(),
            service,
            context,
            playerContext,
            NullLogger<SeekBarViewModel>.Instance)
        {
            Length = Episode.TotalMilliseconds
        };

        var recordedRefreshed = new TaskCompletionSource();
        recorded.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(MediaViewModel.IsWatched) && recorded.IsWatched)
                recordedRefreshed.TrySetResult();
        };

        seekBar.Receive(new QueueCurrentItemChangedMessage(recorded));
        seekBar.UpdateProgress(TimeSpan.FromMinutes(19));

        // The queue moves on while the write is still held back...
        seekBar.Receive(new QueueCurrentItemChangedMessage(next));
        gate.SetResult();

        // ...and the item that was recorded is the one that gets refreshed.
        Task finished = await Task.WhenAny(recordedRefreshed.Task, Task.Delay(TimeSpan.FromSeconds(10)));
        await Assert.That(ReferenceEquals(finished, recordedRefreshed.Task)).IsTrue()
            .Because("The item that was recorded must show as watched once the write lands.");
        await Assert.That(recorded.IsWatched).IsTrue();
        await Assert.That(next.IsWatched).IsFalse();
    }

    private static readonly TimeSpan Episode = TimeSpan.FromMinutes(20);
}
