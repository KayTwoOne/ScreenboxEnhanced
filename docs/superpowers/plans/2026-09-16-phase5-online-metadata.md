# Phase 5: Optional Online Metadata — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Let the user opt in to enriching their library with real poster art, synopses and year data pulled from online databases, matched by folder name.

**Architecture:** Providers sit behind `IMetadataProvider` so none of them is reachable without going through a coordinator that checks a settings gate. Results land in a replaceable `folder_scrape_cache` table, kept separate from the durable `folder_metadata` so a scrape can never destroy something the user set by hand. Nothing touches the network until the user turns it on.

**Tech Stack:** C# 14, .NET 10 (`net10.0-windows10.0.26100.0`), UWP with `UseUwp=true`, `System.Net.Http`, `System.Text.Json` with source generation, Microsoft.Data.Sqlite, TUnit.

**Spec:** `docs/superpowers/specs/2026-09-14-personal-media-centre-design.md` (Phase 5)

**Depends on:** Phase 1 (`folder_metadata`, `ArtworkService`) and Phase 3 (`EpisodeInfoParser`), both complete.

## The non-negotiable constraint

**This feature is off by default and must make no network request until the user explicitly enables it in Settings.** That is not a preference — it is the basis on which the design was accepted. A folder name is user data; sending it to a third party without consent is not acceptable.

Concretely:
- The master toggle defaults to **off**.
- With the toggle off, no provider is constructed and no `HttpClient` is created.
- A test must prove that a coordinator with the feature disabled performs zero provider calls.

## Two things this codebase has never had

1. **No `HttpClient` anywhere.** `grep -rn "HttpClient" --include=*.cs` returns nothing today. This phase introduces the app's first network layer, so be conservative: one shared client, explicit timeouts, cancellation support, and no retry storms.
2. **No external API integration.** `Secrets.cs` holds only AppCenter and Sentry telemetry keys. AniList and Jikan need no key. TMDB needs one the **user** supplies in Settings — never hardcode a key, never commit one.

The `internetClient` capability is already declared in `Package.appxmanifest`, so no manifest change is needed.

## Global Constraints

- Build with MSBuild from Visual Studio 2026 only. **Never `dotnet build`.** Path: `C:\Program Files\Microsoft Visual Studio\18\Community\MSBuild\Current\Bin\MSBuild.exe`, argument form `-t:Build -p:Configuration=Debug -p:Platform=x64`.
- Close the running app before building — it locks `Screenbox.Core.dll` (MSB3027).
- Before building the app project, put vswhere on PATH: prepend `${env:ProgramFiles(x86)}\Microsoft Visual Studio\Installer`.
- Run tests with `dotnet run --project Screenbox.Core.Tests/Screenbox.Core.Tests.csproj`. **Run five times** — this suite had a thread-scheduling flake and one green run is insufficient.
- **No test may make a real network request.** Providers are exercised through fakes or a stubbed `HttpMessageHandler`. A test suite that depends on a third-party API being up is not a test suite.
- `folder_scrape_cache` is **replaceable cache** — `EnsureReplaceableTable` is correct for it. `folder_metadata` is durable and must keep its additive migration. Never let a scrape write over `CustomTitle` or a manual poster.
- The project is AOT-compatible (`IsAotCompatible`). Use `System.Text.Json` **source generation** via the existing `CoreJsonContext` pattern — reflection-based serialization will warn or fail under trimming.
- Localized strings go ONLY in `Screenbox/Strings/en-US/Resources.resw`.
- ViewModels must never reference localized strings.
- An unhandled exception reaching an `async void` handler terminates this app with no crash log.
- Never put `x:Bind` inside a XAML resource nothing references.
- Nullable reference types enabled: `is null` / `is not null`.
- This is UWP: `Windows.UI.Xaml`, never `Microsoft.UI.Xaml`.
- Conventional Commits. Branch: `feat/personal-media-centre`.
- Do not write scratch files into the repository root.

