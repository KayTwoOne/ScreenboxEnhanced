using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using Screenbox.Core.Models;

namespace Screenbox.Core.Services;

public sealed partial class DatabaseService
{
    /// <inheritdoc/>
    public async Task SaveWatchStateAsync(WatchStateDto state)
    {
        await EnsureInitializedAsync();
        using var connection = CreateConnection();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            INSERT INTO watch_state (location, completed, last_played, duration_ticks, last_position_ticks, original_location)
            VALUES (@loc, @done, @played, @ticks, @pos, @origLoc)
            ON CONFLICT(location) DO UPDATE SET
                completed = excluded.completed,
                last_played = excluded.last_played,
                duration_ticks = excluded.duration_ticks,
                last_position_ticks = excluded.last_position_ticks,
                original_location = excluded.original_location;
            """;
        cmd.Parameters.AddWithValue("@loc", state.Location);
        cmd.Parameters.AddWithValue("@done", state.Completed ? 1 : 0);
        cmd.Parameters.AddWithValue("@played", (object?)state.LastPlayed?.UtcTicks ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@ticks", (object?)state.Duration?.Ticks ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@pos", state.LastPosition.Ticks);
        cmd.Parameters.AddWithValue("@origLoc", (object?)state.OriginalLocation ?? DBNull.Value);
        cmd.ExecuteNonQuery();
    }

    /// <inheritdoc/>
    public async Task<WatchStateDto?> LoadWatchStateAsync(string location)
    {
        await EnsureInitializedAsync();
        using var connection = CreateConnection();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            SELECT location, completed, last_played, duration_ticks, last_position_ticks, original_location
            FROM watch_state WHERE location = @loc;
            """;
        cmd.Parameters.AddWithValue("@loc", location);

        using SqliteDataReader reader = cmd.ExecuteReader();
        return reader.Read() ? ReadRow(reader) : null;
    }

    /// <inheritdoc/>
    public async Task<List<WatchStateDto>> ListWatchStateAsync()
    {
        await EnsureInitializedAsync();
        using var connection = CreateConnection();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT location, completed, last_played, duration_ticks, last_position_ticks, original_location FROM watch_state;";

        var result = new List<WatchStateDto>();
        using SqliteDataReader reader = cmd.ExecuteReader();
        while (reader.Read()) result.Add(ReadRow(reader));
        return result;
    }

    /// <inheritdoc/>
    public async Task DeleteWatchStateAsync(string location)
    {
        await EnsureInitializedAsync();
        using var connection = CreateConnection();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "DELETE FROM watch_state WHERE location = @loc;";
        cmd.Parameters.AddWithValue("@loc", location);
        cmd.ExecuteNonQuery();
    }

    private static WatchStateDto ReadRow(SqliteDataReader reader) => new()
    {
        Location = reader.GetString(0),
        Completed = reader.GetInt32(1) != 0,
        LastPlayed = reader.IsDBNull(2) ? null : new DateTimeOffset(reader.GetInt64(2), TimeSpan.Zero),
        Duration = reader.IsDBNull(3) ? null : new TimeSpan(reader.GetInt64(3)),
        LastPosition = reader.IsDBNull(4) ? TimeSpan.Zero : new TimeSpan(reader.GetInt64(4)),
        OriginalLocation = reader.IsDBNull(5) ? null : reader.GetString(5)
    };
}
