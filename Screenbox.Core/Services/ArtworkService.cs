using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Screenbox.Core.Enums;
using Screenbox.Core.Helpers;
using Screenbox.Core.Models;
using Windows.Graphics.Imaging;
using Windows.Storage;
using Windows.Storage.FileProperties;
using Windows.Storage.Streams;

namespace Screenbox.Core.Services;

/// <inheritdoc cref="IArtworkService"/>
public sealed class ArtworkService : IArtworkService
{
    /// <summary>Folder name inside local storage where artwork is cached.</summary>
    public const string ArtworkFolderName = "Artwork";

    /// <summary>
    /// How deep to search for a video when generating a poster. Series folders commonly
    /// nest as Series/Season/Extras, so three levels is the practical minimum.
    /// </summary>
    public const int MaxRecursionDepth = 3;

    private static readonly string[] ConventionStems = ["poster", "folder", "cover"];
    private static readonly string[] ConventionExtensions = [".jpg", ".jpeg", ".png"];

    private readonly IDatabaseService _databaseService;
    private readonly ILogger<ArtworkService> _logger;

    /// <summary>
    /// Folders already searched, without success, for anything to show. Resolving artwork runs on
    /// every container realization, so without this a folder holding no video is walked three
    /// levels deep on every scroll-back. Entries live for the session and are dropped when the
    /// folder gains a manual poster or when the library is rescanned.
    /// </summary>
    private readonly ConcurrentDictionary<string, byte> _foldersWithoutArtwork = new(StringComparer.OrdinalIgnoreCase);

    private static long _artworkSequence;

    public ArtworkService(IDatabaseService databaseService, ILogger<ArtworkService> logger)
    {
        _databaseService = databaseService;
        _logger = logger;
    }