## File Structure

| File | Responsibility |
|---|---|
| `Screenbox.Core/Models/FolderScrapeCacheDto.cs` | Cached scrape result |
| `Screenbox.Core/Models/MetadataMatch.cs` | One provider result |
| `Screenbox.Core/Services/DatabaseService.ScrapeCache.cs` | SQL persistence |
| `Screenbox.Core/Services/IMetadataProvider.cs` | Provider contract |
| `Screenbox.Core/Services/AniListMetadataProvider.cs` | GraphQL, no key |
| `Screenbox.Core/Services/JikanMetadataProvider.cs` | REST fallback, no key |
| `Screenbox.Core/Services/TmdbMetadataProvider.cs` | User-supplied key |
| `Screenbox.Core/Helpers/SeriesNameNormalizer.cs` | Folder name → search query |
| `Screenbox.Core/Services/IMetadataProviderFactory.cs` | Builds providers only after the gate passes |
| `Screenbox.Core/Coordinators/MetadataScrapeCoordinator.cs` | Gate, order, cache, apply |

## HttpClient lifetime — decide this once, in the factory

Do **not** register `HttpClient` as a DI singleton, and do not give each provider its own. Both are wrong here for the same reason: a client registered in DI is constructed during app startup, which breaks the guarantee that nothing network-capable exists until the user opts in.

Instead, `MetadataProviderFactory` owns a single lazily-created `HttpClient`, created on first use **after** the settings gate has passed, and shared by whichever providers are enabled. Give it an explicit timeout (10 seconds is reasonable) and a descriptive `User-Agent` — Jikan asks for one, and it is good manners for AniList too. The factory should be `IDisposable` and dispose the client.

One shared client across the three providers is correct: it gives consistent timeouts and connection pooling, and there is never a moment where an unused provider holds an idle socket.

---

### Task 1: `folder_scrape_cache` schema and persistence

**Files:**
- Create: `Screenbox.Core/Models/FolderScrapeCacheDto.cs`
- Create: `Screenbox.Core/Services/DatabaseService.ScrapeCache.cs`
- Modify: `Screenbox.Core/Services/DatabaseService.Schema.cs`
- Modify: `Screenbox.Core/Services/IDatabaseService.cs`
- Test: `Screenbox.Core.Tests/Database/FolderScrapeCacheTests.cs`

**Interfaces:**
- Consumes: `DatabaseService`, `TestDirectoryFixture`, and the existing `EnsureReplaceableTable` helper.
- Produces: `FolderScrapeCacheDto { string Path; string? Provider; string? ProviderId; string? Synopsis; int? Year; string? ScrapedPoster; DateTimeOffset? LastScraped }` and on `IDatabaseService`: `SaveScrapeCacheAsync`, `LoadScrapeCacheAsync`, `ClearScrapeCacheAsync`.

Unlike `folder_metadata`, this table **is** disposable — everything in it can be refetched. Register it with `EnsureReplaceableTable`, which is the correct helper for cache tables and is already used for `library_folders`, `media_records` and `playback_progress`.

- [ ] **Step 1: Write the failing test**

Create `Screenbox.Core.Tests/Database/FolderScrapeCacheTests.cs` covering: a full round-trip of every field; `LoadScrapeCacheAsync` returning null for an unknown path; a save overwriting an existing row rather than duplicating; `ClearScrapeCacheAsync` emptying the table; and — most importantly — that clearing the scrape cache leaves `folder_metadata` untouched:

