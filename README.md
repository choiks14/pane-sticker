# PaneSticker

A translucent, always-on-top overlay that labels every pane in **Windows Terminal** with its actual
working directory — synced live as you split, resize, or move the window.

Windows Terminal gives you no way to tell which split is which project. PaneSticker draws a border
and a folder-path badge over each pane, so a wall of terminals stops being a guessing game.

## Features

- **Live pane tracking** — follows splits, resizes, window moves, tab switches, and focus changes
- **Real working directories**, not guesses — resolved from the shell's own process tree
- **Always on top**, click-through — the overlay never steals focus or blocks input
- **Adjustable opacity** — slider plus global hotkeys
- **Per-monitor DPI correct** — pixel-accurate on mixed 100% / 150% / 200% setups
- Manual pane names, configurable badge content, colors, position, and border thickness
- Single portable `.exe` — no installer, no runtime prerequisite

## Install

Download `PaneSticker.exe` from [Releases](../../releases) and run it. That's it — it's a
self-contained build, so no .NET runtime is required. The app lives in the system tray.

## Usage

Right-click the tray icon for settings. Defaults show each pane's full folder path in the
top-left corner.

| Hotkey | Action |
| --- | --- |
| `Ctrl` + `Alt` + `P` | Toggle overlay |
| `Ctrl` + `Alt` + `-` | Decrease opacity |
| `Ctrl` + `Alt` + `=` | Increase opacity |

Settings are stored at `%APPDATA%\PaneSticker\settings.json`.

## How it works

Windows Terminal exposes neither pane geometry nor per-pane working directories through any public
API. PaneSticker reconstructs both:

**Geometry** — each pane is a `TermControl` element in the window's UI Automation tree. Its
`BoundingRectangle` gives exact physical-pixel bounds. A global `WinEvent` hook triggers an
immediate rescan on move, resize, focus, and reorder; a background poll covers everything else.

**Working directory** — resolved by joining two things Windows *does* expose:

1. Each pane's shell is a direct child of `WindowsTerminal.exe`. `AttachConsole` + `GetConsoleTitle`
   reads that pane's console title, which matches the `HelpText` UI Automation exposes for the same
   pane — an exact pane-to-process link.
2. The real directory lives in the shell's *descendants*, not the shell itself (PowerShell's
   `Set-Location` never changes the process CWD). Reading `RTL_USER_PROCESS_PARAMETERS.CurrentDirectory`
   from each descendant's PEB and taking the most common path yields the project root.

When two panes share a console title, the pane cannot be identified unambiguously, and PaneSticker
leaves it blank rather than showing a wrong folder. Screen-scraping the shell prompt is kept as a
fallback.

## Build

Requires the .NET 9 SDK on Windows.

```powershell
git clone https://github.com/choiks14/pane-sticker.git
cd pane-sticker
dotnet publish src/PaneSticker -c Release -r win-x64 --self-contained true `
  -p:PublishSingleFile=true -p:EnableCompressionInSingleFile=true `
  -p:IncludeNativeLibrariesForSelfExtract=true -o dist
```

## Compatibility

Windows 10 1703+ / Windows 11, x64. Requires Windows Terminal.

## License

MIT — see [LICENSE](LICENSE).

---

## 한국어

Windows Terminal 의 분할된 각 패인 위에 **실제 작업 폴더 경로**를 반투명 오버레이로 표시합니다.
분할·리사이즈·창 이동·포커스 변경을 실시간으로 따라갑니다.

- 트레이 아이콘 우클릭 → 설정에서 투명도·색상·배지 내용·위치 조정
- 단축키: `Ctrl+Alt+P` 켜기/끄기, `Ctrl+Alt+-` / `Ctrl+Alt+=` 투명도
- 클릭이 통과하므로 터미널 조작을 방해하지 않습니다
- 폴더 경로는 화면 텍스트가 아니라 셸 프로세스 트리에서 직접 읽습니다

[Releases](../../releases) 에서 `PaneSticker.exe` 를 받아 실행하면 됩니다. .NET 설치 불필요.
