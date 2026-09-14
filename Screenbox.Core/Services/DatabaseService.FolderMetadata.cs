using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using Screenbox.Core.Enums;
using Screenbox.Core.Models;

namespace Screenbox.Core.Services;

public sealed partial class DatabaseService
{
    /// <inheritdoc/>
    /// <remarks>
    /// Writes the whole row. Callers that only own some of the columns must use the targeted
    /// writers below instead, so a poster update cannot silently blank a custom title written
    /// between their read and their write.
    /// </remarks>
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
    public async Task<List<FolderMetadataDto>> ListFolderMetadataAsync()
    {
        await EnsureInitializedAsync();
        using var connection = CreateConnection();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            SELECT path, custom_title, poster_file, poster_source, provider_pin, sort_order
            FROM folder_metadata;
            """;

        var results = new List<FolderMetadataDto>();
        using SqliteDataReader reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            results.Add(new FolderMetadataDto
            {
                Path = reader.GetString(0),
                CustomTitle = reader.IsDBNull(1) ? null : reader.GetString(1),
                PosterFile = reader.IsDBNull(2) ? null : reader.GetString(2),
                PosterSource = (PosterSource)reader.GetInt32(3),
                ProviderPin = reader.IsDBNull(4) ? null : reader.GetString(4),
                SortOrder = reader.IsDBNull(5) ? null : reader.GetInt32(5)
            });
        }

        return results;
    }

    /// <inheritdoc/>
    public async Task<string?> SetFolderPosterAsync(string path, string posterFile, PosterSource source)
    {
        await EnsureInitializedAsync();
        using var connection = CreateConnection();

        // The read and the write are one immediate transaction: an artwork update must not be able
        // to observe a row, lose a race to a concurrent title edit, and then write back stale
        // columns. The upsert also touches only the poster columns, so nothing else can be clobbered.
        using SqliteTransaction transaction = connection.BeginTransaction(deferred: false);

        string? previousPosterFile;
        using (SqliteCommand read = connection.CreateCommand())
        {
            read.Transaction = transaction;
            read.CommandText = "SELECT poster_file FROM folder_metadata WHERE path = @path;";
            read.Parameters.AddWithValue("@path", path);
            previousPosterFile = read.ExecuteScalar() as string;
        }

        using (SqliteCommand write = connection.CreateCommand())
        {
            write.Transaction = transaction;
            write.CommandText = """
                INSERT INTO folder_metadata (path, poster_file, poster_source)
                VALUES (@path, @poster, @source)
                ON CONFLICT(path) DO UPDATE SET
                    poster_file = excluded.poster_file,
                    poster_source = excluded.poster_source;
                """;
            write.Parameters.AddWithValue("@path", path);
            write.Parameters.AddWithValue("@poster", posterFile);
            write.Parameters.AddWithValue("@source", (int)source);
            write.ExecuteNonQuery();
        }

        transaction.Commit();
        return previousPosterFile;
    }

    /// <inheritdoc/>
    public async Task SetFolderCustomTitleAsync(string path, string? customTitle)
    {
        await EnsureInitializedAsync();
        using var connection = CreateConnection();
        using var cmd = connection.CreateCommand();

        // Touches custom_title only, so a title edit cannot roll back a poster written since the
        // dialog was opened. A single statement needs no explicit transaction.
        cmd.CommandText = """
            INSERT INTO folder_metadata (path, custom_title)
            VALUES (@path, @title)
            ON CONFLICT(path) DO UPDATE SET custom_title = excluded.custom_title;
            """;
        cmd.Parameters.AddWithValue("@path", path);
        cmd.Parameters.AddWithValue("@title", (object?)customTitle ?? DBNull.Value);
        cmd.ExecuteNonQuery();
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
