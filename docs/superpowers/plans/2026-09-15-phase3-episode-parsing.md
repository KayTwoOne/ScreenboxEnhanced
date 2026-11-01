# Phase 3: Episode Parsing — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Extract season, episode, title and specials classification from filenames, so episodes sort 1, 2, … 10, 11 instead of 1, 10, 11, 2, and so extras are not counted as episodes.

**Architecture:** A pure `EpisodeInfoParser` turns a filename into a structured `EpisodeInfo`. Nothing else in the phase does any parsing — ordering and display consume the parser's output. The parser has no dependencies, so it can be tested exhaustively without a UWP host.

**Tech Stack:** C# 14, .NET 10 (`net10.0-windows10.0.26100.0`), UWP with `UseUwp=true`, TUnit.

**Spec:** `docs/superpowers/specs/2026-09-14-personal-media-centre-design.md` (Phase 3)

**Independence:** This phase shares no files with Phase 2. Both can be implemented concurrently.

## Global Constraints

- Build with MSBuild from Visual Studio 2026 only. **Never `dotnet build`.** Path: `C:\Program Files\Microsoft Visual Studio\18\Community\MSBuild\Current\Bin\MSBuild.exe`, argument form `-t:Build -p:Configuration=Debug -p:Platform=x64`.
- Close the running app before building — it locks `Screenbox.Core.dll`.
- `vswhere.exe` must be on PATH before building the app project.
- Run tests with `dotnet run --project Screenbox.Core.Tests/Screenbox.Core.Tests.csproj`. 56 currently pass.
- `EpisodeInfoParser` must contain **no I/O** — no file access, no database, no UWP types. It takes strings and returns a record.
- This is UWP: `Windows.UI.Xaml`, never `Microsoft.UI.Xaml`.
- Nullable reference types are enabled. Use `is null` / `is not null`.
- ViewModels must never reference localized strings.
- Conventional Commits. Branch: `feat/personal-media-centre`.

## File Structure

| File | Responsibility |
|---|---|
| `Screenbox.Core/Models/EpisodeInfo.cs` | Parsed result record |
| `Screenbox.Core/Helpers/EpisodeInfoParser.cs` | Pure filename → EpisodeInfo |
| `Screenbox.Core/Helpers/EpisodeComparer.cs` | Ordering built on parser output |
| `Screenbox.Core/ViewModels/FolderViewPageViewModel.cs` | Applies the ordering |

---

### Task 1: `EpisodeInfoParser`

**Files:**
- Create: `Screenbox.Core/Models/EpisodeInfo.cs`
- Create: `Screenbox.Core/Helpers/EpisodeInfoParser.cs`
- Test: `Screenbox.Core.Tests/Helpers/EpisodeInfoParserTests.cs`

**Interfaces:**
- Consumes: nothing.
- Produces: `EpisodeInfo` record with `int? Season`, `int? Episode`, `string? Title`, `int? Version`, `bool IsSpecial`, `bool IsMovie`; and `EpisodeInfoParser.Parse(string fileName, string? parentFolderName = null)` returning `EpisodeInfo`.

This is the highest-risk logic in the whole project and the cheapest to test. The corpus below comes from a real library — every row is an acceptance case, not an invented example.

#### Reference corpus

| Filename | Expected |
|---|---|
| `Death Note - 01x02.mkv` | S1 E2 |
| `[Starbez] Gachiakuta - S01E02 (BD 1080p HEVC Opus) [Dual Audio] [03153BDA].mkv` | S1 E2 |
| `[sam] Chainsaw Man - 02v2 [BD 1080p FLAC] [0BB54194].mkv` | E2, version 2 |
| `[Anime Time] Attack on Titan - 01.mkv` | E1 |
| `Jujutsu Kaisen - 001 - Ryoumen Sukuna.mkv` | E1, title `Ryoumen Sukuna` |
| `[DB]Kimi no Na wa._-_(Dual Audio_10bit_BD1080p_x265).mkv` | movie, no episode |
| `[Judas] Chainsaw Man – The Movie Reze Arc.mkv` | movie (en-dash separator) |
| `[sam] Chainsaw Man - NCED 01 [BD 1080p FLAC] [0719C366].mkv` | special, not episode 1 |

