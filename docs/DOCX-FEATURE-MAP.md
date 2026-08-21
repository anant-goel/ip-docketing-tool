# TRADEMARK MANAGEMENT SOFTWARE — spec to implementation map

Every numbered section of `TRADEMARK_MANAGEMENT_SOFTWARE.docx`, where it lives
in the code, and — stated plainly — what is genuinely automatic versus what
still needs a person. The distinction matters: a docketing tool that quietly
overstates its automation is worse than one that does less and says so.

---

## 1. Dashboard — monthly calendar of all upcoming deadlines, plus automated notification to internal team members

| Piece | Where | Status |
|---|---|---|
| Monthly calendar with density colouring per urgency | `Views/CalendarPage.xaml(.cs)` | Done |
| All deadline kinds feed it (filing, exam response, opposition, renewal, hearing) | `Services/RuleEngineService`, `SeedData` rules | Done |
| Deadline metrics on the dashboard | `Views/DashboardPage` | Done |
| Per-person digest of approaching + overdue deadlines | `Services/TeamNotificationService`, dashboard "Team notifications" strip | **Computed automatically** |
| Windows toast reminder | `MainWindow.RefreshReminders` | **Fires on its own**, once per day, 30-min timer |
| Emailing the digest to each team member | "Email" button per row | **Needs a click.** No SMTP host, no Graph token, no service account exists in this app, so nothing can leave the machine unattended. The button opens a pre-filled draft in the default mail client. |

Owner resolution order: the matter's assignee → the deadline's `ResponsibleUser`
matched against the team list by name → "Unassigned". People with a clean sheet
are omitted, because a digest that says "nothing to do" trains everyone to stop
reading them.

## 2. Trademark Master Database

`Views/MattersPage`, `Models/Matter`, `Services/MatterService`.

TM number, mark details, proprietor, class, filing date, current status,
attorney — all present and, as of this phase, **all editable**. Status, filing
date, registration number, registration date and renewal date existed in the
model but had no way in through the UI, so a matter could only ever be
`Pending` with no filing date.

Assignment to a team member is a one-dropdown "Assign" button on each row rather
than being buried in the full edit dialog.

## 3. Opposition Management Database

`Views/OppositionsPage`, `Models/Opposition`, `Services/OppositionService`.

Both directions (filed by us / filed against our marks) live in one register,
distinguished by `Direction`, as the spec asks. Case details, status tracking,
deadlines, hearing schedule and assignment are all there. Documents can now be
filed against an opposition, not just a matter — the model always allowed it,
the UI never did.

Two Indian opposition rules are seeded so the dates compute rather than being
hand-typed: opposition period 4 months from journal publication (s.21(1) /
Rule 42), counter-statement 2 months from notice (s.21(2) / Rule 44).

## 4. Trademark Journal Monitoring (weekly)

`Services/JournalFetchService`, `Views/JournalPage`.

**Genuinely automatic.** The journal listing page at
`search.ipindia.gov.in/IPOJournal/Journal/Trademark` has no login, no OTP and no
CAPTCHA, so "Pull latest weekly issues" really does read it live and log each
issue with its class-range PDF links. There is also a fetch-by-date-and-class
lookup.

Fragility, honestly: it parses the page's table generically rather than by CSS
selector, which survives cosmetic changes but not a structural rewrite of the
page. That risk is inherent to any scraper.

## 5. Trademark Status Tracker, including opposition status

`Views/StatusTrackerPage`, `Services/StatusTrackerService`. **New this phase.**

One mark, complete history: current status and all identity fields, every logged
event, every deadline with its nominal/operative pair and governing rule, every
opposition touching the mark in either direction, and every document grouped by
the categories the spec names — examination reports, hearing notices, orders,
opposition proceedings, registration certificates, TMR portal documents
(`Data/DocumentTypes`).

**"Tool to print the status and documents"**: the Print button renders a
self-contained HTML sheet to `%LocalAppData%\IPDocketing\Print` and opens it in
the default browser with the print dialog already up. That also gives
save-as-PDF for free. WinUI 3's `PrintManager` needs MSIX package identity and a
window-handle interop path this deliberately unpackaged app does not have, so
this is the route that actually produces paper rather than a button that throws.

## 6. Trademark Search

`Views/TrademarkSearchPage`, `MatterService.Search`. **New this phase.**

