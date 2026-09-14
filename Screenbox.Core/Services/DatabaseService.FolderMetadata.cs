using System;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using Screenbox.Core.Enums;
using Screenbox.Core.Models;

namespace Screenbox.Core.Services;

public sealed partial class DatabaseService
{
    /// <inheritdoc/>
    public async Task SaveFolderMetadataAsync(FolderMetadataDto metadata)
    {
        await EnsureInitializedAsync();
        using var connection = CreateConnection();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            INSERT OR REPLACE INTO folder_metadata
                (path, custom_title, poster_file, poster_source, provider_pin, sort_order)
            VALUES
                (@path, @title, @poster, @source, @pin, @order);
            """;
        cmd.Parameters.AddWithValue("@path", metadata.Path);
        cmd.Parameters.AddWithValue("@title", (object?)metadata.CustomTitle ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@poster", (object?)metadata.PosterFile ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@source", (int)metadata.PosterSource);
        cmd.Parameters.AddWithValue("@pin", (object?)metadata.ProviderPin ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@order", (object?)metadata.SortOrder ?? DBNull.Value);
        cmd.ExecuteNonQuery();
    }

    /// <inheritdoc/>
    public async Task<FolderMetadataDto?> LoadFolderMetadataAsync(string path)
    {
        await EnsureInitializedAsync();
        using var connection = CreateConnection();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            SELECT path, custom_title, poster_file, poster_source, provider_pin, sort_order
            FROM folder_metadata WHERE path = @path;
            """;
        cmd.Parameters.AddWithValue("@path", path);

        using SqliteDataReader reader = cmd.ExecuteReader();
        if (!reader.Read()) return null;

        return new FolderMetadataDto
        {
            Path = reader.GetString(0),
            CustomTitle = reader.IsDBNull(1) ? null : reader.GetString(1),
            PosterFile = reader.IsDBNull(2) ? null : reader.GetString(2),
            PosterSource = (PosterSource)reader.GetInt32(3),
            ProviderPin = reader.IsDBNull(4) ? null : reader.GetString(4),
            SortOrder = reader.IsDBNull(5) ? null : reader.GetInt32(5)
        };
    }

    /// <inheritdoc/>
    public async Task DeleteFolderMetadataAsync(string path)
    {
        await EnsureInitializedAsync();
        using var connection = CreateConnection();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "DELETE FROM folder_metadata WHERE path = @path;";
        cmd.Parameters.AddWithValue("@path", path);
        cmd.ExecuteNonQuery();
    }
}
