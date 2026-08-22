# IMPLEMENTATION_STATUS

**Application:** IP Docketing | By Anant Goel
**Baseline:** live working tree at `D:\editing tool\github\ip-docketing tool`
**Last updated:** 22 Aug 2026
**Phase:** 1 (audit and stabilisation) — audit complete, implementation partial

---

## 0. Read this first — the constraint that shapes everything below

The assistant working on this repository runs in a **Linux cloud container**. It
can read and write your files through the desktop bridge, but it **cannot build
or run this application**:

- The .NET SDK package feed is blocked from that container.
- WinUI 3 is a Windows desktop framework; it will not run on Linux regardless.
- There is no shell available on your Windows machine through the bridge, so it
  cannot build remotely either.

**Consequence: no code written for this project in these sessions has ever been
through a compiler.** Your brief specifies "build the application" and "run
relevant tests" after every phase. That loop cannot be executed by the
assistant. It has to be executed by you, with build output pasted back.

Everything below distinguishes **statically verified** (checked by inspection or
scripted analysis) from **verified** (built, run, and exercised end to end).
Nothing in this repository is currently in the second category.

---

## 1. Environment and architecture (Phase 1, step 2)

| Aspect | Finding |
|---|---|
| Language | C# 12, `net8.0-windows10.0.19041.0` |
| UI framework | WinUI 3 (Windows App SDK), unpackaged |
| Core library | `IPDocketing.Core`, `net8.0-windows`, nullable enabled |
| Database | SQLite via EF Core |
| Schema management | **`EnsureCreated()` + a manual `schemaVersion` constant.** No EF migrations. |
| Browser automation | WebView2, two hosts: visible (`IpIndiaPortalPage`) and hidden (`HeadlessJournalDownloader`) |
| PDF / OCR | `PdfTextExtractor`, `TesseractTextExtractor`, `ChainedTextExtractor` (Windows OCR fallback) |
| Notifications | `Microsoft.Windows.AppNotifications` |
| Packaging | Unpackaged WinUI 3; no installer project present |
| User data location | `AppDataDirectory` (correct — not the install folder) |
| Encryption | `EncryptionService`, used for DB backups (`.db.enc`) |
| **Test project** | **None. There is no test project anywhere in the solution.** |

## 2. Blocking findings

### 2.1 You are running a stale binary
The Journal Watch screenshot shows an error string
("…or class 31 wasn't in a parseable range…") that **no longer exists anywhere in
the source**. The running executable predates several rounds of fixes. Rebuild
before assessing anything as broken.

### 2.2 The requirements document is not reachable
"TRADEMARK MANAGEMENT SOFTWARE-INTERNAL" is **not in the repository and was not
attached**. There is no `.docx` anywhere in the tree. `docs/DOCX-FEATURE-MAP.md`
(29 KB) appears to be a derived map of it and is being used as a proxy, but the
traceability matrix in §AF cannot be completed against a document nobody can
read. Please attach it.

### 2.3 A schema change currently destroys the database
`App.xaml.cs` bumps a `schemaVersion` constant; on a mismatch it takes an
encrypted snapshot and then **deletes the database file**. Several remaining
features (status history, batch checkpoints, journal match audit trail,
correspondence metadata, users/permissions) require new tables. **Moving to real
EF Core migrations is a prerequisite for Phases 2, 4, 5 and 9** and is the single
highest-priority engineering task in this plan.

## 3. Requirements / implementation matrix

State key: **W** working · **P** partial · **B** broken · **S** stub · **M** missing