All four match modes (exact, contains, phonetic, starts-with), the word/device
split, the three additional axes (proprietor, attorney/agent, state) plus class,
and both result filters the spec asks for — status of the mark, and any alert
reflected on the status page (`Matter.PortalAlert`, new field).

**Scope, so this is never mistaken for something it isn't:** this searches the
marks recorded in *this docket*. It is not a register search. Searching IP
India's register needs a session behind a CAPTCHA a human has to solve — that is
what the IP India Portal page (embedded WebView2) is for.

Phonetic matching compares the Soundex key of the full mark *and* of its first
word, so "KWIK BRITE" still finds "QUICK BRIGHT"; single-key matching on the
whole string misses multi-word marks almost entirely.

## 7. Trademark Watch Service (weekly)

`Services/WatchService`, `Views/JournalPage`.

Compares marks from a journal issue against the client portfolio using
normalised Levenshtein similarity and records anything at or above the
threshold. "Watch report" generates a printable HTML report grouped by client,
plus a CSV alongside it.

**Not automatic:** the published marks are pasted in, one per line. Extracting
mark listings out of the journal PDFs would need OCR/PDF parsing that is not
built. `IIndiaIpSearchConnector` is the seam where that would land.

The score is an edit-distance shortlist for human review, **not** a
likelihood-of-confusion opinion.

## 8. Automated client updates

`Services/ClientUpdateService`, `Views/ClientUpdatesPage`.

**The drafting is genuinely automatic** — on startup, every client whose last
update is more than seven days old gets a fresh one written, with nobody asking
(`GenerateDueUpdates`, called from `App.OnLaunched`).

**The sending is not, and cannot be from here.** "Open in mail app" produces a
pre-filled draft. The recipient is deliberately left blank: client contact
addresses are not stored anywhere in this schema, and a local docketing tool has
no good reason to start holding client PII.

---

## What would make the un-automated parts automatic

Both gaps come down to one missing thing: a mail transport.

- **SMTP** — a host, port, and an app password, stored through the existing
  encrypted `EncryptionService` path used for API keys.
- **Microsoft Graph** — an app registration in your tenant, if the firm runs
  Microsoft 365.

Either one is a credential only you can create. Once one exists, the digest and
client-update paths already produce finished subject lines and bodies; they would
just need a send call instead of a `mailto:` launch.

---

# Phase 32 additions (beyond the spec)

## Renewals (`Services/RenewalService`, `Views/RenewalsPage`)

The largest gap in the app until now, and the one that actually loses marks.

A registered Indian trademark lasts **ten years from the date of application**
(s.25(1)-(2)) — not from the certificate date, which is what trips people up,
since certificates often issue years after filing. Four dates are docketed, not
one, because docketing only the last is how firms lose marks:

| Date | Basis |
|---|---|
| Renewal window opens | 1 year before expiry (TM-R may be filed from here, Rule 57) |
| Renewal due | the expiry date itself |
| Late renewal closes | expiry + 6 months, surcharge payable (s.25(4)) |
| Restoration closes | expiry + 12 months; the mark is gone after this |

Docketing is idempotent and runs at every launch, so a mark can't sit on the
register without its s.25 dates because nobody pressed a button. "Mark renewed"
rolls the term forward another ten years **from the previous expiry, not from
the payment date** — renewing early doesn't shorten the new term.

Where a matter has no filing date, the term is anchored on the registration date
and the deadline says so explicitly, rather than quietly producing a date that
could be years wrong. Where there's no date at all, nothing is generated — an
invented renewal date is worse than none, because it looks authoritative.

## Portfolio import / export (`Services/PortfolioImportService`)

Nobody re-types four hundred marks by hand, so a docketing system without an
importer is a docketing system with an empty database. Equally, an app you can't
get data out of is one no sensible person commits a portfolio to.

Two-phase by design: validate and preview first, commit second. Headers are
matched case-insensitively in any order against a list of aliases, so a sheet
from another system usually imports after renaming headers rather than
reordering columns. Rows match existing matters on application number then
matter number, so re-importing a corrected sheet updates rather than duplicates.

**Dates are parsed day-first.** Indian practice writes 04/12/2019 as 4 December;
.NET on an en-US machine reads that as 12 April. On a renewal anchor that's a
silent eight-month error, so ISO is tried first, then explicit day-first formats,
and anything ambiguous produces a warning naming the date it settled on.

Freshly imported registrations get their renewal dates docketed immediately — an
imported portfolio with no renewal deadlines is exactly the failure this app
exists to prevent.