    /// <summary>Returns true when the file name is a recognised poster convention.</summary>
    public static bool IsConventionFileName(string fileName)
    {
        string stem = System.IO.Path.GetFileNameWithoutExtension(fileName);
        string ext = System.IO.Path.GetExtension(fileName);
        return ConventionStems.Contains(stem, StringComparer.OrdinalIgnoreCase)
            && ConventionExtensions.Contains(ext, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// True when a convention-file poster has already been copied into local storage and
    /// recorded for this folder, so re-copying and re-writing metadata would be redundant.
    /// <see cref="GetPosterFileNameAsync"/> is called once per item on every folder navigation,
    /// so this check keeps that hot path free of unnecessary file and database I/O.
    /// </summary>
    /// <param name="metadata">Stored metadata for the folder, or null when it has none.</param>
    /// <param name="conventionExtension">Extension of the convention file found inside the folder.</param>
    /// <param name="recordedFileExists">Whether the recorded poster file is present in local storage.</param>
    public static bool ShouldSkipConventionCopy(FolderMetadataDto? metadata, string conventionExtension, bool recordedFileExists)
    {
        // Stored file names carry a write discriminator (see BuildFileName), so the recorded name
        // cannot be recomputed from the folder path. The extension is what distinguishes a
        // convention file that has been swapped for a different image format.
        return recordedFileExists
            && metadata is { PosterSource: PosterSource.Convention, PosterFile: { Length: > 0 } existingFileName }
            && string.Equals(
                System.IO.Path.GetExtension(existingFileName), conventionExtension, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// True when a stored folder path has genuinely disappeared: it sits beneath a library root
    /// that is currently readable, yet the folder itself is gone. Restricting eviction to paths
    /// under a live root keeps a disconnected drive or an offline network share - where every path
    /// looks missing - from destroying the user's titles and manual poster choices.
    /// </summary>
    /// <param name="folderPath">Absolute folder path stored in the metadata row.</param>
    /// <param name="libraryFolderPaths">Absolute paths of the library roots just enumerated.</param>
    /// <param name="directoryExists">Existence probe, injected so the rule can be tested without a filesystem.</param>
    public static bool IsOrphanedFolderPath(
        string folderPath, IReadOnlyList<string> libraryFolderPaths, Func<string, bool> directoryExists)
    {
        if (string.IsNullOrEmpty(folderPath)) return false;

        foreach (string root in libraryFolderPaths)
        {
            if (string.IsNullOrEmpty(root) || !IsUnderRoot(folderPath, root)) continue;
            if (!directoryExists(root)) continue;
            return !directoryExists(folderPath);
        }

        return false;
    }

    private static bool IsUnderRoot(string folderPath, string root)
    {
        string trimmedRoot = root.TrimEnd(System.IO.Path.DirectorySeparatorChar, System.IO.Path.AltDirectorySeparatorChar);
        string trimmedPath = folderPath.TrimEnd(System.IO.Path.DirectorySeparatorChar, System.IO.Path.AltDirectorySeparatorChar);
        if (string.Equals(trimmedPath, trimmedRoot, StringComparison.OrdinalIgnoreCase)) return true;
        return trimmedPath.StartsWith(trimmedRoot + System.IO.Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

    /// <inheritdoc/>
    public async Task<string?> GetPosterFileNameAsync(StorageFolder folder, FolderMetadataDto? metadata)
    {
        try
        {
            StorageFolder artworkFolder = await GetArtworkFolderAsync();
            bool recordedFileExists = metadata?.PosterFile is { Length: > 0 } recordedName
                && await artworkFolder.TryGetItemAsync(recordedName) is not null;

            // A manual or scraped choice is settled by the row alone: no scan of the folder is
            // needed, which is what makes repeated realization of an established folder cheap.
            if (ArtworkResolver.ResolveFromMetadata(metadata) is { } pinned)
            {
                // The row is deliberately left intact so the user's choice is never forgotten, but
                // a file that is gone cannot be drawn. Returning null here is what lets the view
                // fall back to the folder glyph instead of rendering an empty tile.
                if (recordedFileExists) return pinned.FileName;
                _logger.LogWarning(
                    "Poster file '{FileName}' for '{Path}' is missing; falling back to the folder glyph.",
                    metadata?.PosterFile, folder.Path);
                return null;
            }

            if (_foldersWithoutArtwork.ContainsKey(folder.Path)) return null;

            StorageFile? conventionFile = await FindConventionFileAsync(folder);
            ArtworkDecision decision = ArtworkResolver.Resolve(metadata, conventionFile is not null, recordedFileExists);

            if (!decision.NeedsGeneration)
            {
                if (decision.Source is PosterSource.Convention && conventionFile is not null)
                {
                    // Convention posters are resolved on every navigation into a folder, so avoid
                    // re-copying the file and re-writing the database row when nothing has changed.
                    string conventionExtension = System.IO.Path.GetExtension(conventionFile.Name);
                    if (ShouldSkipConventionCopy(metadata, conventionExtension, recordedFileExists))
                    {
                        return metadata?.PosterFile;
                    }

                    return await CopyIntoArtworkFolderAsync(folder, conventionFile, PosterSource.Convention);
                }

                return decision.FileName;
            }

            StorageFile? video = await FindFirstVideoAsync(folder, MaxRecursionDepth);
            if (video is null)
            {
                _foldersWithoutArtwork.TryAdd(folder.Path, 0);
                return null;
            }

            return await GenerateFrameAsync(folder, video);
        }
        catch (Exception e)
        {
            _logger.LogError(e, "Failed to resolve poster artwork for '{Path}'.", folder.Path);
            return null;
        }
    }

    /// <inheritdoc/>
    public async Task<string?> SetManualPosterAsync(StorageFolder folder, StorageFile image)
    {
        try
        {
            _foldersWithoutArtwork.TryRemove(folder.Path, out _);
            return await CopyIntoArtworkFolderAsync(folder, image, PosterSource.Manual);
        }
        catch (Exception e)
        {
            _logger.LogError(e, "Failed to set manual poster for '{Path}'.", folder.Path);
            return null;
        }
    }

    /// <inheritdoc/>
    public async Task PruneOrphanedArtworkAsync(IReadOnlyList<string> libraryFolderPaths)
    {
        // A rescan is the moment the app learns which folders still exist, so it is also the moment
        // to drop rows for folders that do not. Without this nothing is ever evicted: rows are keyed
        // on absolute path, so a deleted, renamed or moved folder leaves its row and its artwork
        // file behind forever.
        _foldersWithoutArtwork.Clear();
        if (libraryFolderPaths.Count is 0) return;

        try
        {
            List<FolderMetadataDto> storedRows = await _databaseService.ListFolderMetadataAsync();
            if (storedRows.Count is 0) return;

            StorageFolder artworkFolder = await GetArtworkFolderAsync();
            foreach (FolderMetadataDto row in storedRows)
            {
                if (!IsOrphanedFolderPath(row.Path, libraryFolderPaths, System.IO.Directory.Exists)) continue;

                await TryDeleteArtworkFileAsync(artworkFolder, row.PosterFile);
                await _databaseService.DeleteFolderMetadataAsync(row.Path);
                _logger.LogInformation("Evicted folder metadata for '{Path}'; the folder no longer exists.", row.Path);
            }
        }
        catch (Exception e)
        {
            _logger.LogError(e, "Failed to prune orphaned folder artwork.");
        }
    }

    [DynamicWindowsRuntimeCast(typeof(StorageFile))]
    private async Task TryDeleteArtworkFileAsync(StorageFolder artworkFolder, string? fileName)
    {
        if (fileName is not { Length: > 0 }) return;
        try
        {
            if (await artworkFolder.TryGetItemAsync(fileName) is StorageFile file)
            {
                await file.DeleteAsync(StorageDeleteOption.PermanentDelete);
            }
        }
        catch (Exception e)
        {
            _logger.LogWarning(e, "Failed to delete artwork file '{Name}'.", fileName);
        }
    }

    private static async Task<StorageFolder> GetArtworkFolderAsync() =>
        await ApplicationData.Current.LocalFolder.CreateFolderAsync(
            ArtworkFolderName, CreationCollisionOption.OpenIfExists);

    private static async Task<StorageFile?> FindConventionFileAsync(StorageFolder folder)
    {
        foreach (StorageFile file in await folder.GetFilesAsync())
        {
            if (IsConventionFileName(file.Name)) return file;
        }

        return null;
    }

    private async Task<StorageFile?> FindFirstVideoAsync(StorageFolder folder, int depth)
    {
        if (depth <= 0) return null;

        IReadOnlyList<StorageFile> files;
        try
        {
            files = await folder.GetFilesAsync();
        }
        catch (Exception e)
        {
            // A locked folder, a permissions failure, or a disconnected network path should not
            // abort the search for the whole tree — just skip what can't be read.
            _logger.LogWarning(e, "Failed to enumerate files in '{Path}' while searching for a video.", folder.Path);
            return null;
        }

        StorageFile? video = files.FirstOrDefault(f => f.ContentType.StartsWith("video", StringComparison.OrdinalIgnoreCase));
        if (video is not null) return video;

        IReadOnlyList<StorageFolder> subfolders;
        try
        {
            subfolders = await folder.GetFoldersAsync();
        }
        catch (Exception e)
        {
            _logger.LogWarning(e, "Failed to enumerate subfolders in '{Path}' while searching for a video.", folder.Path);
            return null;
        }

        foreach (StorageFolder sub in subfolders)
        {
            StorageFile? found;
            try
            {
                found = await FindFirstVideoAsync(sub, depth - 1);
            }
            catch (Exception e)
            {
                // One unreadable subtree (e.g. a locked "Extras" folder) should not cost the
                // whole series its artwork - skip it and keep searching sibling subfolders.
                _logger.LogWarning(e, "Failed to search subfolder '{Path}' for a video.", sub.Path);
                continue;
            }

            if (found is not null) return found;
        }

        return null;
    }

    private async Task<string?> GenerateFrameAsync(StorageFolder folder, StorageFile video)
    {
        using StorageItemThumbnail thumbnail =
            await video.GetThumbnailAsync(ThumbnailMode.SingleItem, requestedSize: 1280, ThumbnailOptions.UseCurrentScale);
        if (thumbnail is not { Type: ThumbnailType.Image }) return null;

        StorageFolder artworkFolder = await GetArtworkFolderAsync();
        string fileName = BuildFileName(folder.Path, PosterSource.AutoFrame, ".jpg");
        StorageFile target = await artworkFolder.CreateFileAsync(fileName, CreationCollisionOption.ReplaceExisting);

        using (IRandomAccessStream output = await target.OpenAsync(FileAccessMode.ReadWrite))
        {
            BitmapDecoder decoder = await BitmapDecoder.CreateAsync(thumbnail);
            BitmapEncoder encoder = await BitmapEncoder.CreateForTranscodingAsync(output, decoder);
            await encoder.FlushAsync();
        }

        await RecordPosterAsync(folder.Path, fileName, PosterSource.AutoFrame, artworkFolder);
        return fileName;
    }

    private async Task<string> CopyIntoArtworkFolderAsync(StorageFolder folder, StorageFile source, PosterSource kind)
    {
        StorageFolder artworkFolder = await GetArtworkFolderAsync();
        string extension = System.IO.Path.GetExtension(source.Name);
        string fileName = BuildFileName(folder.Path, kind, extension);
        await source.CopyAsync(artworkFolder, fileName, NameCollisionOption.ReplaceExisting);
        await RecordPosterAsync(folder.Path, fileName, kind, artworkFolder);
        return fileName;
    }

    /// <summary>
    /// Points the folder's row at the newly written file and removes the file it replaced.
    /// The write touches only the poster columns, so it cannot roll back a custom title saved
    /// between resolving the artwork and storing it.
    /// </summary>
    private async Task RecordPosterAsync(string folderPath, string fileName, PosterSource source, StorageFolder artworkFolder)
    {
        string? supersededFileName = await _databaseService.SetFolderPosterAsync(folderPath, fileName, source);
        if (supersededFileName is not { Length: > 0 }
            || string.Equals(supersededFileName, fileName, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        await TryDeleteArtworkFileAsync(artworkFolder, supersededFileName);
    }

    private static string BuildFileName(string folderPath, PosterSource source, string extension)
    {
        // The prefix marks where the file came from: "m_" is a deliberate user choice, "g_" is
        // derived artwork the app can regenerate.
        string prefix = source is PosterSource.Manual ? "m_" : "g_";
        uint hash = 2166136261u;
        foreach (char c in folderPath)
        {
            hash = (hash ^ char.ToLowerInvariant(c)) * 16777619u;
        }

        // Every write gets a fresh discriminator so replacement artwork lands on a new
        // ms-appdata URI. XAML caches a BitmapImage by URI, so writing new bytes to the name the
        // folder already used would keep showing the old image until the app restarts.
        ulong discriminator = unchecked((ulong)DateTime.UtcNow.Ticks + (ulong)Interlocked.Increment(ref _artworkSequence));
        return string.Concat(prefix, hash.ToString("x8"), "_", discriminator.ToString("x"), extension);
    }
}