#### Ambiguity traps

Two real cases break naive trailing-number matching and must both be covered:

- **`Mob Psycho 100`** — the `100` is part of the series name.
- **`Jujutsu Kaisen 0`** — a film, not episode zero.

This is why a bare number only counts as an episode when preceded by a hyphen separator. The stricter rule is correct even though it declines to parse some loose naming: mislabelling a series as episode 100 is worse than leaving it unparsed.

- [ ] **Step 1: Write the failing test**

Create `Screenbox.Core.Tests/Helpers/EpisodeInfoParserTests.cs`:

```csharp
using Screenbox.Core.Helpers;
using Screenbox.Core.Models;

namespace Screenbox.Core.Tests.Helpers;

public sealed class EpisodeInfoParserTests
{
    [Test]
    public async Task Parse_SeasonXEpisodeFormat()
    {
        EpisodeInfo info = EpisodeInfoParser.Parse("Death Note - 01x02.mkv");
        await Assert.That(info.Season).IsEqualTo(1);
        await Assert.That(info.Episode).IsEqualTo(2);
    }

    [Test]
    public async Task Parse_SxxExxFormatWithGroupAndTags()
    {
        EpisodeInfo info = EpisodeInfoParser.Parse(
            "[Starbez] Gachiakuta - S01E02 (BD 1080p HEVC Opus) [Dual Audio] [03153BDA].mkv");
        await Assert.That(info.Season).IsEqualTo(1);
        await Assert.That(info.Episode).IsEqualTo(2);
        await Assert.That(info.IsSpecial).IsFalse();
    }

    [Test]
    public async Task Parse_BareNumberWithVersionSuffix()
    {
        EpisodeInfo info = EpisodeInfoParser.Parse("[sam] Chainsaw Man - 02v2 [BD 1080p FLAC] [0BB54194].mkv");
        await Assert.That(info.Episode).IsEqualTo(2);
        await Assert.That(info.Version).IsEqualTo(2);
    }

    [Test]
    public async Task Parse_BareNumberWithGroupPrefix()
    {
        EpisodeInfo info = EpisodeInfoParser.Parse("[Anime Time] Attack on Titan - 01.mkv");
        await Assert.That(info.Episode).IsEqualTo(1);
    }

    [Test]
    public async Task Parse_PaddedNumberWithEpisodeTitle()
    {
        EpisodeInfo info = EpisodeInfoParser.Parse("Jujutsu Kaisen - 001 - Ryoumen Sukuna.mkv");
        await Assert.That(info.Episode).IsEqualTo(1);
        await Assert.That(info.Title).IsEqualTo("Ryoumen Sukuna");
    }

    [Test]
    public async Task Parse_MovieWithUnderscoresAndNoEpisodeNumber()
    {
        EpisodeInfo info = EpisodeInfoParser.Parse("[DB]Kimi no Na wa._-_(Dual Audio_10bit_BD1080p_x265).mkv");
        await Assert.That(info.Episode).IsNull();
        await Assert.That(info.IsMovie).IsTrue();
    }

    [Test]
    public async Task Parse_MovieWithEnDashSeparator()
    {
        EpisodeInfo info = EpisodeInfoParser.Parse("[Judas] Chainsaw Man – The Movie Reze Arc.mkv");
        await Assert.That(info.Episode).IsNull();
        await Assert.That(info.IsMovie).IsTrue();
    }

    [Test]
    public async Task Parse_NonCreditEndingIsSpecialNotEpisodeOne()
    {
        EpisodeInfo info = EpisodeInfoParser.Parse("[sam] Chainsaw Man - NCED 01 [BD 1080p FLAC] [0719C366].mkv");
        await Assert.That(info.IsSpecial).IsTrue();
    }

    [Test]
    public async Task Parse_SeriesNameEndingInNumberIsNotAnEpisode()
    {
        // "Mob Psycho 100" — the 100 belongs to the title. No hyphen separator, so no episode.
        EpisodeInfo info = EpisodeInfoParser.Parse("Mob Psycho 100.mkv");
        await Assert.That(info.Episode).IsNull();
    }

    [Test]
    public async Task Parse_FilmNamedWithATrailingZeroIsNotEpisodeZero()
    {
        // "Jujutsu Kaisen 0" is a film.
        EpisodeInfo info = EpisodeInfoParser.Parse("Jujutsu Kaisen 0.mkv");
        await Assert.That(info.Episode).IsNull();
        await Assert.That(info.IsMovie).IsTrue();
    }

    [Test]
    public async Task Parse_ParentFolderMarksItemAsSpecial()
    {
        // A file inside Extras is a special regardless of its own name.
        EpisodeInfo info = EpisodeInfoParser.Parse("Chainsaw Man - 01.mkv", parentFolderName: "Extras");
        await Assert.That(info.IsSpecial).IsTrue();
    }

    [Test]
    public async Task Parse_ParentFolderNamesCoveringKnownSpecialsFolders()
    {
        foreach (string folder in new[] { "Extras", "Specials", "NCOP & NCED", "OAD" })
        {
            EpisodeInfo info = EpisodeInfoParser.Parse("Show - 03.mkv", parentFolderName: folder);
            await Assert.That(info.IsSpecial).IsTrue();
        }
    }

    [Test]
    public async Task Parse_OrdinaryParentFolderDoesNotMarkSpecial()
    {
        EpisodeInfo info = EpisodeInfoParser.Parse("Show - 03.mkv", parentFolderName: "Season 1");
        await Assert.That(info.IsSpecial).IsFalse();
        await Assert.That(info.Episode).IsEqualTo(3);
    }

    [Test]
    public async Task Parse_CrcHashIsNotMistakenForAnEpisodeNumber()
    {
        // An 8-character hex CRC can be all digits, e.g. [03153BDA] vs [12345678].
        EpisodeInfo info = EpisodeInfoParser.Parse("[grp] Show - 04 [1080p] [12345678].mkv");
        await Assert.That(info.Episode).IsEqualTo(4);
    }

    [Test]
    public async Task Parse_EmptyOrExtensionOnlyInputDoesNotThrow()
    {
        await Assert.That(EpisodeInfoParser.Parse("").Episode).IsNull();
        await Assert.That(EpisodeInfoParser.Parse(".mkv").Episode).IsNull();
    }
}
```

