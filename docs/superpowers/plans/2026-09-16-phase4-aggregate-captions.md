# Phase 4: Aggregate Folder Captions — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Replace the bare `N items` folder caption with something useful — episode count, total runtime, and how many are still unwatched.

**Architecture:** A `FolderStatsService` answers one question per folder by querying the existing `media_records` cache in SQL, joined against `watch_state`. No filesystem walking. Results are cached in memory because captions are requested once per tile from a virtualized grid.

**Tech Stack:** C# 14, .NET 10 (`net10.0-windows10.0.26100.0`), UWP with `UseUwp=true`, Microsoft.Data.Sqlite, TUnit.

**Spec:** `docs/superpowers/specs/2026-09-14-personal-media-centre-design.md` (Phase 4)

**Depends on:** Phase 2 (watch_state) and Phase 3 (EpisodeInfoParser), both complete.

## Key design decision — read this before starting

Do **not** compute these numbers by enumerating the filesystem. The library scan already populates `media_records` with a `path` and `duration_ticks` for every file it knows about, and Phase 2 added `watch_state`. Everything this phase needs is already in SQLite:

- episode count — rows under the folder path
- total runtime — `SUM(duration_ticks)` over those rows
- unwatched count — those rows with no completed `watch_state` entry

That is one query per folder rather than a recursive directory walk per tile. Phase 1 established that per-tile work runs on a hot path as the grid virtualizes; a filesystem walk there would stall scrolling on a 37-episode folder.

## Global Constraints

- Build with MSBuild from Visual Studio 2026 only. **Never `dotnet build`.** Path: `C:\Program Files\Microsoft Visual Studio\18\Community\MSBuild\Current\Bin\MSBuild.exe`, argument form `-t:Build -p:Configuration=Debug -p:Platform=x64`.
- Close the running app before building — it locks `Screenbox.Core.dll` and the build fails with MSB3027.
- Before building the app project, put vswhere on PATH: prepend `${env:ProgramFiles(x86)}\Microsoft Visual Studio\Installer`.
- Run tests with `dotnet run --project Screenbox.Core.Tests/Screenbox.Core.Tests.csproj`. **Run the suite five times** — this suite had a thread-scheduling flake and one green run is not sufficient evidence. 115 currently pass.
- Per-tile work belongs in `Screenbox/Behaviors/ThumbnailGridViewBehavior.cs`, never an eager ViewModel loop.
- Never put `x:Bind` inside a XAML resource nothing in the markup references — it crashed a page earlier in this project.
- Localized strings go ONLY in `Screenbox/Strings/en-US/Resources.resw`. Translation is handled through Crowdin.
- ViewModels must never reference localized strings. Formatting belongs in `Screenbox/Helpers/ItemLabelHelper.cs`, which is View layer.
- An unhandled exception reaching an `async void` handler terminates this app with no crash log.
- Nullable reference types enabled: `is null` / `is not null`.
- This is UWP: `Windows.UI.Xaml`, never `Microsoft.UI.Xaml`.
- Conventional Commits. Branch: `feat/personal-media-centre`.
- Do not write scratch files into the repository root; keep temporary output inside `.superpowers/`.

## File Structure

| File | Responsibility |
|---|---|
| `Screenbox.Core/Models/FolderStats.cs` | Result record |
| `Screenbox.Core/Services/DatabaseService.FolderStats.cs` | The SQL |
| `Screenbox.Core/Services/FolderStatsService.cs` | Caching wrapper |
| `Screenbox.Core/ViewModels/StorageItemViewModel.cs` | Surfaces stats |
| `Screenbox/Helpers/ItemLabelHelper.cs` | Formats the caption |

---

### Task 1: Query folder statistics from SQLite

**Files:**
- Create: `Screenbox.Core/Models/FolderStats.cs`
- Create: `Screenbox.Core/Services/DatabaseService.FolderStats.cs`
- Modify: `Screenbox.Core/Services/IDatabaseService.cs`
- Test: `Screenbox.Core.Tests/Database/FolderStatsTests.cs`

**Interfaces:**
- Consumes: the existing `media_records` table (columns include `path`, `duration_ticks`) and `watch_state` from Phase 2 (columns `location`, `completed`). `DatabaseService` has settable `DbFolderPath` and `InitializeAsync()`; `TestDirectoryFixture` provides isolated temp directories.
- Produces: `readonly record struct FolderStats(int EpisodeCount, TimeSpan TotalDuration, int UnwatchedCount)` and `IDatabaseService.GetFolderStatsAsync(string folderPath)` returning `Task<FolderStats>`.