| Module | State | Root cause / gap | Files | DB impact | Test method | Verified |
|---|---|---|---|---|---|---|
| Journal — discovery | P | Listing parses; dates fixed | `JournalFetchService` | none | fixture + live | static only |
| Journal — date `1601` | **fixed** | `DatePicker.Date` set instead of `SelectedDate`; template overwrote it after ctor | `JournalPage.xaml.cs` | none | open page | static only |
| Journal — download | **B → addressed** | Listing links are `__doPostBack`; no URL exists to GET. Whole pipeline was gated on a URL that cannot exist | `JournalPage.xaml.cs`, `HeadlessJournalDownloader` | none | live | **not verified** |
| Journal — statuses | **fixed** | Badge bound to `Reviewed` only | `JournalPage.*` | none (derived) | open page | static only |
| Journal — name search | P | Works only on downloaded PDFs; no page-level PDF/image export, no audit trail | `JournalSearchService` | **needs tables** | fixture | no |
| Journal — cancel/progress | M | No cancellable background job | — | needs job table | — | no |
| Trademark Watch | P | EF translation crash fixed; scoring explainable; no review workflow, no device/visual similarity | `WatchService`, `MarkSimilarityService` | needs alert-workflow columns | unit | static only |
| e-Status — navigation | P | Guided flow exists; tab/radio steps not verified against live DOM | `PortalScripts.EStatusStep` | none | live | no |
| e-Status — parsing | **fixed** | Columnar table read with a key/value assumption → class returned "Filing Mode"; `User Detail` written into `ProprietorName` | `PortalScripts`, `IpIndiaPortalPage` | none | fixture | static only |
| e-Status — history | M | No status-history table | — | **needs table** | — | no |
| Uploaded Documents | P | Panel walk implemented; no checksum, no original filename, no retry UI | `IpIndiaPortalPage` | needs columns | live | no |
| Correspondence | P | Corres./despatch numbers now captured into description; not first-class columns | same | needs table | live | static only |
| e-filing Filed Applications | P | Generic `ExtractTables` + user-confirmed mapping exists; no dedicated parser, no pagination, no sync summary | `PortalScripts.ExtractTables`, `TableImportMapper` | none | fixture | no |
| Batch sync | M | No queue, checkpoints, pause/resume | — | **needs tables** | — | no |
| Virtual cursor mode | M | Not started | — | none | — | no |
| Local AI / NPU | M | Not started; no provider abstraction | — | needs tables | — | no |
| Gmail OAuth | **B** | `GmailOtpService` exists; no credentials import UI, no DPAPI token store, no Settings section | `GmailOtpService`, `SettingsPage` | none | offline | no |
| Dashboard / Calendar | P | Monthly `CalendarView` + Today + Overdue only. No week/day/agenda views, no colour-coded deadline types | `CalendarPage`, `DashboardPage` | none | UI | no |
| Deadline rule engine | P | `RuleEngineService` + `CountryRule` exist; no version or calculation basis shown in UI, no manual-override audit | `RuleEngineService` | needs columns | unit | no |
| Matters master | P | CRUD present; no archive/restore, no duplicate detection, no timeline | `MattersPage`, `MatterService` | needs columns | UI | no |
| Oppositions | P | Records + reports; no configurable stage workflow | `OppositionService` | needs columns | UI | no |
| Documents centre | P | Ingest + OCR; no checksum dedupe, no version compare | `DocumentIngestService` | needs columns | UI | no |
| Users / permissions | M | `TeamMember` exists; no roles or permission checks | — | needs tables | — | no |
| Reports | P | HTML/CSV builders exist for watch and status | `WatchService`, `StatusTrackerService` | none | UI | no |
| **PTO Sync** | **S** | **Genuine stub.** No DB calls at all; both buttons only append log lines | `PtoSyncPage.xaml.cs` | none | — | confirmed stub |
| Diagnostics screen | M | `SelfTest` exists on Journal page only | — | none | — | no |
| Backup / restore | W | `BackupService` + encrypted snapshots | `BackupService` | none | UI | no |
| Tests | **M** | **No test project exists** | — | none | — | — |

## 4. Static verification performed this session

Scripted checks across the whole WinUI project, since compilation is unavailable:

- **XAML → code-behind handlers:** every `Click`/`Loaded`/`SelectionChanged`/etc.
  referenced in all 17 pages resolves to a method. No missing handlers.
- **Bindings:** all 15 `{Binding}` paths in `JournalPage` templates resolve to
  members on `IssueRow` / `AlertRow`.
- **Theme resources:** every custom `StaticResource`/`ThemeResource` key used in
  any XAML is defined in `Themes/`. No dangling keys after the Vibrancy work.
- **`App.*` statics:** every `App.X` referenced in edited pages exists.
- **Record arity:** `PortalReading` (14 positional params) matches its single
  construction site argument-for-argument, in order.
- **Brace/paren balance and XML well-formedness** on every edited file.

This catches the common WinUI build breaks. It does **not** substitute for a
compiler: type inference, nullability, overload resolution and analyzer errors
remain unchecked.

## 5. Recommended sequencing (adapted from your §AD)

Prerequisite before Phase 2: **EF Core migrations**, replacing the
delete-and-rebuild `schemaVersion` mechanism. Everything with "needs table(s)"
above is blocked behind it, and shipping those on the current mechanism means
each one costs the user their database.

Then, in value-per-risk order:

1. **Gmail OAuth (§Q)** — fully offline-testable, no portal dependency.
2. **Filed Applications dedicated parser + pagination + sync summary (§H)**.
3. **Status history + batch queue (§I, §L)** — both need the migration work.
4. **Journal page-level extraction and audit trail (§F.4)**.
5. **Virtual cursor (§O)** — overlay on the existing WebView2; no new browser
   framework, which would put ARM64 packaging at risk for no gain.
6. **Local AI (§P)** — provider abstraction and CPU path first; ONNX Runtime +
   QNN execution provider only after confirming runtime/model/OS alignment on
   the actual Snapdragon X machine.
7. **Test project (§AC)** — needs sanitised HTML/PDF fixtures. Saving
   "Save page as → Webpage, complete" captures of the e-status result page and
   both modals would unblock most parser tests immediately.

## 6. Rollback

Every change so far is in the working tree only. To revert:

```
cd "D:\editing tool\github\ip-docketing tool"
git status
git diff > my-changes.patch     # keep a copy first
git checkout -- src/            # discard all source changes
```

The database is untouched by every change made so far — no schema version has
been bumped, and the new Journal statuses are deliberately derived from existing
columns rather than stored, specifically to avoid triggering the destructive
rebuild described in §2.3.
