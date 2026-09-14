using System;
using System.Collections.Generic;
using System.Linq;
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
    public static bool ShouldSkipConventionCopy(FolderMetadataDto? metadata, string expectedFileName, bool destinationFileExists)
    {
        return destinationFileExists
            && metadata is { PosterSource: PosterSource.Convention, PosterFile: { Length: > 0 } existingFileName }
            && string.Equals(existingFileName, expectedFileName, StringComparison.Ordinal);
    }

    /// <inheritdoc/>
    public async Task<string?> GetPosterFileNameAsync(StorageFolder folder)
    {
        try
        {
            FolderMetadataDto? metadata = await _databaseService.LoadFolderMetadataAsync(folder.Path);
            StorageFolder artworkFolder = await GetArtworkFolderAsync();

            StorageFile? conventionFile = await FindConventionFileAsync(folder);
            bool hasCachedFrame = metadata?.PosterFile is { Length: > 0 } name
                && await artworkFolder.TryGetItemAsync(name) is not null;

            ArtworkDecision decision = ArtworkResolver.Resolve(metadata, conventionFile is not null, hasCachedFrame);

            if (!decision.NeedsGeneration)
            {
                if (decision.Source is PosterSource.Convention && conventionFile is not null)
                {
                    string expectedFileName = BuildFileName(
                        folder.Path, PosterSource.Convention, System.IO.Path.GetExtension(conventionFile.Name));
                    bool destinationExists = await artworkFolder.TryGetItemAsync(expectedFileName) is not null;

                    // Convention posters are resolved on every navigation into a folder, so avoid
                    // re-copying the file and re-writing the database row when nothing has changed.
                    if (ShouldSkipConventionCopy(metadata, expectedFileName, destinationExists))
                    {
                        return expectedFileName;
                    }

                    return await CopyIntoArtworkFolderAsync(folder, conventionFile, PosterSource.Convention);
                }

                return decision.FileName;
            }

            StorageFile? video = await FindFirstVideoAsync(folder, MaxRecursionDepth);
            if (video is null) return null;

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
            return await CopyIntoArtworkFolderAsync(folder, image, PosterSource.Manual);
        }
        catch (Exception e)
        {
            _logger.LogError(e, "Failed to set manual poster for '{Path}'.", folder.Path);
            return null;
        }
    }

    /// <inheritdoc/>
    public async Task ClearGeneratedArtworkAsync()
    {
        StorageFolder artworkFolder = await GetArtworkFolderAsync();
        foreach (StorageFile file in await artworkFolder.GetFilesAsync())
        {
            // Manual posters are prefixed "m_" and must survive a clear.
            if (file.Name.StartsWith("m_", StringComparison.Ordinal)) continue;
            try
            {
                await file.DeleteAsync(StorageDeleteOption.PermanentDelete);
            }
            catch (Exception e)
            {
                _logger.LogError(e, "Failed to delete cached artwork '{Name}'.", file.Name);
            }
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

        await UpsertPosterAsync(folder.Path, fileName, PosterSource.AutoFrame);
        return fileName;
    }

    private async Task<string> CopyIntoArtworkFolderAsync(StorageFolder folder, StorageFile source, PosterSource kind)
    {
        StorageFolder artworkFolder = await GetArtworkFolderAsync();
        string extension = System.IO.Path.GetExtension(source.Name);
        string fileName = BuildFileName(folder.Path, kind, extension);
        await source.CopyAsync(artworkFolder, fileName, NameCollisionOption.ReplaceExisting);
        await UpsertPosterAsync(folder.Path, fileName, kind);
        return fileName;
    }

    private async Task UpsertPosterAsync(string folderPath, string fileName, PosterSource source)
    {
        FolderMetadataDto metadata =
            await _databaseService.LoadFolderMetadataAsync(folderPath) ?? new FolderMetadataDto { Path = folderPath };
        metadata.PosterFile = fileName;
        metadata.PosterSource = source;
        await _databaseService.SaveFolderMetadataAsync(metadata);
    }

    private static string BuildFileName(string folderPath, PosterSource source, string extension)
    {
        // Manual posters are prefixed so ClearGeneratedArtworkAsync can preserve them.
        string prefix = source is PosterSource.Manual ? "m_" : "g_";
        uint hash = 2166136261u;
        foreach (char c in folderPath)
        {
            hash = (hash ^ char.ToLowerInvariant(c)) * 16777619u;
        }

        return string.Concat(prefix, hash.ToString("x8"), extension);
    }
}
