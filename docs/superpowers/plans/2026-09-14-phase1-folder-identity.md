# Phase 1: Folder Identity — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Give every folder in the Videos library its own artwork and an optional display title, replacing the uniform grey glyph.

**Architecture:** A new durable `folder_metadata` SQLite table stores user-authored title and poster choices. A pure `ArtworkResolver` decides which artwork source wins; a UWP-facing `ArtworkService` performs the actual I/O. `StorageItemViewModel` gains `Thumbnail` and `DisplayName`, which `FolderViewPage.xaml` binds to.

**Tech Stack:** C# 14, .NET 10 (`net10.0-windows10.0.26100.0`), UWP with `UseUwp=true`, CommunityToolkit.Mvvm, Microsoft.Data.Sqlite, TUnit.

**Spec:** `docs/superpowers/specs/2026-09-14-personal-media-centre-design.md`

## Global Constraints

- Build with MSBuild from Visual Studio 2026 only. **Never `dotnet build`.** Path: `C:\Program Files\Microsoft Visual Studio\18\Community\MSBuild\Current\Bin\MSBuild.exe`
- `vswhere.exe` **must** be on PATH or native AOT linking fails. Prepend `${env:ProgramFiles(x86)}\Microsoft Visual Studio\Installer` to PATH before building the app project.
- Run tests with `dotnet run --project Screenbox.Core.Tests/Screenbox.Core.Tests.csproj`
- `folder_metadata` is **durable user data**. Register it with the careful pattern used by `EnsurePlaylistsTable`. **Never** register it with `EnsureReplaceableTable`, which drops tables on column drift.
- Artwork files are copied into `ApplicationData.Current.LocalFolder\Artwork\`. Persist only the bare filename, never an absolute path. The app has no `broadFileSystemAccess` capability.
- Nullable reference types are enabled. Use `is null` / `is not null`, never `== null`.
- ViewModels must never access localized strings. Strings belong in the View layer only.
- Conventional Commits for every commit message. All commits go on branch `feat/personal-media-centre`.

## File Structure

| File | Responsibility |
|---|---|
| `Screenbox.Core/Enums/PosterSource.cs` | Enum: where a poster came from |
| `Screenbox.Core/Models/FolderMetadataDto.cs` | Durable folder record |
| `Screenbox.Core/Services/DatabaseService.FolderMetadata.cs` | SQL persistence |
| `Screenbox.Core/Helpers/ArtworkResolver.cs` | Pure priority logic, no I/O |
| `Screenbox.Core/Services/ArtworkService.cs` | UWP I/O: frame capture, file copy |
| `Screenbox.Core/ViewModels/StorageItemViewModel.cs` | Gains `Thumbnail`, `DisplayName` |
| `Screenbox/Pages/FolderViewPage.xaml` | Binds the new properties |

---

### Task 1: `folder_metadata` schema and persistence

**Files:**
- Create: `Screenbox.Core/Enums/PosterSource.cs`
- Create: `Screenbox.Core/Models/FolderMetadataDto.cs`
- Create: `Screenbox.Core/Services/DatabaseService.FolderMetadata.cs`
- Modify: `Screenbox.Core/Services/DatabaseService.Schema.cs`
- Modify: `Screenbox.Core/Services/IDatabaseService.cs`
- Test: `Screenbox.Core.Tests/Database/FolderMetadataTests.cs`

**Interfaces:**
- Consumes: `DatabaseService` (existing, has `DbFolderPath` settable and `InitializeAsync()`), `TestDirectoryFixture` (existing test helper).
- Produces: `FolderMetadataDto { string Path; string? CustomTitle; string? PosterFile; PosterSource PosterSource; string? ProviderPin; int? SortOrder }`; enum `PosterSource { None=0, AutoFrame=1, Manual=2, Convention=3, Scraped=4 }`; and on `IDatabaseService`: `Task SaveFolderMetadataAsync(FolderMetadataDto)`, `Task<FolderMetadataDto?> LoadFolderMetadataAsync(string)`, `Task DeleteFolderMetadataAsync(string)`.

- [ ] **Step 1: Write the failing test**

Create `Screenbox.Core.Tests/Database/FolderMetadataTests.cs`:

```csharp
using Microsoft.Extensions.Logging.Abstractions;
using Screenbox.Core.Enums;
using Screenbox.Core.Models;
using Screenbox.Core.Services;
using Screenbox.Core.Tests.Helpers;

namespace Screenbox.Core.Tests.Database;

public sealed class FolderMetadataTests
{
    private static async Task<DatabaseService> CreateServiceAsync(string dir)
    {
        var db = new DatabaseService(NullLogger<DatabaseService>.Instance) { DbFolderPath = dir };
        await db.InitializeAsync();
        return db;
    }

    [Test]
    public async Task SaveFolderMetadataAsync_RoundTripsAllFields()
    {
        using var fixture = new TestDirectoryFixture();
        DatabaseService db = await CreateServiceAsync(fixture.DirectoryPath);

        var record = new FolderMetadataDto
        {
            Path = @"D:\Media\Anime\Dan Da Dan",
            CustomTitle = "Dandadan",
            PosterFile = "a1b2c3.jpg",
            PosterSource = PosterSource.Manual,
            ProviderPin = "anilist:171018",
            SortOrder = 3
        };

        await db.SaveFolderMetadataAsync(record);
        FolderMetadataDto? loaded = await db.LoadFolderMetadataAsync(record.Path);

        await Assert.That(loaded).IsNotNull();
        await Assert.That(loaded!.CustomTitle).IsEqualTo("Dandadan");
        await Assert.That(loaded.PosterFile).IsEqualTo("a1b2c3.jpg");
        await Assert.That(loaded.PosterSource).IsEqualTo(PosterSource.Manual);
        await Assert.That(loaded.ProviderPin).IsEqualTo("anilist:171018");
        await Assert.That(loaded.SortOrder).IsEqualTo(3);
    }

    [Test]
    public async Task LoadFolderMetadataAsync_ReturnsNullForUnknownPath()
    {
        using var fixture = new TestDirectoryFixture();
        DatabaseService db = await CreateServiceAsync(fixture.DirectoryPath);

        FolderMetadataDto? loaded = await db.LoadFolderMetadataAsync(@"D:\does\not\exist");

        await Assert.That(loaded).IsNull();
    }

