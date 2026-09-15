# Phase 2: Watched State — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Record whether media has been finished and when it was last played, then surface that as watched indicators, progress bars, and a Continue Watching row.

**Architecture:** A new durable `watch_state` table stores completion and last-played per media location, owned by `WatchStateService`. The completion decision is a pure function so it can be tested without playback. The existing `playback_progress` mechanism is left untouched.

**Tech Stack:** C# 14, .NET 10 (`net10.0-windows10.0.26100.0`), UWP with `UseUwp=true`, CommunityToolkit.Mvvm, Microsoft.Data.Sqlite, TUnit.

**Spec:** `docs/superpowers/specs/2026-09-14-personal-media-centre-design.md` (Phase 2)

## Correction to the spec — read this before starting

The spec says to promote `playback_progress` to durable and add `completed` / `last_played` columns to it. **Do not do that.** Phase 1 and a reading of the existing code established two facts that invalidate it:

1. `EnsurePlaylistsTable` — the "careful path" the spec cites as preserving data — executes `DROP TABLE playlists;` on column drift. It preserves nothing. Phase 1 had to build a genuine `ALTER TABLE` migration for `folder_metadata`; see `EnsureFolderMetadataTable` and `RebuildFolderMetadataTable` in `DatabaseService.Schema.cs` for the pattern to copy.
2. `PlaybackProgressTracker` is a **capped LRU** — `private const int Capacity = 64` — held in memory and persisted by `ReplacePlaybackProgressAsync`, which does `DELETE FROM playback_progress;` then reinserts the whole snapshot. Adding a `completed` flag there would silently evict watched history after 64 items. A single series folder in the reference library holds 37 episodes.

Watched state is therefore **its own durable table**, unbounded, and `playback_progress` keeps its current role as a bounded resume cache. This also means Phase 2 touches none of upstream's resume mechanism.

## Global Constraints

- Build with MSBuild from Visual Studio 2026 only. **Never `dotnet build`.** Path: `C:\Program Files\Microsoft Visual Studio\18\Community\MSBuild\Current\Bin\MSBuild.exe`, argument form `-t:Build -p:Configuration=Debug -p:Platform=x64` (Git Bash mangles `/t:`).
- Close the running app before building — it locks `Screenbox.Core.dll` and the build fails with a wall of MSB3027 errors.
- `vswhere.exe` must be on PATH before building the app project: prepend `${env:ProgramFiles(x86)}\Microsoft Visual Studio\Installer`.
- Run tests with `dotnet run --project Screenbox.Core.Tests/Screenbox.Core.Tests.csproj`. 56 currently pass.
- `watch_state` is **durable user data**. Use the `EnsureFolderMetadataTable` migration pattern, never `EnsureReplaceableTable`.
- This is UWP: `Windows.UI.Xaml`, never `Microsoft.UI.Xaml`.
- Nullable reference types are enabled. Use `is null` / `is not null`.
- ViewModels must never reference localized strings.
- Lazy per-tile work belongs in `Screenbox/Behaviors/ThumbnailGridViewBehavior.cs`, not an eager loop in a ViewModel. Phase 1 learned this the hard way.
- An unhandled exception reached from an `async void` handler terminates the process with no log. Never let one escape.
- Conventional Commits. Branch: `feat/personal-media-centre`.

## File Structure

| File | Responsibility |
|---|---|
| `Screenbox.Core/Models/WatchStateDto.cs` | Durable watch record |
| `Screenbox.Core/Services/DatabaseService.WatchState.cs` | SQL persistence |
| `Screenbox.Core/Helpers/WatchThreshold.cs` | Pure completion decision |
| `Screenbox.Core/Services/WatchStateService.cs` | Owns watch state, exposes queries |
| `Screenbox.Core/ViewModels/MediaViewModel.cs` | Gains `IsWatched`, `WatchProgress` |
| `Screenbox.Core/Contexts/WatchStateContext.cs` | Observable Continue Watching collection |

---

### Task 1: `watch_state` schema and persistence

**Files:**
- Create: `Screenbox.Core/Models/WatchStateDto.cs`
- Create: `Screenbox.Core/Services/DatabaseService.WatchState.cs`
- Modify: `Screenbox.Core/Services/DatabaseService.Schema.cs`
- Modify: `Screenbox.Core/Services/IDatabaseService.cs`
- Test: `Screenbox.Core.Tests/Database/WatchStateTests.cs`

**Interfaces:**
- Consumes: `DatabaseService` (settable `DbFolderPath`, `InitializeAsync()`), `TestDirectoryFixture`, and the `EnsureFolderMetadataTable` / `RebuildFolderMetadataTable` migration pattern already in `DatabaseService.Schema.cs`.
- Produces: `WatchStateDto { string Location; bool Completed; DateTimeOffset? LastPlayed; TimeSpan? Duration }`; on `IDatabaseService`: `Task SaveWatchStateAsync(WatchStateDto)`, `Task<WatchStateDto?> LoadWatchStateAsync(string location)`, `Task<List<WatchStateDto>> ListWatchStateAsync()`, `Task DeleteWatchStateAsync(string location)`.

- [ ] **Step 1: Write the failing test**

Create `Screenbox.Core.Tests/Database/WatchStateTests.cs`:

```csharp
using Microsoft.Extensions.Logging.Abstractions;
using Screenbox.Core.Models;
using Screenbox.Core.Services;
using Screenbox.Core.Tests.Helpers;

namespace Screenbox.Core.Tests.Database;

public sealed class WatchStateTests
{
    private static async Task<DatabaseService> CreateServiceAsync(string dir)
    {
        var db = new DatabaseService(NullLogger<DatabaseService>.Instance) { DbFolderPath = dir };
        await db.InitializeAsync();
        return db;
    }

    [Test]
    public async Task SaveWatchStateAsync_RoundTripsAllFields()
    {
        using var fixture = new TestDirectoryFixture();
        DatabaseService db = await CreateServiceAsync(fixture.DirectoryPath);
        var played = new DateTimeOffset(2026, 9, 15, 20, 30, 0, TimeSpan.Zero);

        await db.SaveWatchStateAsync(new WatchStateDto
        {
            Location = @"D:\Media\Anime\Death Note\Death Note - 01x01.mkv",
            Completed = true,
            LastPlayed = played,
            Duration = TimeSpan.FromMinutes(23),
            LastPosition = TimeSpan.FromMinutes(21)
        });

        WatchStateDto? loaded = await db.LoadWatchStateAsync(@"D:\Media\Anime\Death Note\Death Note - 01x01.mkv");

        await Assert.That(loaded).IsNotNull();
        await Assert.That(loaded!.Completed).IsTrue();
        await Assert.That(loaded.LastPlayed).IsEqualTo(played);
        await Assert.That(loaded.Duration).IsEqualTo(TimeSpan.FromMinutes(23));
        await Assert.That(loaded.LastPosition).IsEqualTo(TimeSpan.FromMinutes(21));
    }

    [Test]
    public async Task LoadWatchStateAsync_ReturnsNullForUnknownLocation()
    {
        using var fixture = new TestDirectoryFixture();
        DatabaseService db = await CreateServiceAsync(fixture.DirectoryPath);

        await Assert.That(await db.LoadWatchStateAsync(@"D:\nope.mkv")).IsNull();
    }

    [Test]
    public async Task SaveWatchStateAsync_OverwritesExistingRow()
    {
        using var fixture = new TestDirectoryFixture();
        DatabaseService db = await CreateServiceAsync(fixture.DirectoryPath);
        const string loc = @"D:\Media\Anime\Gachiakuta\ep1.mkv";

        await db.SaveWatchStateAsync(new WatchStateDto { Location = loc, Completed = false });
        await db.SaveWatchStateAsync(new WatchStateDto { Location = loc, Completed = true });

        WatchStateDto? loaded = await db.LoadWatchStateAsync(loc);
        await Assert.That(loaded!.Completed).IsTrue();
        await Assert.That((await db.ListWatchStateAsync()).Count).IsEqualTo(1);
    }

    [Test]
    public async Task WatchState_IsNotCappedLikePlaybackProgress()
    {
        // playback_progress holds at most 64 entries by design. Watched history must not:
        // a single series folder in the reference library has 37 episodes.
        using var fixture = new TestDirectoryFixture();
        DatabaseService db = await CreateServiceAsync(fixture.DirectoryPath);

        for (int i = 0; i < 200; i++)
        {
            await db.SaveWatchStateAsync(new WatchStateDto { Location = $@"D:\ep{i}.mkv", Completed = true });
        }

        await Assert.That((await db.ListWatchStateAsync()).Count).IsEqualTo(200);
    }

    [Test]
    public async Task WatchState_SurvivesAdditiveSchemaMigration()
    {
        using var fixture = new TestDirectoryFixture();
        DatabaseService db = await CreateServiceAsync(fixture.DirectoryPath);
        await db.SaveWatchStateAsync(new WatchStateDto { Location = @"D:\keep.mkv", Completed = true });

        // Simulate a future column being added, the way Phase 1's folder_metadata test does.
        await using (var connection = new Microsoft.Data.Sqlite.SqliteConnection(
            new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder
            {
                DataSource = System.IO.Path.Combine(fixture.DirectoryPath, "screenbox.db")
            }.ToString()))
        {
            await connection.OpenAsync();
            using var cmd = connection.CreateCommand();
            cmd.CommandText = "ALTER TABLE watch_state ADD COLUMN future_column TEXT;";
            cmd.ExecuteNonQuery();
        }

        DatabaseService db2 = await CreateServiceAsync(fixture.DirectoryPath);
        WatchStateDto? loaded = await db2.LoadWatchStateAsync(@"D:\keep.mkv");

        await Assert.That(loaded).IsNotNull();
        await Assert.That(loaded!.Completed).IsTrue();
    }

    [Test]
    public async Task DeleteWatchStateAsync_RemovesTheRow()
    {
        using var fixture = new TestDirectoryFixture();
        DatabaseService db = await CreateServiceAsync(fixture.DirectoryPath);
        const string loc = @"D:\gone.mkv";
        await db.SaveWatchStateAsync(new WatchStateDto { Location = loc, Completed = true });

        await db.DeleteWatchStateAsync(loc);

        await Assert.That(await db.LoadWatchStateAsync(loc)).IsNull();
    }
}
```

- [ ] **Step 2: Run the test to verify it fails**

```bash
dotnet run --project Screenbox.Core.Tests/Screenbox.Core.Tests.csproj
```

Expected: compile failure — `WatchStateDto` and the four database methods do not exist.

- [ ] **Step 3: Create the DTO**

Create `Screenbox.Core/Models/WatchStateDto.cs`:

```csharp
using System;

namespace Screenbox.Core.Models;

/// <summary>
/// Durable record of whether a media item has been watched to completion.
/// Unlike <see cref="MediaPlaybackProgress"/>, which is a bounded resume cache,
/// this is kept for every item the user has played.
/// </summary>
public sealed class WatchStateDto
{
    /// <summary>Media location. Primary key.</summary>
    public string Location { get; set; } = string.Empty;

    /// <summary>Whether playback passed the completion threshold.</summary>
    public bool Completed { get; set; }

    /// <summary>When the item was last played, or null when never recorded.</summary>
    public DateTimeOffset? LastPlayed { get; set; }

    /// <summary>Total duration, used to render progress without reopening the file.</summary>
    public TimeSpan? Duration { get; set; }

    /// <summary>
    /// Last recorded playback position. Stored here rather than read from
    /// <see cref="MediaPlaybackProgress"/> because that list holds only the 64 most recent
    /// items, so progress bars would vanish from older episodes.
    /// </summary>
    public TimeSpan LastPosition { get; set; }
}
```

- [ ] **Step 4: Add the table SQL and migration**

In `Screenbox.Core/Services/DatabaseService.Schema.cs`, add the constant alongside the others:

```csharp
    private const string CreateWatchStateSql = """
        CREATE TABLE IF NOT EXISTS watch_state (
            location            TEXT PRIMARY KEY,
            completed           INTEGER NOT NULL DEFAULT 0,
            last_played         INTEGER,
            duration_ticks      INTEGER,
            last_position_ticks INTEGER NOT NULL DEFAULT 0
        );
        """;
```

Add `EnsureWatchStateTable(connection);` in `EnsureSchemaAsync`, immediately after `EnsureFolderMetadataTable(connection);`, and `ExecuteNonQuery(connection, CreateWatchStateSql);` in `RecreateDatabaseFile`.

Write `EnsureWatchStateTable` by copying the structure of `EnsureFolderMetadataTable` exactly — additive `ALTER TABLE ... ADD COLUMN` for missing columns, falling back to the copy-then-rebuild path only when additive migration is impossible. Do not write a bare `DROP TABLE`. The expected column set is `["location", "completed", "last_played", "duration_ticks", "last_position_ticks"]`.

- [ ] **Step 5: Implement the database methods**

Create `Screenbox.Core/Services/DatabaseService.WatchState.cs`:

```csharp
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
            INSERT INTO watch_state (location, completed, last_played, duration_ticks, last_position_ticks)
            VALUES (@loc, @done, @played, @ticks, @pos)
            ON CONFLICT(location) DO UPDATE SET
                completed = excluded.completed,
                last_played = excluded.last_played,
                duration_ticks = excluded.duration_ticks,
                last_position_ticks = excluded.last_position_ticks;
            """;
        cmd.Parameters.AddWithValue("@loc", state.Location);
        cmd.Parameters.AddWithValue("@done", state.Completed ? 1 : 0);
        cmd.Parameters.AddWithValue("@played", (object?)state.LastPlayed?.UtcTicks ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@ticks", (object?)state.Duration?.Ticks ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@pos", state.LastPosition.Ticks);
        cmd.ExecuteNonQuery();
    }

    /// <inheritdoc/>
    public async Task<WatchStateDto?> LoadWatchStateAsync(string location)
    {
        await EnsureInitializedAsync();
        using var connection = CreateConnection();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            SELECT location, completed, last_played, duration_ticks, last_position_ticks
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
        cmd.CommandText = "SELECT location, completed, last_played, duration_ticks, last_position_ticks FROM watch_state;";

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
        LastPosition = reader.IsDBNull(4) ? TimeSpan.Zero : new TimeSpan(reader.GetInt64(4))
    };
}
```

- [ ] **Step 6: Declare the methods on the interface**

Add to `Screenbox.Core/Services/IDatabaseService.cs`:

```csharp
    /// <summary>Saves durable watched state for a media location.</summary>
    Task SaveWatchStateAsync(WatchStateDto state);

    /// <summary>Loads watched state, or null when the location has none.</summary>
    Task<WatchStateDto?> LoadWatchStateAsync(string location);

    /// <summary>Lists all watched state rows.</summary>
    Task<List<WatchStateDto>> ListWatchStateAsync();

    /// <summary>Removes watched state for a location.</summary>
    Task DeleteWatchStateAsync(string location);
```

- [ ] **Step 7: Run the tests and verify they pass**

```bash
dotnet run --project Screenbox.Core.Tests/Screenbox.Core.Tests.csproj
```

Expected: PASS, 62 total (56 existing + 6 new).

- [ ] **Step 8: Commit**

```bash
git add Screenbox.Core/Models/WatchStateDto.cs Screenbox.Core/Services/DatabaseService.WatchState.cs Screenbox.Core/Services/DatabaseService.Schema.cs Screenbox.Core/Services/IDatabaseService.cs Screenbox.Core.Tests/Database/WatchStateTests.cs
git commit -m "feat(playback): add durable watch_state table"
```

---

### Task 2: `WatchThreshold` — pure completion decision

**Files:**
- Create: `Screenbox.Core/Helpers/WatchThreshold.cs`
- Test: `Screenbox.Core.Tests/Helpers/WatchThresholdTests.cs`

**Interfaces:**
- Consumes: nothing. This task has no dependencies by design.
- Produces: `WatchThreshold.IsComplete(TimeSpan position, TimeSpan duration, double thresholdPercent)` returning `bool`; `WatchThreshold.DefaultPercent` (double, `0.9`); `WatchThreshold.Progress(TimeSpan position, TimeSpan duration)` returning `double` in `[0, 1]`.

Keeping this pure means the completion rule is fully testable without a player, a file, or a UWP host.

- [ ] **Step 1: Write the failing test**

Create `Screenbox.Core.Tests/Helpers/WatchThresholdTests.cs`:

```csharp
using Screenbox.Core.Helpers;

namespace Screenbox.Core.Tests.Helpers;

public sealed class WatchThresholdTests
{
    [Test]
    public async Task DefaultPercent_IsNinetyPercent()
    {
        await Assert.That(WatchThreshold.DefaultPercent).IsEqualTo(0.9);
    }

    [Test]
    public async Task IsComplete_TrueAtThreshold()
    {
        var duration = TimeSpan.FromMinutes(20);
        await Assert.That(WatchThreshold.IsComplete(TimeSpan.FromMinutes(18), duration, 0.9)).IsTrue();
    }

    [Test]
    public async Task IsComplete_FalseJustBelowThreshold()
    {
        var duration = TimeSpan.FromMinutes(20);
        await Assert.That(WatchThreshold.IsComplete(TimeSpan.FromMinutes(17), duration, 0.9)).IsFalse();
    }

    [Test]
    public async Task IsComplete_FalseWhenDurationIsZeroOrNegative()
    {
        // A zero duration means the length is unknown. Treating that as complete would mark
        // everything watched the moment it opened.
        await Assert.That(WatchThreshold.IsComplete(TimeSpan.FromMinutes(5), TimeSpan.Zero, 0.9)).IsFalse();
        await Assert.That(WatchThreshold.IsComplete(TimeSpan.FromMinutes(5), TimeSpan.FromSeconds(-1), 0.9)).IsFalse();
    }

    [Test]
    public async Task IsComplete_TrueWhenPositionExceedsDuration()
    {
        var duration = TimeSpan.FromMinutes(20);
        await Assert.That(WatchThreshold.IsComplete(TimeSpan.FromMinutes(25), duration, 0.9)).IsTrue();
    }

    [Test]
    public async Task Progress_IsClampedToZeroAndOne()
    {
        var duration = TimeSpan.FromMinutes(10);
        await Assert.That(WatchThreshold.Progress(TimeSpan.FromMinutes(5), duration)).IsEqualTo(0.5);
        await Assert.That(WatchThreshold.Progress(TimeSpan.FromMinutes(-5), duration)).IsEqualTo(0d);
        await Assert.That(WatchThreshold.Progress(TimeSpan.FromMinutes(50), duration)).IsEqualTo(1d);
    }

    [Test]
    public async Task Progress_IsZeroWhenDurationUnknown()
    {
        await Assert.That(WatchThreshold.Progress(TimeSpan.FromMinutes(5), TimeSpan.Zero)).IsEqualTo(0d);
    }
}
```

- [ ] **Step 2: Run the test to verify it fails**

```bash
dotnet run --project Screenbox.Core.Tests/Screenbox.Core.Tests.csproj
```

Expected: compile failure — `WatchThreshold` does not exist.

- [ ] **Step 3: Write the implementation**

Create `Screenbox.Core/Helpers/WatchThreshold.cs`:

```csharp
using System;

namespace Screenbox.Core.Helpers;

/// <summary>
/// Decides when playback counts as finished. Pure logic so the rule can be verified
/// without a player, a media file, or a UWP host.
/// </summary>
public static class WatchThreshold
{
    /// <summary>Fraction of the runtime that must be reached to count as watched.</summary>
    public const double DefaultPercent = 0.9;

    /// <summary>
    /// Returns true when <paramref name="position"/> has reached the completion threshold.
    /// A non-positive duration means the length is unknown and never counts as complete —
    /// otherwise every item would be marked watched the moment it was opened.
    /// </summary>
    public static bool IsComplete(TimeSpan position, TimeSpan duration, double thresholdPercent)
    {
        if (duration <= TimeSpan.Zero) return false;
        return position.Ticks >= duration.Ticks * thresholdPercent;
    }

    /// <summary>
    /// Returns playback progress as a fraction in the range 0 to 1, or 0 when the duration
    /// is unknown.
    /// </summary>
    public static double Progress(TimeSpan position, TimeSpan duration)
    {
        if (duration <= TimeSpan.Zero) return 0d;
        double raw = (double)position.Ticks / duration.Ticks;
        return raw < 0d ? 0d : raw > 1d ? 1d : raw;
    }
}
```

- [ ] **Step 4: Run the tests and verify they pass**

```bash
dotnet run --project Screenbox.Core.Tests/Screenbox.Core.Tests.csproj
```

Expected: PASS, 69 total (62 + 7 new).

- [ ] **Step 5: Commit**

```bash
git add Screenbox.Core/Helpers/WatchThreshold.cs Screenbox.Core.Tests/Helpers/WatchThresholdTests.cs
git commit -m "feat(playback): add pure watch completion threshold"
```

---

### Task 3: `WatchStateService`

**Files:**
- Create: `Screenbox.Core/Services/IWatchStateService.cs`
- Create: `Screenbox.Core/Services/WatchStateService.cs`
- Modify: `Screenbox.Core/Common/ServiceHelpers.cs`
- Test: `Screenbox.Core.Tests/Services/WatchStateServiceTests.cs`

**Interfaces:**
- Consumes: `IDatabaseService.SaveWatchStateAsync` / `LoadWatchStateAsync` / `ListWatchStateAsync` from Task 1; `WatchThreshold.IsComplete` / `Progress` / `DefaultPercent` from Task 2.
- Produces: `IWatchStateService` with `Task LoadAsync()`, `Task RecordProgressAsync(string location, TimeSpan position, TimeSpan duration)`, `bool IsWatched(string location)`, `double GetProgress(string location)`, `IReadOnlyList<WatchStateDto> GetContinueWatching(int limit)`, `Task SetWatchedAsync(string location, bool watched)`, and `double ThresholdPercent { get; set; }`.

