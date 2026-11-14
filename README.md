# PaneSticker

**English** · [한국어](#한국어)

A translucent, always-on-top overlay that labels every pane in **Windows Terminal** with its actual
working directory — synced live as you split, resize, or move the window.

Windows Terminal gives you no way to tell which split is which project. PaneSticker draws a border
and a folder-path badge over each pane, so a wall of terminals stops being a guessing game.

## Features

- **Live pane tracking** — follows splits, resizes, window moves, tab switches, and focus changes
- **Real working directories**, not guesses — resolved from each pane's own shell process
- **Stays on the project root** — while a coding agent such as Claude Code hops between folders,
  the badge keeps showing the folder the session was started in
- **Idle shells too** — a plain PowerShell pane shows its current location, even after `cd`
- **Always on top**, click-through — the overlay never steals focus or blocks input
- **Adjustable opacity** — slider plus global hotkeys
- **Per-monitor DPI correct** — pixel-accurate on mixed 100% / 150% / 200% setups
- Manual pane names, configurable badge content, colors, position, and border thickness
- Single portable `.exe` — no installer, no runtime prerequisite

## Install

Download `PaneSticker.exe` from [Releases](https://github.com/mintsoft-opensource/pane-sticker/releases)
and run it. It's a self-contained build, so no .NET runtime is required. The app lives in the
system tray.

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
API. PaneSticker reconstructs both.

**Geometry** — each pane is a `TermControl` element in the window's UI Automation tree. Its
`BoundingRectangle` gives exact physical-pixel bounds. A global `WinEvent` hook triggers an
immediate rescan on move, resize, focus, and reorder; a background poll covers everything else.
Each pane is tracked by its UI Automation `RuntimeId`, so splitting or closing a pane never moves
one pane's folder onto another.

**Pane ↔ shell** — each pane's shell is a direct child of `WindowsTerminal.exe`. `AttachConsole` +
`GetConsoleTitle` reads that pane's console title, which matches the `HelpText` UI Automation
exposes for the same pane. Leading spinner glyphs (`✳`, `⠂` …) that CLI tools animate are ignored
when matching.

**Working directory** — chosen per shell:

- **cmd and others** — `cd` changes the process CWD, so the shell's own CWD is read from its PEB.
- **PowerShell** — `Set-Location` never changes the process CWD, so the shell itself is not trusted:
  1. Waiting at a prompt → the path on the cursor line (`PS D:\path>`), read from that shell's own
     console buffer. Only the cursor line is read, so command output never affects it.
  2. Running a command → the CWD of the *shallowest* descendant (e.g. `claude.exe`), which inherits
     `$PWD` at launch. Deeper helper processes that tools spawn in other folders are ignored.
  3. Otherwise → the last folder seen for that shell.

When two panes share a console title and point to different folders, the pane cannot be identified
unambiguously, and PaneSticker leaves the badge blank rather than show a wrong folder.

### Known limitations

- Custom prompts (e.g. oh-my-posh) that don't look like `PS D:\path>` can't be parsed; the badge
  falls back to the last folder seen.
- Idle PowerShell panes with identical titles can't be told apart.

## Build

Requires the .NET 9 SDK on Windows.

```powershell
git clone https://github.com/mintsoft-opensource/pane-sticker.git
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

[English](#panesticker) · **한국어**

**Windows Terminal** 의 분할된 각 패인 위에 **실제 작업 폴더 경로**를 반투명 오버레이로 표시합니다.
분할·리사이즈·창 이동을 실시간으로 따라갑니다.

Windows Terminal 은 어느 분할 화면이 어느 프로젝트인지 알려주지 않습니다. PaneSticker 는 패인마다
테두리와 폴더 경로 배지를 그려서, 터미널을 여러 개 띄워도 헷갈리지 않게 해 줍니다.

### 주요 기능

- **실시간 패인 추적** — 분할, 크기 변경, 창 이동, 탭 전환, 포커스 변경을 따라갑니다
- **추정이 아닌 실제 작업 폴더** — 각 패인의 셸 프로세스에서 직접 읽습니다
- **프로젝트 루트 고정** — Claude Code 같은 코딩 에이전트가 작업 중에 폴더를 옮겨 다녀도
  세션을 시작한 폴더가 계속 표시됩니다
- **대기 중인 셸도 표시** — 아무것도 실행하지 않은 PowerShell 패인도 `cd` 한 현재 위치를 보여 줍니다
- **항상 위, 클릭 통과** — 포커스를 뺏거나 입력을 막지 않습니다
- **투명도 조절** — 슬라이더와 전역 단축키
- **모니터별 DPI 대응** — 100% / 150% / 200% 가 섞인 환경에서도 픽셀 단위로 정확합니다
- 패인 이름 직접 지정, 배지 내용·색상·위치·테두리 두께 설정
- 단일 실행 파일 `.exe` — 설치 프로그램도, 별도 런타임도 필요 없습니다

### 설치

[Releases](https://github.com/mintsoft-opensource/pane-sticker/releases) 에서 `PaneSticker.exe` 를
받아 실행하면 됩니다. 런타임이 포함된 빌드라 .NET 을 따로 설치할 필요가 없습니다. 앱은 시스템
트레이에 상주합니다.

### 사용법

트레이 아이콘을 우클릭하면 설정이 열립니다. 기본값은 각 패인 왼쪽 위에 폴더 전체 경로를 표시합니다.

| 단축키 | 동작 |
| --- | --- |
| `Ctrl` + `Alt` + `P` | 오버레이 켜기/끄기 |
| `Ctrl` + `Alt` + `-` | 투명도 낮추기 |
| `Ctrl` + `Alt` + `=` | 투명도 높이기 |

설정은 `%APPDATA%\PaneSticker\settings.json` 에 저장됩니다.

### 동작 원리

Windows Terminal 은 패인의 위치도, 패인별 작업 폴더도 공개 API 로 알려주지 않습니다.
PaneSticker 는 둘 다 직접 알아냅니다.

**위치** — 각 패인은 창의 UI Automation 트리에 있는 `TermControl` 요소입니다. 그 `BoundingRectangle`
이 물리 픽셀 단위의 정확한 영역입니다. 전역 `WinEvent` 훅이 이동·크기 변경·포커스·순서 변경 때 즉시
다시 스캔하고, 나머지는 백그라운드 폴링이 맡습니다. 패인은 UI Automation `RuntimeId` 로 구분하므로,
분할하거나 닫아도 한 패인의 폴더가 다른 패인으로 넘어가지 않습니다.

**패인 ↔ 셸 연결** — 각 패인의 셸은 `WindowsTerminal.exe` 의 직계 자식입니다. `AttachConsole` +
`GetConsoleTitle` 로 그 패인의 콘솔 제목을 읽으면, UI Automation 이 같은 패인에 노출하는 `HelpText`
와 일치합니다. CLI 도구가 제목 앞에 돌리는 스피너 글자(`✳`, `⠂` …)는 비교할 때 무시합니다.

**작업 폴더** — 셸 종류에 따라 다르게 정합니다.

- **cmd 등** — `cd` 하면 프로세스 CWD 가 바뀌므로, 셸 자신의 CWD 를 PEB 에서 읽습니다.
- **PowerShell** — `Set-Location` 은 프로세스 CWD 를 바꾸지 않으므로 셸 자신의 값은 믿지 않습니다.
  1. 프롬프트에서 입력 대기 중 → 그 셸 콘솔 버퍼에서 커서 줄의 경로(`PS D:\path>`)를 읽습니다.
     커서 줄만 읽으므로 명령 출력에 영향받지 않습니다.
  2. 명령 실행 중 → 가장 *가까운* 자손 프로세스(예: `claude.exe`)의 CWD 를 씁니다. 실행 시점의
     `$PWD` 를 물려받은 값입니다. 도구가 다른 폴더에서 띄우는 더 깊은 하위 프로세스는 무시합니다.
  3. 그 밖의 경우 → 그 셸에서 마지막으로 확인한 폴더를 씁니다.

두 패인의 콘솔 제목이 같은데 폴더가 서로 다르면 어느 패인인지 특정할 수 없습니다. 이때는 틀린 폴더를
보여 주는 대신 배지를 비워 둡니다.

### 알려진 한계

- oh-my-posh 처럼 `PS D:\path>` 형태가 아닌 커스텀 프롬프트는 읽지 못합니다. 이때는 마지막으로 확인한
  폴더를 표시합니다.
- 제목이 같은 대기 중 PowerShell 패인끼리는 구분하지 못합니다.

### 빌드

Windows 에 .NET 9 SDK 가 필요합니다.

```powershell
git clone https://github.com/mintsoft-opensource/pane-sticker.git
cd pane-sticker
dotnet publish src/PaneSticker -c Release -r win-x64 --self-contained true `
  -p:PublishSingleFile=true -p:EnableCompressionInSingleFile=true `
  -p:IncludeNativeLibrariesForSelfExtract=true -o dist
```

### 호환성

Windows 10 1703 이상 / Windows 11, x64. Windows Terminal 이 필요합니다.

### 라이선스

MIT — [LICENSE](LICENSE) 참고.