    [Test]
    public async Task FolderMetadata_SurvivesReinitialization()
    {
        using var fixture = new TestDirectoryFixture();
        DatabaseService db = await CreateServiceAsync(fixture.DirectoryPath);
        await db.SaveFolderMetadataAsync(new FolderMetadataDto
        {
            Path = @"D:\Media\Anime\Gachiakuta",
            CustomTitle = "Gachiakuta",
            PosterSource = PosterSource.Manual
        });

        // A second service over the same folder simulates an app restart.
        DatabaseService db2 = await CreateServiceAsync(fixture.DirectoryPath);
        FolderMetadataDto? loaded = await db2.LoadFolderMetadataAsync(@"D:\Media\Anime\Gachiakuta");

        await Assert.That(loaded).IsNotNull();
        await Assert.That(loaded!.CustomTitle).IsEqualTo("Gachiakuta");
    }

    [Test]
    public async Task DeleteFolderMetadataAsync_RemovesTheRecord()
    {
        using var fixture = new TestDirectoryFixture();
        DatabaseService db = await CreateServiceAsync(fixture.DirectoryPath);
        const string path = @"D:\Media\Anime\Suzume";
        await db.SaveFolderMetadataAsync(new FolderMetadataDto { Path = path, CustomTitle = "Suzume" });

        await db.DeleteFolderMetadataAsync(path);

        await Assert.That(await db.LoadFolderMetadataAsync(path)).IsNull();
    }
}
```

- [ ] **Step 2: Run the test to verify it fails**

```bash
dotnet run --project Screenbox.Core.Tests/Screenbox.Core.Tests.csproj
```

Expected: compile failure — `FolderMetadataDto`, `PosterSource` and the three database methods do not exist.

- [ ] **Step 3: Create the enum**

Create `Screenbox.Core/Enums/PosterSource.cs`:

```csharp
namespace Screenbox.Core.Enums;

/// <summary>
/// Identifies where a folder's poster image came from, so a refresh can regenerate
/// derived artwork without discarding a manual choice made by the user.
/// </summary>
public enum PosterSource
{
    /// <summary>No poster has been resolved.</summary>
    None = 0,

    /// <summary>Generated from a video frame inside the folder.</summary>
    AutoFrame = 1,

    /// <summary>Explicitly chosen by the user.</summary>
    Manual = 2,

    /// <summary>Found as poster.jpg or folder.jpg inside the folder.</summary>
    Convention = 3,

    /// <summary>Downloaded from an online metadata provider.</summary>
    Scraped = 4
}
```

- [ ] **Step 4: Create the DTO**

Create `Screenbox.Core/Models/FolderMetadataDto.cs`:

```csharp
using Screenbox.Core.Enums;

namespace Screenbox.Core.Models;

/// <summary>
/// Durable, user-authored metadata for a single library folder.
/// </summary>
public sealed class FolderMetadataDto
{
    /// <summary>Absolute path of the folder. Primary key.</summary>
    public string Path { get; set; } = string.Empty;

    /// <summary>Display title overriding the folder name, or null to use the folder name.</summary>
    public string? CustomTitle { get; set; }

    /// <summary>File name within the local Artwork folder. Never an absolute path.</summary>
    public string? PosterFile { get; set; }

    /// <summary>Where <see cref="PosterFile"/> came from.</summary>
    public PosterSource PosterSource { get; set; } = PosterSource.None;

    /// <summary>Confirmed external provider match, for example "anilist:171018".</summary>
    public string? ProviderPin { get; set; }

    /// <summary>Optional manual ordering position.</summary>
    public int? SortOrder { get; set; }
}
```

- [ ] **Step 5: Add the table SQL**

In `Screenbox.Core/Services/DatabaseService.Schema.cs`, add alongside the other `Create*Sql` constants:

```csharp
    private const string CreateFolderMetadataSql = """
        CREATE TABLE IF NOT EXISTS folder_metadata (
            path          TEXT PRIMARY KEY,
            custom_title  TEXT,
            poster_file   TEXT,
            poster_source INTEGER NOT NULL DEFAULT 0,
            provider_pin  TEXT,
            sort_order    INTEGER
        );
        """;
```

- [ ] **Step 6: Register the table with the durable pattern**

In `EnsureSchemaAsync`, add this line immediately after `EnsurePlaylistItemsTable(connection);`:

```csharp
        EnsureFolderMetadataTable(connection);
```

Add the method next to `EnsurePlaylistsTable`. It mirrors that method exactly — this is deliberate, because `folder_metadata` holds user-authored data and must never be dropped by `EnsureReplaceableTable`:

```csharp
    private static void EnsureFolderMetadataTable(SqliteConnection connection)
    {
        HashSet<string> actualColumns = ReadTableColumns(connection, "folder_metadata");
        string[] expectedColumns =
            ["path", "custom_title", "poster_file", "poster_source", "provider_pin", "sort_order"];

        if (actualColumns.Count is 0)
        {
            ExecuteNonQuery(connection, CreateFolderMetadataSql);
            return;
        }

        if (!HasSchemaDrift(actualColumns, expectedColumns))
        {
            return;
        }

        ExecuteNonQuery(connection, "DROP TABLE folder_metadata;");
        ExecuteNonQuery(connection, CreateFolderMetadataSql);
    }
```

In `RecreateDatabaseFile`, add alongside the other create statements:

```csharp
        ExecuteNonQuery(connection, CreateFolderMetadataSql);
```

- [ ] **Step 7: Implement the database methods**

Create `Screenbox.Core/Services/DatabaseService.FolderMetadata.cs`:

```csharp
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
```

- [ ] **Step 8: Declare the methods on the interface**

In `Screenbox.Core/Services/IDatabaseService.cs`, add before the closing brace:

```csharp
    /// <summary>Saves durable, user-authored metadata for a single folder.</summary>
    Task SaveFolderMetadataAsync(FolderMetadataDto metadata);

    /// <summary>Loads folder metadata, or null when the folder has none.</summary>
    Task<FolderMetadataDto?> LoadFolderMetadataAsync(string path);

    /// <summary>Removes folder metadata.</summary>
    Task DeleteFolderMetadataAsync(string path);