Two things make this query subtle, and both have tests below:

- `watch_state.location` is stored **upper-invariant** (Phase 2 normalizes it so the cache key and the SQL primary key cannot diverge), while `media_records.path` keeps its original casing. The join must be case-insensitive or every episode reads as unwatched.
- A path prefix match must not match sibling folders. `D:\Anime\Dan Da Dan` must not pull in rows from `D:\Anime\Dandadan 2nd Season`. Match on the path plus a trailing separator.

- [ ] **Step 1: Write the failing test**

Create `Screenbox.Core.Tests/Database/FolderStatsTests.cs`:

```csharp
using Microsoft.Extensions.Logging.Abstractions;
using Screenbox.Core.Enums;
using Screenbox.Core.Models;
using Screenbox.Core.Services;
using Screenbox.Core.Tests.Helpers;

namespace Screenbox.Core.Tests.Database;

public sealed class FolderStatsTests
{
    private static async Task<DatabaseService> CreateServiceAsync(string dir)
    {
        var db = new DatabaseService(NullLogger<DatabaseService>.Instance) { DbFolderPath = dir };
        await db.InitializeAsync();
        return db;
    }

    private static VideoCacheRecordDto Video(string path, TimeSpan duration) => new()
    {
        Path = path,
        Title = System.IO.Path.GetFileNameWithoutExtension(path),
        Duration = duration
    };

    [Test]
    public async Task GetFolderStatsAsync_CountsEpisodesAndSumsRuntime()
    {
        using var fixture = new TestDirectoryFixture();
        DatabaseService db = await CreateServiceAsync(fixture.DirectoryPath);

        await db.SaveVideoCacheAsync([@"D:\Anime"],
        [
            Video(@"D:\Anime\Death Note\ep1.mkv", TimeSpan.FromMinutes(23)),
            Video(@"D:\Anime\Death Note\ep2.mkv", TimeSpan.FromMinutes(23))
        ]);

        FolderStats stats = await db.GetFolderStatsAsync(@"D:\Anime\Death Note");

        await Assert.That(stats.EpisodeCount).IsEqualTo(2);
        await Assert.That(stats.TotalDuration).IsEqualTo(TimeSpan.FromMinutes(46));
        await Assert.That(stats.UnwatchedCount).IsEqualTo(2);
    }

    [Test]
    public async Task GetFolderStatsAsync_SubtractsWatchedEpisodes()
    {
        using var fixture = new TestDirectoryFixture();
        DatabaseService db = await CreateServiceAsync(fixture.DirectoryPath);

        await db.SaveVideoCacheAsync([@"D:\Anime"],
        [
            Video(@"D:\Anime\Death Note\ep1.mkv", TimeSpan.FromMinutes(23)),
            Video(@"D:\Anime\Death Note\ep2.mkv", TimeSpan.FromMinutes(23))
        ]);
        await db.SaveWatchStateAsync(new WatchStateDto
        {
            Location = @"D:\ANIME\DEATH NOTE\EP1.MKV",
            Completed = true
        });

        FolderStats stats = await db.GetFolderStatsAsync(@"D:\Anime\Death Note");

        await Assert.That(stats.EpisodeCount).IsEqualTo(2);
        await Assert.That(stats.UnwatchedCount).IsEqualTo(1);
    }

    [Test]
    public async Task GetFolderStatsAsync_JoinIsCaseInsensitive()
    {
        // watch_state.location is stored upper-invariant; media_records.path keeps its original
        // casing. A case-sensitive join would report every episode as unwatched.
        using var fixture = new TestDirectoryFixture();
        DatabaseService db = await CreateServiceAsync(fixture.DirectoryPath);

        await db.SaveVideoCacheAsync([@"D:\Anime"],
            [Video(@"D:\Anime\Gachiakuta\Episode 01.mkv", TimeSpan.FromMinutes(24))]);
        await db.SaveWatchStateAsync(new WatchStateDto
        {
            Location = @"D:\ANIME\GACHIAKUTA\EPISODE 01.MKV",
            Completed = true
        });

        FolderStats stats = await db.GetFolderStatsAsync(@"D:\Anime\Gachiakuta");

        await Assert.That(stats.UnwatchedCount).IsEqualTo(0);
    }

    [Test]
    public async Task GetFolderStatsAsync_DoesNotMatchSiblingFolderWithSharedPrefix()
    {
        // "Dan Da Dan" must not absorb rows from "Dandadan 2nd Season".
        using var fixture = new TestDirectoryFixture();
        DatabaseService db = await CreateServiceAsync(fixture.DirectoryPath);

        await db.SaveVideoCacheAsync([@"D:\Anime"],
        [
            Video(@"D:\Anime\Dan Da Dan\ep1.mkv", TimeSpan.FromMinutes(24)),
            Video(@"D:\Anime\Dandadan 2nd Season\ep1.mkv", TimeSpan.FromMinutes(24))
        ]);

        FolderStats stats = await db.GetFolderStatsAsync(@"D:\Anime\Dan Da Dan");

        await Assert.That(stats.EpisodeCount).IsEqualTo(1);
    }

    [Test]
    public async Task GetFolderStatsAsync_IncludesNestedSeasonFolders()
    {
        // Series folders hold only subfolders; the count must reach into them.
        using var fixture = new TestDirectoryFixture();
        DatabaseService db = await CreateServiceAsync(fixture.DirectoryPath);

        await db.SaveVideoCacheAsync([@"D:\Anime"],
        [
            Video(@"D:\Anime\Attack On Titan\Season 1\ep1.mkv", TimeSpan.FromMinutes(24)),
            Video(@"D:\Anime\Attack On Titan\Season 2\ep1.mkv", TimeSpan.FromMinutes(24))
        ]);

        FolderStats stats = await db.GetFolderStatsAsync(@"D:\Anime\Attack On Titan");

        await Assert.That(stats.EpisodeCount).IsEqualTo(2);
    }

    [Test]
    public async Task GetFolderStatsAsync_ReturnsEmptyStatsForUnknownFolder()
    {
        using var fixture = new TestDirectoryFixture();
        DatabaseService db = await CreateServiceAsync(fixture.DirectoryPath);

        FolderStats stats = await db.GetFolderStatsAsync(@"D:\does\not\exist");

        await Assert.That(stats.EpisodeCount).IsEqualTo(0);
        await Assert.That(stats.TotalDuration).IsEqualTo(TimeSpan.Zero);
        await Assert.That(stats.UnwatchedCount).IsEqualTo(0);
    }

    [Test]
    public async Task GetFolderStatsAsync_TreatsNullDurationAsZeroRatherThanThrowing()
    {
        using var fixture = new TestDirectoryFixture();
        DatabaseService db = await CreateServiceAsync(fixture.DirectoryPath);

        await db.SaveVideoCacheAsync([@"D:\Anime"],
            [Video(@"D:\Anime\Suzume\movie.mkv", TimeSpan.Zero)]);

        FolderStats stats = await db.GetFolderStatsAsync(@"D:\Anime\Suzume");

        await Assert.That(stats.EpisodeCount).IsEqualTo(1);
        await Assert.That(stats.TotalDuration).IsEqualTo(TimeSpan.Zero);
    }
}
```

