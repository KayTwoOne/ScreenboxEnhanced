# Personal Media Centre — Design

**Date:** 2026-09-14
**Status:** Approved (pending spec review)
**Repo:** [KayTwoOne/ScreenboxEnhanced](https://github.com/KayTwoOne/ScreenboxEnhanced) (fork of `huynhsontung/Screenbox`)

## Summary

Turn the Screenbox Videos area from a file browser into a personal media centre. Folders gain
visual and textual identity, playback gains watched state, and filenames gain structural meaning.
An optional, opt-in metadata layer can enrich all of it from online sources.

The work is split into five phases. Phases 1–3 are mutually independent and can be implemented
concurrently; phases 4–5 depend on them.

## Motivation

The Videos page currently renders every folder with an identical grey glyph. `FolderViewPage.xaml`
binds each tile's `ThumbnailSource` to `Media.Thumbnail`, and `StorageItemViewModel` only populates
`Media` for files — so folders always fall through to `PlaceholderIconSource`. A library of
seventeen series is visually indistinguishable.

Beyond artwork, the app has no concept of whether anything has been watched. A search for
`IsWatched`, `LastPlayed` or `Watched` across `Screenbox.Core` returns nothing, and
`playback_progress` stores only `location` and `position_ticks`. There is no completion flag and no
timestamp, which rules out resume prompts, watched indicators, "Continue Watching", and unwatched
counts.

## Goals

- Every folder shows meaningful artwork without manual effort, with a manual override available.
- Folders can carry a display title distinct from their on-disk name.
- Playback records whether media was finished and when it was last played.
- Episode files sort and display by parsed season/episode rather than lexical filename order.
- Folder captions convey useful aggregate information.
- Online metadata enrichment is available but **disabled by default** and confined to Settings.

## Non-goals

- Renaming or moving any file on disk. All naming is display-only.
- Transcoding, streaming to other devices, or library sharing.
- Replacing the existing playlist or queue systems.
- Music library parity. This work targets video; music may reuse the services later.

## Constraints discovered

These shaped the design and must be respected by implementers.

### Persistence boundaries are load-bearing

`DatabaseService.Schema.cs` defines `EnsureReplaceableTable`, which **drops and recreates** a table
whenever its column set drifts from the expected list. `library_folders`, `media_records` and
`playback_progress` are all registered this way. `playlists` and `playlist_items` use a separate,
careful path.

Any table holding user-authored data must therefore use the durable pattern. Adding a column to a
replaceable table silently destroys its contents on next launch.

### The app has no broad filesystem access

`Package.appxmanifest` declares only `internetClient`, `backgroundMediaPlayback`, `videosLibrary`,
`musicLibrary`, `privateNetworkClientServer`, `picturesLibrary` and `removableStorage`. There is no
`broadFileSystemAccess`.

Referencing an artwork file by absolute path would require a `FutureAccessList` token per folder.
That list is capped at 1000 entries and breaks if the user moves the source image. Artwork is
therefore **copied into application local storage**, and only a bare filename is persisted.

### Series folders contain no video files

Nesting is the norm, not the exception:

```
Anime/Attack On Titan/Attack on Titan Season 1/Extras/
Anime/Jujutsu Kaisen/Season 1/
Anime/Mob Psycho 100/Season 3/
```

The series-level folder — precisely the one most in need of artwork — typically holds only
subdirectories. Auto-poster generation must therefore recurse depth-first to locate a video file,
with a bounded depth to avoid pathological trees.

### No artwork conventions exist in practice

A scan of the reference library found zero `.jpg` or `.png` files at any depth. `poster.jpg` /
`folder.jpg` convention detection is still specified because it is cheap and benefits users who
scrape externally, but it is not the mechanism that solves the default case.

## Persistence design

Three schema changes. The split between durable and cache is deliberate: a failed or changed scrape
must never be able to destroy something the user authored by hand.

### `folder_metadata` — durable

User-authored, irreplaceable. Registered with the careful migration path used by `playlists`,
never with `EnsureReplaceableTable`.

```sql
CREATE TABLE IF NOT EXISTS folder_metadata (
    path          TEXT PRIMARY KEY,
    custom_title  TEXT,
    poster_file   TEXT,
    poster_source INTEGER NOT NULL DEFAULT 0,
    provider_pin  TEXT,
    sort_order    INTEGER
);
```

- `poster_file` is a filename within `ApplicationData.Current.LocalFolder\Artwork\`, never an
  absolute path.
- `poster_source` is an enum: `0` none, `1` auto-frame, `2` manual, `3` convention, `4` scraped.
  It exists so a refresh can regenerate auto-derived art while leaving manual choices untouched.
- `provider_pin` records a confirmed external match (e.g. `anilist:21`) so a corrected match is
  never re-guessed.

### `folder_scrape_cache` — replaceable

Everything refetchable from the network. `EnsureReplaceableTable` is appropriate here.

```sql
CREATE TABLE IF NOT EXISTS folder_scrape_cache (
    path           TEXT PRIMARY KEY,
    provider       TEXT,
    synopsis       TEXT,
    year           INTEGER,
    scraped_poster TEXT,
    episode_titles TEXT,
    last_scraped   INTEGER
);
```

### `playback_progress` — promoted to durable

**This is an approved, deliberate deviation from `AGENTS.md`.**

The repository currently documents `playback_progress` as rebuildable cache. It is not. A resume
position cannot be regenerated from any other source — it is a record of something the user did.
It is also the only "cache" table whose loss is user-visible and irreversible.

The table is therefore promoted to the durable pattern and extended additively:

```sql
ALTER TABLE playback_progress ADD COLUMN completed    INTEGER NOT NULL DEFAULT 0;
ALTER TABLE playback_progress ADD COLUMN last_played  INTEGER;
```

Implementers **must not** achieve this by adding columns to the `EnsureReplaceableTable`
registration, which would drop every existing resume position on first launch. The migration must:

1. Detect the pre-migration schema (columns `location`, `position_ticks` only).
2. Apply the two `ALTER TABLE` statements in a transaction, preserving all rows.
3. Move the table's registration to the durable path so future drift is migrated, not dropped.

`AGENTS.md` must be updated in the same change to document the new boundary and the reason.

## Architecture

New components follow the repository's existing Views / ViewModels / Contexts / Coordinators /
Services layering.

| Component | Layer | Responsibility |
|---|---|---|
| `FolderMetadataService` | Service | CRUD over `folder_metadata` |
| `ArtworkService` | Service | Recursive frame capture, manual copy, convention lookup, cache eviction |
| `EpisodeInfoParser` | Helper | Pure function: filename to structured episode info |
| `WatchStateService` | Service | Completion threshold, last-played, unwatched aggregation |
| `IMetadataProvider` | Service | Interface implemented per source |
| `MetadataScrapeCoordinator` | Coordinator | Matching, rate limiting, cache lifetime, settings gate |
| `FolderMetadataDialogViewModel` | ViewModel | Edit artwork and title |

`EpisodeInfoParser` is deliberately a pure, dependency-free static helper. It carries the highest
risk of subtle incorrectness and is the cheapest thing in the design to test exhaustively.

### ViewModel changes

- `StorageItemViewModel` gains `Thumbnail`, `DisplayName`, `WatchProgress`, `IsWatched`,
  `UnwatchedCount`. `DisplayName` falls back to `Name` when no custom title is set.
- `MediaViewModel` exposes `IsWatched` and `ResumePosition`.
- `HomePageViewModel` gains a `ContinueWatching` collection alongside the existing `Recent`.

### View changes

`FolderViewPage.xaml` binds `ThumbnailSource` to the new `StorageItemViewModel.Thumbnail` and
`Title` to `DisplayName`. The existing `PlaceholderIconSource` glyph is retained as the fallback
for folders with no resolvable artwork.

New UI uses the app's existing Fluent tokens, `{ThemeResource}` brushes and
`x:Bind` conventions. No new fonts, palettes or bespoke card styling are introduced; the visual
improvement comes from real poster artwork at correct aspect ratio replacing uniform grey glyphs.

## Episode parsing

### Reference corpus

Drawn verbatim from a real library. These are the acceptance cases.

| Filename | Expected result |
|---|---|
| `Death Note - 01x02.mkv` | S1 E2 |
| `[Starbez] Gachiakuta - S01E02 (BD 1080p HEVC Opus) [Dual Audio] [03153BDA].mkv` | S1 E2 |
| `[sam] Chainsaw Man - 02v2 [BD 1080p FLAC] [0BB54194].mkv` | E2, version 2 |
| `[Anime Time] Attack on Titan - 01.mkv` | E1 |
| `Jujutsu Kaisen - 001 - Ryoumen Sukuna.mkv` | E1, title `Ryoumen Sukuna` |
| `[DB]Kimi no Na wa._-_(Dual Audio_10bit_BD1080p_x265).mkv` | Movie, no episode |
| `[Judas] Chainsaw Man – The Movie Reze Arc.mkv` | Movie, no episode (en-dash separator) |
| `[sam] Chainsaw Man - NCED 01 [BD 1080p FLAC] [0719C366].mkv` | Special (NCED 1), excluded from episode count |

### Algorithm

1. Strip the file extension.
2. Strip a leading release-group prefix: a `[...]` group at position zero, with or without a
   following space.
3. Strip trailing tag groups: `[...]` and `(...)` occurring after the last episode-bearing segment.
   An 8-character hexadecimal group is always a CRC and always stripped.
4. Normalise separators: underscores to spaces; en-dash (`–`) and em-dash (`—`) to hyphen; collapse
   runs of whitespace.
5. Classify specials by marker: `NCOP`, `NCED`, `OAD`, `OVA`, `SP`, `Special`, `Extra`. A file in a
   directory named `Extras`, `NCOP & NCED`, `Specials` or `OAD` is a special regardless of filename.
6. Match episode patterns in priority order, first match wins:
   - `S(\d+)E(\d+)` — season and episode
   - `(\d+)x(\d+)` — season and episode
   - separator followed by digits: ` - (\d+)` optionally followed by `v(\d+)`
   - a bare trailing number **only** where preceded by a hyphen separator
7. If a third `-`-delimited segment follows the episode number, treat it as the episode title.
8. If no episode number matches, classify as a movie or standalone.

### Ambiguity traps

Two real cases in the reference library break naive trailing-number matching, and both must be
covered by tests:

- `Mob Psycho 100` — the `100` is part of the series name. A folder or file named this way must not
  be read as episode 100.
- `Jujutsu Kaisen 0` — a film, not episode zero.

This is why step 6 requires a hyphen separator before a bare number rather than matching any
trailing digits. Series names ending in numerals are common enough that the stricter rule is
correct even though it declines to parse some legitimately loose naming.

## Phases

```
Phase 1  Folder identity      ─┐
Phase 2  Watched state        ─┼─ mutually independent, implement concurrently
Phase 3  Episode parsing      ─┘
                               ↓ QA gate
Phase 4  Aggregate captions   ─┬─ mutually independent
Phase 5  Optional metadata    ─┘
                               ↓ QA gate
```

### Phase 1 — Folder identity

`folder_metadata` table, `FolderMetadataService`, `ArtworkService`,
`StorageItemViewModel.Thumbnail` and `DisplayName`, the edit dialog, and `FolderViewPage.xaml`
binding changes.

Artwork resolution order: manual override, then scraped poster, then `poster.jpg` / `folder.jpg`
convention, then recursive first-video frame, then the existing glyph.

### Phase 2 — Watched state

`playback_progress` migration and promotion, `WatchStateService`, completion threshold applied in
the playback pipeline, watched indicators and progress bars on tiles, and the `ContinueWatching`
collection on the home page.

Default completion threshold is 90% of duration, configurable in Settings.

### Phase 3 — Episode parsing

`EpisodeInfoParser` and its test suite, natural episode ordering in folder and queue views, episode
title display, and specials classification. Next-episode playback follows from correct ordering.

### Phase 4 — Aggregate captions

Replaces the current `N items` folder caption with episode count, total runtime and unwatched
count. Depends on phases 1–3.

### Phase 5 — Optional online metadata

`IMetadataProvider` and three implementations, `MetadataScrapeCoordinator`, the Settings section,
and a match-confirmation flow. Depends on phases 1 and 3.

Providers:

| Provider | Coverage | API key |
|---|---|---|
| AniList | Anime, GraphQL | Not required |
| Jikan (MyAnimeList) | Anime, REST, fallback for AniList misses | Not required |
| TMDB | Live-action film and television | User supplies their own |

All network behaviour is gated behind a master toggle that is **off by default**. With the toggle
off, no provider is constructed and no outbound request is made. Folder names are only ever
transmitted after the user has explicitly enabled the feature.

Unmatched folders — the reference library contains `hayase-cache`, which matches nothing — must
surface a clear "no match" state with a manual search option, never a wrong match silently applied.

## Settings surface

A new "Media library" section on the existing Settings page.

| Setting | Default |
|---|---|
| Watched threshold | 90% |
| Show watched indicators | On |
| Online metadata (master toggle) | **Off** |
| AniList / Jikan / TMDB provider checkboxes | Off, hidden until master toggle is on |
| TMDB API key | Empty |
| Scan library now | Action, disabled while master toggle is off |
| Clear cached artwork | Action |

"Clear cached artwork" removes auto-generated and scraped posters from local storage and resets
their `poster_source` rows. It must not delete manually chosen artwork.

## Testing

Automated tests run through `Screenbox.Core.Tests` (TUnit, `net10.0-windows10.0.26100.0`):

```bash
dotnet run --project Screenbox.Core.Tests/Screenbox.Core.Tests.csproj
```

- `EpisodeInfoParser` is covered exhaustively against the reference corpus above, including both
  ambiguity traps. This is pure logic with no dependencies and should reach full branch coverage.
- `FolderMetadataService` and `WatchStateService` are tested against a temporary SQLite file,
  including the `playback_progress` migration path: a database seeded with pre-migration rows must
  retain every row and position after migration. This test is mandatory and is the single most
  important regression guard in the change.
- Metadata providers are exercised through `IMetadataProvider` fakes. No test performs network I/O.
- XAML-dependent tests require the UWP Unit Test App runner with `[UITestMethod]` per the
  repository's testing constraints.

Manual verification against the reference library, per phase:

1. Folders show distinct artwork; a manual override survives an app restart and a library refresh.
2. A partially watched episode shows progress; finishing it marks it watched; it appears under
   Continue Watching.
3. Episodes order 1, 2, … 10, 11 rather than 1, 10, 11, 2. Extras are excluded from counts.
4. Folder captions report plausible episode counts and runtimes.
5. With metadata disabled, no network request is issued. Enabling it populates matches and reports
   failures rather than guessing.

## Toolchain requirements

Implementation cannot be verified without these. As of 2026-09-14 the development machine has the
.NET 10 SDK (10.0.401, user-scope) but **neither** of the following:

- **Windows SDK 10.0.26100** — required by CsWinRT, which fails with
  `Could not find the Windows SDK in the registry`. Needed to build and test `Screenbox.Core`.
- **Visual Studio 2026 (v18)** with the Universal Windows Platform workload — required to build and
  run the `Screenbox` app project. `dotnet build` is explicitly unsupported for UWP projects.

`gh` CLI is also absent and is assumed by the repository's pull request conventions.

## Risks

| Risk | Mitigation |
|---|---|
| Migration drops existing resume positions | Mandatory migration test seeded with pre-migration rows |
| Frame capture is slow across a large library | Generate lazily on first view, cache to local storage, bound recursion depth |
| Parser misreads series names ending in digits | Hyphen-separator rule plus explicit tests for both known traps |
| Scraper applies a wrong match silently | Confirmation flow, `provider_pin` on confirmed matches, explicit no-match state |
| Artwork cache grows unbounded | Eviction on library removal, plus a Settings action to clear it |
| Concurrent phase work causes merge conflicts | Phases 1–3 touch disjoint files; schema changes are additive and separately reviewed |

## Defaults chosen, revisitable after first run

- `ContinueWatching` sits **above** the existing `Recent` row rather than replacing it. Recent and
  Continue Watching answer different questions, and preserving `Recent` keeps the change additive.
  Revisit only if the two rows prove visually redundant in the running app.
- Auto-poster composition: a single representative frame versus a composite of several. Single
  frame ships first; composition is a later refinement if the result looks poor.