```csharp
    [Test]
    public async Task ClearScrapeCacheAsync_DoesNotTouchDurableFolderMetadata()
    {
        using var fixture = new TestDirectoryFixture();
        DatabaseService db = await CreateServiceAsync(fixture.DirectoryPath);
        await db.SaveFolderMetadataAsync(new FolderMetadataDto
        {
            Path = @"D:\Anime\Dan Da Dan",
            CustomTitle = "Dandadan",
            PosterFile = "m_abc.png",
            PosterSource = PosterSource.Manual
        });
        await db.SaveScrapeCacheAsync(new FolderScrapeCacheDto
        {
            Path = @"D:\Anime\Dan Da Dan",
            Provider = "anilist",
            Synopsis = "..."
        });

        await db.ClearScrapeCacheAsync();

        FolderMetadataDto? durable = await db.LoadFolderMetadataAsync(@"D:\Anime\Dan Da Dan");
        await Assert.That(durable).IsNotNull();
        await Assert.That(durable!.CustomTitle).IsEqualTo("Dandadan");
        await Assert.That(durable.PosterFile).IsEqualTo("m_abc.png");
    }
```

That test is the point of the two-table split. Do not remove or weaken it.

- [ ] **Step 2: Run it and confirm it fails.** Expected: compile failure, the DTO and methods do not exist.

- [ ] **Step 3: Implement**, following the shape of `DatabaseService.FolderMetadata.cs` exactly — same parameter binding style, same `DBNull` handling in both directions, same `ReadRow` pattern. Add the SQL constant next to the others and register it with `EnsureReplaceableTable(connection, "folder_scrape_cache", CreateFolderScrapeCacheSql, "path", "provider", "provider_id", "synopsis", "year", "scraped_poster", "last_scraped");`, and add it to `RecreateDatabaseFile`.

- [ ] **Step 4: Run the suite five times.** Expected 127 + your tests.

- [ ] **Step 5: Commit** — `feat(metadata): add replaceable folder scrape cache table`

---

### Task 2: Provider contract and series-name normalizer

**Files:**
- Create: `Screenbox.Core/Models/MetadataMatch.cs`
- Create: `Screenbox.Core/Services/IMetadataProvider.cs`
- Create: `Screenbox.Core/Helpers/SeriesNameNormalizer.cs`
- Test: `Screenbox.Core.Tests/Helpers/SeriesNameNormalizerTests.cs`

**Interfaces:**
- Consumes: nothing. Both are dependency-free by design.
- Produces: `MetadataMatch` record with `string ProviderId`, `string Title`, `string? Synopsis`, `int? Year`, `string? PosterUrl`, `double Confidence`; `IMetadataProvider` with `string Name { get; }` and `Task<IReadOnlyList<MetadataMatch>> SearchAsync(string query, CancellationToken ct)`; and `SeriesNameNormalizer.Normalize(string folderName)` returning a search query.

The normalizer matters more than it looks. Folder names in a real library are not clean search terms, and a bad query produces a confident wrong match — worse than no match.

Test corpus, drawn from the reference library:

| Folder | Query |
|---|---|
| `Attack On Titan` | `Attack On Titan` |
| `Attack on Titan Season 1` | `Attack on Titan` |
| `Season 1` | *(empty — a bare season folder is not searchable; caller should use the parent)* |
| `Jujutsu Kaisen 0` | `Jujutsu Kaisen 0` |
| `Mob Psycho 100` | `Mob Psycho 100` |
| `Dandadan 2nd Season` | `Dandadan` |
| `hayase-cache` | `hayase-cache` *(will match nothing — that is correct)* |
| `NCOP & NCED` | *(empty — a specials folder is not a series)* |
| `Extras` | *(empty)* |

Note `Mob Psycho 100` and `Jujutsu Kaisen 0` keep their trailing numbers: those are part of the title. This is the same trap Phase 3's parser has tests for, and the normalizer must not "helpfully" strip them.

- [ ] **Step 1: Write the failing tests** covering every row above, plus: empty input returns empty; a folder named only whitespace returns empty.

- [ ] **Step 2: Run and confirm failure.**

- [ ] **Step 3: Implement.** Strip a trailing `Season N` / `Nth Season` suffix, strip known specials-folder names to empty, trim, collapse whitespace. Do not strip trailing digits generally.

- [ ] **Step 4: Run the suite five times.**

