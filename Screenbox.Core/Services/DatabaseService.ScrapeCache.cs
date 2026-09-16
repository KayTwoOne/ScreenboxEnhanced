using System;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using Screenbox.Core.Models;

namespace Screenbox.Core.Services;

public sealed partial class DatabaseService
{
    /// <inheritdoc/>
    public async Task SaveScrapeCacheAsync(FolderScrapeCacheDto cache)
    {
        await EnsureInitializedAsync();
        using var connection = CreateConnection();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            INSERT OR REPLACE INTO folder_scrape_cache
                (path, provider, provider_id, synopsis, year, scraped_poster, last_scraped)
            VALUES
                (@path, @provider, @providerId, @synopsis, @year, @poster, @lastScraped);
            """;
        cmd.Parameters.AddWithValue("@path", cache.Path);
        cmd.Parameters.AddWithValue("@provider", (object?)cache.Provider ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@providerId", (object?)cache.ProviderId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@synopsis", (object?)cache.Synopsis ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@year", (object?)cache.Year ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@poster", (object?)cache.ScrapedPoster ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@lastScraped", (object?)cache.LastScraped?.UtcTicks ?? DBNull.Value);
        cmd.ExecuteNonQuery();
    }

    /// <inheritdoc/>
    public async Task<FolderScrapeCacheDto?> LoadScrapeCacheAsync(string path)
    {
        await EnsureInitializedAsync();
        using var connection = CreateConnection();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            SELECT path, provider, provider_id, synopsis, year, scraped_poster, last_scraped
            FROM folder_scrape_cache WHERE path = @path;
            """;
        cmd.Parameters.AddWithValue("@path", path);

        using SqliteDataReader reader = cmd.ExecuteReader();
        return reader.Read() ? ReadScrapeCacheRow(reader) : null;
    }

    /// <inheritdoc/>
    public async Task ClearScrapeCacheAsync()
    {
        await EnsureInitializedAsync();
        using var connection = CreateConnection();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "DELETE FROM folder_scrape_cache;";
        cmd.ExecuteNonQuery();
    }

    private static FolderScrapeCacheDto ReadScrapeCacheRow(SqliteDataReader reader) => new()
    {
        Path = reader.GetString(0),
        Provider = reader.IsDBNull(1) ? null : reader.GetString(1),
        ProviderId = reader.IsDBNull(2) ? null : reader.GetString(2),
        Synopsis = reader.IsDBNull(3) ? null : reader.GetString(3),
        Year = reader.IsDBNull(4) ? null : reader.GetInt32(4),
        ScrapedPoster = reader.IsDBNull(5) ? null : reader.GetString(5),
        LastScraped = reader.IsDBNull(6) ? null : new DateTimeOffset(reader.GetInt64(6), TimeSpan.Zero)
    };
}