State is cached in memory after `LoadAsync` so `IsWatched` and `GetProgress` are synchronous — they are called per tile from a virtualized list and must not hit the database on the UI path. Phase 1's I3 finding covers why.

- [ ] **Step 1: Write the failing test**

Create `Screenbox.Core.Tests/Services/WatchStateServiceTests.cs`:

```csharp
using Microsoft.Extensions.Logging.Abstractions;
using Screenbox.Core.Models;
using Screenbox.Core.Services;
using Screenbox.Core.Tests.Helpers;

namespace Screenbox.Core.Tests.Services;

public sealed class WatchStateServiceTests
{
    private static async Task<(WatchStateService service, DatabaseService db)> CreateAsync(string dir)
    {
        var db = new DatabaseService(NullLogger<DatabaseService>.Instance) { DbFolderPath = dir };
        await db.InitializeAsync();
        var service = new WatchStateService(db, NullLogger<WatchStateService>.Instance);
        await service.LoadAsync();
        return (service, db);
    }

    [Test]
    public async Task RecordProgressAsync_MarksWatchedPastThreshold()
    {
        using var fixture = new TestDirectoryFixture();
        (WatchStateService service, _) = await CreateAsync(fixture.DirectoryPath);
        const string loc = @"D:\ep1.mkv";

        await service.RecordProgressAsync(loc, TimeSpan.FromMinutes(19), TimeSpan.FromMinutes(20));

        await Assert.That(service.IsWatched(loc)).IsTrue();
    }

    [Test]
    public async Task RecordProgressAsync_DoesNotMarkWatchedBelowThreshold()
    {
        using var fixture = new TestDirectoryFixture();
        (WatchStateService service, _) = await CreateAsync(fixture.DirectoryPath);
        const string loc = @"D:\ep2.mkv";

        await service.RecordProgressAsync(loc, TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(20));

        await Assert.That(service.IsWatched(loc)).IsFalse();
        await Assert.That(service.GetProgress(loc)).IsEqualTo(0.25);
    }

    [Test]
    public async Task WatchedStateNeverRegressesOnRewatch()
    {
        // Finishing an episode then restarting it must not clear the watched flag, otherwise
        // rewatching a series silently erases the user's history.
        using var fixture = new TestDirectoryFixture();
        (WatchStateService service, _) = await CreateAsync(fixture.DirectoryPath);
        const string loc = @"D:\ep3.mkv";

        await service.RecordProgressAsync(loc, TimeSpan.FromMinutes(19), TimeSpan.FromMinutes(20));
        await service.RecordProgressAsync(loc, TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(20));

        await Assert.That(service.IsWatched(loc)).IsTrue();
    }

    [Test]
    public async Task SetWatchedAsync_CanClearTheFlagExplicitly()
    {
        using var fixture = new TestDirectoryFixture();
        (WatchStateService service, _) = await CreateAsync(fixture.DirectoryPath);
        const string loc = @"D:\ep4.mkv";
        await service.RecordProgressAsync(loc, TimeSpan.FromMinutes(19), TimeSpan.FromMinutes(20));

        await service.SetWatchedAsync(loc, watched: false);

        await Assert.That(service.IsWatched(loc)).IsFalse();
    }

    [Test]
    public async Task State_SurvivesReload()
    {
        using var fixture = new TestDirectoryFixture();
        (WatchStateService service, DatabaseService db) = await CreateAsync(fixture.DirectoryPath);
        const string loc = @"D:\ep5.mkv";
        await service.RecordProgressAsync(loc, TimeSpan.FromMinutes(19), TimeSpan.FromMinutes(20));

        var reloaded = new WatchStateService(db, NullLogger<WatchStateService>.Instance);
        await reloaded.LoadAsync();

        await Assert.That(reloaded.IsWatched(loc)).IsTrue();
    }

    [Test]
    public async Task GetContinueWatching_ReturnsPartiallyWatchedNewestFirstAndExcludesFinished()
    {
        using var fixture = new TestDirectoryFixture();
        (WatchStateService service, _) = await CreateAsync(fixture.DirectoryPath);
        var duration = TimeSpan.FromMinutes(20);

        await service.RecordProgressAsync(@"D:\older.mkv", TimeSpan.FromMinutes(5), duration);
        await service.RecordProgressAsync(@"D:\finished.mkv", TimeSpan.FromMinutes(19), duration);
        await service.RecordProgressAsync(@"D:\newer.mkv", TimeSpan.FromMinutes(8), duration);

        IReadOnlyList<WatchStateDto> result = service.GetContinueWatching(10);

        await Assert.That(result.Count).IsEqualTo(2);
        await Assert.That(result[0].Location).IsEqualTo(@"D:\newer.mkv");
        await Assert.That(result[1].Location).IsEqualTo(@"D:\older.mkv");
    }

    [Test]
    public async Task GetContinueWatching_RespectsTheLimit()
    {
        using var fixture = new TestDirectoryFixture();
        (WatchStateService service, _) = await CreateAsync(fixture.DirectoryPath);
        var duration = TimeSpan.FromMinutes(20);

        for (int i = 0; i < 10; i++)
        {
            await service.RecordProgressAsync($@"D:\ep{i}.mkv", TimeSpan.FromMinutes(5), duration);
        }

        await Assert.That(service.GetContinueWatching(3).Count).IsEqualTo(3);
    }

    [Test]
    public async Task IsWatched_ReturnsFalseForUnknownLocation()
    {
        using var fixture = new TestDirectoryFixture();
        (WatchStateService service, _) = await CreateAsync(fixture.DirectoryPath);

        await Assert.That(service.IsWatched(@"D:\never-seen.mkv")).IsFalse();
        await Assert.That(service.GetProgress(@"D:\never-seen.mkv")).IsEqualTo(0d);
    }
}
```

