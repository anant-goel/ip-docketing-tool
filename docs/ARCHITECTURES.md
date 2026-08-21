# Building for x64 and ARM64

## What changed

The project already declared `<Platforms>x86;x64;ARM64</Platforms>` and
`<RuntimeIdentifiers>win-x86;win-x64;win-arm64</RuntimeIdentifiers>` — it was the
**workflow** that only ever built x64, with `-r win-x64` hard-coded in three
places. That is now a matrix.

Run the workflow and pick from the **architecture** dropdown:

| Choice | Produces |
|---|---|
| `both` (default) | `IPDocketing-win-x64.zip` and `IPDocketing-win-arm64.zip` |
| `x64 only` | just x64 |
| `arm64 only` | just arm64 |

`fail-fast` is off, so an ARM64 problem doesn't throw away a good x64 build. The
whole point of building both is finding out which ones work.

## Which one to run

- **Snapdragon X / any Windows-on-ARM machine** → `win-arm64`. The x64 build
  *will* run there under emulation, but it is measurably slower, and PDF OCR
  over a 400-page Journal is exactly the kind of CPU-bound work where that
  shows. It also blocks the NPU path entirely.
- **Intel / AMD** → `win-x64`.
- `win-x86` is declared but not built by the workflow. Add it to the matrix if
  you ever need 32-bit; there is no other reason to.

## Why the source needed no changes

Every dependency is either pure managed or ships an ARM64 native asset the SDK
selects from the publish RID:

| Dependency | ARM64 |
|---|---|
| PdfPig, EF Core, HtmlAgilityPack, Google API clients | pure managed |
| SQLitePCLRaw (`e_sqlite3`) | native, `win-arm64` asset in the bundle |
| WebView2 (`WebView2Loader.dll`) | native, RID-selected |
| Windows App SDK runtime | native, RID-selected |
| `Windows.Media.Ocr`, `Windows.Data.Pdf` | OS APIs, present on ARM64 Windows |

Nothing in the code does anything architecture-specific — no P/Invoke to a
named native library, no pointer-size assumptions, no x86 intrinsics.

## The two real gotchas

**1. WebView2 Evergreen runtime.** The embedded IP India browser needs the
**ARM64** WebView2 runtime on an ARM64 machine. Windows 11 on ARM ships it, but a
stripped or heavily-managed image may not, and the failure looks like the portal
page simply never loading. Installer:
`https://developer.microsoft.com/microsoft-edge/webview2/` — pick ARM64.

**2. signtool runs on the runner, not the target.** The optional signing step
picks an **x64** signtool deliberately, because the GitHub runner is x64 — a
signing tool signs any PE file regardless of that file's architecture. Selecting
an ARM64 signtool would produce an executable that can't run on the runner at
all. The filter was tightened from `-match "x64"` to `-match "\\x64\\"` so it matches
a path segment rather than any occurrence of the substring — the loose version
happened to work, but only by luck of how the Windows Kits tree is laid out.

## Verifying you got the right build

```powershell
# On the target machine
$env:PROCESSOR_ARCHITECTURE          # ARM64 or AMD64

# Against the produced exe
[Reflection.AssemblyName]::GetAssemblyName("publish\IPDocketing.exe").ProcessorArchitecture
```

An ARM64 build launched on x64 fails immediately with a "not supported on this
platform" style error rather than silently misbehaving, so a mix-up is obvious.

## Publishing locally

```powershell
dotnet publish src/IPDocketing.WinUI/IPDocketing.WinUI.csproj `
  -c Release -r win-arm64 --self-contained true -o publish-arm64

# On a Copilot+ ARM64 machine, keep the AI runtime so the NPU path exists:
dotnet publish src/IPDocketing.WinUI/IPDocketing.WinUI.csproj `
  -c Release -r win-arm64 --self-contained true -p:TrimWindowsAi=false -o publish-arm64
```