```

- [ ] **Step 9: Run the tests and verify they pass**

```bash
dotnet run --project Screenbox.Core.Tests/Screenbox.Core.Tests.csproj
```

Expected: PASS, 32 total (28 existing + 4 new).

- [ ] **Step 10: Commit**

```bash
git add Screenbox.Core/Enums/PosterSource.cs Screenbox.Core/Models/FolderMetadataDto.cs Screenbox.Core/Services/DatabaseService.FolderMetadata.cs Screenbox.Core/Services/DatabaseService.Schema.cs Screenbox.Core/Services/IDatabaseService.cs Screenbox.Core.Tests/Database/FolderMetadataTests.cs
git commit -m "feat(library): add durable folder_metadata table"
```

---

### Task 2: `ArtworkResolver` — pure artwork priority logic

**Files:**
- Create: `Screenbox.Core/Helpers/ArtworkResolver.cs`
- Test: `Screenbox.Core.Tests/Helpers/ArtworkResolverTests.cs`

**Interfaces:**
- Consumes: `FolderMetadataDto`, `PosterSource` from Task 1.
- Produces: `ArtworkResolver.Resolve(FolderMetadataDto? metadata, bool hasConventionFile, bool hasCachedFrame)` returning `ArtworkDecision`; record `ArtworkDecision(PosterSource Source, string? FileName, bool NeedsGeneration)`.

This task deliberately contains **no I/O**. Separating the decision from the file access is what makes the priority order testable without a UWP host.

- [ ] **Step 1: Write the failing test**

Create `Screenbox.Core.Tests/Helpers/ArtworkResolverTests.cs`:

```csharp
using Screenbox.Core.Enums;
using Screenbox.Core.Helpers;
using Screenbox.Core.Models;

namespace Screenbox.Core.Tests.Helpers;

public sealed class ArtworkResolverTests
{
    [Test]
    public async Task Resolve_PrefersManualOverEverythingElse()
    {
        var metadata = new FolderMetadataDto
        {
            Path = @"D:\x", PosterFile = "manual.jpg", PosterSource = PosterSource.Manual
        };

        ArtworkDecision decision = ArtworkResolver.Resolve(metadata, hasConventionFile: true, hasCachedFrame: true);

        await Assert.That(decision.Source).IsEqualTo(PosterSource.Manual);
        await Assert.That(decision.FileName).IsEqualTo("manual.jpg");
        await Assert.That(decision.NeedsGeneration).IsFalse();
    }

    [Test]
    public async Task Resolve_PrefersScrapedOverConvention()
    {
        var metadata = new FolderMetadataDto
        {
            Path = @"D:\x", PosterFile = "scraped.jpg", PosterSource = PosterSource.Scraped
        };

        ArtworkDecision decision = ArtworkResolver.Resolve(metadata, hasConventionFile: true, hasCachedFrame: true);

        await Assert.That(decision.Source).IsEqualTo(PosterSource.Scraped);
        await Assert.That(decision.FileName).IsEqualTo("scraped.jpg");
    }

    [Test]
    public async Task Resolve_UsesConventionWhenNoStoredPoster()
    {
        ArtworkDecision decision = ArtworkResolver.Resolve(metadata: null, hasConventionFile: true, hasCachedFrame: true);

        await Assert.That(decision.Source).IsEqualTo(PosterSource.Convention);
        await Assert.That(decision.NeedsGeneration).IsFalse();
    }

    [Test]
    public async Task Resolve_RequestsGenerationWhenNothingAvailable()
    {
        ArtworkDecision decision = ArtworkResolver.Resolve(metadata: null, hasConventionFile: false, hasCachedFrame: false);

        await Assert.That(decision.Source).IsEqualTo(PosterSource.None);
        await Assert.That(decision.FileName).IsNull();
        await Assert.That(decision.NeedsGeneration).IsTrue();
    }

    [Test]
    public async Task Resolve_UsesCachedFrameWithoutRegenerating()
    {
        var metadata = new FolderMetadataDto
        {
            Path = @"D:\x", PosterFile = "frame.jpg", PosterSource = PosterSource.AutoFrame
        };

        ArtworkDecision decision = ArtworkResolver.Resolve(metadata, hasConventionFile: false, hasCachedFrame: true);

        await Assert.That(decision.Source).IsEqualTo(PosterSource.AutoFrame);
        await Assert.That(decision.NeedsGeneration).IsFalse();
    }

    [Test]
    public async Task Resolve_RegeneratesWhenStoredFrameFileIsMissing()
    {
        var metadata = new FolderMetadataDto
        {
            Path = @"D:\x", PosterFile = "gone.jpg", PosterSource = PosterSource.AutoFrame
        };

        ArtworkDecision decision = ArtworkResolver.Resolve(metadata, hasConventionFile: false, hasCachedFrame: false);

        await Assert.That(decision.NeedsGeneration).IsTrue();
    }

    [Test]
    public async Task Resolve_KeepsManualEvenWhenFileMissingSoUserChoiceIsNotSilentlyLost()
    {
        var metadata = new FolderMetadataDto
        {
            Path = @"D:\x", PosterFile = "manual.jpg", PosterSource = PosterSource.Manual
        };

        ArtworkDecision decision = ArtworkResolver.Resolve(metadata, hasConventionFile: true, hasCachedFrame: false);

        await Assert.That(decision.Source).IsEqualTo(PosterSource.Manual);
    }
}
```

- [ ] **Step 2: Run the test to verify it fails**

```bash
dotnet run --project Screenbox.Core.Tests/Screenbox.Core.Tests.csproj
```

Expected: compile failure — `ArtworkResolver` and `ArtworkDecision` do not exist.

- [ ] **Step 3: Write the implementation**

Create `Screenbox.Core/Helpers/ArtworkResolver.cs`:

```csharp
using Screenbox.Core.Enums;
using Screenbox.Core.Models;

namespace Screenbox.Core.Helpers;

/// <summary>
/// The outcome of resolving which artwork a folder should display.
/// </summary>
/// <param name="Source">The winning artwork source.</param>
/// <param name="FileName">File name within the local Artwork folder, or null when none applies.</param>
/// <param name="NeedsGeneration">True when a frame must be captured to satisfy the request.</param>
public readonly record struct ArtworkDecision(PosterSource Source, string? FileName, bool NeedsGeneration);