**Verified against the real code:** `VideoCacheRecordDto` has `Path` (string), `Title` (string) and `Duration` (**`TimeSpan`, not ticks**) — the test helper above uses those names. The underlying SQL column is `duration_ticks`, so the DTO and the column deliberately differ; do not "fix" one to match the other. Still confirm the real signature of `SaveVideoCacheAsync` in `DatabaseService.LibraryCache.cs` before writing the test, and report any mismatch rather than inventing an API.

- [ ] **Step 2: Run the test to verify it fails**

```bash
dotnet run --project Screenbox.Core.Tests/Screenbox.Core.Tests.csproj
```

Expected: compile failure — `FolderStats` and `GetFolderStatsAsync` do not exist.

- [ ] **Step 3: Create the model**

Create `Screenbox.Core/Models/FolderStats.cs`:

```csharp
using System;

namespace Screenbox.Core.Models;

/// <summary>
/// Aggregate information about the media inside a library folder, derived from the
/// cached library records rather than the file system.
/// </summary>
/// <param name="EpisodeCount">Number of known media files at or below the folder.</param>
/// <param name="TotalDuration">Combined runtime of those files.</param>
/// <param name="UnwatchedCount">How many have not been watched to completion.</param>
public readonly record struct FolderStats(int EpisodeCount, TimeSpan TotalDuration, int UnwatchedCount);
```

- [ ] **Step 4: Implement the query**

Create `Screenbox.Core/Services/DatabaseService.FolderStats.cs`:

```csharp
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
```