- [ ] **Step 5: Commit** — `feat(metadata): add provider contract and series name normalizer`

---

### Task 3: AniList provider

**Files:**
- Create: `Screenbox.Core/Services/AniListMetadataProvider.cs`
- Create: `Screenbox.Core/Models/Serialization/AniListModels.cs`
- Modify: the existing JSON source-generation context
- Test: `Screenbox.Core.Tests/Services/AniListMetadataProviderTests.cs`

**Interfaces:**
- Consumes: `IMetadataProvider`, `MetadataMatch` from Task 2.
- Produces: `AniListMetadataProvider(HttpClient client)` implementing `IMetadataProvider` with `Name => "anilist"`.

AniList is the primary provider for this library: public GraphQL endpoint at `https://graphql.anilist.co`, **no API key**, and far better anime coverage than TMDB.

**Testing rule — no test may hit the network.** Construct the provider with an `HttpClient` backed by a stubbed `HttpMessageHandler` that returns canned JSON. Cover: a normal result set mapping correctly; an empty result set returning an empty list, not null; a malformed JSON body being handled without throwing; a non-200 status returning empty rather than throwing; and cancellation propagating.

- [ ] **Step 1: Write the failing tests** with a `StubHttpMessageHandler` test helper placed in `Screenbox.Core.Tests/Helpers/`.

- [ ] **Step 2: Run and confirm failure.**

- [ ] **Step 3: Implement.** Query `Page.media(search:$q, type:ANIME)` requesting `id`, `title { romaji english }`, `description`, `startDate { year }`, `coverImage { large }`. Map to `MetadataMatch`; prefer the English title when present, else romaji. Strip HTML from `description` — AniList returns markup in it. Set a timeout and honour the `CancellationToken`. Never let an exception escape: log and return empty.

Verify the exact response shape against AniList's live schema documentation before finalising the models — do not guess field names. If the shape differs from the above, follow the real schema and note it in your report.

- [ ] **Step 4: Run the suite five times**, and confirm no test performs real I/O.

- [ ] **Step 5: Commit** — `feat(metadata): add AniList provider`

---

### Task 4: Jikan and TMDB providers

**Files:**
- Create: `Screenbox.Core/Services/JikanMetadataProvider.cs`
- Create: `Screenbox.Core/Services/TmdbMetadataProvider.cs`
- Create: their serialization models
- Test: one test file per provider

**Interfaces:**
- Consumes: `IMetadataProvider`, `MetadataMatch`.
- Produces: `JikanMetadataProvider(HttpClient)` with `Name => "jikan"`; `TmdbMetadataProvider(HttpClient, string apiKey)` with `Name => "tmdb"`.

Jikan (`https://api.jikan.moe/v4/anime?q=`) needs no key and serves as the fallback when AniList returns nothing. TMDB needs a key the **user** supplies — never hardcode one, never commit one, and `TmdbMetadataProvider` must return empty immediately when the key is null or empty rather than issuing a request that will 401.

**Testing rule — no test may make a real network request.** Build each provider on an `HttpClient` backed by the `StubHttpMessageHandler` test helper, returning canned JSON.

Required cases for EACH of the two providers:
1. A normal response maps to `MetadataMatch` values correctly — title, year, synopsis and poster URL all populated from the right fields.
2. An empty result set returns an empty list, never null.
3. A malformed JSON body returns empty without throwing.
4. A non-200 status (use 500) returns empty without throwing.
5. A cancelled `CancellationToken` propagates rather than being swallowed.

One additional case for TMDB only:
6. An empty or null API key returns empty and issues **zero** HTTP calls. Assert the stub handler's call count is 0 — a provider that fires a request destined to 401 is wrong.