- [ ] **Step 2: Run the test to verify it fails**

```bash
dotnet run --project Screenbox.Core.Tests/Screenbox.Core.Tests.csproj
```

Expected: compile failure — `WatchStateService` does not exist.

- [ ] **Step 3: Create the interface**

Create `Screenbox.Core/Services/IWatchStateService.cs`:

```csharp
using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Screenbox.Core.Models;

namespace Screenbox.Core.Services;

/// <summary>
/// Tracks which media has been watched, and how far through the rest is.
/// </summary>
public interface IWatchStateService
{
    /// <summary>Fraction of runtime that counts as watched. Defaults to 0.9.</summary>
    double ThresholdPercent { get; set; }

    /// <summary>Loads persisted state into memory. Call once at startup.</summary>
    Task LoadAsync();

    /// <summary>Records a playback position and marks the item watched once past the threshold.</summary>
    Task RecordProgressAsync(string location, TimeSpan position, TimeSpan duration);

    /// <summary>Returns whether the item has been watched. Synchronous: called per tile.</summary>
    bool IsWatched(string location);

    /// <summary>Returns progress from 0 to 1. Synchronous: called per tile.</summary>
    double GetProgress(string location);

    /// <summary>Returns partially watched items, most recently played first.</summary>
    IReadOnlyList<WatchStateDto> GetContinueWatching(int limit);

    /// <summary>Explicitly sets or clears the watched flag.</summary>
    Task SetWatchedAsync(string location, bool watched);
}
```

- [ ] **Step 4: Implement the service**

Create `Screenbox.Core/Services/WatchStateService.cs`:

```csharp
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Screenbox.Core.Helpers;
using Screenbox.Core.Models;

namespace Screenbox.Core.Services;

/// <inheritdoc cref="IWatchStateService"/>
public sealed class WatchStateService : IWatchStateService
{
    /// <inheritdoc/>
    public double ThresholdPercent { get; set; } = WatchThreshold.DefaultPercent;

    private readonly IDatabaseService _databaseService;
    private readonly ILogger<WatchStateService> _logger;
    private readonly ConcurrentDictionary<string, WatchStateDto> _cache = new(StringComparer.OrdinalIgnoreCase);

    public WatchStateService(IDatabaseService databaseService, ILogger<WatchStateService> logger)
    {
        _databaseService = databaseService;
        _logger = logger;
    }

    /// <inheritdoc/>
    public async Task LoadAsync()
    {
        try
        {
            foreach (WatchStateDto row in await _databaseService.ListWatchStateAsync())
            {
                _cache[row.Location] = row;
            }
        }
        catch (Exception e)
        {
            // Watched state is an enhancement. Failing to load it must not stop the app starting.
            _logger.LogError(e, "Failed to load watch state.");
        }
    }

    /// <inheritdoc/>
    public async Task RecordProgressAsync(string location, TimeSpan position, TimeSpan duration)
    {
        if (string.IsNullOrEmpty(location)) return;

        WatchStateDto state = _cache.TryGetValue(location, out WatchStateDto? existing)
            ? existing
            : new WatchStateDto { Location = location };

        // Watched never regresses. Restarting a finished episode is a rewatch, not an unwatch.
        state.Completed |= WatchThreshold.IsComplete(position, duration, ThresholdPercent);
        state.LastPlayed = DateTimeOffset.UtcNow;
        state.Duration = duration > TimeSpan.Zero ? duration : state.Duration;
        state.LastPosition = position;
        _cache[location] = state;

        await PersistAsync(state);
    }

    /// <inheritdoc/>
    public bool IsWatched(string location) =>
        _cache.TryGetValue(location, out WatchStateDto? state) && state.Completed;

    /// <inheritdoc/>
    public double GetProgress(string location)
    {
        if (!_cache.TryGetValue(location, out WatchStateDto? state)) return 0d;
        if (state.Completed) return 1d;
        return state.Duration is { } duration
            ? WatchThreshold.Progress(state.LastPosition, duration)
            : 0d;
    }

    /// <inheritdoc/>
    public IReadOnlyList<WatchStateDto> GetContinueWatching(int limit) =>
        _cache.Values
            .Where(state => !state.Completed && state.LastPlayed is not null)
            .OrderByDescending(state => state.LastPlayed!.Value)
            .Take(limit)
            .ToList();

    /// <inheritdoc/>
    public async Task SetWatchedAsync(string location, bool watched)
    {
        WatchStateDto state = _cache.TryGetValue(location, out WatchStateDto? existing)
            ? existing
            : new WatchStateDto { Location = location };

        state.Completed = watched;
        state.LastPlayed ??= DateTimeOffset.UtcNow;
        _cache[location] = state;

        await PersistAsync(state);
    }

    private async Task PersistAsync(WatchStateDto state)
    {
        try
        {
            await _databaseService.SaveWatchStateAsync(state);
        }
        catch (Exception e)
        {
            _logger.LogError(e, "Failed to persist watch state for '{Location}'.", state.Location);
        }
    }
}
```

- [ ] **Step 5: Register the service**

In `Screenbox.Core/Common/ServiceHelpers.cs`, next to the other service registrations:

```csharp
        services.AddSingleton<IWatchStateService, WatchStateService>();
```

- [ ] **Step 6: Run the tests and verify they pass**

```bash
dotnet run --project Screenbox.Core.Tests/Screenbox.Core.Tests.csproj
```

Expected: PASS, 77 total (69 + 8 new).

- [ ] **Step 7: Build Screenbox.Core**

```bash
"C:\Program Files\Microsoft Visual Studio\18\Community\MSBuild\Current\Bin\MSBuild.exe" Screenbox.Core/Screenbox.Core.csproj -t:Build -p:Configuration=Debug -p:Platform=x64 -m
```

Expected: exit code 0.

- [ ] **Step 8: Commit**

```bash
git add Screenbox.Core/Services/IWatchStateService.cs Screenbox.Core/Services/WatchStateService.cs Screenbox.Core/Models/WatchStateDto.cs Screenbox.Core/Services/DatabaseService.WatchState.cs Screenbox.Core/Services/DatabaseService.Schema.cs Screenbox.Core/Common/ServiceHelpers.cs Screenbox.Core.Tests/
git commit -m "feat(playback): add watch state service"
```

---

### Task 4: Record completion during playback

**Files:**
- Modify: `Screenbox.Core/ViewModels/SeekBarViewModel.cs:459-470`
- Modify: `Screenbox.Core/Services/ISettingsService.cs` and its implementation

**Interfaces:**
- Consumes: `IWatchStateService.RecordProgressAsync` and `ThresholdPercent` from Task 3.
- Produces: no new public API. Adds a `WatchedThresholdPercent` setting.

`SeekBarViewModel.UpdateProgress` already runs on every position tick and calls `_playbackProgressTracker.UpdateProgress`. Record watch state from the same place, and do **not** replace or alter the existing tracker call — resume positions and watched state are separate concerns.

- [ ] **Step 1: Add the setting**

Add a `double WatchedThresholdPercent { get; set; }` property to `ISettingsService` and its implementation, defaulting to `WatchThreshold.DefaultPercent`, following the pattern of the existing settings properties in that file.

- [ ] **Step 2: Record progress alongside the existing tracker call**

In `SeekBarViewModel`, inject `IWatchStateService`, and in `UpdateProgress` add the watch-state call next to the existing tracker update:

```csharp
            _playbackProgressTracker.UpdateProgress(_currentItem.Location, position);
            _ = RecordWatchStateAsync(_currentItem.Location, position);
```

Add the helper, which must never let an exception escape — this runs from a position-changed handler:

```csharp
    private async Task RecordWatchStateAsync(string location, TimeSpan position)
    {
        try
        {
            await _watchStateService.RecordProgressAsync(location, position, NaturalDuration);
        }
        catch (Exception e)
        {
            _logger.LogError(e, "Failed to record watch state for '{Location}'.", location);
        }
    }
```

If `SeekBarViewModel` has no logger, use the duration property that actually exists on it for the total length — inspect the class and use the correct member rather than assuming `NaturalDuration`. Report which you used.

- [ ] **Step 3: Load watch state at startup**

Find where `IPlaybackProgressTracker.LoadFromDiskAsync` is called during app initialization and call `IWatchStateService.LoadAsync()` in the same place, so the cache is warm before any tile renders.

- [ ] **Step 4: Build and test**

```bash
dotnet run --project Screenbox.Core.Tests/Screenbox.Core.Tests.csproj
```

Expected: PASS, 77 total.

```bash
"C:\Program Files\Microsoft Visual Studio\18\Community\MSBuild\Current\Bin\MSBuild.exe" Screenbox.Core/Screenbox.Core.csproj -t:Build -p:Configuration=Debug -p:Platform=x64 -m
```

Expected: exit code 0.

- [ ] **Step 5: Commit**

```bash
git add Screenbox.Core/ViewModels/SeekBarViewModel.cs Screenbox.Core/Services/ISettingsService.cs Screenbox.Core/Services/SettingsService.cs
git commit -m "feat(playback): record watch state during playback"
```

---

### Task 5: Watched indicator and progress bar on tiles

**Files:**
- Modify: `Screenbox.Core/ViewModels/MediaViewModel.cs`
- Modify: `Screenbox/Behaviors/ThumbnailGridViewBehavior.cs`
- Modify: `Screenbox/Controls/CommonGridViewItem.xaml`

**Interfaces:**
- Consumes: `IWatchStateService.IsWatched` / `GetProgress` from Task 3.
- Produces: `MediaViewModel.IsWatched` (observable bool) and `MediaViewModel.WatchProgress` (observable double, 0 to 1).

- [ ] **Step 1: Surface the state on MediaViewModel**

Add observable properties and a refresh method that reads the synchronous cache:

```csharp
    [ObservableProperty] public partial bool IsWatched { get; set; }
    [ObservableProperty] public partial double WatchProgress { get; set; }

    /// <summary>
    /// Refreshes watched state from the in-memory cache. Cheap and synchronous by design:
    /// this runs per tile as items scroll into view.
    /// </summary>
    public void RefreshWatchState(IWatchStateService watchStateService)
    {
        IsWatched = watchStateService.IsWatched(Location);
        WatchProgress = watchStateService.GetProgress(Location);
    }
```

- [ ] **Step 2: Call it from the lazy per-tile loader**

In `ThumbnailGridViewBehavior.OnContainerContentChanging`, inside the existing `case MediaViewModel media:` branch, add the refresh. Resolve `IWatchStateService` via `Ioc.Default.GetRequiredService<IWatchStateService>()`, matching how the surrounding code obtains services. Do the same in the `case StorageItemViewModel storageItem:` branch for `storageItem.Media` when it is not null.

