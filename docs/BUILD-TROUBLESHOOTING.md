# If the build fails

## First real build: 17 Aug 2026 — results

Four errors, all now fixed. Recording what was actually true, because several of
my earlier warnings turned out to be wrong:

| Predicted | Reality |
|---|---|
| `Microsoft.WindowsAppSDK 2.4.0` may not resolve | **Resolved fine.** Pulled `microsoft.windowsappsdk.winui 2.3.6`. The version override exists but was not needed. |
| `PdfPig 0.1.9` may not resolve | **Resolved fine.** No restore error. |
| `KeySpline` as a dictionary resource | Never reached — markup compile aborted first. Removed anyway; nothing used it. |
| Contravariant `(object, object)` handlers | Never reached. Tightened anyway. |
| — | **`CompositionTarget` is `Microsoft.UI.Xaml.Media` in WinUI 3, not `Microsoft.UI.Xaml`.** Missed this one entirely. |
| — | **`CoreWebView2Environment.CreateAsync` has no `browserExecutableFolder` named parameter** in this WebView2 build. Now positional. |

### About `WMC9999: Object reference not set` and `WMC1509`

These looked alarming and were not a real problem. `MarkupCompilePass2` needs the
compiled assembly of the project it is processing; the three C# errors meant no
assembly was produced, so pass 2 received no `LocalAssembly`, warned about it
(WMC1509), and then dereferenced null (WMC9999).

**A XAML internal error following C# errors in the same build is almost always a
cascade.** Fix the C# errors first and re-run before investigating the XAML.

---

Phases 30–37 have been through a compiler once, at phase 37. They were validated by parsing
every XAML file, cross-checking that every resource key and event handler
resolves, confirming brace balance across all C#, and transcribing the
similarity logic into Python to test its scoring — but none of that is a build.

Expect something to fail on the first run. This is the order I'd check.

---

## 1. NuGet restore fails on `Microsoft.WindowsAppSDK 2.4.0`

**Most likely single failure.** That version string came from your phase-29
project file, not from anything I verified. If the Windows App SDK never
released a 2.4.0, restore dies immediately.

```
dotnet build ... -p:WindowsAppSdkVersion=1.7.250606001
```

Known-good fallbacks, newest first: `1.7.250606001`, `1.6.250228001`,
`1.5.240627000`.

Lowering it may also require `WindowsSdkBuildToolsVersion` to come down —
`10.0.22621.3233` pairs with the 1.5/1.6 line.

## 2. Restore fails on `PdfPig 0.1.9`

New in phase 34, for Journal text extraction. MIT, netstandard2.0, so it should
be fine — but it's the only dependency I added.

```
dotnet build ... -p:PdfPigVersion=0.1.8
```

If PdfPig can't be resolved at all, the fastest way to keep building is to
delete `src/IPDocketing.WinUI/Services/PdfTextExtractor.cs` and the one line in
`App.xaml.cs` that calls `AutoSync.UseExtractor(...)`. Everything else still
works; the Journal pipeline just downloads PDFs without reading them, and the
Automation page reports that plainly.

## 3. XAML compile errors

The XAML parses as XML and every `StaticResource`/`ThemeResource` key resolves
to a definition — I checked both mechanically. What I could **not** check is
whether WinUI accepts particular constructs at compile time. The ones I'd
suspect, in order:

- `<KeySpline x:Key="LiquidEase" ...>` in `LiquidGlassMerged.xaml` — a KeySpline
  as a standalone dictionary resource. If it complains, delete those three lines;
  nothing references it yet.
- `<CornerRadius>` and `<Thickness>` as dictionary resources — standard, but
  worth knowing they're there.
- `Setter Property="ScrollViewer.HorizontalScrollBarVisibility"` in
  `GlassReadoutStyle` — an attached-property setter inside a Style.

## 4. C# errors I'd expect

- **Contravariant handler signatures.** Several pages use
  `Filter_Changed(object sender, object e)` for both `SelectionChanged` and
  `Checked`. Method-group contravariance makes this legal, but if the XAML
  compiler's generated code objects, split them into two correctly-typed methods.
- **`PrimaryButtonText = string.Empty`** to hide a dialog button — used in the
  import previews.
- **Nullable warnings** — many, none fatal. `TreatWarningsAsErrors` is off.

## 5. Publish succeeds but the app won't start

- Check `%LocalAppData%\IPDocketing\crash-log.txt` first. Every startup step
  writes there.
- If it's a schema error, delete `%LocalAppData%\IPDocketing\ipdocketing.db`.
  The pre-change snapshot in `Backups\` is your recovery.
- If the window is blank, the splash fix didn't take — tell me and I'll look
  again at `WaitForFirstPaintAsync`.

## 6. Trim removed something needed

`TrimPublishOutput` deletes the Windows AI stack and non-English `.mui`
resources. It hard-fails if `IPDocketing.exe`, `Microsoft.WindowsAppRuntime.dll`,
`Microsoft.ui.xaml.dll` or `WebView2Loader.dll` go missing, but it can't know
about everything.

```
dotnet publish ... -p:TrimPublishOutput=false
```

If that fixes a runtime failure, tell me which feature broke and I'll narrow the
patterns.

---

## Bisecting

If the build fails and the error doesn't map to anything above, the phases are
independent enough to strip back:

| Phase | Delete to remove it |
|---|---|
| 35 accuracy | `MarkSimilarityService.cs` — but `WatchService` and `MatterService` now call it |
| 34 automation | `AutoSyncService.cs`, `JournalMarkParser.cs`, `PdfTextExtractor.cs`, `AutomationPage.*` |
| 33 page import | `TableImportMapper.cs`, the two handlers in `IpIndiaPortalPage.xaml.cs` |
| 32 renewals/import | `RenewalService.cs`, `PortfolioImportService.cs`, `RenewalsPage.*` |
| 31 fixes | `PortalScripts.cs` — the splash and csproj changes should stay |

Each also needs its `App.xaml.cs` wiring and `MainWindow` nav entry removed.

**Send me the first 20 lines of the actual error.** That's worth more than any
further guessing from me.