- [ ] **Step 1: Write the failing tests** for both providers, covering every case above.
- [ ] **Step 2: Run the suite and confirm they fail** for the right reason — the provider types do not exist.
- [ ] **Step 3: Implement both providers.** Verify each API's real response shape against its current documentation before finalising the serialization models — do not guess field names. Jikan v4 returns its results under a `data` array; TMDB returns `results`. Use `System.Text.Json` source generation, not reflection, because the project is AOT-compatible.
- [ ] **Step 4: Run the suite five times** and confirm no test performs real I/O.
- [ ] **Step 5: Commit** — `feat(metadata): add Jikan and TMDB providers`

---

### Task 5: Scrape coordinator with the settings gate

**Files:**
- Create: `Screenbox.Core/Coordinators/MetadataScrapeCoordinator.cs`
- Create: `Screenbox.Core/Services/IMetadataScrapeCoordinator.cs`
- Modify: `Screenbox.Core/Services/ISettingsService.cs` and its implementation
- Modify: `Screenbox.Core/Common/ServiceHelpers.cs`
- Test: `Screenbox.Core.Tests/Coordinators/MetadataScrapeCoordinatorTests.cs`

**Interfaces:**
- Consumes: `IMetadataProvider` implementations, `SeriesNameNormalizer`, `IDatabaseService` scrape-cache methods from Task 1, `IArtworkService` from Phase 1, `ISettingsService`.
- Produces: `IMetadataScrapeCoordinator` with `Task<MetadataMatch?> ScrapeFolderAsync(string folderPath, string folderName, CancellationToken ct)` and `Task ScrapeLibraryAsync(IProgress<int>? progress, CancellationToken ct)`.

New settings, all defaulting to off/empty:
- `MetadataEnabled` (bool, **false**)
- `MetadataUseAniList`, `MetadataUseJikan`, `MetadataUseTmdb` (bool)
- `TmdbApiKey` (string, empty)

Follow the storage-key and default conventions of the neighbouring settings properties exactly — read them first. Remember `TestSettingsService` in the test project implements `ISettingsService` and must be updated too, or the suite will not compile.

**The tests that matter most:**

```csharp
    [Test]
    public async Task ScrapeFolderAsync_MakesNoProviderCallWhenDisabled()
    {
        // The feature is opt-in. With the master toggle off, nothing may reach a provider.
        var provider = new CountingFakeProvider();
        var settings = new TestSettingsService { MetadataEnabled = false };
        var coordinator = CreateCoordinator(settings, provider);

        MetadataMatch? result = await coordinator.ScrapeFolderAsync(@"D:\Anime\Dan Da Dan", "Dan Da Dan", default);

        await Assert.That(result).IsNull();
        await Assert.That(provider.CallCount).IsEqualTo(0);
    }

    [Test]
    public async Task ScrapeFolderAsync_DoesNotEvenConstructAProviderWhenDisabled()
    {
        // Not calling a provider is not enough. A provider's constructor allocates an HttpClient,
        // and the guarantee the user accepted is that nothing network-capable comes into existence
        // until they opt in. Resolve providers through a factory so construction itself is gated.
        var factory = new CountingProviderFactory();
        var settings = new TestSettingsService { MetadataEnabled = false };
        var coordinator = CreateCoordinatorWithFactory(settings, factory);

        await coordinator.ScrapeFolderAsync(@"D:\Anime\Dan Da Dan", "Dan Da Dan", default);

        await Assert.That(factory.ConstructionCount).IsEqualTo(0);
    }
```

The second test drives a design requirement: **providers must be obtained lazily through a factory**, not injected as already-constructed instances. If the coordinator takes `IEnumerable<IMetadataProvider>` in its constructor, DI will have built all three — and their `HttpClient`s — before the gate is ever consulted, and the guarantee is broken no matter what the gate does afterwards.

Define a small `IMetadataProviderFactory` with a method that returns the enabled providers, and have the coordinator call it only after the settings check passes.

Also cover: a cached result short-circuits without calling a provider; a disabled provider is skipped while enabled ones still run; providers are tried in order and the first non-empty result wins; a folder that matches nothing records a no-match rather than throwing; and — critically — **a scrape never overwrites a manual poster or a user's custom title**:

```csharp
    [Test]
    public async Task ScrapeFolderAsync_NeverOverwritesAManualPosterOrCustomTitle()
    {
        // folder_metadata is durable user data. A scrape may fill blanks, never clobber choices.
    }
```

Write that test against the real `folder_metadata` round-trip, not a mock.

- [ ] Follow TDD. Run the suite five times. Build `Screenbox.Core`.
- [ ] **Commit** — `feat(metadata): add scrape coordinator behind an opt-in settings gate`

---

### Task 6: Settings UI and applying results

**Files:**
- Modify: `Screenbox/Pages/SettingsPage.xaml` and its ViewModel
- Modify: `Screenbox/Strings/en-US/Resources.resw`
- Modify: `Screenbox.Core/Services/ArtworkService.cs` (download and store a scraped poster)

**Interfaces:**
- Consumes: `IMetadataScrapeCoordinator` from Task 5; `IArtworkService` from Phase 1.
- Produces: no new public API beyond an artwork method for storing a downloaded image.

The Settings section lives under the existing Libraries grouping, built with the same `ctc:SettingsCard` / `ctc:SettingsExpander` controls the page already uses. Read the existing markup and match it — no new styling, no new colours or fonts.

Layout:
- **Online metadata** — master toggle, default off. Everything below is disabled or hidden while it is off.
- Provider checkboxes: AniList, Jikan, TMDB.
- TMDB API key — a `PasswordBox`, empty by default, with a one-line note that the user supplies their own key.
- **Scan library now** — a button, disabled while the master toggle is off, showing progress while running.

Scraped posters go through `IArtworkService` and land in local storage with `PosterSource.Scraped`, exactly like every other artwork source. `ArtworkResolver` already ranks Manual above Scraped — Phase 1 built and tested that precedence, so a scraped poster can never displace a manual one.

- [ ] **Step 1: Add the strings** to `en-US` only.
- [ ] **Step 2: Add the settings properties** to the Settings ViewModel, mirroring existing ones.
- [ ] **Step 3: Add the markup**, matching the page's existing card/expander structure.
- [ ] **Step 4: Add poster download** to `ArtworkService` — fetch the URL, store it, record `PosterSource.Scraped`. Guard it: a failed download must not throw, and must leave any existing artwork intact.
- [ ] **Step 5: Wire the scan button**, with nothing escaping into an `async void` handler.
- [ ] **Step 6: Build the full app.**
- [ ] **Step 7: Manual verification.**
  - With the toggle **off**, use a network monitor or provider logging to confirm **zero outbound requests** during a full library browse. This is the constraint the whole design rests on — verify it, do not assume it.
  - Turn it on, enable AniList, scan. Confirm real posters appear for matched series.
  - Confirm `hayase-cache` reports no match rather than silently taking a wrong one.
  - Confirm a folder with a manual poster keeps it after a scan.
- [ ] **Step 8: Commit** — `feat(metadata): add opt-in online metadata settings and apply scraped artwork`

---

## Verification

Run the suite five times — all clean — and build the full app. Then the manual checks in Task 6 Step 7, especially the zero-requests-when-disabled check.

## Deliberate deviation from the spec

The spec's `folder_scrape_cache` includes an `episode_titles` column. This plan omits it, because per-episode metadata is out of scope for this phase and Phase 3's filename parser already supplies episode titles from the local files — which is more reliable than matching episode lists across providers, and works offline. If online episode titles are wanted later, adding the column is an additive change to a replaceable cache table and costs nothing.

## Out of scope

- Per-episode metadata. This phase matches at series level only.
- A manual "search and pick the right match" dialog for mismatches. The coordinator records a no-match; surfacing a picker is follow-up work.
- Rate-limit backoff beyond a simple delay between requests. If a scan of a large library trips AniList's limits, that is a follow-up.
- Scraping for music.