## Agent / attorney code (`Matter.AttorneyCode`, `TeamMember.AgentCode`)

Stored separately from the attorney's name, because the name is what the register
displays while the code is what identifies a firm's filings unambiguously — two
agents share a surname far more often than a registration number.

## Importing your own filings from IP India (`PortalScripts.ExtractTables`, `TableImportMapper`)

**The public search cannot do this.** `tmrsearch.ipindia.gov.in/tmrpublicsearch`
offers Wordmark, Vienna Code and Phonetic only — there is no agent-code facet,
so there is no way to ask it "show me everything filed under my code". Blog
posts claiming otherwise do not match the live form.

**Your own CEFS account can.** Sign into Comprehensive e-Filing Services with
your own agent credentials and your applications list is right there. That is
your data, in your session — the only lawful route to a bulk view of your
portfolio.

The flow:

1. **My filings (CEFS)** opens the e-Filing login in the embedded browser. You
   sign in yourself; the app never handles the credentials.
2. Navigate to your applications list.
3. **Import filings from page** reads the tables already rendered on screen.
4. Pick the table (if the page has several), then confirm the column mapping —
   pre-filled by matching header wording against the phrasings Indian portals
   use ("Date of Application", "Valid Upto", "Proprietor").
5. Preview, then import.

Two design decisions worth stating:

**No hard-coded selectors for CEFS.** I have never seen that page's DOM, and a
targeted selector would repeat the prior-art bug — it would half-work and
quietly produce wrong data. Reading every table generically and asking you to
confirm the columns works on the CEFS list, on an e-Register result, or on any
other HTML table, and keeps working when the page changes.

**Mapping is confirmed, never silent.** A wrong column would write hundreds of
records with a registration date sitting in the filing date field, which anchors
every renewal term years off, and nothing on screen would say so.

Everything routes through `PortfolioImportService` — the same validation, the
same day-first date parsing, the same duplicate detection and preview as a CSV
import. There is no second, weaker path.

**Limit:** only rows currently rendered are read. If the list is paginated, set
it to show all rows or import each page in turn.

---

# Phase 34 — the unattended pipeline

## What runs with no human at all

`Services/AutoSyncService` + `JournalMarkParser` + `PdfTextExtractor`, with
`Views/AutomationPage` as the control surface.

Every 1–24 hours (your choice) the app, unprompted:

1. Reads the Journal listing and records any issue it hasn't seen
2. Downloads the PDFs into a local library, resumable and deduplicated
3. Extracts the text — real text layer where there is one, OCR where there isn't
4. Parses the published marks out of that text
5. Runs the similarity watch against your portfolio
6. Raises conflict alerts

No clicks, no login, no CAPTCHA. Each stage stamps its own completion time on
the issue row, so an interrupted run resumes rather than re-downloading
hundreds of megabytes.

**Why this one can be automatic when the register cannot:** the Journal listing
and its PDFs are published openly — no account, no OTP, no CAPTCHA, no rate
gate. Reading a public government publication on a schedule is what it is
published for. The register search sits behind a CAPTCHA precisely because the
Registry has decided automated bulk access to it is not on offer. This pipeline
does the first and never touches the second.

## Text extraction and OCR

Two paths, and the difference is tracked everywhere downstream:

- **Text layer (PdfPig)** — exact. The characters come out of the file as
  published. The Journal is typeset digitally, so this is the normal path, and a
  400-page issue takes seconds.
- **OCR (`Windows.Data.Pdf` + `Windows.Media.Ocr`)** — a guess, used only for
  pages with no text layer.

**Windows' own OCR rather than Tesseract**, deliberately. Windows ships both an
OCR engine and a PDF rasteriser as OS components: no extra native binaries, no
~30 MB of language data in the publish output, nothing more for the trim target
to reason about, and no new way for the Actions build to fail. Tesseract is more
accurate on hard scans, but this app is already carrying more runtime than it
needs.

`ExtractionResult` reports which method produced it, and the parser drops every
confidence score by 20 points for OCR text. That is not pessimism for its own
sake: OCR is a *worse* guess than usual on trademark journals, because marks are
set in stylised display type OCR engines aren't trained on, and a mark is often a
coined word with no dictionary to fall back on.

If Windows has no OCR language pack installed, that is reported rather than
silently yielding nothing — a watch service that quietly reads nothing is worse
than one that says it couldn't read.

## Parsing the Journal

