# IP Docketing — WinUI 3

A native Windows 10/11 desktop docketing application built with .NET 8,
WinUI 3, Windows App SDK, EF Core and SQLite. The interface uses a Mica
foundation with Acrylic supporting surfaces and a restrained Liquid Glass
visual system.

## Highlights

- Portfolio dashboard with live deadline metrics
- Matter, deadline and document registers backed by SQLite
- Versioned jurisdiction rules with calendar-correct month arithmetic
- Nominal and operative due dates with non-working-day roll-forward
- SHA-256 hash-chained audit trail
- Encrypted local database sealing and encrypted backups
- CSV report exports
- Mica window backdrop, in-app Acrylic surfaces and solid accessibility fallbacks
- Clean `NavigationView` shell with custom title bar and responsive pane

Live USPTO/EPO/WIPO ingestion and OCR require separately configured providers.
The UI exposes honest connection and processing states without pretending those
services are active.

## Project layout

```text
src/
  IPDocketing.Core/       Models, EF Core context, rule engine and services
  IPDocketing.WinUI/      Primary WinUI 3 application
  IPDocketing.App/        Legacy WPF application retained during migration
```

Both desktop front ends share `%LOCALAPPDATA%\IPDocketing\` and the same Core
services. Use one UI at a time while validating a migration build.

## Build on Windows

Install Visual Studio 2022 with the .NET desktop and Windows application
development workloads, then run:

```powershell
dotnet restore src/IPDocketing.WinUI/IPDocketing.WinUI.csproj
dotnet build src/IPDocketing.WinUI/IPDocketing.WinUI.csproj -c Release -r win-x64
dotnet run --project src/IPDocketing.WinUI/IPDocketing.WinUI.csproj -c Release -r win-x64
```

The WinUI project is unpackaged and Windows App SDK self-contained for a simple
portable distribution. A publish contains the executable plus its required
runtime files; keep the whole publish folder together.

## Backdrop behavior

The app follows this explicit order:

1. **Mica BaseAlt** on supported Windows 11 systems.
2. **Desktop Acrylic** when Mica is unavailable but Acrylic is supported.
3. **Solid `#080B12`** if composition is unavailable.

Inside the window, cards and navigation panels use WinUI in-app Acrylic with
their own solid `FallbackColor`. Windows automatically suppresses material when
transparency, high-contrast, battery or graphics policy requires it.

Only `Window.SystemBackdrop` owns the window material. The project does not
attach a second `MicaController` or `DesktopAcrylicController`; those controller
classes are used only for runtime support checks.

## Liquid Glass design translation

The web reference implementations use shaders and displacement maps. WinUI uses
the closest native, maintainable translation:

- compositor-backed Mica/Acrylic for environmental color and blur;
- translucent saturated surfaces with crisp foreground content;
- directional gradient borders for a Fresnel-like edge;
- top glare, rounded superellipse-like geometry and compact pill controls;
- restrained hover/press feedback supplied by native WinUI controls.

Visual technique references:

- [liquid-glass-studio](https://github.com/iyinchao/liquid-glass-studio)
- [liquid-glass-react](https://github.com/rdev/liquid-glass-react)

No WebGL, SVG filter or React runtime is embedded in the desktop application.

## GitHub Actions

The workflow is manual-only. Open **Actions → Build Windows App → Run workflow**.
It restores, builds and publishes the WinUI 3 project for `win-x64`, then uploads
one `IPDocketing-win-x64.zip` artifact. Optional release creation and Authenticode
signing remain available through the workflow inputs and repository secrets.

## Known external dependencies

- PTO live sync needs registered office API credentials and provider clients.
- OCR needs a local engine or cloud OCR implementation.
- Unsigned downloads can trigger SmartScreen; production distribution should use
  a trusted Authenticode certificate.

## What changed in phase 30

Merged the two Liquid Glass reference repositories, closed the remaining gaps
against the internal trademark-management spec, and fixed a set of defects found
while reading the whole tree. Full spec mapping: `docs/DOCX-FEATURE-MAP.md`.
Attribution and what could/couldn't be ported: `THIRD-PARTY-NOTICES.md`.

### New

- **Status Tracker** page (spec §5) — one mark's full prosecution and opposition
  history, with a Print button that renders a self-contained HTML sheet and opens
  it in the browser with the print dialog up (also gives save-as-PDF).
- **TM Search** page (spec §6) — exact / contains / phonetic / starts-with,
  word vs device, proprietor / attorney / state / class, then filtered by status
  and by portal alert. Searches this docket, not the IP India register.
- **Team notification digests** (spec §1) — per-person overdue and approaching
  deadline summaries on the dashboard.
- **Weekly journal pull** (spec §4) and **watch reports** (spec §7).
- **Automatic client-update drafting** (spec §8) — runs at startup for anything
  over a week old.
- Document categories from the spec (examination report, hearing notice, order,
  opposition proceeding, registration certificate, TMR portal document), and
  documents can now be filed against an opposition.

### Defects fixed

- **`Soundex` threw `IndexOutOfRangeException`** on any mark starting with a
  digit or symbol — `codes[first - 'A']` indexed straight off the first
  character. Indian marks routinely start with digits ("5 STAR"). Non-letters
  are now stripped first.
- **The holiday calendar was US-only.** Every deadline, Indian ones included,
  rolled against Juneteenth while ignoring 26 January. Calendars are now keyed
  by jurisdiction and resolved per matter, with a documented seam for the CGPDTM
  annual list (only fixed-date holidays are encoded — Diwali, Holi, Eid and
  per-branch closures still have to be loaded each year).
- **Backups ran every 60 seconds.** With four days of retention that is 5,760
  full DPAPI-encrypted copies of the whole database. Now 15 minutes, skipped
  entirely when the file is unchanged, with a hard count ceiling.
- **A schema version bump deleted your data outright.** It now takes a
  restorable encrypted snapshot first.
- **Status and filing date were unreachable from the UI** — both existed on the
  model but had no editor, so a matter could only ever be `Pending` with no
  filing date, which made the dashboard metrics and any status view structurally
  unable to show anything real. Status, filing date, registration number,
  registration date, renewal date and portal alert are all editable now.
- **`MarkType` was stamped on non-trademarks**, putting patents into the
  word/device split.
- **`PublishProfile` pointed at a `.pubxml` that does not exist in this repo** —
  on newer SDKs an unresolvable publish profile fails the publish step. Removed;
  the workflow was already driving the publish with `-r win-x64` on the command
  line.
- Government endpoints are now called with an honest `User-Agent` instead of
  none, which some front-ends reject or serve differently.

### Build notes

The workflow now caches NuGet packages, verifies the restore actually resolved,
and checks `IPDocketing.exe` exists before packaging — a publish that "succeeds"
while producing no executable is a real failure mode with self-contained WinUI.

If restore fails on the runner, the first thing to check is
`Microsoft.WindowsAppSDK` `2.4.0` in `IPDocketing.WinUI.csproj`. If that version
does not resolve, pin it to a known-good `1.7.x` and rerun.

The reference repositories under `/third_party` are documentation only. Nothing
there is compiled; the WPF demo's project file is renamed
`DemoApp.csproj.reference` so it can never join a build.

## What changed in phase 31

Three fixes, driven by the screenshot and the shipped folder.

### 1. Prior-art auto-fill now actually fills

The screenshot showed the exact signature of the bug: **Class filled with 29,
Wordmark left empty.** That is not a random failure, it is what happens when you
select fields by guessing at ids.

The old code did `document.querySelector('input[id*="mark" i], input[name*="mark" i]')`.
`querySelector` returns the *first* match in document order, and that page
carries hidden ASP.NET WebForms inputs plus the plumbing for the Well Known
Marks and Prohibited Marks tabs — several of which have "mark" in their id or
name. So it matched an invisible field and wrote the value there. `"class"` was
unambiguous on that page, which is precisely why only that one appeared to work.

Even with both text boxes filled, the form still could not have searched: the
page has two radio groups (Search Type — Wordmark / Vienna Code / Phonetic, and
the wordmark criteria — Start With / Contains / Match With) and neither was ever
touched.

The filler now lives in `Views/PortalScripts.cs` and finds fields the way a
person does — by the label text beside them. Hidden, disabled and read-only
inputs are discarded *before* scoring, so an invisible field can never win.
Values are written through the native property setter with input/change/blur
events, because a plain `.value =` assignment is silently ignored by controlled
inputs — the box looks filled and submits empty. Both radio groups are now set,
from two new dropdowns on the toolbar.

A **Diagnose** button lists every field the live page exposes with the label text
it sits under, and copies the report. That failure mode is invisible otherwise:
a field fills, the wrong one, and nothing on screen says so.

**The CAPTCHA is explicitly excluded.** Any field whose label mentions a captcha,
an answer, or the arithmetic prompt is dropped from the candidate set before
anything is scored, and the fill path never clicks Search. You solve it and
submit; this only types values you already entered into the app.

### 2. Blank window on launch

Two causes, both fixed.

`splash.Activate()` only *requests* that a window be shown — content is painted
when the message loop next runs. The old code followed it with a single
`Task.Yield()`, which hands back one continuation and is not the same as waiting
for a frame. Then it ran the entire database bring-up (schema rebuild,
`EnsureCreated`, seeding, a full client-update pass) **synchronously on the UI
thread**, so the loop never turned. On a cold start that is several seconds of
empty window frame.

`SplashWindow.WaitForFirstPaintAsync` now completes only once the content has
genuinely rendered (Loaded, plus one rendering tick), with a timeout so a machine
that cannot composite still boots. All the database work moved to
`Task.Run` — EF Core's `DbContext` has no thread affinity, it only must not be
used concurrently, and nothing else touches those statics until the await
returns. The splash paints immediately, the progress bar animates, and the status
line reports each stage.

### 3. Publish output trimmed

Windows App SDK 2.x self-contained drags in the **entire Windows AI platform** —
ONNX Runtime (21 MB), DirectML (18 MB), the Phi Silica / content-safety /
imaging projections, Widgets and Search — whether or not the app references any
of it. This app references none: there is no `Microsoft.Windows.AI` or
`Microsoft.ML` anywhere in the source. That was **49 MB across 36 files**, plus
**88 locale folders** of native `.mui` resources for languages the UI has no
strings in.

The new `TrimPublishOutput` target removes them after publish. These are lazily
activated native components — nothing loads them unless a WinRT class from those
namespaces is activated. Safe *for this app*; it would not be for one that used
them. `DebugType=none` in Release stops `.pdb` files being emitted at all.

The target has guard rails: it hard-fails the build if `IPDocketing.exe`,
`Microsoft.WindowsAppRuntime.dll`, `Microsoft.ui.xaml.dll` or
`WebView2Loader.dll` went missing, so an over-broad pattern can never quietly
ship a broken folder. Disable with `-p:TrimPublishOutput=false`.

Roughly 457 files → ~245, and about 228 MB → ~175 MB.

**Also:** the `IPDocketing.exe.WebView2` folder in the zip you sent is WebView2's
runtime user-data store — cache, cookies, crash dumps — which it creates beside
the executable on first use and grows to tens of megabytes. It is now redirected
to `%LocalAppData%\IPDocketing\WebView2`, so it stays out of the app folder and
your portal session survives replacing the app folder on update.

### One build-breaking bug fixed from phase 30

A comment I added to `IPDocketing.WinUI.csproj` contained `--self-contained`.
A double hyphen is illegal inside an XML comment, so the project file would not
have parsed. Caught by validation this round; worth knowing it was there.
