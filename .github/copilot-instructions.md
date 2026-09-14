# GitHub instructions

- Use conventional commit format for clear and concise commit messages.
- Use conventional prefixes for pull request titles to summarize changes effectively.
- Reference relevant issues or user stories in the pull requests.
- When describing changes, include the context, motivation, and impact of the changes. Do not just list the changes made.
- **GitHub CLI (`gh`) on Windows**: When creating or updating pull requests, issues, or releases via `gh` CLI, **always pass `--body-file` (or `-F`)** pointing to a markdown file instead of inline `--body` / `-b`. Inline markdown strings in Windows shells frequently suffer from broken quoting, mangled newlines, and character escape artifacts.

## Build and test

- **Always use MSBuild from Visual Studio 2026 (version 18)** for restoring and compiling projects. **NEVER use `dotnet build`**, as UWP and WinRT tooling requires Visual Studio 2026 MSBuild.
- MSBuild can be located via `vswhere` (`& "${env:ProgramFiles(x86)}\Microsoft Visual Studio\Installer\vswhere.exe" -version "[18.0,19.0)" -latest -requires Microsoft.Component.MSBuild -find MSBuild\**\Bin\MSBuild.exe`) or standard path (`"C:\Program Files\Microsoft Visual Studio\18\Community\MSBuild\Current\Bin\MSBuild.exe"`).
- Restore the solution: `msbuild Screenbox.slnx -t:restore -p:Platform=x64 -p:Configuration=Debug`
- Build `Screenbox.Core`: `msbuild Screenbox.Core/Screenbox.Core.csproj /t:Build /p:Configuration=Debug /p:Platform=x64 /m`
- Restore the app project before building it if needed: `msbuild Screenbox/Screenbox.csproj /p:Configuration=Debug /p:Platform=x64 /t:Restore /m`
- Build the app project: `msbuild Screenbox/Screenbox.csproj /p:Configuration=Debug /p:Platform=x64 /t:Build /m`
- Run the full automated test suite: `dotnet run --project Screenbox.Core.Tests/Screenbox.Core.Tests.csproj`

## Persistence boundaries

- Treat `library_folders`, `media_records`, and `playback_progress` as rebuildable cache data.
- Treat `playlists`, `playlist_items`, and `folder_metadata` as durable user data and preserve them during schema changes or recovery work.
- `folder_metadata` holds custom folder titles and manual poster choices. Nothing can reconstruct them: the folders themselves carry no record of the user's edit, so a dropped row is permanent data loss, not a cache miss that the next library scan repairs.
- Prefer per-table recovery or migration over recreating the whole database file when durable data is involved.
- **What protects `folder_metadata`**: `EnsureFolderMetadataTable` migrates the table rather than dropping it. Missing columns are added with `ALTER TABLE ... ADD COLUMN`; drift that cannot be expressed additively rebuilds the table by copying the existing rows across. Add columns by extending `FolderMetadataColumns`, which drives both the CREATE and the ALTER statements. Never register the table with `EnsureReplaceableTable`, and do not copy the shape of `EnsurePlaylistsTable` or `EnsurePlaylistItemsTable` — despite the "durable" label on playlists, both of those **do** drop their table on drift.
- **What does not protect it**: `RecreateDatabaseFile` still deletes `screenbox.db` wholesale when the schema cannot be opened or migrated at all (`SqliteException` or `IOException` during init). That is pre-existing app-wide corruption recovery and it takes `folder_metadata` and `playlists` with it. Durable here means "survives schema changes", not "survives a corrupt database file". There is no backup or export today.
- For library cache refreshes, prefer replacing the cached rows for the affected media type over incremental stale-record cleanup logic unless there is a clear performance need.
- `folder_metadata` is the exception to the line above: its rows are keyed on absolute path and nothing rebuilds them, so they are evicted explicitly. `IArtworkService.PruneOrphanedArtworkAsync` runs after a video library cache write and removes rows (and their artwork files) only for folders that have disappeared from beneath a library root that is currently readable. Paths outside every scanned root, and paths under an offline drive or network share, are deliberately kept.
