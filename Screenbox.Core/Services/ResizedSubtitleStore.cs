using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Windows.Storage;
using Windows.Storage.AccessCache;

namespace Screenbox.Core.Services;

/// <inheritdoc cref="IResizedSubtitleStore"/>
public sealed class ResizedSubtitleStore : IResizedSubtitleStore
{
    private const string FolderName = "ResizedSubtitles";

    // Marks this class's entries in the FutureAccessList, which holds at most 1000 and survives
    // restarts. The player opens files through it, so entries have to be removed by hand.
    private const string AccessListMetadata = "resized-subtitle";

    private const string MrlScheme = "winrt://";

    private readonly Dictionary<string, StorageFile> _files = new();

    /// <inheritdoc/>
    public async Task<string> WriteAsync(string content, CancellationToken cancellationToken)
    {
        StorageFolder folder = await ApplicationData.Current.TemporaryFolder.CreateFolderAsync(
            FolderName, CreationCollisionOption.OpenIfExists);
        cancellationToken.ThrowIfCancellationRequested();

        StorageFile file = await folder.CreateFileAsync($"{Guid.NewGuid():N}.ass", CreationCollisionOption.ReplaceExisting);

        // With a byte order mark, VLC does not have to guess the encoding of non-ASCII subtitles.
        UTF8Encoding encoding = new(encoderShouldEmitUTF8Identifier: true);
        byte[] bytes = [.. encoding.GetPreamble(), .. encoding.GetBytes(content)];
        await FileIO.WriteBytesAsync(file, bytes);

        string token = StorageApplicationPermissions.FutureAccessList.Add(file, AccessListMetadata);
        string mrl = MrlScheme + token;
        lock (_files)
        {
            _files[mrl] = file;
        }

        return mrl;
    }

    /// <inheritdoc/>
    public async Task ReleaseAsync(IReadOnlyCollection<string> mrls)
    {
        foreach (string mrl in mrls)
        {
            StorageApplicationPermissions.FutureAccessList.Remove(mrl.Substring(MrlScheme.Length));

            StorageFile? file;
            lock (_files)
            {
                _files.Remove(mrl, out file);
            }

            if (file is not null)
            {
                await TryDeleteAsync(file);
            }
        }
    }

    /// <inheritdoc/>
    public async Task DeleteAllAsync()
    {
        StorageItemAccessList accessList = StorageApplicationPermissions.FutureAccessList;
        string[] tokens = accessList.Entries
            .Where(entry => entry.Metadata == AccessListMetadata)
            .Select(entry => entry.Token)
            .ToArray();
        foreach (string token in tokens)
        {
            accessList.Remove(token);
        }

        StorageFolder folder = await ApplicationData.Current.TemporaryFolder.CreateFolderAsync(
            FolderName, CreationCollisionOption.OpenIfExists);
        foreach (StorageFile file in await folder.GetFilesAsync())
        {
            await TryDeleteAsync(file);
        }
    }

    private static async Task TryDeleteAsync(StorageFile file)
    {
        try
        {
            await file.DeleteAsync(StorageDeleteOption.PermanentDelete);
        }
        catch (Exception)
        {
            // VLC can hold a file open until its input has fully closed. It goes at the next startup.
        }
    }
}
