using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using Screenbox.Core.Coordinators;
using Screenbox.Core.Models;
using Windows.Media.Playback;
using Windows.Storage;
using Windows.Storage.FileProperties;

namespace Screenbox.Core.Playback;

/// <summary>
/// Connects the subtitle resizer to LibVLC and to the file behind a playback item.
/// </summary>
internal sealed class VlcSubtitleResizeTarget : ISubtitleResizeTarget
{
    // Subtitle blocks are spread through the whole file with video and audio frames between them.
    // After skipping a frame, a read needs only a few header bytes, but a large buffer means most
    // of those are served from memory instead of crossing into WinRT for each one.
    private const int ReadBufferSize = 128 * 1024;

    private const string MkvFileType = ".mkv";

    // StorageProvider.Id of a file on a local drive. Network and cloud files have other ids.
    private const string LocalProviderId = "computer";

    private readonly VlcMediaPlayer _player;
    private readonly PlaybackItem _item;

    public VlcSubtitleResizeTarget(VlcMediaPlayer player, PlaybackItem item)
    {
        _player = player;
        _item = item;
    }

    /// <inheritdoc/>
    public bool IsReady =>
        ReferenceEquals(_player.PlaybackItem, _item) &&
        _player.PlaybackState is MediaPlaybackState.Playing or MediaPlaybackState.Paused;

    /// <inheritdoc/>
    public SubtitleTrackInfo? SelectedTrack
    {
        get
        {
            PlaybackSubtitleTrackList tracks = _item.SubtitleTracks;
            int index = tracks.SelectedIndex;
            return index >= 0 && index < tracks.Count ? tracks[index].EmbeddedInfo : null;
        }
    }

    /// <inheritdoc/>
    public IReadOnlyList<SubtitleTrackInfo> EmbeddedTracks
    {
        get
        {
            List<SubtitleTrackInfo> tracks = new();
            foreach (SubtitleTrack track in _item.SubtitleTracks)
            {
                if (track.EmbeddedInfo is { } info) tracks.Add(info);
            }

            return tracks;
        }
    }

    /// <inheritdoc/>
    [DynamicWindowsRuntimeCast(typeof(StorageFile))]
    public async Task<SubtitleSourceFile?> GetSourceFileAsync()
    {
        if (_item.OriginalSource is not StorageFile file) return null;
        if (!MkvFileType.Equals(file.FileType, StringComparison.OrdinalIgnoreCase)) return null;
        if (string.IsNullOrEmpty(file.Path)) return null;
        if (!LocalProviderId.Equals(file.Provider.Id, StringComparison.OrdinalIgnoreCase)) return null;

        BasicProperties properties = await file.GetBasicPropertiesAsync();
        return new SubtitleSourceFile(file.Path, (long)properties.Size, properties.DateModified, () => OpenAsync(file));
    }

    /// <inheritdoc/>
    public bool ShowResized(string mrl) => _player.AddGeneratedSubtitle(mrl);

    /// <inheritdoc/>
    public void ShowOriginal(SubtitleTrackInfo track) => _player.VlcPlayer.SetSpu(track.VlcSpu);

    private static async Task<Stream> OpenAsync(StorageFile file)
    {
        var stream = await file.OpenReadAsync();
        return stream.AsStreamForRead(ReadBufferSize);
    }
}
