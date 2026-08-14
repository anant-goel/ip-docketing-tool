# IP Docketing - Enterprise Desktop App (Windows / WPF)

A native Windows desktop application (.NET 8, WPF, MVVM) implementing the core
of an enterprise IP docketing system for patents, trademarks, copyrights, and
trade secrets: a dynamic multi-jurisdiction deadline rule engine, a portfolio
of matters with family-tree relationships, deadline tracking with color-coded
urgency, a local-first SQLite database, document filing, and an append-only
audit trail.

It borrows its data-model shape (Matter / Event / Deadline-style docketing)
from the open-source [phpIP](https://github.com/jjdejong/phpip) project's
approach to IP portfolio management, and its rule-engine architecture
(versioned rules, nominal-vs-effective dates, hash-chained audit ledger)
from the reference guides at
[ip-docketing.com](https://github.com/ip-docketing/ip-docketing) - reimplemented
natively for Windows in C#/EF Core.

## What's implemented vs. stubbed

This is a working, compilable scaffold - not a 1:1 build of every bullet in
the original spec (some of those, like live USPTO/EPO/WIPO ingestion, need
registered API credentials that only you can provision). Here's the honest
breakdown:

| Feature | Status |
|---|---|
| Local SQLite database, auto-created & seeded on first run | Working |
| Matters (Patents/Trademarks/Copyrights/Trade Secrets) - add & list | Working |
| Family tree (parent/child matters, e.g. continuations, foreign counterparts) | Working (`MatterService.GetFamily`) |
| Dynamic rule engine: versioned rules resolved by (event, jurisdiction, date) | Working - seeded with cited US/EP/PCT/CN statutory rules, calendar-correct month arithmetic (`DateTime.AddMonths`, not fixed day counts) |
| Nominal vs. effective deadline, with non-working-day roll-forward | Working (`HolidayCalendarService`) - both dates shown side by side in the Deadlines grid |
| Hash-chained, append-only audit ledger (SHA-256, tamper-evident) | Working - each record hashes its payload + the prior record's hash; **Settings → Verify chain integrity** re-walks the whole ledger and confirms nothing was altered |
| Deadline list, color-coded by urgency (red/amber/indigo/green) | Working |
| Mark deadline complete / apply statutory extension | Working |
| Document filing: import dialog + drag-and-drop | Working (stores metadata + file path) |
| OCR of PTO PDF notices | **Stubbed** - `Document.OcrText`/`OcrProcessed` exist; wire a real engine (e.g. Tesseract via `Tesseract` NuGet, or a cloud OCR API) behind an `IOcrService` |
| PTO Sync (USPTO TSDR/PAIR, EPO, WIPO) | **Stubbed UI/architecture only** - no live calls. Implement `IPtoSyncClient` per office with your registered API credentials |
| CSV export (Reports) | Working |
| RBAC roles | UI selector only - no permission enforcement wired to it yet |
| Windows tray icon + balloon notification for overdue deadlines | Working |
| Ctrl+N / Ctrl+D shortcuts | Working (jump to Matters / Deadlines) |
| Dark mode toggle | UI toggle present; theme swap not yet wired (Tokens.xaml has both light/dark colors defined - swap the merged dictionary at runtime to finish it) |
| GitHub Actions: automatic build on push/PR/tag | Working |
| GitHub Actions: manual "Run workflow" trigger with build-config choice | Working (`workflow_dispatch` inputs - see below) |

## Project layout

```
ip-docketing-app/
  src/
    IPDocketing.Core/        # Plain .NET 8 class library: models, EF Core DbContext, services
      Models/                # Matter, Event, Deadline, CountryRule, Document, PtoNotice, UserAction
      Data/                  # AppDbContext (SQLite), SeedData
      Services/              # RuleEngineService, DeadlineService, MatterService, AuditService
    IPDocketing.App/         # net8.0-windows WPF app (the actual UI)
      Views/                 # Dashboard, Matters, Deadlines, Documents, PtoSync, Reports, Settings
      ViewModels/             # MVVM (CommunityToolkit.Mvvm)
      Themes/                 # Tokens.xaml (design tokens/colors), Styles.xaml (controls)
      MainWindow.xaml         # Title bar, nav rail, workspace, status bar
  .github/workflows/build.yml # CI: builds + publishes a self-contained win-x64 exe
```

## Building locally (Windows, with Visual Studio or the CLI)

```
dotnet restore src/IPDocketing.App/IPDocketing.App.csproj
dotnet run --project src/IPDocketing.App/IPDocketing.App.csproj
```

The app creates its SQLite database at
`%LOCALAPPDATA%\IPDocketing\ipdocketing.db` the first time it runs, and seeds
it with sample jurisdiction rules and a couple of example matters/deadlines
so the dashboard isn't empty.

## Building with GitHub Actions (what you asked for)

1. Push this folder's contents to your `ip-docketing` GitHub repo (root of
   the repo should contain `src/`, `.github/`, this README, etc.).
2. **Automatic:** push to `main`/`master` or open a PR - the workflow runs
   on its own.
3. **Manual trigger (the toggle):** go to the repo's **Actions** tab ->
   **"Build Windows App"** -> click **"Run workflow"**. You'll get two
   options before it starts:
   - **configuration**: `Release` (default) or `Debug`
   - **create_release**: check this to have the built `.exe` attached to a
     GitHub Release immediately, even without pushing a version tag
4. Either way, the workflow:
   - Restores and builds the app on `windows-latest`.
   - Publishes a **self-contained, single-file** `IPDocketing.exe` for
     `win-x64` (no .NET runtime install needed on the target machine).
   - Uploads it as a build artifact (`IPDocketing-win-x64`) you can download
     from the Actions run summary page.
5. To cut a release build tied to a version number instead, push a tag like
   `v1.0.0` - the workflow attaches `publish/IPDocketing.exe` to that
   release automatically (this path doesn't need the manual toggle).

## Rule engine architecture

The deadline engine follows a deterministic, five-stage-inspired shape:
event -> versioned rule resolution -> calendar-correct period math ->
non-working-day roll -> hash-chained audit record.

- **Rules are versioned and cited.** Each `CountryRule` carries a statute
  citation, an `EffectiveFrom` date, and a `RuleVersion` tag. Resolution
  picks the version that was actually in force on the triggering event's
  date (`RuleEngineService`), so a later statutory change never
  retroactively alters an already-computed deadline.
- **Base date, period, and roll calendar are independent inputs.** Periods
  defined in months (the statutory norm - "3 months to respond to an OA")
  use `DateTime.AddMonths`, which clamps invalid end-of-month dates the same
  way `dateutil.relativedelta` does, instead of an inexact fixed day count.
  The non-working-day roll lives entirely in `HolidayCalendarService` and is
  never mixed into the period math.
- **Both dates are kept.** Every `Deadline` stores `NominalDueDate` (before
  roll) and `DueDate` (effective, after roll) so an auditor can see *why*
  the operative date moved. Both appear as separate columns in the
  Deadlines grid.
- **The audit trail is hash-chained.** `AuditService.Log` computes
  `SHA256(priorRecordHash + payload)` for every action, and
  `AuditService.VerifyChainIntegrity()` re-walks the whole ledger to prove
  no historical row was altered - exposed as **Settings -> Verify chain
  integrity**.

This mirrors the reference architecture described at
[ip-docketing.com's rule-engine guide](https://www.ip-docketing.com/automated-deadline-calculation-rule-engines/),
scoped down to what a single-tenant desktop app needs (no live portal
ingestion, no Python/Pydantic - the same principles implemented in C#/EF Core).

## Design tokens (from the spec)

Defined in `src/IPDocketing.App/Themes/Tokens.xaml`:

- Brand/accent: Enterprise Cobalt Navy `#0F3460`, Tech Blue hover `#2B5AED`
- Overdue / hard deadline: Crimson `#DC143C`
- Upcoming / warning (3-14 days): Amber `#FF8C00`
- Pending PTO response: Indigo `#6A5ACD`
- Completed / cleared: Emerald `#2ECC71`
- Typography: Segoe UI Variable (falls back to Segoe UI), compact 32px data
  rows for high-density grids

## Extending the rule engine

Jurisdictions are pure data - add a row to `CountryRule` (via
`SeedData.cs` for now, or a future Settings screen) with a country code,
matter type, triggering `EventType`, and a day offset, and the
`RuleEngineService` will start calculating that deadline automatically
whenever a matching `Event` is recorded against a matter.

## Known limitations / next steps

- No real PTO API integration (needs your USPTO/EPO/WIPO credentials).
- No OCR engine wired in (interface point noted in `DocumentsViewModel`).
- RBAC role is stored but not yet enforced on any action.
- Dark mode toggle exists but doesn't yet hot-swap the theme dictionary.
- No MSIX packaging/installer - ships as a portable single-file `.exe`.
