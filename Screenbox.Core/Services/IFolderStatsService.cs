using System.Threading.Tasks;
using Screenbox.Core.Models;

namespace Screenbox.Core.Services;

/// <summary>
/// Provides aggregate statistics for library folders, cached because captions are
/// requested once per tile as the grid virtualizes.
/// </summary>
public interface IFolderStatsService
{
    /// <summary>Returns stats for a folder, computing and caching them if needed.</summary>
    Task<FolderStats> GetAsync(string folderPath);

    /// <summary>Returns cached stats without touching the database.</summary>
    bool TryGetCached(string folderPath, out FolderStats stats);

    /// <summary>Drops all cached stats. Call when watch state or the library changes.</summary>
    void Invalidate();
}