- [ ] **Step 2: Run the test to verify it fails**

```bash
dotnet run --project Screenbox.Core.Tests/Screenbox.Core.Tests.csproj
```

Expected: compile failure — `EpisodeInfo` and `EpisodeInfoParser` do not exist.

- [ ] **Step 3: Create the model**

Create `Screenbox.Core/Models/EpisodeInfo.cs`:

```csharp
namespace Screenbox.Core.Models;

/// <summary>
/// Structured information extracted from a media filename.
/// </summary>
/// <param name="Season">Season number, or null when the name carries none.</param>
/// <param name="Episode">Episode number, or null for movies and unparsable names.</param>
/// <param name="Title">Episode title when the name carries one.</param>
/// <param name="Version">Release version from a "v2" suffix, or null.</param>
/// <param name="IsSpecial">True for NCOP, NCED, OAD, OVA and anything under a specials folder.</param>
/// <param name="IsMovie">True when no episode number was found and the item is not a special.</param>
public readonly record struct EpisodeInfo(
    int? Season,
    int? Episode,
    string? Title,
    int? Version,
    bool IsSpecial,
    bool IsMovie);
```

- [ ] **Step 4: Write the parser**

Create `Screenbox.Core/Helpers/EpisodeInfoParser.cs`. Implement the algorithm below. Pure logic only — no I/O, no UWP types.

