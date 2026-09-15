using System.Collections.Generic;
using System.Threading.Tasks;
using Screenbox.Core.Enums;
using Screenbox.Core.Models;

namespace Screenbox.Core.Services;

/// <summary>
/// Manages the application's SQLite database.
/// Most tables are a quick cache layer that can be rebuilt by rescanning the libraries, but
/// <c>playlists</c>, <c>playlist_items</c> and <c>folder_metadata</c> hold durable, user-authored
/// data that cannot be reconstructed. Schema changes migrate those tables rather than dropping
/// them; whole-file recovery from a corrupt database still loses them.
/// </summary>
public interface IDatabaseService
{
    /// <summary>
    /// Initializes the database, creating it if necessary and applying the schema.
    /// If corruption is detected, the database file is deleted and recreated from scratch.
    /// </summary>
    Task InitializeAsync();

    /// <summary>
    /// Reads cached library folders and media records for the requested media type.
    /// </summary>
    Task<RawCacheLoadResultDto> LoadLibraryCacheAsync(MediaPlaybackType mediaType);

    /// <summary>Saves the complete cached music snapshot to the database.</summary>
    Task SaveMusicCacheAsync(IReadOnlyList<string> folderPaths, IReadOnlyList<MusicCacheRecordDto> records);

    /// <summary>Saves the complete cached video snapshot to the database.</summary>
    Task SaveVideoCacheAsync(IReadOnlyList<string> folderPaths, IReadOnlyList<VideoCacheRecordDto> records);

    /// <summary>Replaces playback progress rows with the provided snapshot.</summary>
    Task ReplacePlaybackProgressAsync(IReadOnlyList<MediaPlaybackProgress> snapshot);

    /// <summary>Loads all playback progress entries.</summary>
    Task<List<MediaPlaybackProgress>> LoadPlaybackProgressAsync();

    /// <summary>Persists a playlist and its items.</summary>
    Task SavePlaylistAsync(PlaylistRecordDto playlist);

    /// <summary>Loads a playlist and its items, or null when not found.</summary>
    Task<PlaylistRecordDto?> LoadPlaylistAsync(string id);

    /// <summary>Lists all playlists with their items.</summary>
    Task<List<PlaylistRecordDto>> ListPlaylistsAsync();

    /// <summary>Deletes a playlist and cascades to its items.</summary>
    Task DeletePlaylistAsync(string id);

    /// <summary>
    /// Saves durable, user-authored metadata for a single folder, replacing the whole row.
    /// Prefer <see cref="SetFolderPosterAsync"/> or <see cref="SetFolderCustomTitleAsync"/> when
    /// only some columns are owned by the caller: a full-row write built from a stale read
    /// silently discards whatever another writer changed in the meantime.
    /// </summary>
    Task SaveFolderMetadataAsync(FolderMetadataDto metadata);

    /// <summary>Loads folder metadata, or null when the folder has none.</summary>
    Task<FolderMetadataDto?> LoadFolderMetadataAsync(string path);

    /// <summary>Lists every stored folder metadata row, for maintenance such as evicting orphans.</summary>
    Task<List<FolderMetadataDto>> ListFolderMetadataAsync();

    /// <summary>
    /// Records a folder's poster without reading or rewriting any other column, creating the row
    /// when it does not exist. Returns the poster file name that was stored before, or null when
    /// there was none, so the caller can delete the superseded file.
    /// </summary>
    Task<string?> SetFolderPosterAsync(string path, string posterFile, PosterSource source);

    /// <summary>
    /// Sets a folder's custom display title, or clears it when <paramref name="customTitle"/> is
    /// null, without touching any other column.
    /// </summary>
    Task SetFolderCustomTitleAsync(string path, string? customTitle);

    /// <summary>Removes folder metadata.</summary>
    Task DeleteFolderMetadataAsync(string path);

    /// <summary>Saves durable watched state for a media location.</summary>
    Task SaveWatchStateAsync(WatchStateDto state);

    /// <summary>Loads watched state, or null when the location has none.</summary>
    Task<WatchStateDto?> LoadWatchStateAsync(string location);

    /// <summary>Lists all watched state rows.</summary>
    Task<List<WatchStateDto>> ListWatchStateAsync();

    /// <summary>Removes watched state for a location.</summary>
    Task DeleteWatchStateAsync(string location);
}