Declare it on `IDatabaseService`:

```csharp
    /// <summary>Returns aggregate media statistics for a library folder.</summary>
    Task<FolderStats> GetFolderStatsAsync(string folderPath);
```

Note the escaping interacts with the path separator on Windows: `\` is both the separator and the chosen LIKE escape character. Verify the escaping is correct against a path containing a literal underscore — folder names like `hayase-cache` are fine, but `Season_1` would break a naive implementation. If the escaping proves awkward, using `substr`/`instr` instead of `LIKE` is acceptable; state what you chose and why.

- [ ] **Step 5: Run the tests until they pass**

```bash
dotnet run --project Screenbox.Core.Tests/Screenbox.Core.Tests.csproj
```

Expected: PASS, 122 total (115 + 7 new). Run five times.

- [ ] **Step 6: Commit**

```bash
git add Screenbox.Core/Models/FolderStats.cs Screenbox.Core/Services/DatabaseService.FolderStats.cs Screenbox.Core/Services/IDatabaseService.cs Screenbox.Core.Tests/Database/FolderStatsTests.cs
git commit -m "feat(library): query aggregate folder statistics from the library cache"
```

---

### Task 2: `FolderStatsService` with caching

**Files:**
- Create: `Screenbox.Core/Services/IFolderStatsService.cs`
- Create: `Screenbox.Core/Services/FolderStatsService.cs`
- Modify: `Screenbox.Core/Common/ServiceHelpers.cs`
- Test: `Screenbox.Core.Tests/Services/FolderStatsServiceTests.cs`

**Interfaces:**
- Consumes: `IDatabaseService.GetFolderStatsAsync` from Task 1.
- Produces: `IFolderStatsService` with `Task<FolderStats> GetAsync(string folderPath)`, `bool TryGetCached(string folderPath, out FolderStats stats)`, and `void Invalidate()`.

`TryGetCached` exists because the caption is requested per tile as the grid virtualizes. The synchronous path returns immediately from cache; the async path fills it. This mirrors how `IWatchStateService.IsWatched` is synchronous for the same reason — see the Phase 2 plan's rationale.

- [ ] **Step 1: Write the failing test**

Create `Screenbox.Core.Tests/Services/FolderStatsServiceTests.cs` covering:

```csharp
using Microsoft.Extensions.Logging.Abstractions;
using Screenbox.Core.Models;
using Screenbox.Core.Services;
using Screenbox.Core.Tests.Helpers;

namespace Screenbox.Core.Tests.Services;

public sealed class FolderStatsServiceTests
{
    private static async Task<(FolderStatsService service, DatabaseService db)> CreateAsync(string dir)
    {
        var db = new DatabaseService(NullLogger<DatabaseService>.Instance) { DbFolderPath = dir };
        await db.InitializeAsync();
        return (new FolderStatsService(db, NullLogger<FolderStatsService>.Instance), db);
    }

    [Test]
    public async Task GetAsync_ReturnsStatsAndPopulatesTheCache()
    {
        using var fixture = new TestDirectoryFixture();
        (FolderStatsService service, DatabaseService db) = await CreateAsync(fixture.DirectoryPath);
        await db.SaveVideoCacheAsync([@"D:\Anime"],
            [new VideoCacheRecordDto { Path = @"D:\Anime\Show\ep1.mkv", Title = "ep1", Duration = TimeSpan.FromMinutes(20) }]);

        FolderStats first = await service.GetAsync(@"D:\Anime\Show");

        await Assert.That(first.EpisodeCount).IsEqualTo(1);
        await Assert.That(service.TryGetCached(@"D:\Anime\Show", out FolderStats cached)).IsTrue();
        await Assert.That(cached.EpisodeCount).IsEqualTo(1);
    }

    [Test]
    public async Task TryGetCached_ReturnsFalseBeforeAnyLoad()
    {
        using var fixture = new TestDirectoryFixture();
        (FolderStatsService service, _) = await CreateAsync(fixture.DirectoryPath);

        await Assert.That(service.TryGetCached(@"D:\Anime\Show", out _)).IsFalse();
    }