/// <summary>
/// Decides which artwork source wins for a folder. Pure logic with no I/O so the
/// priority order can be verified without a UWP host.
/// </summary>
public static class ArtworkResolver
{
    /// <summary>
    /// Resolves the artwork to display.
    /// Priority: manual, then scraped, then convention file, then a cached or newly generated frame.
    /// </summary>
    /// <param name="metadata">Stored metadata for the folder, or null when it has none.</param>
    /// <param name="hasConventionFile">Whether poster.jpg or folder.jpg exists inside the folder.</param>
    /// <param name="hasCachedFrame">Whether the stored poster file is present on disk.</param>
    public static ArtworkDecision Resolve(FolderMetadataDto? metadata, bool hasConventionFile, bool hasCachedFrame)
    {
        // A manual choice always wins and is never silently replaced, even when the
        // file is temporarily missing. Losing it would discard deliberate user work.
        if (metadata is { PosterSource: PosterSource.Manual, PosterFile: { Length: > 0 } manual })
        {
            return new ArtworkDecision(PosterSource.Manual, manual, NeedsGeneration: false);
        }

        if (metadata is { PosterSource: PosterSource.Scraped, PosterFile: { Length: > 0 } scraped })
        {
            return new ArtworkDecision(PosterSource.Scraped, scraped, NeedsGeneration: false);
        }

        if (hasConventionFile)
        {
            return new ArtworkDecision(PosterSource.Convention, metadata?.PosterFile, NeedsGeneration: false);
        }

        if (metadata is { PosterSource: PosterSource.AutoFrame, PosterFile: { Length: > 0 } frame } && hasCachedFrame)
        {
            return new ArtworkDecision(PosterSource.AutoFrame, frame, NeedsGeneration: false);
        }

        return new ArtworkDecision(PosterSource.None, FileName: null, NeedsGeneration: true);
    }
}
```

- [ ] **Step 4: Run the tests and verify they pass**

```bash
dotnet run --project Screenbox.Core.Tests/Screenbox.Core.Tests.csproj
```

Expected: PASS, 39 total (32 + 7 new).

- [ ] **Step 5: Commit**

```bash
git add Screenbox.Core/Helpers/ArtworkResolver.cs Screenbox.Core.Tests/Helpers/ArtworkResolverTests.cs
git commit -m "feat(library): add pure artwork priority resolver"
```

---

### Task 3: `ArtworkService` — recursive video discovery and frame capture

**Files:**
- Create: `Screenbox.Core/Services/IArtworkService.cs`
- Create: `Screenbox.Core/Services/ArtworkService.cs`
- Modify: `Screenbox.Core/Common/ServiceHelpers.cs`
- Test: `Screenbox.Core.Tests/Services/ArtworkServiceTests.cs`

**Interfaces:**
- Consumes: `ArtworkResolver.Resolve` and `ArtworkDecision` from Task 2; `IDatabaseService.LoadFolderMetadataAsync` / `SaveFolderMetadataAsync` from Task 1; existing `IFilesService`.
- Produces: `IArtworkService` with `Task<string?> GetPosterFileNameAsync(StorageFolder folder)`, `Task<string?> SetManualPosterAsync(StorageFolder folder, StorageFile image)`, `Task ClearGeneratedArtworkAsync()`. On the concrete `ArtworkService`, three public members exist for testing without a UWP host: `const string ArtworkFolderName`, `const int MaxRecursionDepth`, `static bool IsConventionFileName(string)`. The recursive search itself stays private because it requires real `StorageFolder` instances.

The recursion depth constant is public because series folders such as `Attack On Titan/Attack on Titan Season 1/Extras` nest three levels, and a bounded depth prevents pathological trees from stalling the UI.

- [ ] **Step 1: Write the failing test for depth bounding**

Create `Screenbox.Core.Tests/Services/ArtworkServiceTests.cs`:

```csharp
using Screenbox.Core.Services;

namespace Screenbox.Core.Tests.Services;

public sealed class ArtworkServiceTests
{
    [Test]
    public async Task MaxRecursionDepth_IsAtLeastThreeToCoverNestedSeasonFolders()
    {
        // Real libraries nest as Series/Season/Extras, so anything below 3 would
        // fail to find a video for the series-level folder that most needs artwork.
        await Assert.That(ArtworkService.MaxRecursionDepth).IsGreaterThanOrEqualTo(3);
    }

    [Test]
    public async Task ArtworkFolderName_IsStableAndRelative()
    {
        await Assert.That(ArtworkService.ArtworkFolderName).IsEqualTo("Artwork");
    }

    [Test]
    public async Task IsConventionFileName_MatchesKnownConventionsCaseInsensitively()
    {
        await Assert.That(ArtworkService.IsConventionFileName("poster.jpg")).IsTrue();
        await Assert.That(ArtworkService.IsConventionFileName("Folder.JPG")).IsTrue();
        await Assert.That(ArtworkService.IsConventionFileName("poster.png")).IsTrue();
        await Assert.That(ArtworkService.IsConventionFileName("cover.jpg")).IsTrue();
        await Assert.That(ArtworkService.IsConventionFileName("episode01.jpg")).IsFalse();
        await Assert.That(ArtworkService.IsConventionFileName("poster.txt")).IsFalse();
    }
}
```

- [ ] **Step 2: Run the test to verify it fails**

```bash
dotnet run --project Screenbox.Core.Tests/Screenbox.Core.Tests.csproj
```

Expected: compile failure — `ArtworkService` does not exist.

- [ ] **Step 3: Create the interface**

Create `Screenbox.Core/Services/IArtworkService.cs`:

```csharp
using System.Threading.Tasks;
using Windows.Storage;

namespace Screenbox.Core.Services;

/// <summary>
/// Resolves and produces poster artwork for library folders.
/// </summary>
public interface IArtworkService
{
    /// <summary>
    /// Returns the poster file name within the local Artwork folder for the given folder,
    /// generating one from a video frame when necessary. Returns null when no artwork
    /// could be produced, in which case the caller should fall back to the folder glyph.
    /// </summary>
    Task<string?> GetPosterFileNameAsync(StorageFolder folder);

    /// <summary>
    /// Copies the chosen image into local storage and records it as the manual poster.
    /// Returns the stored file name.
    /// </summary>
    Task<string?> SetManualPosterAsync(StorageFolder folder, StorageFile image);

    /// <summary>
    /// Deletes generated and scraped artwork from local storage. Manual posters are preserved.
    /// </summary>
    Task ClearGeneratedArtworkAsync();
}
```

- [ ] **Step 4: Implement the service**

Create `Screenbox.Core/Services/ArtworkService.cs`:

```csharp
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Screenbox.Core.Enums;
using Screenbox.Core.Helpers;
using Screenbox.Core.Models;
using Windows.Graphics.Imaging;
using Windows.Storage;
using Windows.Storage.FileProperties;
using Windows.Storage.Streams;

namespace Screenbox.Core.Services;

/// <inheritdoc cref="IArtworkService"/>
public sealed class ArtworkService : IArtworkService
{
    /// <summary>Folder name inside local storage where artwork is cached.</summary>
    public const string ArtworkFolderName = "Artwork";