```csharp
using System;
using System.Linq;
using System.Text.RegularExpressions;
using Screenbox.Core.Models;

namespace Screenbox.Core.Helpers;

/// <summary>
/// Extracts season, episode, title and specials classification from a media filename.
/// Pure logic with no I/O so the rules can be verified against a real corpus without a host.
/// </summary>
public static partial class EpisodeInfoParser
{
    private static readonly string[] SpecialFolderNames =
        ["extras", "specials", "ncop & nced", "ncop", "nced", "oad", "ova", "bonus"];

    private static readonly string[] SpecialMarkers =
        ["ncop", "nced", "oad", "ova", "special"];

    [GeneratedRegex(@"[Ss](\d{1,2})[Ee](\d{1,4})", RegexOptions.CultureInvariant)]
    private static partial Regex SeasonEpisodeRegex();

    [GeneratedRegex(@"(\d{1,2})[xX](\d{1,4})", RegexOptions.CultureInvariant)]
    private static partial Regex SeasonXEpisodeRegex();

    // A bare number only counts when a hyphen separator precedes it. Without this rule,
    // "Mob Psycho 100" parses as episode 100 and "Jujutsu Kaisen 0" as episode zero.
    [GeneratedRegex(@"-\s*(\d{1,4})(?:[vV](\d+))?(?:\s|$|\.)", RegexOptions.CultureInvariant)]
    private static partial Regex SeparatedEpisodeRegex();

    /// <summary>
    /// Parses a filename into structured episode information.
    /// </summary>
    /// <param name="fileName">File name, with or without extension.</param>
    /// <param name="parentFolderName">
    /// Immediate parent folder name. A file inside a specials folder is a special regardless
    /// of its own name.
    /// </param>
    public static EpisodeInfo Parse(string fileName, string? parentFolderName = null)
    {
        if (string.IsNullOrWhiteSpace(fileName))
        {
            return new EpisodeInfo(null, null, null, null, false, false);
        }

        string name = StripExtension(fileName);
        name = StripGroupPrefix(name);
        string normalized = Normalize(name);

        bool isSpecial = IsSpecialFolder(parentFolderName) || HasSpecialMarker(normalized);

        string withoutTags = StripTrailingTags(normalized);

        int? season = null;
        int? episode = null;
        int? version = null;

        Match sxe = SeasonEpisodeRegex().Match(withoutTags);
        Match sxxe = SeasonXEpisodeRegex().Match(withoutTags);
        Match sep = SeparatedEpisodeRegex().Match(withoutTags);

        if (sxe.Success)
        {
            season = int.Parse(sxe.Groups[1].Value);
            episode = int.Parse(sxe.Groups[2].Value);
        }
        else if (sxxe.Success)
        {
            season = int.Parse(sxxe.Groups[1].Value);
            episode = int.Parse(sxxe.Groups[2].Value);
        }
        else if (sep.Success)
        {
            episode = int.Parse(sep.Groups[1].Value);
            if (sep.Groups[2].Success) version = int.Parse(sep.Groups[2].Value);
        }

        string? title = ExtractTitle(withoutTags);
        bool isMovie = episode is null && !isSpecial;

        return new EpisodeInfo(season, episode, title, version, isSpecial, isMovie);
    }

    private static string StripExtension(string fileName)
    {
        int dot = fileName.LastIndexOf('.');
        return dot > 0 ? fileName[..dot] : fileName;
    }

    private static string StripGroupPrefix(string name)
    {
        // "[sam] Chainsaw Man - 02" and "[DB]Kimi no Na wa." both start with a release group.
        name = name.TrimStart();
        if (!name.StartsWith('[')) return name;
        int close = name.IndexOf(']');
        return close < 0 ? name : name[(close + 1)..].TrimStart();
    }

    private static string Normalize(string name) =>
        name.Replace('_', ' ')
            .Replace('\u2013', '-')   // en-dash
            .Replace('\u2014', '-')   // em-dash
            .Trim();

    private static string StripTrailingTags(string name)
    {
        // Remove bracketed and parenthesised tag groups: resolutions, codecs, CRC hashes.
        // Done after episode-bearing text so "[12345678]" cannot be read as an episode number.
        string stripped = Regex.Replace(name, @"\[[^\]]*\]", " ", RegexOptions.CultureInvariant);
        stripped = Regex.Replace(stripped, @"\([^\)]*\)", " ", RegexOptions.CultureInvariant);
        return Regex.Replace(stripped, @"\s+", " ", RegexOptions.CultureInvariant).Trim();
    }

    private static bool IsSpecialFolder(string? folderName) =>
        folderName is { Length: > 0 } &&
        SpecialFolderNames.Contains(folderName.Trim().ToLowerInvariant());

    private static bool HasSpecialMarker(string name)
    {
        string lower = name.ToLowerInvariant();
        return SpecialMarkers.Any(marker =>
            Regex.IsMatch(lower, $@"\b{Regex.Escape(marker)}\b", RegexOptions.CultureInvariant));
    }

    private static string? ExtractTitle(string name)
    {
        // "Jujutsu Kaisen - 001 - Ryoumen Sukuna" carries a title in the third segment.
        string[] parts = name.Split('-', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 3) return null;
        string candidate = parts[^1];
        return candidate.Length > 0 && !candidate.All(char.IsDigit) ? candidate : null;
    }
}
```

