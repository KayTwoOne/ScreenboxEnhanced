using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Screenbox.Core.Models;
using Screenbox.Core.Models.Serialization;

namespace Screenbox.Core.Services;

public sealed partial class DatabaseService
{
    private async Task EnsureInitializedAsync()
    {
        Task? initializationTask;

        lock (_initLock)
        {
            _initializationTask ??= InitializeCoreAsync();
            initializationTask = _initializationTask;
        }

        await initializationTask;
    }

    private async Task InitializeCoreAsync()
    {
        string dbPath = Path.Combine(DbFolderPath, DbFileName);
        string connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = dbPath,
            Mode = SqliteOpenMode.ReadWriteCreate,
        }.ToString();

        _connectionString = connectionString;

        try
        {
            await EnsureSchemaAsync();
        }
        catch (Exception ex) when (ex is SqliteException or IOException)
        {
            _logger.LogError(ex, "Failed to initialize the database schema. Recreating the database file.");
            RecreateDatabaseFile(dbPath, connectionString);
        }
    }

    private async Task EnsureSchemaAsync()
    {
        using var connection = new SqliteConnection(_connectionString);
        connection.Open();

        ExecuteNonQuery(connection, "PRAGMA journal_mode=WAL;");
        ExecuteNonQuery(connection, "PRAGMA foreign_keys=OFF;");

        bool migrationComplete;
        using var transaction = connection.BeginTransaction();
        EnsureReplaceableTable(connection, "library_folders", CreateLibraryFoldersSql, "id", "path", "media_type");
        EnsureReplaceableTable(connection, "media_records", CreateMediaRecordsSql,
            "path", "title", "media_type", "date_added", "duration_ticks", "year",
            "artist", "album", "album_artist", "composers", "genre", "track_number", "bitrate",
            "subtitle", "producers", "writers", "width", "height", "video_bitrate");
        EnsureReplaceableTable(connection, "playback_progress", CreatePlaybackProgressSql, "location", "position_ticks");
        EnsurePlaylistsTable(connection);
        EnsurePlaylistItemsTable(connection);
        EnsureFolderMetadataTable(connection);
        EnsureWatchStateTable(connection);
        migrationComplete = await TryImportLegacyPlaylistsAsync(connection);
        transaction.Commit();
        ExecuteNonQuery(connection, "PRAGMA foreign_keys=ON;");

        if (migrationComplete)
        {
            TryDeleteLegacyMigrationArtifacts();
        }
    }

    /// <summary>
    /// Last-resort recovery for a database file that cannot be opened or migrated at all. This
    /// deletes the file, which destroys durable user-authored data in <c>folder_metadata</c> and
    /// <c>playlists</c> along with the rebuildable cache tables. Per-table migration
    /// (see <see cref="EnsureFolderMetadataTable"/>) is what keeps that data safe from routine
    /// schema changes; nothing protects it from genuine file corruption.
    /// </summary>
    private void RecreateDatabaseFile(string dbPath, string connectionString)
    {
        _connectionString = null;
        TryDeleteDatabase(dbPath);
        _connectionString = connectionString;

        using var connection = new SqliteConnection(_connectionString);
        connection.Open();
        ExecuteNonQuery(connection, "PRAGMA journal_mode=WAL;");
        ExecuteNonQuery(connection, "PRAGMA foreign_keys=OFF;");

        using var transaction = connection.BeginTransaction();
        ExecuteNonQuery(connection, CreateLibraryFoldersSql);
        ExecuteNonQuery(connection, CreateMediaRecordsSql);
        ExecuteNonQuery(connection, CreatePlaybackProgressSql);
        ExecuteNonQuery(connection, CreatePlaylistsSql);
        ExecuteNonQuery(connection, CreatePlaylistItemsSql);
        ExecuteNonQuery(connection, BuildCreateTableSql(FolderMetadataTableName, FolderMetadataColumns));
        ExecuteNonQuery(connection, CreateWatchStateSql);
        transaction.Commit();
        ExecuteNonQuery(connection, "PRAGMA foreign_keys=ON;");
    }

    private static void EnsureReplaceableTable(SqliteConnection connection, string tableName, string createSql, params string[] expectedColumns)
    {
        HashSet<string> actualColumns = ReadTableColumns(connection, tableName);
        if (actualColumns.Count is 0)
        {
            ExecuteNonQuery(connection, createSql);
            return;
        }

        if (HasSchemaDrift(actualColumns, expectedColumns))
        {
            ExecuteNonQuery(connection, $"DROP TABLE IF EXISTS {tableName};");
            ExecuteNonQuery(connection, createSql);
        }
    }

    private static void EnsurePlaylistsTable(SqliteConnection connection)
    {
        HashSet<string> actualColumns = ReadTableColumns(connection, "playlists");
        string[] expectedColumns = ["id", "display_name", "last_updated"];
        if (actualColumns.Count is 0)
        {
            ExecuteNonQuery(connection, CreatePlaylistsSql);
            return;
        }

        if (!HasSchemaDrift(actualColumns, expectedColumns))
        {
            return;
        }

        ExecuteNonQuery(connection, "DROP TABLE playlists;");
        ExecuteNonQuery(connection, CreatePlaylistsSql);
    }

    private static void EnsurePlaylistItemsTable(SqliteConnection connection)
    {
        HashSet<string> actualColumns = ReadTableColumns(connection, "playlist_items");
        string[] expectedColumns = ["id", "playlist_id", "path", "sort_order"];
        if (actualColumns.Count is 0)
        {
            ExecuteNonQuery(connection, CreatePlaylistItemsSql);
            return;
        }

        if (!HasSchemaDrift(actualColumns, expectedColumns))
        {
            return;
        }

        ExecuteNonQuery(connection, "DROP TABLE playlist_items;");
        ExecuteNonQuery(connection, CreatePlaylistItemsSql);
    }

    // `folder_metadata` holds durable, user-authored data: custom folder titles and manual poster
    // choices that nothing else in the app can reconstruct. It is therefore the one table here that
    // is NEVER dropped on column drift. Missing columns are added with ALTER TABLE, and a shape that
    // cannot be reached additively is rebuilt by copying the existing rows into the new table.
    // Registering it with EnsureReplaceableTable - or copying the drop-and-recreate shape of
    // EnsurePlaylistsTable and EnsurePlaylistItemsTable, which do destroy their rows on drift -
    // would wipe every custom title and poster choice the first time a column is added.
    private static void EnsureFolderMetadataTable(SqliteConnection connection)
    {
        HashSet<string> actualColumns = ReadTableColumns(connection, FolderMetadataTableName);
        if (actualColumns.Count is 0)
        {
            ExecuteNonQuery(connection, BuildCreateTableSql(FolderMetadataTableName, FolderMetadataColumns));
            return;
        }

        string[] expectedColumns = FolderMetadataColumns.Select(column => column.Name).ToArray();
        if (!HasSchemaDrift(actualColumns, expectedColumns))
        {
            return;
        }

        // Columns the stored database has never heard of can simply be appended in place, which
        // leaves every existing row untouched. SQLite cannot ADD COLUMN for a PRIMARY KEY or UNIQUE
        // column, and it cannot remove a column this way, so those cases fall through to the rebuild.
        var expectedNames = new HashSet<string>(expectedColumns, StringComparer.OrdinalIgnoreCase);
        bool hasUnknownColumns = actualColumns.Any(column => !expectedNames.Contains(column));
        (string Name, string Definition)[] missingColumns =
            FolderMetadataColumns.Where(column => !actualColumns.Contains(column.Name)).ToArray();

        if (!hasUnknownColumns && missingColumns.All(column => CanAddColumnInPlace(column.Definition)))
        {
            foreach ((string name, string definition) in missingColumns)
            {
                ExecuteNonQuery(connection, $"ALTER TABLE {FolderMetadataTableName} ADD COLUMN {name} {definition};");
            }

            return;
        }

        RebuildFolderMetadataTable(connection, actualColumns);
    }

    /// <summary>
    /// Copies every stored row into a table with the current shape, for drift that
    /// <c>ALTER TABLE ... ADD COLUMN</c> cannot express (a removed or retyped column). Columns that
    /// exist in both shapes carry their values across; anything else falls back to its default.
    /// </summary>
    private static void RebuildFolderMetadataTable(SqliteConnection connection, HashSet<string> actualColumns)
    {
        const string stagingTableName = "folder_metadata_migrating";
        ExecuteNonQuery(connection, $"DROP TABLE IF EXISTS {stagingTableName};");
        ExecuteNonQuery(connection, BuildCreateTableSql(stagingTableName, FolderMetadataColumns));

        string[] sharedColumns = FolderMetadataColumns
            .Where(column => actualColumns.Contains(column.Name))
            .Select(column => column.Name)
            .ToArray();

        if (sharedColumns.Length > 0)
        {
            string columnList = string.Join(", ", sharedColumns);
            ExecuteNonQuery(connection,
                $"INSERT OR REPLACE INTO {stagingTableName} ({columnList}) SELECT {columnList} FROM {FolderMetadataTableName};");
        }

        ExecuteNonQuery(connection, $"DROP TABLE {FolderMetadataTableName};");
        ExecuteNonQuery(connection, $"ALTER TABLE {stagingTableName} RENAME TO {FolderMetadataTableName};");
    }

    // `watch_state` holds durable, unbounded watched-history data: whether each media item has been
    // watched to completion. Unlike `playback_progress` (a capped 64-entry LRU that is rebuilt by
    // deleting and reinserting the whole table), watch_state must never be dropped on column drift,
    // or a user's entire watch history would be silently lost the first time a column is added.
    // Missing columns are added with ALTER TABLE, and a shape that cannot be reached additively is
    // rebuilt by copying the existing rows into the new table, exactly like EnsureFolderMetadataTable.
    private static void EnsureWatchStateTable(SqliteConnection connection)
    {
        HashSet<string> actualColumns = ReadTableColumns(connection, WatchStateTableName);
        if (actualColumns.Count is 0)
        {
            ExecuteNonQuery(connection, BuildCreateTableSql(WatchStateTableName, WatchStateColumns));
            return;
        }

        string[] expectedColumns = WatchStateColumns.Select(column => column.Name).ToArray();
        if (!HasSchemaDrift(actualColumns, expectedColumns))
        {
            return;
        }

        var expectedNames = new HashSet<string>(expectedColumns, StringComparer.OrdinalIgnoreCase);
        bool hasUnknownColumns = actualColumns.Any(column => !expectedNames.Contains(column));
        (string Name, string Definition)[] missingColumns =
            WatchStateColumns.Where(column => !actualColumns.Contains(column.Name)).ToArray();

        if (!hasUnknownColumns && missingColumns.All(column => CanAddColumnInPlace(column.Definition)))
        {
            foreach ((string name, string definition) in missingColumns)
            {
                ExecuteNonQuery(connection, $"ALTER TABLE {WatchStateTableName} ADD COLUMN {name} {definition};");
            }

            return;
        }

        RebuildWatchStateTable(connection, actualColumns);
    }

    /// <summary>
    /// Copies every stored row into a table with the current shape, for drift that
    /// <c>ALTER TABLE ... ADD COLUMN</c> cannot express (a removed or retyped column). Columns that
    /// exist in both shapes carry their values across; anything else falls back to its default.
    /// </summary>
    private static void RebuildWatchStateTable(SqliteConnection connection, HashSet<string> actualColumns)
    {
        const string stagingTableName = "watch_state_migrating";
        ExecuteNonQuery(connection, $"DROP TABLE IF EXISTS {stagingTableName};");
        ExecuteNonQuery(connection, BuildCreateTableSql(stagingTableName, WatchStateColumns));

        string[] sharedColumns = WatchStateColumns
            .Where(column => actualColumns.Contains(column.Name))
            .Select(column => column.Name)
            .ToArray();

        if (sharedColumns.Length > 0)
        {
            string columnList = string.Join(", ", sharedColumns);
            ExecuteNonQuery(connection,
                $"INSERT OR REPLACE INTO {stagingTableName} ({columnList}) SELECT {columnList} FROM {WatchStateTableName};");
        }

        ExecuteNonQuery(connection, $"DROP TABLE {WatchStateTableName};");
        ExecuteNonQuery(connection, $"ALTER TABLE {stagingTableName} RENAME TO {WatchStateTableName};");
    }

    /// <summary>
    /// True when SQLite accepts the column definition in an <c>ALTER TABLE ... ADD COLUMN</c>
    /// statement. Key and unique constraints, and NOT NULL without a default, must be rebuilt.
    /// </summary>
    private static bool CanAddColumnInPlace(string definition)
    {
        if (definition.Contains("PRIMARY KEY", StringComparison.OrdinalIgnoreCase)) return false;
        if (definition.Contains("UNIQUE", StringComparison.OrdinalIgnoreCase)) return false;
        return !definition.Contains("NOT NULL", StringComparison.OrdinalIgnoreCase)
               || definition.Contains("DEFAULT", StringComparison.OrdinalIgnoreCase);
    }

    private static string BuildCreateTableSql(string tableName, (string Name, string Definition)[] columns)
    {
        string body = string.Join(",\n    ", columns.Select(column => $"{column.Name} {column.Definition}"));
        return $"CREATE TABLE IF NOT EXISTS {tableName} (\n    {body}\n);";
    }

    private async Task<bool> TryImportLegacyPlaylistsAsync(SqliteConnection connection)
    {
        // Legacy playlists are stored in a folder named "Playlists" within the local app data folder, same as DB folder path.
        string playlistsDirPath = Path.Combine(DbFolderPath, LegacyPlaylistsFolderName);
        if (TableHasRows(connection, "playlists") || !Directory.Exists(playlistsDirPath))
        {
            return true;
        }

        string[] jsonPlaylistFiles = Directory.GetFiles(playlistsDirPath, "*.json");
        if (jsonPlaylistFiles.Length is 0)
        {
            return true;
        }

        using var upsertPlaylistCommand = connection.CreateCommand();
        upsertPlaylistCommand.CommandText = """
            INSERT OR REPLACE INTO playlists (id, display_name, last_updated)
            VALUES (@id, @name, @updated);
            """;
        var playlistIdParameter = upsertPlaylistCommand.Parameters.Add("@id", SqliteType.Text);
        var playlistNameParameter = upsertPlaylistCommand.Parameters.Add("@name", SqliteType.Text);
        var playlistUpdatedParameter = upsertPlaylistCommand.Parameters.Add("@updated", SqliteType.Integer);

        using var clearItemsCommand = connection.CreateCommand();
        clearItemsCommand.CommandText = "DELETE FROM playlist_items WHERE playlist_id = @id;";
        var clearItemsPlaylistIdParameter = clearItemsCommand.Parameters.Add("@id", SqliteType.Text);

        using var insertItemCommand = connection.CreateCommand();
        insertItemCommand.CommandText = """
            INSERT INTO playlist_items (playlist_id, path, sort_order)
            VALUES (@pid, @path, @order);
            """;
        var itemPlaylistIdParameter = insertItemCommand.Parameters.Add("@pid", SqliteType.Text);
        var itemPathParameter = insertItemCommand.Parameters.Add("@path", SqliteType.Text);
        var itemOrderParameter = insertItemCommand.Parameters.Add("@order", SqliteType.Integer);

        bool hasImportFailure = false;
        foreach (string filePath in jsonPlaylistFiles)
        {
            PlaylistRecordDto? playlist = null;
            try
            {
                string json = await File.ReadAllTextAsync(filePath);
                playlist = JsonSerializer.Deserialize(json, CoreJsonContext.Default.PlaylistRecordDto);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to read legacy playlist '{Path}'.", filePath);
                hasImportFailure = true;
                continue;
            }

            if (playlist is null || string.IsNullOrWhiteSpace(playlist.Id))
            {
                hasImportFailure = true;
                continue;
            }

            playlistIdParameter.Value = playlist.Id;
            playlistNameParameter.Value = string.IsNullOrWhiteSpace(playlist.DisplayName) ? playlist.Id : playlist.DisplayName;
            playlistUpdatedParameter.Value = playlist.LastUpdated == default
                ? DateTimeOffset.UtcNow.UtcTicks
                : playlist.LastUpdated.UtcTicks;
            upsertPlaylistCommand.ExecuteNonQuery();

            clearItemsPlaylistIdParameter.Value = playlist.Id;
            clearItemsCommand.ExecuteNonQuery();

            for (int i = 0; i < playlist.Items.Count; i++)
            {
                RawMediaRecordDto item = playlist.Items[i];
                if (string.IsNullOrWhiteSpace(item.Path))
                {
                    continue;
                }

                itemPlaylistIdParameter.Value = playlist.Id;
                itemPathParameter.Value = item.Path;
                itemOrderParameter.Value = i;
                insertItemCommand.ExecuteNonQuery();
            }
        }

        return !hasImportFailure;
    }

    private static bool TableHasRows(SqliteConnection connection, string tableName)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = $"SELECT EXISTS(SELECT 1 FROM {tableName} LIMIT 1);";
        return cmd.ExecuteScalar() is long value && value == 1;
    }

    private void TryDeleteLegacyMigrationArtifacts()
    {
        // Legacy files are stored within the local app data folder, same as DB folder path.
        foreach (string fileName in LegacyLocalFileNames)
        {
            try
            {
                string filePath = Path.Combine(DbFolderPath, fileName);
                if (File.Exists(filePath)) File.Delete(filePath);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to delete legacy artifact '{FileName}'.", fileName);
            }
        }

        try
        {
            string legacyPlaylistsFolder = Path.Combine(DbFolderPath, LegacyPlaylistsFolderName);
            if (Directory.Exists(legacyPlaylistsFolder)) Directory.Delete(legacyPlaylistsFolder, recursive: true);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to delete legacy folder '{FolderName}'.", LegacyPlaylistsFolderName);
        }
    }

    private static HashSet<string> ReadTableColumns(SqliteConnection connection, string tableName)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = $"PRAGMA table_info({tableName});";
        using var reader = cmd.ExecuteReader();

        var columns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        while (reader.Read())
        {
            columns.Add(reader.GetString(1));
        }

        return columns;
    }

    private static bool HasSchemaDrift(HashSet<string> actualColumns, params string[] expectedColumns)
    {
        return !actualColumns.SetEquals(expectedColumns);
    }

    private static void ExecuteNonQuery(SqliteConnection connection, string sql)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    private static void ExecuteNonQuery(SqliteConnection connection, string sql, params SqlParameterDto[] parameters)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = sql;
        foreach (SqlParameterDto parameter in parameters)
        {
            cmd.Parameters.AddWithValue(parameter.Name, parameter.Value);
        }

        cmd.ExecuteNonQuery();
    }

    private void TryDeleteDatabase(string dbPath)
    {
        foreach (string file in new[] { dbPath, dbPath + "-shm", dbPath + "-wal" })
        {
            try
            {
                if (File.Exists(file))
                {
                    File.Delete(file);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to delete database file '{FilePath}'.", file);
            }
        }
    }

    private const string CreateLibraryFoldersSql = """
        CREATE TABLE IF NOT EXISTS library_folders (
            id         INTEGER PRIMARY KEY AUTOINCREMENT,
            path       TEXT    UNIQUE NOT NULL,
            media_type INTEGER        NOT NULL
        );
        """;

    private const string CreateMediaRecordsSql = """
        CREATE TABLE IF NOT EXISTS media_records (
            path          TEXT PRIMARY KEY,
            title         TEXT,
            media_type    INTEGER,
            date_added    INTEGER,
            duration_ticks INTEGER,
            year          INTEGER,
            artist        TEXT,
            album         TEXT,
            album_artist  TEXT,
            composers     TEXT,
            genre         TEXT,
            track_number  INTEGER,
            bitrate       INTEGER,
            subtitle      TEXT,
            producers     TEXT,
            writers       TEXT,
            width         INTEGER,
            height        INTEGER,
            video_bitrate INTEGER
        );
        """;

    private const string CreatePlaybackProgressSql = """
        CREATE TABLE IF NOT EXISTS playback_progress (
            location      TEXT    PRIMARY KEY,
            position_ticks INTEGER NOT NULL
        );
        """;

    private const string CreatePlaylistsSql = """
        CREATE TABLE IF NOT EXISTS playlists (
            id           TEXT PRIMARY KEY,
            display_name TEXT    NOT NULL,
            last_updated INTEGER NOT NULL
        );
        """;

    private const string CreatePlaylistItemsSql = """
        CREATE TABLE IF NOT EXISTS playlist_items (
            id          INTEGER PRIMARY KEY AUTOINCREMENT,
            playlist_id TEXT    NOT NULL REFERENCES playlists(id) ON DELETE CASCADE,
            path        TEXT    NOT NULL,
            sort_order  INTEGER NOT NULL
        );
        """;

    private const string FolderMetadataTableName = "folder_metadata";

    /// <summary>
    /// The single source of truth for the <c>folder_metadata</c> shape. The same definitions build
    /// the CREATE TABLE statement for a new database and the ALTER TABLE statement that adds a
    /// column to an existing one, so a column added here migrates without dropping any user data.
    /// </summary>
    private static readonly (string Name, string Definition)[] FolderMetadataColumns =
    [
        ("path", "TEXT PRIMARY KEY"),
        ("custom_title", "TEXT"),
        ("poster_file", "TEXT"),
        ("poster_source", "INTEGER NOT NULL DEFAULT 0"),
        ("provider_pin", "TEXT"),
        ("sort_order", "INTEGER")
    ];

    private const string WatchStateTableName = "watch_state";

    private const string CreateWatchStateSql = """
        CREATE TABLE IF NOT EXISTS watch_state (
            location            TEXT PRIMARY KEY,
            completed           INTEGER NOT NULL DEFAULT 0,
            last_played         INTEGER,
            duration_ticks      INTEGER,
            last_position_ticks INTEGER NOT NULL DEFAULT 0,
            original_location   TEXT
        );
        """;

    /// <summary>
    /// The single source of truth for the <c>watch_state</c> shape. The same definitions build
    /// the CREATE TABLE statement for a new database and the ALTER TABLE statement that adds a
    /// column to an existing one, so a column added here migrates without dropping watch history.
    /// </summary>
    private static readonly (string Name, string Definition)[] WatchStateColumns =
    [
        ("location", "TEXT PRIMARY KEY"),
        ("completed", "INTEGER NOT NULL DEFAULT 0"),
        ("last_played", "INTEGER"),
        ("duration_ticks", "INTEGER"),
        ("last_position_ticks", "INTEGER NOT NULL DEFAULT 0"),
        ("original_location", "TEXT")
    ];
}
