# IPDocketing.Core.Tests

The first automated tests in this repository. Until now there was no test
project anywhere in the solution.

## Running them

```powershell
cd "D:\editing tool\github\ip-docketing tool"
dotnet test tests\IPDocketing.Core.Tests\IPDocketing.Core.Tests.csproj
```

Deliberately **not** added to `IPDocketing.sln`. Editing the solution file blind
risks breaking your build for no benefit — `dotnet test` takes the project path
directly. Add it through Visual Studio's "Add existing project" when convenient.

## What these cover

Only `IPDocketing.Core`. No WinUI, no WebView2, no network, no portal. The suite
runs on any Windows machine with the .NET 8 SDK, without the app installed.

| File | Covers | Regression it guards |
|---|---|---|
| `ClassRangeTests` | `JournalFetchService.TryParseClassRange` | Underscore and en-dash label shapes silently matching nothing |
| `MarkSimilarityTests` | Normalisation, phonetic keys, scoring, class weighting | KWIK BRITE / QUICK BRIGHT and LAXMI / LAKSHMI scoring under threshold |
| `WatchServiceTests` | `RunWatch` end to end against SQLite | **The LINQ-translation crash that made the watch do nothing** |
| `JournalServiceTests` | Add / dedupe / RemoveDuplicates / ordering | Journal 2274 appearing twice |
| `StatusTrackerTests` | Dossier assembly and printable output | Broken navigation properties; unencoded marks in generated HTML |

## Why a real SQLite file, not the in-memory provider

EF Core's in-memory provider does not translate LINQ to SQL — it evaluates
against objects, so it happily runs queries that throw against a real database.

That is not academic. The trademark watch was dead because of a projection EF
could not translate (`string + int` compiles to `String.Concat(object, object)`).
An in-memory test would have passed while the feature crashed in front of the
user. `TestDatabase` therefore creates a real file in `%TEMP%` per test and
deletes it afterwards.

## Known gaps

- **One test is skipped, not failing.** `SharedGenericWordsAreNotAConflict`
  documents a real defect: when both marks reduce to nothing distinctive,
  `Compare` falls back to the full strings, so "SUPER FOODS" vs "SUPER TOOLS"
  scores about 82 and would raise an alert — the exact false positive the
  distinctive-core design exists to prevent. It needs a deliberate decision
  before it is "fixed", because two all-generic marks must still be comparable.
  The skip reason carries the detail.
- **Date parsing is untested.** `JournalFetchService.ParseDate` and `CellText`
  are `private`. Testing them needs either `InternalsVisibleTo` on the Core
  project or promotion to `internal`. Held back until the main build is green,
  to avoid adding churn to an already-uncompiled changeset.
- **No parser fixtures yet.** The portal parsers live in the WinUI project as
  JavaScript strings, so they are not reachable from here. Saving
  "Save page as → Webpage, complete" captures of the e-status result page and
  both modals would let the extraction logic be tested without the live portal —
  that is the single highest-value thing you could add to make this suite cover
  the portal work.