The Journal is a typeset publication, not a data feed, and its layout drifts
between issues and classes. So the parser anchors on the one thing that is
reliable — a 6–8 digit application number followed by a date — and then makes a
best effort at the mark, proprietor and class in the block around it, scoring
each entry.

Low-confidence entries are still returned and flagged. For a watch service, a
possible conflict you review and dismiss costs a minute; one you never saw costs
a mark. Nothing in the app treats a high score as permission to skip a human.

## The honest split, shown on screen

| | |
|---|---|
| **Fully automatic** | Journal discovery, download, extraction, OCR, parsing, watch, alerts |
| **One solve, then unattended** | e-Status bulk fetch and document download — you solve one CAPTCHA, the queue then works through hundreds of numbers in that session |
| **Never automated** | The CAPTCHA itself, and CEFS sign-in |

This is on the Automation page rather than buried in a readme, because
misunderstanding it is exactly how someone ends up believing their register data
is current when only the Journal side has refreshed.

Auto-sync ships **off**. The first pass downloads several large PDFs, and doing
that unasked on a metered connection isn't the app's decision to make.

---

# Phase 35 — accuracy of matching

## The problem with the old score

The watch used one signal: normalised Levenshtein distance over raw strings.
That is wrong in both directions, and I verified each of these by transcribing
the logic and running it:

| Pair | Old score | New score |
|---|---|---|
| SHUBH LAXMI / SHUBH LAXMI FOODS PVT LTD | 44% — **missed** | 100% |
| KWIK BRITE / QUICK BRIGHT | 50% — **missed** | 92% (phonetic) |
| LAXMI / LAKSHMI | 57% — **missed** | 92% (phonetic) |
| SUNRISE / SUN RISE | 88% | 100% |
| SUPER FOODS / SUPER TOOLS | 82% — **false alarm** | 27% |
| ZODIAC / ZODIAK | 83% | 92% (phonetic) |

The false negatives are the expensive ones — a conflict nobody saw. The false
positive is the corrosive one: an alert list full of marks sharing only "SUPER"
is a list nobody reads.

## `MarkSimilarityService`

Five independent signals; the strongest governs, and each is named:

1. **Spelling** — edit distance after folding case, diacritics, punctuation, spacing
2. **Token set** — distinctive words only, order-independent
3. **Phonetic** — Metaphone-style key with Indian-English variants folded in (KSH/X, PH/F, V/W, doubled letters, transliterated vowels)
4. **Containment** — one distinctive core wholly inside the other
5. **OCR-tolerant** — re-run with the characters OCR actually confuses treated as equal, consulted only when the text came from OCR

Plus:

- **Non-distinctive words stripped** before comparison — corporate forms and the generic trade words that appear across thousands of Indian marks.
- **Devanagari transliteration**, so a Devanagari mark can be compared against a romanised portfolio.
- **Class weighting** — same class raises the score, related classes (a working Nice coordination shortlist) hold it, unrelated classes lower it but never zero it, because a strong mark can be opposed across classes on reputation.

Two fixes came out of testing rather than theory: `IGHT→IT` and `KW→K`. Without
them "KWIK BRITE" / "QUICK BRIGHT" scored 45%, because QU collapsed to K while
KW went to KV, and the silent GH in BRIGHT survived.

## Explainability

Every alert now records which signal fired and why, shown in the row, on the
printed report and in the CSV. A bare percentage is something a reviewer can
neither check nor trust, and unexplained alerts are the ones people stop
reading. OCR-derived alerts carry a "verify against the PDF" flag.

## Performance

Levenshtein was allocating a full `(n+1)×(m+1)` matrix per comparison. On a
Journal run that is one matrix per published mark per portfolio matter — a
400-page issue against 500 marks is hundreds of thousands of allocations. Now
two rows. Portfolio marks are also normalised once per run rather than inside
the inner loop, and a cheap 55% reject runs before any class work.

## Fuzzy duplicate detection on import

A row with no application number is checked against existing marks; a ≥90% match
**warns** and names the probable duplicate. It never silently merges — a
near-identical name in a different class is often a genuinely separate
registration, and collapsing two real matters into one is far worse than a
duplicate you can see and delete.

## Search phonetics

`MatterService`'s phonetic search mode now uses the same key. Classic Soundex
treats LAXMI and LAKSHMI as unrelated, which on an Indian register is the single
most common variant there is.

---

# Phase 37 — trademark file auto-fetcher

