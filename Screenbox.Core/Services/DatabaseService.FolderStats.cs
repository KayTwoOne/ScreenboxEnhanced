using System;
using System.IO;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using Screenbox.Core.Models;

namespace Screenbox.Core.Services;

public sealed partial class DatabaseService
{
    /// <inheritdoc/>
    public async Task<FolderStats> GetFolderStatsAsync(string folderPath)
    {
        if (string.IsNullOrEmpty(folderPath)) return default;

        await EnsureInitializedAsync();
        using var connection = CreateConnection();
        using var cmd = connection.CreateCommand();

        // The trailing separator stops "Dan Da Dan" matching "Dandadan 2nd Season".
        // The watch_state join is case-insensitive because watch_state.location is stored
        // upper-invariant while media_records.path keeps its original casing.
        cmd.CommandText = """
            SELECT
                COUNT(*),
                COALESCE(SUM(m.duration_ticks), 0),
                SUM(CASE WHEN w.completed = 1 THEN 0 ELSE 1 END)
            FROM media_records m
            LEFT JOIN watch_state w
                ON UPPER(w.location) = UPPER(m.path)
            WHERE UPPER(m.path) LIKE UPPER(@prefix) ESCAPE '\';
            """;

        string prefix = folderPath.TrimEnd(Path.DirectorySeparatorChar)
            + Path.DirectorySeparatorChar;
        cmd.Parameters.AddWithValue("@prefix", EscapeLike(prefix) + "%");

        using SqliteDataReader reader = cmd.ExecuteReader();
        if (!reader.Read()) return default;

        int count = reader.GetInt32(0);
        long ticks = reader.GetInt64(1);
        int unwatched = reader.IsDBNull(2) ? 0 : reader.GetInt32(2);
        return new FolderStats(count, new TimeSpan(ticks), unwatched);
    }

    /// <summary>
    /// Escapes LIKE wildcards so a folder name containing % or _ matches literally.
    /// </summary>
    private static string EscapeLike(string value) =>
        value.Replace(@"\", @"\\").Replace("%", @"\%").Replace("_", @"\_");
}
