# Third-party notices

## Reference implementations vendored under `/third_party`

Both are included as **source references only**. Neither is compiled, neither is
referenced by any project, and the WPF demo's project file has been renamed to
`DemoApp.csproj.reference` so it can never be picked up by a solution-wide or
directory-wide build. The `IPDocketing.WinUI.csproj` also carries explicit
`<Compile Remove>` / `<Page Remove>` entries for the folder.

### `third_party/liquid-glass-web`

Web (SVG + WebGL) liquid glass implementation. Its effect is a `backdrop-filter`
feeding an `feDisplacementMap` driven by a generated specular map.

**What was portable:** the tuned constants, which are what actually make the
effect read as glass. Ported verbatim into
`src/IPDocketing.WinUI/Themes/LiquidGlassMerged.xaml`:

| Reference value | Ported to |
|---|---|
| `--tint-color` white @ 6% | `GlassTintOverlayBrush` |
| `--shadow-color` white @ 45%, blur 20, spread -5 | `BezelInnerRimBrush` |
| `--outer-shadow-blur` 24px, black @ 18% | button bloom layer |
| `--glass-radius` 60 on a 300×200 pane | `GlassRadiusLarge/Medium/Small` (held to the same ~20–30% of the short edge rather than copied as a pixel value, which would render as a pill on a 40px control) |
| specular opacity 0.5, saturation 4, bezel 60 | `SpecularSweepBrush` |
| `cubic-bezier(0.32, 0.72, 0, 1)` | `LiquidEase` KeySpline; button transitions |

**What was not:** the displacement itself. WinUI has no backdrop displacement
filter and no equivalent.

### `third_party/wpf-liquid-glass`

WPF implementation using a pixel shader (`GlassyEffect.ps`) over a screen
capture of whatever sits behind the window.

**What was portable:** its layer ordering and edge treatment —
`#18000000` substrate over the system blur, `#4DFFFFFF` content wash, and the
four-stop diagonal rim (`#80FFFFFF → #40A6E1FF → #40FFB3E1 → #80FFFFFF`).
These became `GlassSubstrateBrush`, `GlassContentWashBrush` and
`IridescentRimBrush`.

**What was not, and why:**

- **The shader.** WinUI 3 removed `ShaderEffect` entirely; there is no
  equivalent without taking on Win2D and a composition effect pipeline. That
  would be a new native dependency and a new class of build failure on a CI
  runner, for a visual delta most people would not notice on a form-heavy app.
- **The screen-capture-per-move approach.** Recapturing the desktop on every
  window move and resize is a poor trade for a docketing app that sits open all
  day next to a browser.
- **Its macOS-style traffic-light caption buttons.** Replacing the Windows
  caption controls costs muscle memory and accessibility for no functional gain.

The system backdrop (`DesktopAcrylicBackdrop`, falling back to Mica, falling
back to solid) does the environmental blur natively and correctly, including
honouring the user's transparency, battery and high-contrast settings — which a
hand-rolled shader would not.

Licences for both are retained in their respective folders.

## NuGet packages

`Microsoft.WindowsAppSDK`, `Microsoft.Windows.SDK.BuildTools`,
`CommunityToolkit.Mvvm`, `Microsoft.Web.WebView2`,
`Microsoft.EntityFrameworkCore` (+ `.Sqlite`),
`System.Security.Cryptography.ProtectedData`, `Google.Apis.Gmail.v1`,
`HtmlAgilityPack`. Licences are as published on nuget.org.
