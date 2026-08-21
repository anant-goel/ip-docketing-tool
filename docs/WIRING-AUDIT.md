# Wiring audit

Method-level scan of both projects for code that exists but nothing calls.
19 public service methods had no caller. Here is what each one was, and what
I did about it.

## Fixed — these were real functional gaps

| Method | What was broken |
|---|---|
| `JournalService.MarkReviewed` | Every Journal issue showed **"Pending review" forever** with no way to clear it. This is visible in your screenshot and is part of why the page looked stuck. The badge is now a button. |
| `BackupService.SnapshotBeforeDestructiveChange` | Written, then never used — `App.xaml.cs` inlined the encryption call instead. Now used by the restore path. |
| `HolidayCalendarService.AddOfficeClosures` | The seam for Indian movable holidays (Diwali, Holi, Eid) had **no caller**, so those dates were never in the calendar and a deadline could roll onto one. Now loads `holidays.txt`, created with instructions on first run. |
| Backup restore | `RestoreFrom` had no caller: backups could be taken but never restored, which makes the feature decorative. There is now a Restore button — and `BackupList` was `SelectionMode="None"`, so a selection-based restore could never have worked anyway. |

Restore is staged rather than immediate: EF Core holds an open connection to the
database file, so swapping it underneath a live `DbContext` corrupts rather than
restores. The decrypted file is written alongside and swapped in at next launch,
after a safety snapshot of the current database.

`RestoreFrom` itself remains uncalled — it writes straight over the target path,
which is exactly what can't be done while the app is running. It is superseded,
not missing.

## Left alone deliberately — superseded compatibility wrappers

`SearchByMarkExact`, `SearchByMarkContains`, `SearchByMarkStartsWith`,
`SearchByMarkPhonetic`, `SearchByProprietor`, `SearchByAttorney`,
`SearchByState`, `SearchByAssignee`.

All eight were kept as thin wrappers when the search was rewritten into
`Search(MarkSearchQuery)` so nothing would break. The search page uses the
unified method. They are dead but harmless.

## Still unwired — real gaps, not yet built

| Method | Missing UI |
|---|---|
| `DeadlineService.AddManual` | No way to add a deadline by hand. Everything is rule-generated, so anything the rule engine doesn't cover can't be docketed at all. **This is the most significant remaining gap.** |
| `OppositionService.AssignTo` | Oppositions can't be assigned to a team member from the UI, though matters can. |
| `OppositionService.GetByDirection` | No filed-by-us / filed-against-us filter on the oppositions list. |
| `WatchService.GetAllIncludingDismissed` | No way to see dismissed alerts again. |
| `MatterService.GetFamily` | Parent/child matter families are modelled but never displayed. |

## Known incomplete elsewhere

- One `SELECTOR TODO` remains in `IpIndiaPortalPage.xaml.cs` — the bulk-fetch
  result table parser is still a guess. The panel readers built from your
  screenshots are not guesses; that one path is.
- `AutoSyncService` needs `UseExtractor` called or it downloads without reading.
  It is wired in `App.xaml.cs`; if PdfPig is ever removed, that breaks silently
  except for a note on the Automation page.