    /// <summary>
    /// How deep to search for a video when generating a poster. Series folders commonly
    /// nest as Series/Season/Extras, so three levels is the practical minimum.
    /// </summary>
    public const int MaxRecursionDepth = 3;

    private static readonly string[] ConventionStems = ["poster", "folder", "cover"];
    private static readonly string[] ConventionExtensions = [".jpg", ".jpeg", ".png"];

    private readonly IDatabaseService _databaseService;
    private readonly ILogger<ArtworkService> _logger;

    public ArtworkService(IDatabaseService databaseService, ILogger<ArtworkService> logger)
    {
        _databaseService = databaseService;
        _logger = logger;
    }

    /// <summary>Returns true when the file name is a recognised poster convention.</summary>
    public static bool IsConventionFileName(string fileName)
    {
        string stem = System.IO.Path.GetFileNameWithoutExtension(fileName);
        string ext = System.IO.Path.GetExtension(fileName);
        return ConventionStems.Contains(stem, StringComparer.OrdinalIgnoreCase)
            && ConventionExtensions.Contains(ext, StringComparer.OrdinalIgnoreCase);
    }

    /// <inheritdoc/>
    public async Task<string?> GetPosterFileNameAsync(StorageFolder folder)
    {
        try
        {
            FolderMetadataDto? metadata = await _databaseService.LoadFolderMetadataAsync(folder.Path);
            StorageFolder artworkFolder = await GetArtworkFolderAsync();

            StorageFile? conventionFile = await FindConventionFileAsync(folder);
            bool hasCachedFrame = metadata?.PosterFile is { Length: > 0 } name
                && await artworkFolder.TryGetItemAsync(name) is not null;

            ArtworkDecision decision = ArtworkResolver.Resolve(metadata, conventionFile is not null, hasCachedFrame);

            if (!decision.NeedsGeneration)
            {
                if (decision.Source is PosterSource.Convention && conventionFile is not null)
                {
                    return await CopyIntoArtworkFolderAsync(folder, conventionFile, PosterSource.Convention);
                }

                return decision.FileName;
            }

            StorageFile? video = await FindFirstVideoAsync(folder, MaxRecursionDepth);
            if (video is null) return null;

            return await GenerateFrameAsync(folder, video);
        }
        catch (Exception e)
        {
            _logger.LogError(e, "Failed to resolve poster artwork for '{Path}'.", folder.Path);
            return null;
        }
    }

    /// <inheritdoc/>
    public async Task<string?> SetManualPosterAsync(StorageFolder folder, StorageFile image)
    {
        try
        {
            return await CopyIntoArtworkFolderAsync(folder, image, PosterSource.Manual);
        }
        catch (Exception e)
        {
            _logger.LogError(e, "Failed to set manual poster for '{Path}'.", folder.Path);
            return null;
        }
    }

    /// <inheritdoc/>
    public async Task ClearGeneratedArtworkAsync()
    {
        StorageFolder artworkFolder = await GetArtworkFolderAsync();
        foreach (StorageFile file in await artworkFolder.GetFilesAsync())
        {
            // Manual posters are prefixed "m_" and must survive a clear.
            if (file.Name.StartsWith("m_", StringComparison.Ordinal)) continue;
            try
            {
                await file.DeleteAsync(StorageDeleteOption.PermanentDelete);
            }
            catch (Exception e)
            {
                _logger.LogError(e, "Failed to delete cached artwork '{Name}'.", file.Name);
            }
        }
    }

    private static async Task<StorageFolder> GetArtworkFolderAsync() =>
        await ApplicationData.Current.LocalFolder.CreateFolderAsync(
            ArtworkFolderName, CreationCollisionOption.OpenIfExists);

    private static async Task<StorageFile?> FindConventionFileAsync(StorageFolder folder)
    {
        foreach (StorageFile file in await folder.GetFilesAsync())
        {
            if (IsConventionFileName(file.Name)) return file;
        }

        return null;
    }

    private static async Task<StorageFile?> FindFirstVideoAsync(StorageFolder folder, int depth)
    {
        if (depth <= 0) return null;

        IReadOnlyList<StorageFile> files = await folder.GetFilesAsync();
        StorageFile? video = files.FirstOrDefault(f => f.ContentType.StartsWith("video", StringComparison.OrdinalIgnoreCase));
        if (video is not null) return video;

        foreach (StorageFolder sub in await folder.GetFoldersAsync())
        {
            StorageFile? found = await FindFirstVideoAsync(sub, depth - 1);
            if (found is not null) return found;
        }

        return null;
    }

    private async Task<string?> GenerateFrameAsync(StorageFolder folder, StorageFile video)
    {
        using StorageItemThumbnail thumbnail =
            await video.GetThumbnailAsync(ThumbnailMode.SingleItem, requestedSize: 1280, ThumbnailOptions.UseCurrentScale);
        if (thumbnail is not { Type: ThumbnailType.Image }) return null;

        StorageFolder artworkFolder = await GetArtworkFolderAsync();
        string fileName = BuildFileName(folder.Path, PosterSource.AutoFrame, ".jpg");
        StorageFile target = await artworkFolder.CreateFileAsync(fileName, CreationCollisionOption.ReplaceExisting);

        using (IRandomAccessStream output = await target.OpenAsync(FileAccessMode.ReadWrite))
        {
            BitmapDecoder decoder = await BitmapDecoder.CreateAsync(thumbnail);
            BitmapEncoder encoder = await BitmapEncoder.CreateForTranscodingAsync(output, decoder);
            await encoder.FlushAsync();
        }

        await UpsertPosterAsync(folder.Path, fileName, PosterSource.AutoFrame);
        return fileName;
    }

    private async Task<string> CopyIntoArtworkFolderAsync(StorageFolder folder, StorageFile source, PosterSource kind)
    {
        StorageFolder artworkFolder = await GetArtworkFolderAsync();
        string extension = System.IO.Path.GetExtension(source.Name);
        string fileName = BuildFileName(folder.Path, kind, extension);
        await source.CopyAsync(artworkFolder, fileName, NameCollisionOption.ReplaceExisting);
        await UpsertPosterAsync(folder.Path, fileName, kind);
        return fileName;
    }

    private async Task UpsertPosterAsync(string folderPath, string fileName, PosterSource source)
    {
        FolderMetadataDto metadata =
            await _databaseService.LoadFolderMetadataAsync(folderPath) ?? new FolderMetadataDto { Path = folderPath };
        metadata.PosterFile = fileName;
        metadata.PosterSource = source;
        await _databaseService.SaveFolderMetadataAsync(metadata);
    }