    [Test]
    public async Task Invalidate_ClearsTheCacheSoStatsAreRecomputed()
    {
        // Watching an episode changes the unwatched count; the cache must not go stale.
        using var fixture = new TestDirectoryFixture();
        (FolderStatsService service, DatabaseService db) = await CreateAsync(fixture.DirectoryPath);
        await db.SaveVideoCacheAsync([@"D:\Anime"],
            [new VideoCacheRecordDto { Path = @"D:\Anime\Show\ep1.mkv", Title = "ep1", Duration = TimeSpan.FromMinutes(20) }]);
        await service.GetAsync(@"D:\Anime\Show");

        await db.SaveWatchStateAsync(new WatchStateDto { Location = @"D:\ANIME\SHOW\EP1.MKV", Completed = true });
        service.Invalidate();
        FolderStats after = await service.GetAsync(@"D:\Anime\Show");

        await Assert.That(after.UnwatchedCount).IsEqualTo(0);
    }

    [Test]
    public async Task GetAsync_IsCaseInsensitiveOnTheCacheKey()
    {
        using var fixture = new TestDirectoryFixture();
        (FolderStatsService service, DatabaseService db) = await CreateAsync(fixture.DirectoryPath);
        await db.SaveVideoCacheAsync([@"D:\Anime"],
            [new VideoCacheRecordDto { Path = @"D:\Anime\Show\ep1.mkv", Title = "ep1", Duration = TimeSpan.Zero }]);
        await service.GetAsync(@"D:\Anime\Show");

        await Assert.That(service.TryGetCached(@"d:\anime\show", out _)).IsTrue();
    }

    [Test]
    public async Task GetAsync_DoesNotThrowWhenTheDatabaseFails()
    {
        // Captions are an enhancement. A failure must degrade to empty stats, not break the grid.
        using var fixture = new TestDirectoryFixture();
        (FolderStatsService service, _) = await CreateAsync(fixture.DirectoryPath);

        FolderStats stats = await service.GetAsync(string.Empty);

        await Assert.That(stats.EpisodeCount).IsEqualTo(0);
    }
}
```

Adjust `VideoCacheRecordDto` property names to the real ones if they differ.

- [ ] **Step 2: Run the test to verify it fails**

Expected: compile failure — `FolderStatsService` does not exist.

- [ ] **Step 3: Create the interface**

Create `Screenbox.Core/Services/IFolderStatsService.cs`:

```csharp
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
```

- [ ] **Step 4: Implement the service**

Create `Screenbox.Core/Services/FolderStatsService.cs` with a `ConcurrentDictionary<string, FolderStats>` keyed with `StringComparer.OrdinalIgnoreCase`, a try/catch around the database call that logs and returns `default`, and `Invalidate` clearing the dictionary.

Register it in `Screenbox.Core/Common/ServiceHelpers.cs` alongside the other services:

```csharp
        services.AddSingleton<IFolderStatsService, FolderStatsService>();
```

- [ ] **Step 5: Invalidate the cache when watch state changes**

Watching an episode changes the unwatched count for its containing folder, so the cache goes stale.

Call `IFolderStatsService.Invalidate()` from **`FolderViewPageViewModel.Receive(RefreshFolderMessage)`** — the existing handler the app already uses to refresh folder content. That is the right hook because it fires exactly when the view is about to re-read its items, so the next per-tile stats request recomputes.

Inject `IFolderStatsService` into `FolderViewPageViewModel` for this. Do NOT call `Invalidate()` from `WatchStateService` — that would couple a Core service to a view-refresh concern and risks a circular dependency between the two services.

Accepted limitation to state in your report: the count refreshes when the folder view refreshes, not the instant an episode passes the watched threshold mid-playback. That is fine — the user is watching, not looking at the grid.

- [ ] **Step 6: Run the tests and build**

Expected: PASS, 127 total (122 + 5 new). Run five times.

```bash
"C:\Program Files\Microsoft Visual Studio\18\Community\MSBuild\Current\Bin\MSBuild.exe" Screenbox.Core/Screenbox.Core.csproj -t:Build -p:Configuration=Debug -p:Platform=x64 -m
```

- [ ] **Step 7: Commit**

```bash
git add Screenbox.Core/Services/IFolderStatsService.cs Screenbox.Core/Services/FolderStatsService.cs Screenbox.Core/Common/ServiceHelpers.cs Screenbox.Core.Tests/Services/FolderStatsServiceTests.cs
git commit -m "feat(library): add cached folder statistics service"
```

---

### Task 3: Render the aggregate caption

**Files:**
- Modify: `Screenbox.Core/ViewModels/StorageItemViewModel.cs`
- Modify: `Screenbox.Core/Factories/StorageItemViewModelFactory.cs` (the only caller of the constructor — it changes with it)
- Modify: `Screenbox/Behaviors/ThumbnailGridViewBehavior.cs`
- Modify: `Screenbox/Helpers/ItemLabelHelper.cs`
- Modify: `Screenbox/Strings/en-US/Resources.resw`
- Modify: `Screenbox/Pages/FolderViewPage.xaml`

**Interfaces:**
- Consumes: `IFolderStatsService.GetAsync` / `TryGetCached` from Task 2.
- Produces: `StorageItemViewModel.Stats` (observable `FolderStats?`) and `Task LoadFolderStatsAsync()`; a new `ItemLabelHelper.GetFolderStatsCaption(...)` overload.

- [ ] **Step 1: Add the strings**

In `Screenbox/Strings/en-US/Resources.resw` ONLY:

```xml
  <data name="EpisodesCount" xml:space="preserve">
    <value>{0} episodes</value>
  </data>
  <data name="UnwatchedCount" xml:space="preserve">
    <value>{0} unwatched</value>
  </data>