Do not add an eager loop in a ViewModel. Phase 1 established that per-tile work belongs here.

- [ ] **Step 3: Render the indicator**

In `CommonGridViewItem.xaml`, add a progress bar pinned to the bottom edge of the thumbnail, visible only when `WatchProgress` is above zero and the item is not watched, and a check glyph in the corner when `IsWatched` is true.

Use the app's existing Fluent tokens and `{ThemeResource}` brushes. Do not introduce new colors, fonts, or a bespoke card style — the visual weight should come from the artwork, with the indicator reading as a quiet overlay rather than a second focal point.

Add the two new dependency properties to `CommonGridViewItem.xaml.cs` following the pattern of the existing ones, and bind them from the item templates that already bind `ThumbnailSource`.

- [ ] **Step 4: Build the full app**

Close the running app first, then ensure vswhere is on PATH:

```bash
"C:\Program Files\Microsoft Visual Studio\18\Community\MSBuild\Current\Bin\MSBuild.exe" Screenbox/Screenbox.csproj -t:Build -p:Configuration=Debug -p:Platform=x64 -m
```

Expected: exit code 0.

- [ ] **Step 5: Manual verification**

Play an episode partway, return to the folder, and confirm a progress bar appears. Play one past 90% and confirm it shows as watched instead.

- [ ] **Step 6: Commit**

```bash
git add Screenbox.Core/ViewModels/MediaViewModel.cs Screenbox/Behaviors/ThumbnailGridViewBehavior.cs Screenbox/Controls/CommonGridViewItem.xaml Screenbox/Controls/CommonGridViewItem.xaml.cs
git commit -m "feat(library): show watched indicators and progress on tiles"
```

---

### Task 6: Continue Watching on the home page

**Files:**
- Create: `Screenbox.Core/Contexts/WatchStateContext.cs`
- Modify: `Screenbox.Core/ViewModels/HomePageViewModel.cs`
- Modify: `Screenbox/Pages/HomePage.xaml`
- Modify: `Screenbox/Strings/en-US/Resources.resw`

**Interfaces:**
- Consumes: `IWatchStateService.GetContinueWatching` from Task 3; `MediaViewModelFactory` (existing).
- Produces: `WatchStateContext.ContinueWatching` (`ObservableCollection<MediaViewModel>`) and `HomePageViewModel.ContinueWatching` exposing it, mirroring how `RecentContext.Recent` is exposed today.

- [ ] **Step 1: Add the string**

In `Screenbox/Strings/en-US/Resources.resw` only — never other language folders, translation goes through Crowdin:

```xml
  <data name="ContinueWatching" xml:space="preserve">
    <value>Continue watching</value>
  </data>
```

- [ ] **Step 2: Create the context**

Create `Screenbox.Core/Contexts/WatchStateContext.cs` following the structure of the existing `RecentContext`: an observable collection populated from `IWatchStateService.GetContinueWatching`, with a refresh method that maps each `WatchStateDto.Location` to a `MediaViewModel` via `MediaViewModelFactory`. Skip locations that no longer resolve to a file rather than throwing.

Register it in `ServiceHelpers.cs` as a singleton alongside the other contexts.

- [ ] **Step 3: Expose it on the home page ViewModel**

In `HomePageViewModel`, inject `WatchStateContext` and expose:

```csharp
    public ObservableCollection<MediaViewModel> ContinueWatching => _watchStateContext.ContinueWatching;
```

Refresh it wherever `Recent` is currently refreshed.

- [ ] **Step 4: Add the row to the page**

In `HomePage.xaml`, add a Continue Watching section **above** the existing Recent section — the spec's chosen default, since the two answer different questions and keeping Recent makes the change additive. Copy the Recent section's markup structure so the two rows are visually consistent, and collapse the section when the collection is empty.

- [ ] **Step 5: Build the full app**

```bash
"C:\Program Files\Microsoft Visual Studio\18\Community\MSBuild\Current\Bin\MSBuild.exe" Screenbox/Screenbox.csproj -t:Build -p:Configuration=Debug -p:Platform=x64 -m
```

Expected: exit code 0.

- [ ] **Step 6: Manual verification**

Play an episode partway, go Home, and confirm it appears under Continue Watching. Finish it and confirm it drops off the row.

- [ ] **Step 7: Commit**

```bash
git add Screenbox.Core/Contexts/WatchStateContext.cs Screenbox.Core/ViewModels/HomePageViewModel.cs Screenbox.Core/Common/ServiceHelpers.cs Screenbox/Pages/HomePage.xaml Screenbox/Strings/en-US/Resources.resw
git commit -m "feat(home): add continue watching row"
```

---

## Verification

```bash
dotnet run --project Screenbox.Core.Tests/Screenbox.Core.Tests.csproj
```

Expected: 77 passing, 0 failing.

```bash
"C:\Program Files\Microsoft Visual Studio\18\Community\MSBuild\Current\Bin\MSBuild.exe" Screenbox/Screenbox.csproj -t:Build -p:Configuration=Debug -p:Platform=x64 -m
```

Expected: exit code 0.

## Out of scope

- Episode parsing and next-episode playback. That is Phase 3.
- Unwatched counts on folder tiles. That is Phase 4, and it needs Phase 3's episode classification to avoid counting `NCED` and `Extras` as episodes.
- Any change to `playback_progress` or `PlaybackProgressTracker`. Resume positions keep working exactly as they do now.