    private static string BuildFileName(string folderPath, PosterSource source, string extension)
    {
        // Manual posters are prefixed so ClearGeneratedArtworkAsync can preserve them.
        string prefix = source is PosterSource.Manual ? "m_" : "g_";
        uint hash = 2166136261u;
        foreach (char c in folderPath)
        {
            hash = (hash ^ char.ToLowerInvariant(c)) * 16777619u;
        }

        return string.Concat(prefix, hash.ToString("x8"), extension);
    }
}
```

- [ ] **Step 5: Register the service**

In `Screenbox.Core/Common/ServiceHelpers.cs`, add next to the other service registrations (near `services.AddSingleton<IFilesService, FilesService>();`):

```csharp
        services.AddSingleton<IArtworkService, ArtworkService>();
```

- [ ] **Step 6: Run the tests and verify they pass**

```bash
dotnet run --project Screenbox.Core.Tests/Screenbox.Core.Tests.csproj
```

Expected: PASS, 42 total (39 + 3 new).

- [ ] **Step 7: Build Screenbox.Core to confirm the UWP APIs compile**

```bash
"C:\Program Files\Microsoft Visual Studio\18\Community\MSBuild\Current\Bin\MSBuild.exe" Screenbox.Core/Screenbox.Core.csproj /t:Build /p:Configuration=Debug /p:Platform=x64 /m
```

Expected: `Build succeeded`, exit code 0.

- [ ] **Step 8: Commit**

```bash
git add Screenbox.Core/Services/IArtworkService.cs Screenbox.Core/Services/ArtworkService.cs Screenbox.Core/Common/ServiceHelpers.cs Screenbox.Core.Tests/Services/ArtworkServiceTests.cs
git commit -m "feat(library): add artwork service with recursive frame capture"
```

---

### Task 4: Surface artwork and display name on `StorageItemViewModel`

**Files:**
- Modify: `Screenbox.Core/ViewModels/StorageItemViewModel.cs`
- Modify: `Screenbox.Core/Factories/StorageItemViewModelFactory.cs`

**Interfaces:**
- Consumes: `IArtworkService.GetPosterFileNameAsync` from Task 3; `IDatabaseService.LoadFolderMetadataAsync` from Task 1.
- Produces: on `StorageItemViewModel`, `BitmapImage? Thumbnail` (observable), `string DisplayName` (observable), and `Task LoadFolderArtworkAsync()`.

`StorageItemViewModel` currently takes four constructor arguments. Two more are added, so the factory must be updated in the same task — they change together.

- [ ] **Step 1: Add the properties and loader**

In `Screenbox.Core/ViewModels/StorageItemViewModel.cs`, add these `using` directives:

```csharp
using Windows.UI.Xaml.Media.Imaging;
using Screenbox.Core.Models;
```

This project is UWP and uses `Windows.UI.Xaml`, **not** `Microsoft.UI.Xaml`. Using the WinUI 3
namespace here will not compile. Confirm against `MediaViewModel.cs:20`, which uses the same type.

Add the observable properties next to the existing `CaptionText` and `ItemCount` declarations:

```csharp
    [ObservableProperty] public partial BitmapImage? Thumbnail { get; set; }
    [ObservableProperty] public partial string DisplayName { get; set; }
```

Add two fields alongside `_filesService`:

```csharp
    private readonly IArtworkService _artworkService;
    private readonly IDatabaseService _databaseService;
```

Change the constructor signature to accept them and assign both, then initialise `DisplayName` to `Name` after the existing name assignment:

```csharp
        DisplayName = Name;
```

Add the loader method:

```csharp
    /// <summary>
    /// Loads the custom title and poster artwork for a folder. Does nothing for files,
    /// whose artwork already comes from <see cref="MediaViewModel.Thumbnail"/>.
    /// </summary>
    [DynamicWindowsRuntimeCast(typeof(StorageFolder))]
    public async Task LoadFolderArtworkAsync()
    {
        if (StorageItem is not StorageFolder folder || string.IsNullOrEmpty(folder.Path)) return;

        try
        {
            FolderMetadataDto? metadata = await _databaseService.LoadFolderMetadataAsync(folder.Path);
            if (metadata?.CustomTitle is { Length: > 0 } title)
            {
                DisplayName = title;
            }

            string? posterFile = await _artworkService.GetPosterFileNameAsync(folder);
            if (posterFile is not { Length: > 0 }) return;

            var uri = new Uri($"ms-appdata:///local/{ArtworkService.ArtworkFolderName}/{posterFile}");
            Thumbnail = new BitmapImage(uri) { DecodePixelWidth = 400 };
        }
        catch (Exception e)
        {
            _logger.LogError(e, "Failed to load folder artwork for '{Path}'.", Path);
        }
    }
```

- [ ] **Step 2: Update the factory**

In `Screenbox.Core/Factories/StorageItemViewModelFactory.cs`, add the two dependencies to the constructor and pass them through:

```csharp
    private readonly IArtworkService _artworkService;
    private readonly IDatabaseService _databaseService;
```

Update `GetInstance` to:

```csharp
    public StorageItemViewModel GetInstance(IStorageItem storageItem)
    {
        return new StorageItemViewModel(
            _filesService, _mediaFactory, _logger, storageItem, _artworkService, _databaseService);
    }
```

- [ ] **Step 3: Build Screenbox.Core to verify it compiles**

```bash
"C:\Program Files\Microsoft Visual Studio\18\Community\MSBuild\Current\Bin\MSBuild.exe" Screenbox.Core/Screenbox.Core.csproj /t:Build /p:Configuration=Debug /p:Platform=x64 /m
```

Expected: `Build succeeded`, exit code 0.

- [ ] **Step 4: Run the tests to confirm nothing regressed**

```bash
dotnet run --project Screenbox.Core.Tests/Screenbox.Core.Tests.csproj
```

Expected: PASS, 42 total.

- [ ] **Step 5: Commit**

```bash
git add Screenbox.Core/ViewModels/StorageItemViewModel.cs Screenbox.Core/Factories/StorageItemViewModelFactory.cs
git commit -m "feat(library): surface folder artwork and display name on storage items"
```

---

### Task 5: Bind artwork in the folder view

**Files:**
- Modify: `Screenbox/Pages/FolderViewPage.xaml:145-161`
- Modify: `Screenbox.Core/ViewModels/FolderViewPageViewModel.cs`

**Interfaces:**
- Consumes: `StorageItemViewModel.Thumbnail`, `DisplayName`, `LoadFolderArtworkAsync()` from Task 4.
- Produces: no new public API. This is the task that makes the feature visible.

- [ ] **Step 1: Bind the new properties**

In `Screenbox/Pages/FolderViewPage.xaml`, within the `DataTemplate` for `StorageItemViewModel`, change the `Title` and `ThumbnailSource` bindings:

```xml
                        Title="{x:Bind DisplayName, Mode=OneWay}"