```

Check how the existing `ItemsCount` entry is declared — this project uses ReswPlus, and a pluralised or parameterised string may need a specific comment format to generate the right method signature. Match the existing convention exactly rather than inventing one; if `ItemsCount` uses a plural form, use the same mechanism so "1 episode" reads correctly.

- [ ] **Step 2: Surface stats on the ViewModel**

Add to `StorageItemViewModel`:

```csharp
    [ObservableProperty] public partial FolderStats? Stats { get; set; }
```

and a loader that is a no-op for files, wrapped in try/catch using the existing `_logger`, calling `IFolderStatsService.GetAsync` for folders. Inject `IFolderStatsService` through the constructor and update `StorageItemViewModelFactory` in the same commit — they change together.

- [ ] **Step 3: Load it from the lazy per-tile loader**

In `ThumbnailGridViewBehavior.OnContainerContentChanging`, inside the existing `case StorageItemViewModel storageItem:` branch, call the new loader. Do not add an eager ViewModel loop.

- [ ] **Step 4: Format the caption**

Add an overload to `ItemLabelHelper`:

```csharp
    /// <summary>
    /// Builds a folder caption from aggregate statistics, falling back to the plain item
    /// count when statistics are not available yet.
    /// </summary>
    public static string GetFolderStatsCaption(uint itemCount, FolderStats? stats)
    {
        if (stats is not { EpisodeCount: > 0 } s) return Strings.Resources.ItemsCount(itemCount);

        string episodes = Strings.Resources.EpisodesCount(s.EpisodeCount);
        string runtime = Humanizer.ToDuration(s.TotalDuration);
        return s.UnwatchedCount > 0
            ? $"{episodes} · {runtime} · {Strings.Resources.UnwatchedCount(s.UnwatchedCount)}"
            : $"{episodes} · {runtime}";
    }
```

Confirm `Humanizer.ToDuration` is the helper this codebase already uses for durations — `StorageItemViewModel` calls it for file captions. Reuse it rather than formatting by hand.

- [ ] **Step 5: Bind it**

In `Screenbox/Pages/FolderViewPage.xaml`, change the folder tile's `Caption` binding to use the new overload, passing `ItemCount` and `Stats`. Keep the existing file-caption path unchanged.

- [ ] **Step 6: Build the full app**

Close the running app first, put vswhere on PATH, then build `Screenbox/Screenbox.csproj`.

- [ ] **Step 7: Manual verification**

Open the Videos page. Folder captions should read like `37 episodes · 14h 12m · 35 unwatched` rather than `37 items`. An empty folder should still read as before. Watch an episode and confirm the unwatched count drops after the view refreshes.

- [ ] **Step 8: Commit**

```bash
git add Screenbox.Core/ViewModels/StorageItemViewModel.cs Screenbox.Core/Factories/StorageItemViewModelFactory.cs Screenbox/Behaviors/ThumbnailGridViewBehavior.cs Screenbox/Helpers/ItemLabelHelper.cs Screenbox/Strings/en-US/Resources.resw Screenbox/Pages/FolderViewPage.xaml
git commit -m "feat(library): show episode count, runtime and unwatched in folder captions"
```

---

## Verification

Run the suite five times — all must be clean — and build the full app.

## Out of scope

- Excluding specials from the episode count. `EpisodeInfoParser.Parse` can classify them, but doing so per row would mean parsing every filename on the query path. If the counts look wrong because of `Extras` folders, that is a follow-up.
- Aggregate captions anywhere other than the folder grid.
- Online metadata. That is Phase 5.