The implementer may adjust the regexes to make the corpus pass, but must not relax the hyphen-separator rule in `SeparatedEpisodeRegex` — the two ambiguity-trap tests exist to hold that rule in place.

- [ ] **Step 5: Run the tests and iterate until they pass**

```bash
dotnet run --project Screenbox.Core.Tests/Screenbox.Core.Tests.csproj
```

Expected: PASS, 71 total (56 existing + 15 new).

If a corpus case fails, fix the parser, not the test. Every row came from a real library.

- [ ] **Step 6: Commit**

```bash
git add Screenbox.Core/Models/EpisodeInfo.cs Screenbox.Core/Helpers/EpisodeInfoParser.cs Screenbox.Core.Tests/Helpers/EpisodeInfoParserTests.cs
git commit -m "feat(library): add pure episode filename parser"
```

---

### Task 2: `EpisodeComparer` — natural ordering

**Files:**
- Create: `Screenbox.Core/Helpers/EpisodeComparer.cs`
- Test: `Screenbox.Core.Tests/Helpers/EpisodeComparerTests.cs`

**Interfaces:**
- Consumes: `EpisodeInfoParser.Parse` and `EpisodeInfo` from Task 1.
- Produces: `EpisodeComparer` implementing `IComparer<string>`, comparing by filename; and `EpisodeComparer.Instance` (a shared readonly instance).

- [ ] **Step 1: Write the failing test**

Create `Screenbox.Core.Tests/Helpers/EpisodeComparerTests.cs`:

```csharp
using Screenbox.Core.Helpers;

namespace Screenbox.Core.Tests.Helpers;

public sealed class EpisodeComparerTests
{
    [Test]
    public async Task Sort_OrdersEpisodesNumericallyNotLexically()
    {
        string[] names =
        [
            "Show - 10.mkv",
            "Show - 2.mkv",
            "Show - 1.mkv",
            "Show - 11.mkv"
        ];

        Array.Sort(names, EpisodeComparer.Instance);

        await Assert.That(names[0]).IsEqualTo("Show - 1.mkv");
        await Assert.That(names[1]).IsEqualTo("Show - 2.mkv");
        await Assert.That(names[2]).IsEqualTo("Show - 10.mkv");
        await Assert.That(names[3]).IsEqualTo("Show - 11.mkv");
    }

    [Test]
    public async Task Sort_OrdersBySeasonThenEpisode()
    {
        string[] names = ["Show - S02E01.mkv", "Show - S01E09.mkv", "Show - S01E10.mkv"];

        Array.Sort(names, EpisodeComparer.Instance);

        await Assert.That(names[0]).IsEqualTo("Show - S01E09.mkv");
        await Assert.That(names[1]).IsEqualTo("Show - S01E10.mkv");
        await Assert.That(names[2]).IsEqualTo("Show - S02E01.mkv");
    }

    [Test]
    public async Task Sort_PlacesSpecialsAfterRegularEpisodes()
    {
        string[] names = ["Show - NCED 01.mkv", "Show - 02.mkv", "Show - 01.mkv"];

        Array.Sort(names, EpisodeComparer.Instance);

        await Assert.That(names[0]).IsEqualTo("Show - 01.mkv");
        await Assert.That(names[1]).IsEqualTo("Show - 02.mkv");
        await Assert.That(names[2]).IsEqualTo("Show - NCED 01.mkv");
    }

    [Test]
    public async Task Sort_FallsBackToNameForUnparsableItems()
    {
        string[] names = ["Beta.mkv", "Alpha.mkv"];

        Array.Sort(names, EpisodeComparer.Instance);

        await Assert.That(names[0]).IsEqualTo("Alpha.mkv");
        await Assert.That(names[1]).IsEqualTo("Beta.mkv");
    }

    [Test]
    public async Task Sort_IsStableForIdenticalEpisodeNumbers()
    {
        // Two versions of the same episode must not be reordered arbitrarily.
        await Assert.That(EpisodeComparer.Instance.Compare("Show - 01.mkv", "Show - 01.mkv")).IsEqualTo(0);
    }
}
```