```

```xml
                        ThumbnailSource="{x:Bind Thumbnail, Mode=OneWay, FallbackValue={x:Null}}"
```

Leave `PlaceholderIconSource` exactly as it is. It remains the fallback for folders where no artwork could be produced.

- [ ] **Step 2: Trigger artwork loading when items are populated**

In `Screenbox.Core/ViewModels/FolderViewPageViewModel.cs`, locate where `UpdateCaptionAsync` is invoked for items after a fetch. Add an artwork load alongside it so both run off the UI thread:

```csharp
        foreach (StorageItemViewModel item in Items)
        {
            await item.UpdateCaptionAsync();
            await item.LoadFolderArtworkAsync();
        }
```

If the existing code already iterates items to update captions, add the single `LoadFolderArtworkAsync` call inside that existing loop rather than adding a second loop.

- [ ] **Step 3: Build the full app**

```bash
"C:\Program Files\Microsoft Visual Studio\18\Community\MSBuild\Current\Bin\MSBuild.exe" Screenbox/Screenbox.csproj /t:Build /p:Configuration=Debug /p:Platform=x64 /m
```

Ensure `vswhere.exe` is on PATH first, or native AOT linking fails with a misleading MSB3073 error.

Expected: `Build succeeded`, exit code 0.

- [ ] **Step 4: Run the tests**

```bash
dotnet run --project Screenbox.Core.Tests/Screenbox.Core.Tests.csproj
```

Expected: PASS, 42 total.

- [ ] **Step 5: Manual verification**

Launch the app, open Videos, and confirm folders now show frames from their contents rather than identical grey glyphs. Series folders that contain only subfolders (for example `Attack On Titan`) must also show artwork, proving the recursion works.

- [ ] **Step 6: Commit**

```bash
git add Screenbox/Pages/FolderViewPage.xaml Screenbox.Core/ViewModels/FolderViewPageViewModel.cs
git commit -m "feat(library): show folder artwork and custom titles in folder view"
```

---

### Task 6: Edit dialog for custom artwork and title

**Files:**
- Create: `Screenbox/Dialogs/EditFolderDialog.xaml`
- Create: `Screenbox/Dialogs/EditFolderDialog.xaml.cs`
- Modify: `Screenbox/Strings/en-US/Resources.resw`
- Modify: `Screenbox/Pages/FolderViewPage.xaml` (ItemFlyout, from line 24)
- Modify: `Screenbox/Pages/FolderViewPage.xaml.cs`

**Interfaces:**
- Consumes: `IArtworkService.SetManualPosterAsync` from Task 3; `IDatabaseService.SaveFolderMetadataAsync` / `LoadFolderMetadataAsync` from Task 1; `StorageItemViewModel.LoadFolderArtworkAsync()` from Task 4.
- Produces: `EditFolderDialog(string currentTitle)` with `Task<EditFolderResult?> GetResultAsync()`; record `EditFolderResult(string Title, StorageFile? Poster)`.

This is the feature that motivated the whole project: choosing your own image for a folder. Follow
the existing `RenamePlaylistDialog` pattern exactly — same `DefaultStyleKey`, flow direction and
theme handling.

- [ ] **Step 1: Add the localized strings**

In `Screenbox/Strings/en-US/Resources.resw`, add these entries in the same format as the surrounding
`<data>` elements:

```xml
  <data name="EditFolder" xml:space="preserve">
    <value>Edit folder</value>
  </data>
  <data name="EditFolderTitlePlaceholder" xml:space="preserve">
    <value>Display title</value>
  </data>
  <data name="ChooseImage" xml:space="preserve">
    <value>Choose image</value>
  </data>
  <data name="Save" xml:space="preserve">
    <value>Save</value>
  </data>
```

Do not add these strings to any other language folder. Translations are handled through Crowdin.

- [ ] **Step 2: Create the dialog markup**

Create `Screenbox/Dialogs/EditFolderDialog.xaml`:

```xml
<ContentDialog
    x:Class="Screenbox.Dialogs.EditFolderDialog"
    xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
    xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
    xmlns:d="http://schemas.microsoft.com/expression/blend/2008"
    xmlns:mc="http://schemas.openxmlformats.org/markup-compatibility/2006"
    xmlns:strings="using:Screenbox.Strings"
    Title="{x:Bind strings:Resources.EditFolder}"
    CloseButtonText="{x:Bind strings:Resources.Cancel}"
    DefaultButton="Primary"
    PrimaryButtonText="{x:Bind strings:Resources.Save}"
    Style="{StaticResource DefaultContentDialogStyle}"
    mc:Ignorable="d">

    <StackPanel Spacing="12">
        <TextBox
            x:Name="TitleTextBox"
            MaxLength="200"
            PlaceholderText="{x:Bind strings:Resources.EditFolderTitlePlaceholder}" />
        <Button
            x:Name="ChooseImageButton"
            Click="ChooseImageButton_OnClick"
            Content="{x:Bind strings:Resources.ChooseImage}"
            HorizontalAlignment="Stretch" />
        <Image
            x:Name="PreviewImage"
            MaxHeight="200"
            Stretch="Uniform"
            Visibility="Collapsed" />
    </StackPanel>
</ContentDialog>
```

- [ ] **Step 3: Create the dialog code-behind**

Create `Screenbox/Dialogs/EditFolderDialog.xaml.cs`:

```csharp
using System;
using System.Threading.Tasks;
using Screenbox.Helpers;
using Windows.Storage;
using Windows.Storage.Pickers;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Media.Imaging;

namespace Screenbox.Dialogs;

/// <summary>The outcome of editing a folder's display title and poster.</summary>
/// <param name="Title">The trimmed display title. Empty means clear the custom title.</param>
/// <param name="Poster">The chosen image, or null when the poster is unchanged.</param>
public sealed record EditFolderResult(string Title, StorageFile? Poster);

public sealed partial class EditFolderDialog : ContentDialog
{
    private StorageFile? _chosenImage;

