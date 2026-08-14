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

