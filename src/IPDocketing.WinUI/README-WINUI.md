# IP Docketing — WinUI 3 migration

This project is the primary Windows UI and shares `IPDocketing.Core` with the
legacy WPF application. Data remains under `%LOCALAPPDATA%\IPDocketing\`.

## Build

```powershell
dotnet build src/IPDocketing.WinUI/IPDocketing.WinUI.csproj -c Release -r win-x64
```

Requires Visual Studio 2022, .NET 8 and the Windows application development
workload.

## Material stack

- Window: `MicaBackdrop` with `MicaKind.BaseAlt`.
- Windows 10 fallback: `DesktopAcrylicBackdrop` when supported.
- Final fallback: solid `#080B12`.
- Panels and cards: WinUI `AcrylicBrush` with theme-aware solid fallback colors.
- High contrast: material resources resolve to system solid colors.

The recommended Windows App SDK path is used: assign `Window.SystemBackdrop`
once. Do not also attach a controller to the same window. `MicaController` and
`DesktopAcrylicController` are called only through `IsSupported()` to choose a
material safely.

## Navigation repair

`MainWindow.xaml` contains one complete `NavigationView`, one menu definition,
one settings route and one content `Frame`. Navigation is centralized in
`NavigateTo`, avoiding duplicate nested markup and repeated frame navigation.

## Migration status

| Area | WinUI 3 status |
|---|---|
| Dashboard | Live metrics and nearest deadlines |
| Matters | Live portfolio register |
| Deadlines | Live operative/nominal date register |
| Documents | Live document and OCR state register |
| Reports | CSV exports |
| Settings | Encrypted backup and audit verification |
| PTO Sync | Connection UI; live provider client still required |

