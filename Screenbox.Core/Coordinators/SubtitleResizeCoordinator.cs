using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Messaging;
using CommunityToolkit.Mvvm.Messaging.Messages;
using Microsoft.Extensions.Logging;
using Screenbox.Core.Contexts;
using Screenbox.Core.Events;
using Screenbox.Core.Helpers;
using Screenbox.Core.Messages;
using Screenbox.Core.Models;
using Screenbox.Core.Playback;
using Screenbox.Core.Services;
using Windows.Media.Core;
using Windows.Media.Playback;
using Windows.System;

namespace Screenbox.Core.Coordinators;

/// <inheritdoc cref="ISubtitleResizeCoordinator"/>
/// <remarks>
/// <para>
/// All state here is touched on the UI thread: VLC's events are moved there before they do
/// anything. Reading the file is the only slow step, and it runs on the thread pool. The original
/// track keeps playing until the resized file is ready, because nothing is swapped until then.
/// </para>
/// <para>
/// Anything that goes wrong leaves the original track playing. Failures are logged, never thrown:
/// every path that starts work without awaiting it goes through <see cref="RunApplyAsync"/> or
/// <see cref="ReleaseGeneratedAsync"/>, which catch everything.
/// </para>
/// </remarks>
public sealed partial class SubtitleResizeCoordinator : ObservableRecipient, ISubtitleResizeCoordinator,
    IRecipient<PropertyChangedMessage<IMediaPlayer?>>,
    IRecipient<SettingsChangedMessage>
{
    // A slider drag changes the setting at every step. VLC cannot remove a track once it is added,
    // so waiting for the drag to settle keeps it from loading a file per step.
    private static readonly TimeSpan DefaultScaleChangeDelay = TimeSpan.FromMilliseconds(300);

    private const int TrackCacheCapacity = 4;

    private readonly ISettingsService _settingsService;
    private readonly IResizedSubtitleStore _store;
    private readonly PlayerContext _playerContext;
    private readonly ILogger<SubtitleResizeCoordinator> _logger;
    private readonly TimeSpan _scaleChangeDelay;
    private readonly DispatcherQueue? _dispatcherQueue;
    private readonly LruCache<SourceKey, MatroskaHeader?> _headers = new(TrackCacheCapacity);
    private readonly LruCache<TrackKey, ResizableSubtitle?> _subtitles = new(TrackCacheCapacity);
    private readonly List<string> _generatedMrls = new();
    private CancellationTokenSource? _applyCts;
    private ISubtitleResizeTarget? _target;
    private ShownFile? _shown;
    private VlcMediaPlayer? _player;
    private PlaybackItem? _item;

    public SubtitleResizeCoordinator(
        ISettingsService settingsService,
        IResizedSubtitleStore store,
        PlayerContext playerContext,
        ILogger<SubtitleResizeCoordinator> logger,
        TimeSpan? scaleChangeDelay = null)
    {
        _settingsService = settingsService;
        _store = store;
        _playerContext = playerContext;
        _logger = logger;
        _scaleChangeDelay = scaleChangeDelay ?? DefaultScaleChangeDelay;
        _dispatcherQueue = DispatcherQueue.GetForCurrentThread();
        StartupCleanup = DeleteStaleFilesAsync();

        Messenger.Register<PropertyChangedMessage<IMediaPlayer?>>(this);
        Messenger.Register<SettingsChangedMessage>(this);

        // The player may already exist if this was constructed after it.
        if (playerContext.MediaPlayer is { } player)
        {
            RunOnUiThread(() => AttachPlayer(player));
        }
    }

    /// <summary>Gets the most recently started update. Tests await this to know it has finished.</summary>
    internal Task PendingUpdate { get; private set; } = Task.CompletedTask;

    /// <summary>Gets the most recently started release of generated files.</summary>
    internal Task PendingRelease { get; private set; } = Task.CompletedTask;

    /// <summary>Gets the deletion of files left by an earlier session. It never faults.</summary>
    internal Task StartupCleanup { get; }

    public void Receive(PropertyChangedMessage<IMediaPlayer?> message)
    {
        if (!ReferenceEquals(message.Sender, _playerContext)) return;

        // PlayerContext is set from a background thread while the player initialises.
        RunOnUiThread(() => AttachPlayer(message.NewValue));
    }

    public void Receive(SettingsChangedMessage message)
    {
        if (message.SettingsName != nameof(ISettingsService.SubtitleScale)) return;
        ScheduleApply(_scaleChangeDelay);
    }

    /// <summary>
    /// Makes this target the one to resize for, in place of the previous one. The previous item's
    /// files are no longer needed: VLC drops an input's slaves along with it.
    /// </summary>
    internal void AttachTarget(ISubtitleResizeTarget? target)
    {
        CancelPendingApply();
        _shown = null;
        _target = target;
        StartRelease();
        ScheduleApply(TimeSpan.Zero);
    }

    /// <summary>
    /// Handles a change of the selected track. The player has already switched to that track's own
    /// ES, so nothing resized is showing any more, even if this track and size were shown earlier.
    /// </summary>
    internal void OnSelectionChanged()
    {
        _shown = null;
        ScheduleApply(TimeSpan.Zero);
    }

    /// <summary>Handles playback starting or resuming, which is when a track can first be added.</summary>
    internal void OnPlaybackResumed() => ScheduleApply(TimeSpan.Zero);

    /// <summary>Handles the input restarting, which drops its slaves. Playing again will re-apply.</summary>
    internal void OnInputRestarted() => ResetAfterInputEnded();

    /// <summary>Handles playback reaching the end. The generated files are no longer needed.</summary>
    internal void OnPlaybackEnded() => ResetAfterInputEnded();

    private void ResetAfterInputEnded()
    {
        CancelPendingApply();
        _shown = null;
        StartRelease();
    }

    private void CancelPendingApply()
    {
        _applyCts?.Cancel();
        _applyCts = null;
    }

    private void ScheduleApply(TimeSpan delay)
    {
        CancelPendingApply();
        if (_target is not { } target) return;

        CancellationTokenSource cts = new();
        _applyCts = cts;
        PendingUpdate = RunApplyAsync(target, delay, cts.Token);
    }

    private async Task RunApplyAsync(ISubtitleResizeTarget target, TimeSpan delay, CancellationToken cancellationToken)
    {
        try
        {
            if (delay > TimeSpan.Zero)
            {
                await Task.Delay(delay, cancellationToken);
            }

            await ApplyAsync(target, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            // A newer change took over.
        }
        catch (Exception e)
        {
            // The original track is still playing, so this costs the size change and nothing else.
            _logger.LogError(e, "Failed to resize subtitles.");
        }
    }

    /// <summary>Brings the player in line with the selected track and the size setting.</summary>
    private async Task ApplyAsync(ISubtitleResizeTarget target, CancellationToken cancellationToken)
    {
        if (!target.IsReady) return;

        int scale = _settingsService.SubtitleScale;
        SubtitleTrackInfo? track = target.SelectedTrack;
        if (scale == SubtitleScaleRange.Default || track is not { IsResizableCodec: true })
        {
            ShowOriginal(target, track);
            return;
        }

        if (_shown == new ShownFile(track.VlcSpu, scale)) return;

        ResizableSubtitle? subtitle = await LoadSubtitleAsync(target, track, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        if (subtitle is null)
        {
            ShowOriginal(target, track);
            return;
        }

        await StartupCleanup;
        string mrl = await _store.WriteAsync(ResizedAssBuilder.Render(subtitle, scale), cancellationToken);
        _generatedMrls.Add(mrl);
        cancellationToken.ThrowIfCancellationRequested();

        // The selection can change on VLC's thread at any moment. If it has, the update it triggers
        // is on its way, and adding this file now would put the old track back on screen.
        if (target.SelectedTrack?.VlcSpu != track.VlcSpu) return;

        if (target.ShowResized(mrl))
        {
            _shown = new ShownFile(track.VlcSpu, scale);
        }
    }

    /// <summary>Goes back to the embedded track if a resized file is what is showing.</summary>
    private void ShowOriginal(ISubtitleResizeTarget target, SubtitleTrackInfo? track)
    {
        if (_shown is null) return;
        _shown = null;
        if (track is not null)
        {
            target.ShowOriginal(track);
        }
    }

    private async Task<ResizableSubtitle?> LoadSubtitleAsync(
        ISubtitleResizeTarget target, SubtitleTrackInfo track, CancellationToken cancellationToken)
    {
        if (await target.GetSourceFileAsync() is not { } source) return null;
        cancellationToken.ThrowIfCancellationRequested();

        SourceKey sourceKey = new(source.Path, source.Size, source.Modified);
        Stream? stream = null;
        try
        {
            if (!_headers.TryGet(sourceKey, out MatroskaHeader? header))
            {
                stream = await source.OpenReadAsync();
                Stream headerStream = stream;
                header = await Task.Run(() => MatroskaSubtitleReader.ReadHeader(headerStream, cancellationToken), cancellationToken);
                _headers.Add(sourceKey, header);
            }

            if (header is null) return null;

            MatroskaTrack? matroskaTrack = MatroskaSubtitleTrackMapper.Map(target.EmbeddedTracks, track, header.Tracks);
            if (matroskaTrack is null) return null;

            TrackKey trackKey = new(sourceKey, matroskaTrack.Number);
            if (_subtitles.TryGet(trackKey, out ResizableSubtitle? cached)) return cached;

            stream ??= await source.OpenReadAsync();
            Stream trackStream = stream;
            ulong trackNumber = matroskaTrack.Number;
            ResizableSubtitle? subtitle = await Task.Run(() =>
            {
                MatroskaTrackData? data = MatroskaSubtitleReader.ReadTrack(trackStream, trackNumber, cancellationToken);
                return data is { Blocks.Count: > 0 } ? ResizedAssBuilder.Parse(data) : null;
            }, cancellationToken);

            _subtitles.Add(trackKey, subtitle);
            return subtitle;
        }
        finally
        {
            stream?.Dispose();
        }
    }

    private void StartRelease()
    {
        if (_generatedMrls.Count == 0) return;
        string[] mrls = _generatedMrls.ToArray();
        _generatedMrls.Clear();
        PendingRelease = ReleaseGeneratedAsync(mrls);
    }

    private async Task ReleaseGeneratedAsync(IReadOnlyCollection<string> mrls)
    {
        try
        {
            await _store.ReleaseAsync(mrls);
        }
        catch (Exception e)
        {
            // Whatever is left is deleted at the next startup.
            _logger.LogWarning(e, "Failed to delete generated subtitle files.");
        }
    }

    private async Task DeleteStaleFilesAsync()
    {
        try
        {
            await _store.DeleteAllAsync();
        }
        catch (Exception e)
        {
            _logger.LogWarning(e, "Failed to delete generated subtitle files from an earlier session.");
        }
    }

    private void AttachPlayer(IMediaPlayer? player)
    {
        if (_player is { } oldPlayer)
        {
            oldPlayer.PlaybackItemChanged -= OnPlaybackItemChanged;
            oldPlayer.PlaybackStateChanged -= OnPlaybackStateChanged;
            oldPlayer.MediaOpened -= OnMediaOpened;
            oldPlayer.MediaEnded -= OnMediaEnded;
        }

        _player = player as VlcMediaPlayer;
        if (_player is { } newPlayer)
        {
            newPlayer.PlaybackItemChanged += OnPlaybackItemChanged;
            newPlayer.PlaybackStateChanged += OnPlaybackStateChanged;
            newPlayer.MediaOpened += OnMediaOpened;
            newPlayer.MediaEnded += OnMediaEnded;
        }

        AttachItem(_player?.PlaybackItem);
    }

    private void AttachItem(PlaybackItem? item)
    {
        if (ReferenceEquals(_item, item)) return;

        if (_item is { } oldItem)
        {
            oldItem.SubtitleTracks.SelectedIndexChanged -= OnSelectedSubtitleChanged;
        }

        _item = item;
        if (item is not null)
        {
            item.SubtitleTracks.SelectedIndexChanged += OnSelectedSubtitleChanged;
        }

        AttachTarget(item is not null && _player is { } player ? new VlcSubtitleResizeTarget(player, item) : null);
    }

    private void OnPlaybackItemChanged(IMediaPlayer sender, ValueChangedEventArgs<PlaybackItem?> args) =>
        RunOnUiThread(() => AttachItem(args.NewValue));

    private void OnSelectedSubtitleChanged(ISingleSelectMediaTrackList sender, object? args) =>
        RunOnUiThread(OnSelectionChanged);

    private void OnPlaybackStateChanged(IMediaPlayer sender, ValueChangedEventArgs<MediaPlaybackState> args)
    {
        if (args.NewValue is MediaPlaybackState.Playing or MediaPlaybackState.Paused)
        {
            RunOnUiThread(OnPlaybackResumed);
        }
    }

    private void OnMediaOpened(IMediaPlayer sender, EventArgs args) => RunOnUiThread(OnInputRestarted);

    private void OnMediaEnded(IMediaPlayer sender, EventArgs args) => RunOnUiThread(OnPlaybackEnded);

    private void RunOnUiThread(Action action)
    {
        if (_dispatcherQueue is null)
        {
            action();
        }
        else
        {
            _dispatcherQueue.TryEnqueue(() => action());
        }
    }

    /// <summary>Identifies a version of a file. A changed file has a new key and is read again.</summary>
    private readonly record struct SourceKey(string Path, long Size, DateTimeOffset Modified);

    private readonly record struct TrackKey(SourceKey Source, ulong TrackNumber);

    /// <summary>The track and size of the resized file VLC is playing.</summary>
    private sealed record ShownFile(int VlcSpu, int Scale);
}