- [ ] **Step 2: Run the test to verify it fails**

```bash
dotnet run --project Screenbox.Core.Tests/Screenbox.Core.Tests.csproj
```

Expected: compile failure — `EpisodeComparer` does not exist.

- [ ] **Step 3: Write the implementation**

Create `Screenbox.Core/Helpers/EpisodeComparer.cs`:

```csharp
using System;
using System.Collections.Generic;
using Screenbox.Core.Models;

namespace Screenbox.Core.Helpers;

/// <summary>
/// Orders media filenames by parsed season and episode, so episodes read 1, 2, … 10, 11
/// rather than the lexical 1, 10, 11, 2. Specials sort after regular episodes; anything
/// unparsable falls back to an ordinal name comparison.
/// </summary>
public sealed class EpisodeComparer : IComparer<string>
{
    /// <summary>Shared instance. The comparer holds no state.</summary>
    public static readonly EpisodeComparer Instance = new();

    /// <inheritdoc/>
    public int Compare(string? x, string? y)
    {
        if (ReferenceEquals(x, y)) return 0;
        if (x is null) return -1;
        if (y is null) return 1;

        EpisodeInfo left = EpisodeInfoParser.Parse(x);
        EpisodeInfo right = EpisodeInfoParser.Parse(y);

        // Specials always trail regular episodes.
        if (left.IsSpecial != right.IsSpecial) return left.IsSpecial ? 1 : -1;

        // A name with no season is treated as season 1 so "Show - 02" and "Show - S01E03"
        // order sensibly against each other inside the same folder.
        int season = (left.Season ?? 1).CompareTo(right.Season ?? 1);
        if (season != 0) return season;

        if (left.Episode is { } le && right.Episode is { } re)
        {
            int episode = le.CompareTo(re);
            if (episode != 0) return episode;
        }
        else if (left.Episode is not null)
        {
            return -1;
        }
        else if (right.Episode is not null)
        {
            return 1;
        }

        return string.Compare(x, y, StringComparison.OrdinalIgnoreCase);
    }
}
```

Note the ordering of the checks: the specials test must come before the season test, because a special with no parsed season would otherwise sort among the regular episodes.

- [ ] **Step 4: Run the tests and verify they pass**

```bash
dotnet run --project Screenbox.Core.Tests/Screenbox.Core.Tests.csproj
```

Expected: PASS, 76 total (71 + 5 new).

- [ ] **Step 5: Commit**

```bash
git add Screenbox.Core/Helpers/EpisodeComparer.cs Screenbox.Core.Tests/Helpers/EpisodeComparerTests.cs
git commit -m "feat(library): add natural episode ordering"
```

---

### Task 3: Apply the ordering in the folder view

**Files:**
- Modify: `Screenbox.Core/ViewModels/FolderViewPageViewModel.cs:227-251`

**Interfaces:**
- Consumes: `EpisodeComparer.Instance` from Task 2.
- Produces: no new public API.

`FetchFolderContentAsync(StorageFolder)` pages through `_filesService.GetSupportedItems(folder)` 30 items at a time and appends each to `Items`. Sorting must happen after the full set is fetched, not per page, or pages interleave incorrectly.

- [ ] **Step 1: Sort after the fetch loop completes**

After the `while` loop in `FetchFolderContentAsync(StorageFolder folder)` finishes and before `IsEmpty` is set, reorder `Items` using the comparer. Sort a local list and then rebuild the observable collection in one pass rather than moving items individually, so the UI does not observe an intermediate scrambled order:

```csharp
        // Episodes arrive in the query's lexical order, which puts episode 10 between 1 and 2.
        List<StorageItemViewModel> ordered = [.. Items.OrderBy(item => item.Name, EpisodeComparer.Instance)];
        if (!ordered.SequenceEqual(Items))
        {
            Items.Clear();
            foreach (StorageItemViewModel item in ordered) Items.Add(item);
        }
```

Folders must keep their existing position relative to files — check how the query currently interleaves them and preserve that grouping. If folders and files are mixed, sort within each group rather than across both. State in your report which arrangement you found and what you did.

