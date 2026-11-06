using CommunityToolkit.Mvvm.Messaging;
using Screenbox.Core.Contexts;
using Screenbox.Core.Coordinators;
using Screenbox.Core.Messages;
using Screenbox.Core.Services;
using Screenbox.Core.Tests.Helpers;
using Screenbox.Core.ViewModels;
using Windows.Storage;

namespace Screenbox.Core.Tests.ViewModels;

/// <summary>Collects subtitle size messages for as long as it is registered.</summary>
internal sealed class SubtitleScaleMessageLog : IRecipient<SettingsChangedMessage>, IDisposable
{
    public List<SettingsChangedMessage> Messages { get; } = [];

    public SubtitleScaleMessageLog() => WeakReferenceMessenger.Default.Register<SettingsChangedMessage>(this);

    public void Receive(SettingsChangedMessage message)
    {
        if (message.SettingsName == nameof(ISettingsService.SubtitleScale)) Messages.Add(message);
    }

    public void Dispose() => WeakReferenceMessenger.Default.UnregisterAll(this);
}

/// <summary>
/// Covers the subtitle size slider's property. These send real settings messages on the shared
/// messenger, so they must not run alongside each other.
/// </summary>
[NotInParallel(nameof(PlayerControlsViewModelTests))]
public sealed class PlayerControlsViewModelTests
{
    private sealed class IdlePlayQueueCoordinator : IPlayQueueCoordinator
    {
        public event EventHandler? CanNavigateChanged { add { } remove { } }
        public bool CanNext() => false;
        public bool CanPrevious() => false;
        public Task NextAsync() => Task.CompletedTask;
        public Task PreviousAsync() => Task.CompletedTask;
        public void Clear() { }
        public Task EnqueueAsync(IReadOnlyList<IStorageItem> files, int insertIndex = -1) => Task.CompletedTask;
        public void Remove(MediaViewModel item) { }
        public void InsertNext(MediaViewModel item) { }
        public void ResetCurrentItem() { }
    }

    // The window and files services are only stored by the constructor, and these tests never reach
    // a command that would use them.
    private static PlayerControlsViewModel CreateViewModel(TestSettingsService settings) =>
        new(new PlayQueueContext(), null!, new IdlePlayQueueCoordinator(), settings, null!, new PlayerContext());

    [Test]
    public async Task SubtitleScale_StartsFromTheSetting()
    {
        PlayerControlsViewModel viewModel = CreateViewModel(new TestSettingsService { SubtitleScale = 180 });

        await Assert.That(viewModel.SubtitleScale).IsEqualTo(180d);
    }

    [Test]
    public async Task SubtitleScale_WritesTheSettingAndTellsOthers()
    {
        TestSettingsService settings = new();
        PlayerControlsViewModel viewModel = CreateViewModel(settings);
        using SubtitleScaleMessageLog log = new();

        viewModel.SubtitleScale = 150;

        await Assert.That(settings.SubtitleScale).IsEqualTo(150);
        await Assert.That(log.Messages.Count).IsEqualTo(1);
        await Assert.That(log.Messages[0].Origin).IsEqualTo(typeof(PlayerControlsViewModel));
    }

    [Test]
    public async Task SubtitleScale_LoadingTheCurrentValueSendsNothing()
    {
        using SubtitleScaleMessageLog log = new();

        CreateViewModel(new TestSettingsService { SubtitleScale = 180 });

        await Assert.That(log.Messages.Count).IsEqualTo(0);
    }

    [Test]
    public async Task SubtitleScale_FollowsAChangeMadeElsewhere()
    {
        TestSettingsService settings = new();
        PlayerControlsViewModel viewModel = CreateViewModel(settings);
        using SubtitleScaleMessageLog log = new();

        settings.SubtitleScale = 220;
        WeakReferenceMessenger.Default.Send(new SettingsChangedMessage(nameof(ISettingsService.SubtitleScale), typeof(SettingsPageViewModel)));

        await Assert.That(viewModel.SubtitleScale).IsEqualTo(220d);

        // Following the change must not announce it a second time.
        await Assert.That(log.Messages.Count).IsEqualTo(1);
    }

    [Test]
    public async Task SubtitleScale_WritesAWholeNumberInRange()
    {
        TestSettingsService settings = new();
        PlayerControlsViewModel viewModel = CreateViewModel(settings);

        viewModel.SubtitleScale = 149.6;
        await Assert.That(settings.SubtitleScale).IsEqualTo(150);

        viewModel.SubtitleScale = 900;
        await Assert.That(settings.SubtitleScale).IsEqualTo(250);
    }
}