`PortalScripts.ExtractDocumentLinks` + `FetchFileAsBase64`,
`Services/DocumentIngestService`, driven from the IP India Portal page.

Paste application numbers, press **Fetch documents for these numbers**, and the
app opens each status page in turn inside your existing session, downloads every
document it lists, classifies it, and files it against the matching matter.
One CAPTCHA solve covers the whole run.

## How the download works

The documents are not public URLs — fetching them from outside the browser
returns a login page. So the fetch runs *inside the page context* with
`credentials: 'include'`, carrying the session cookies established when you
signed in and solved the CAPTCHA. Files come back base64 and are written to
`%LocalAppData%\IPDocketing\Documents\<matter>\`.

Capped at 25 MB per file: marshalling larger through `ExecuteScriptAsync` holds
it three times over in memory, and a status-page attachment above that size is
far more likely to be a wrong link than a real filing.

## Deduplication is by content, not name

The Registry serves the same document under different URLs and display names
across visits, often as a generic `ViewDocument.pdf`. Every download is SHA-256
hashed and compared against what's already on the matter, so re-running adds
nothing. A docket where each refresh appends another copy of the same
examination report is one nobody can read.

## Classification

Link label plus surrounding row text maps onto the docx section 5 categories.
Ordered most-specific first — "Reply to Examination Report" must file as
correspondence, not as an examination report, and it contains the latter string.
Anything unrecognised becomes "TMR Portal Document" rather than "General",
because knowing where it came from is more useful than not.

## Status changes become events

With the checkbox ticked, the status read off the page is applied to the matter
— but a change is written as a real `Event` in the prosecution history, not a
silent field overwrite. Status is what the whole docket turns on (Objected
starts a reply clock, Advertised starts the opposition period), so what it was
before stays visible.

## Failure handling

- **Session expiry** is detected by an HTML content-type on a document fetch —
  the server returns the login page with a 200 status. The run stops and says so
  rather than filing login pages as examination reports.
- **Unmatched numbers** are reported and skipped. Filing a document against no
  matter leaves an orphan nobody finds again.
- **Re-running after an interruption** is safe — already-filed documents are
  skipped by hash.

## What this is not

It does not solve the CAPTCHA and does not sign in for you. The Registry gates
the status page deliberately; this works inside a session you opened, not around
the gate. What it removes is clicking Download forty times and then filing forty
PDFs by hand.

---

# Phase 40 — driven by real screenshots

## Journal Watch: the "01 Jan 1601" bug

Your screenshot said *"No journal issue found on/before 01 Jan 1601"*. That is
`DateTimeOffset.MinValue` — the FILETIME epoch — which is what a WinUI
`DatePicker` returns when it has never been touched. The fetch was asking IP
India for a journal published before the Registry existed, and correctly finding
none. The picker is now seeded with today's date, with a guard for any other
route to an unset value.

The "Pending review" badges were correct, incidentally — those issues had been
discovered but never downloaded, which is exactly what that state means.

## Find a name in the Journal

`JournalSearchService`, "Find a name" on the Journal Watch page.

Searches downloaded Journal PDFs for a proprietor or agent name, reports
**which issue and which page**, and saves the surrounding entry as a text file.

This is a different question from the similarity watch. That compares published
*marks* against your portfolio. This finds everything published under a named
*party* — what you want for "did anything go through under KARTIK TRADE MARKS
COMPANY this week?".

Matching is on normalised text, and a page counts as a hit when the clear
majority of distinctive words appear, not only on an exact phrase. Journal
typesetting breaks names across lines and abbreviates them, so exact-phrase
matching alone would miss most genuine appearances. "KARTIK TRADE MARKS COMPANY"
still hits a page reading "M/S KARTIK TRADEMARKS CO.".

**Issues that haven't been downloaded are reported separately, never counted as
"no match".** "Not checked" and "not published" are completely different answers
and must not be conflated.

## e-Register navigation, scripted from your screenshots

`PortalScripts.EStatusStep` walks the flow you showed me, one step per call:
tab → National/IRDI radio → number → (you type the captcha) → View.

Split into steps rather than one script because each transition is a postback:
the next control does not exist in the DOM until the previous page returns. A
single script would find nothing.

`ReadStatusResult` reads "Status:" and "Sub Status:" from the loose text above
the table — from your screenshot that's where the value that matters
("Accepted & Advertised") lives, not in the table itself.

`OpenResultPanel` + `ReadOpenPanelRows` handle both modals. They have different
shapes:

- Uploaded Documents: S.No | Document description | Document Date | View
- Correspondence & Notices: S.No | Corres. No | Corres. Date | Subject | Despatch No | Despatch Date | View

so the description column is found **by header name**, not by index. Filing an
examination report under the wrong label because a column shifted is exactly the
silent error that makes a docket untrustworthy.

---

# Phase 42

## Gmail OTP credentials — the missing file

The app looks for `gmail_client_secret.json` in `%LocalAppData%\IPDocketing\`,
but nothing in the UI said so, showed you the folder, or let you put a file
there. A required setup step with no visible affordance is indistinguishable
from a broken feature — which is exactly how it looked.

Settings now has a Gmail section: status badge, the exact expected path,
a file picker, "Open data folder", setup steps, and Remove.

The picker **validates before accepting**. A Google credentials download can be
an OAuth client, an API key, or a service account, and only an OAuth client of
type *Desktop app* works here. Each wrong type gets a specific message —
a service account has no inbox of its own; a Web application client expects a
redirect URI this app can't provide. Copying the wrong file in would leave the
app looking configured and then failing deep inside the Google library with a
message that explains nothing.

Remove also deletes the token store, because that holds a live refresh token —
leaving it would mean standing mailbox access after you thought you'd revoked it.
The dialog points at myaccount.google.com for full revocation.

## Guided e-Status run

Walks the flow from your screenshots per application number: tab →
National/IRDI → type number → **pause for you to solve the captcha and press
View** → read status → open both panels → file every document.

Each step is a separate call with a wait, because every transition is an ASP.NET
postback: the National/IRDI radio doesn't exist in the DOM until the tab click
round-trips, and the number box doesn't exist until the radio does.

The modal-close step was added after re-reading screenshot 7 — the modal overlays
the panel buttons, so without closing it the second panel could never be opened.

---

# Phase 45

## Import failure: "No column is mapped to Mark"

Your screenshot was right and the importer was wrong. The **Filed Applications**
page has no mark column at all — Sr.No, Form Type, Temp#, Form/Application
Number, Class, Filing Date, Appl. Type, Appl. Ref. No., Appl. Status. Nothing
else. Refusing to import without a mark made that entire page unimportable.

An application number *is* a usable identity for a docket record. A mark is now
required only when there's no application number. Rows without one are recorded
as `[Unnamed - app 7837113]` — deliberately obvious rather than blank, so they're
findable and clearly incomplete — and a Guided e-Status pass fills in the real
name afterwards.

## Journal search: why "not found" kept coming back

The search only ever looked at issues whose PDF had already been downloaded,
and nothing had downloaded any. Every issue was skipped, **zero pages were
searched**, and the honest "0 hits" read exactly like "the name isn't in the
Journal". Those are completely different answers.

Two fixes:

- The search now **downloads what it needs** before searching, rather than
  skipping it.
- When nothing could be obtained, it says so in capitals: *NOTHING WAS SEARCHED
  — this is not the same as the name being absent.*

## Tesseract

Added as the preferred OCR engine, driven as a **subprocess** rather than
through a .NET binding. That was a deliberate call:

- The common `Tesseract` NuGet wrapper ships x86/x64 natives only — **no
  win-arm64**. On your Snapdragon it fails at load with a `DllNotFoundException`
  that explains nothing. `NAPS2.Tesseract.Binaries` is the only package I found
  publishing Windows ARM64 builds, and what it publishes is the executable.
- The in-process wrappers also need the VS2022 C++ redist, which isn't on every
  machine.
- A subprocess isolates crashes: a segfault on a malformed page kills a child,
  not the app.

Everything now routes through `ChainedTextExtractor`, in strict order:

1. **PDF text layer** — exact characters, always preferred
2. **Tesseract** — for pages with no text layer, when installed
3. **Windows OCR** — always-available fallback

The ordering is not arbitrary. OCRing a page that already has a text layer
replaces real characters with guesses, which on a mark like "KWIK BRITE" is
exactly how you lose a conflict you should have caught.

Documents filed from the portal are now OCR'd on ingest and their text stored on
the record, so they're searchable rather than opaque files.

**Not bundled**: binaries plus English data are ~45 MB, which would undo the
publish trimming. Settings has a locator, and reports which engine is actually
live. Without Tesseract the app falls back automatically — nothing breaks.