    [DynamicWindowsRuntimeCast(typeof(FrameworkElement))]
    public EditFolderDialog(string currentTitle)
    {
        this.DefaultStyleKey = typeof(ContentDialog);
        this.InitializeComponent();
        FlowDirection = GlobalizationHelper.GetFlowDirection();
        RequestedTheme = ((FrameworkElement)Window.Current.Content).RequestedTheme;
        TitleTextBox.Text = currentTitle;
        TitleTextBox.SelectAll();
    }

    /// <summary>Shows the dialog and returns the result, or null when cancelled.</summary>
    public async Task<EditFolderResult?> GetResultAsync()
    {
        ContentDialogResult result = await ShowAsync();
        if (result != ContentDialogResult.Primary) return null;
        return new EditFolderResult(TitleTextBox.Text.Trim(), _chosenImage);
    }

    private async void ChooseImageButton_OnClick(object sender, RoutedEventArgs e)
    {
        var picker = new FileOpenPicker { ViewMode = PickerViewMode.Thumbnail };
        picker.FileTypeFilter.Add(".jpg");
        picker.FileTypeFilter.Add(".jpeg");
        picker.FileTypeFilter.Add(".png");

        StorageFile? file = await picker.PickSingleFileAsync();
        if (file is null) return;

        _chosenImage = file;

        using Windows.Storage.Streams.IRandomAccessStream stream =
            await file.OpenAsync(FileAccessMode.Read);
        var bitmap = new BitmapImage();
        await bitmap.SetSourceAsync(stream);
        PreviewImage.Source = bitmap;
        PreviewImage.Visibility = Visibility.Visible;
    }
}
```

The picker grants access to the chosen file for long enough to copy it. That is precisely why the
artwork is copied into local storage rather than referenced by path — see the Global Constraints.

- [ ] **Step 4: Add the context menu entry**

In `Screenbox/Pages/FolderViewPage.xaml`, inside the `MenuFlyout` with `x:Key="ItemFlyout"`, add a
new item after the existing playback items:

```xml
                <MenuFlyoutSeparator />
                <MenuFlyoutItem
                    Click="EditFolder_OnClick"
                    Text="{x:Bind strings:Resources.EditFolder}"
                    Visibility="{x:Bind ViewModel.ContextItem.IsFile, Mode=OneWay, Converter={StaticResource BoolToVisibilityNegationConverter}}" />
```

If `BoolToVisibilityNegationConverter` is not already a page resource, use the converter the project
already uses for inverted visibility. Check `Screenbox/Converters/` and reuse what exists rather
than adding a new converter.

- [ ] **Step 5: Handle the click**

In `Screenbox/Pages/FolderViewPage.xaml.cs`, add:

```csharp
    private async void EditFolder_OnClick(object sender, RoutedEventArgs e)
    {
        StorageItemViewModel? item = ViewModel.ContextItem;
        if (item?.StorageItem is not StorageFolder folder) return;

        var dialog = new EditFolderDialog(item.DisplayName);
        EditFolderResult? result = await dialog.GetResultAsync();
        if (result is null) return;

        await ViewModel.ApplyFolderEditAsync(folder, result.Title, result.Poster);
        await item.LoadFolderArtworkAsync();
    }
```

Add `using Screenbox.Dialogs;`, `using Screenbox.Core.ViewModels;` and `using Windows.Storage;`.

- [ ] **Step 6: Add the ViewModel method**

In `Screenbox.Core/ViewModels/FolderViewPageViewModel.cs`, add the dependencies `IArtworkService` and
`IDatabaseService` to the constructor, then add:

```csharp
    /// <summary>
    /// Persists an edited folder title and, when supplied, a new manual poster.
    /// An empty title clears the custom title so the folder name is used again.
    /// </summary>
    public async Task ApplyFolderEditAsync(StorageFolder folder, string title, StorageFile? poster)
    {
        FolderMetadataDto metadata =
            await _databaseService.LoadFolderMetadataAsync(folder.Path)
            ?? new FolderMetadataDto { Path = folder.Path };

        metadata.CustomTitle = string.IsNullOrWhiteSpace(title) ? null : title;
        await _databaseService.SaveFolderMetadataAsync(metadata);

        if (poster is not null)
        {
            await _artworkService.SetManualPosterAsync(folder, poster);
        }
    }
```

Add `using Screenbox.Core.Models;` to the file.

- [ ] **Step 7: Build the full app**

```bash
"C:\Program Files\Microsoft Visual Studio\18\Community\MSBuild\Current\Bin\MSBuild.exe" Screenbox/Screenbox.csproj /t:Build /p:Configuration=Debug /p:Platform=x64 /m
```

Expected: `Build succeeded`, exit code 0.

- [ ] **Step 8: Manual verification**

Right-click a folder, choose "Edit folder", set a title and pick an image. Confirm the tile updates
immediately, then restart the app and confirm both persist. Then delete the source image from disk
and restart again — the poster must still display, proving it was copied rather than referenced.

- [ ] **Step 9: Commit**

```bash
git add Screenbox/Dialogs/EditFolderDialog.xaml Screenbox/Dialogs/EditFolderDialog.xaml.cs Screenbox/Strings/en-US/Resources.resw Screenbox/Pages/FolderViewPage.xaml Screenbox/Pages/FolderViewPage.xaml.cs Screenbox.Core/ViewModels/FolderViewPageViewModel.cs
git commit -m "feat(library): add edit dialog for folder title and custom artwork"
```

---

## Verification

After all tasks:

```bash
dotnet run --project Screenbox.Core.Tests/Screenbox.Core.Tests.csproj
```

Expected: 42 passing, 0 failing.

```bash
"C:\Program Files\Microsoft Visual Studio\18\Community\MSBuild\Current\Bin\MSBuild.exe" Screenbox/Screenbox.csproj /t:Build /p:Configuration=Debug /p:Platform=x64 /m
```

Expected: exit code 0.

## Deviation from the spec

The spec names a `FolderMetadataService`. This plan realises that responsibility as the
`DatabaseService.FolderMetadata` partial class plus `ArtworkService`, rather than a separate
pass-through type that would only forward calls to `IDatabaseService`. The behaviour the spec
describes is fully covered; only the class count differs. Flag this at review if you disagree.

## Out of scope for this phase

- Watched state, episode parsing and aggregate captions. Those are phases 2, 3 and 4.
- The Settings section and online metadata. That is Phase 5.
- Applying a custom title anywhere other than the folder grid — breadcrumbs and the player still
  show on-disk names. Deliberate: retitling navigation chrome risks confusing the user about where
  files actually live.