Add `using System.Linq;` if not already present.

- [ ] **Step 2: Build Screenbox.Core**

```bash
"C:\Program Files\Microsoft Visual Studio\18\Community\MSBuild\Current\Bin\MSBuild.exe" Screenbox.Core/Screenbox.Core.csproj -t:Build -p:Configuration=Debug -p:Platform=x64 -m
```

Expected: exit code 0.

- [ ] **Step 3: Run the tests**

```bash
dotnet run --project Screenbox.Core.Tests/Screenbox.Core.Tests.csproj
```

Expected: PASS, 76 total.

- [ ] **Step 4: Manual verification**

Open a folder with more than nine episodes — the reference library's `Death Note` has 37. Confirm they read 1, 2, … 9, 10, 11 rather than 1, 10, 11, … 2, 20. Confirm `Extras` content sorts last.

- [ ] **Step 5: Commit**

```bash
git add Screenbox.Core/ViewModels/FolderViewPageViewModel.cs
git commit -m "feat(library): order folder contents by parsed episode number"
```

---

### Task 4: Show episode titles in the caption

**Files:**
- Modify: `Screenbox.Core/ViewModels/StorageItemViewModel.cs`

**Interfaces:**
- Consumes: `EpisodeInfoParser.Parse` from Task 1.
- Produces: no new public API. Enriches the existing `CaptionText`.

`UpdateCaptionAsync` currently sets a file's caption from contributing artists or duration. Where the filename carries an episode title, that is more useful than either.

- [ ] **Step 1: Prefer the episode title in the caption**

In `UpdateCaptionAsync`, in the `case StorageFile file:` branch, parse the filename first and use the title when one is found, falling back to the existing behaviour otherwise:

```csharp
                case StorageFile file:
                    EpisodeInfo episode = EpisodeInfoParser.Parse(file.Name);
                    if (episode.Title is { Length: > 0 } episodeTitle)
                    {
                        CaptionText = episodeTitle;
                        break;
                    }

                    // existing artist / duration logic unchanged below
```

Add `using Screenbox.Core.Helpers;` and `using Screenbox.Core.Models;`.

Do not change the `StorageFolder` branch — folder captions are Phase 4's concern.

- [ ] **Step 2: Build and test**

```bash
"C:\Program Files\Microsoft Visual Studio\18\Community\MSBuild\Current\Bin\MSBuild.exe" Screenbox.Core/Screenbox.Core.csproj -t:Build -p:Configuration=Debug -p:Platform=x64 -m
```

```bash
dotnet run --project Screenbox.Core.Tests/Screenbox.Core.Tests.csproj
```

Expected: exit code 0 and 76 passing.

- [ ] **Step 3: Manual verification**

Open `Jujutsu Kaisen/Season 1` in the reference library. Episode captions should read `Ryoumen Sukuna` and similar rather than a duration.

- [ ] **Step 4: Commit**

```bash
git add Screenbox.Core/ViewModels/StorageItemViewModel.cs
git commit -m "feat(library): show parsed episode titles in captions"
```

---

## Verification

```bash
dotnet run --project Screenbox.Core.Tests/Screenbox.Core.Tests.csproj
```

Expected: 76 passing, 0 failing.

```bash
"C:\Program Files\Microsoft Visual Studio\18\Community\MSBuild\Current\Bin\MSBuild.exe" Screenbox/Screenbox.csproj -t:Build -p:Configuration=Debug -p:Platform=x64 -m
```

Expected: exit code 0. Close the running app and put vswhere on PATH first.

## Out of scope

- Next-episode playback. The ordering this phase establishes makes it possible, but wiring it into the play queue is separate work.
- Aggregate folder captions with episode counts and unwatched totals. That is Phase 4, which needs both this phase's specials classification and Phase 2's watched state.
- Using parsed series names to improve online metadata matching. That is Phase 5, which depends on this phase.

## Note if both plans run concurrently

Phase 2 and Phase 3 share no files. If they are implemented at the same time, the final test count is the union: 56 existing + 21 from Phase 2 + 20 from Phase 3. Each plan's intermediate counts assume it is the only one running, so expect higher numbers and confirm the delta rather than the absolute.
