# Build and verify loop

Run this from the repo root and paste the output back. That is the whole loop —
everything else waits on a clean build.

## The command

```powershell
cd "D:\editing tool\github\ip-docketing tool"

dotnet build src\IPDocketing.WinUI\IPDocketing.WinUI.csproj `
  -c Debug -r win-arm64 --self-contained `
  -v minimal 2>&1 | Tee-Object -FilePath build-output.txt
```

Then paste `build-output.txt`, or just the lines containing `error`:

```powershell
Select-String -Path build-output.txt -Pattern "error" | Select-Object -First 60
```

Sixty is usually plenty — C# errors cascade, so the first few normally explain
the rest.

## What I expect to be wrong

Roughly twelve files have been changed and never compiled. I have already
scripted the checks that catch the usual WinUI build breaks:

- every XAML event handler resolves to a method (all 17 pages)
- every `{Binding}` in the templates I touched resolves to a real member
- every custom theme resource key used in XAML is defined in `Themes/`
- every `App.X` static referenced exists
- `PortalReading`'s 14 positional parameters match its construction site in order
- no duplicate members left behind by the splices
- braces, parens and XML all balance

What that cannot catch, and where the errors will most likely be:

1. **Nullability warnings** — `Nullable` is enabled on both projects. If
   `TreatWarningsAsErrors` is on for the WinUI project, some of my `string?`
   handling will need null-forgiving operators or guards.
2. **Overload resolution** — e.g. `HtmlEntity.DeEntitize`, `File.Move(..., overwrite:)`,
   `Text(JsonElement, string)` against a `JsonElement?`.
3. **`DatePicker.SelectedDate`** — I am confident this exists in WinUI 3; if the
   Windows App SDK version pinned here predates it, the fix reverts to `Date`
   plus a `Loaded` handler.
4. **Storyboard in a ResourceDictionary** — `AmbientDrift` resolves
   `Storyboard.TargetName` against `RootGrid`'s namescope. If it throws it is
   caught, and the background is simply static; it will not fail the build.
5. **`_ = App.DocumentIngest.ExtractTextAsync(docId)`** — fire-and-forget; may
   raise CS4014 if warnings are errors. The discard should suppress it.

## After the build is clean

Four checks, in this order, before we go near new features:

1. Journal Watch opens and the auto-fetch date shows **today**, not
   "day / month / year".
2. The four issues show **"No PDF link"** — that is correct, the listing gives
   none. Press **Get PDF** on 2274 and watch it move to Downloaded.
3. Bulk-fetch **7837113**: Class must read `29`, not `e-Filing`; proprietor must
   read `LEADS BRAND CONNECT PRIVATE LIMITED`, not `Proposed to be used`.
4. Fetch documents for 7837113: both `09/07/2026` examination reports
   (23189976 and 23189978) must be filed **separately**.

## Rollback at any point

```powershell
cd "D:\editing tool\github\ip-docketing tool"
git diff > my-changes.patch      # keep a copy first
git checkout -- src/             # discard all source changes
```

Your database is untouched by everything done so far: no schema version has been
bumped, and the new Journal statuses are derived from existing columns
specifically so that no rebuild is triggered.
