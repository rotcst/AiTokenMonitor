# AI Token Monitor

**English** · [简体中文](README.zh-CN.md) · [한국어](README.ko.md)

[![Release](https://img.shields.io/github/v/release/rotcst/AiTokenMonitor)](https://github.com/rotcst/AiTokenMonitor/releases/latest)
[![License: MIT](https://img.shields.io/badge/License-MIT-blue.svg)](LICENSE)
![Platform](https://img.shields.io/badge/platform-Windows%2010%2F11-lightgrey)

A small always-on-top desktop widget for Windows that monitors your **Codex** and
**Claude Code** usage side by side — rate-limit windows, credit balance, token history —
straight from each provider's own data, whether the CLI or desktop app is running or not.

> Not affiliated with OpenAI or Anthropic. It only reads the local credentials and session
> files those tools already write on your own machine.

## Download

Grab the latest single-file `AiTokenMonitor.exe` from the
**[Releases page](https://github.com/rotcst/AiTokenMonitor/releases/latest)**. No install, no
runtime to set up — it is a self-contained Windows executable. Double-click to run; it lives in the
system tray after opening the main window. Launching it again restores the existing window.

## Features

- **Two providers, one window.** Toggle between Codex and Claude with the tabs at the bottom.
- **Real quota, not guesses.** Reads the same official endpoints the CLIs use:
  - Claude: `GET https://api.anthropic.com/api/oauth/usage` (the source behind `/usage`).
  - Codex: ChatGPT backend `wham/usage`, `wham/profiles/me`, `wham/rate-limit-reset-credits`.
- **5-hour and weekly windows** with exact reset times and countdowns.
- **Credit balance & usage credits**, per-model weekly buckets (Opus / Sonnet / GPT-5-Codex, …),
  spend caps, and the plan's limit notices.
- **Liquid orb mode**: double-click the card to collapse it into a small floating orb split into two
  tanks — Codex on the left, Claude on the right — whose water level is each provider's *remaining*
  quota (a full tank reads 100%, a dry tank 0%; 5-hour window if present, else weekly). The surface
  ripples continuously. Double-click the orb to expand back.
- **Token history**: lifetime, today, last 7 days, a daily bar chart, and a scrollable full list.
- **Current model & context usage**, read from your local session logs — works for both the
  terminal CLI and the desktop app.
- **Trilingual UI** (English / 简体中文 / 한국어), auto-selected from your system language and
  switchable at runtime from either right-click menu.
- **Native window behaviour**: rounded DWM corners, minimize/restore animation, minimize to
  taskbar, hide to tray. Exit lives only in the tray menu so you don't close it by accident.

## How it reads your usage

Both providers work whether or not their client is running, because the app reads the files each
client already writes locally:

| | Terminal CLI | Desktop app |
| --- | --- | --- |
| Claude credentials | `<CLAUDE_CONFIG_DIR or ~/.claude>/.credentials.json` | `%APPDATA%\Claude\config.json`, or Microsoft Store `%LOCALAPPDATA%\Packages\Claude_*\LocalCache\Roaming\Claude\config.json` (OSCrypt-decrypted) |
| Claude sessions | `<CLAUDE_CONFIG_DIR or ~/.claude>/projects/**/*.jsonl` | same directory |
| Codex credentials | `<CODEX_HOME or ~/.codex>/auth.json` | same file |
| Codex sessions | `<CODEX_HOME or ~/.codex>/sessions/**` | plus `archived_sessions/**` |

`CLAUDE_CONFIG_DIR` and `CODEX_HOME` are honoured throughout, so relocating a config directory
never leaves you with "quota loaded but tokens empty".

Codex prefers the official HTTP API; if the stored token has expired it falls back to launching
`codex app-server` (the CLI renews the token itself). The card's status line shows which route is
live.

## Privacy

- Access tokens are used **in memory only**, solely to call the official usage endpoints. They are
  never written to disk or sent anywhere else.
- The desktop token cache is decrypted with the current Windows user's own DPAPI key — the same
  mechanism the desktop app uses — and nothing leaves the machine.
- Session parsing extracts only message IDs, timestamps and `usage` token counts; it never stores
  prompts or replies.
- **Claude prepaid credit balance** (the number on the billing page) is *not* shown, because it is
  not exposed by the OAuth usage endpoint Claude Code uses — reading it would require your full
  claude.ai web session, which this app deliberately does not touch.

## Language

The UI ships in Simplified Chinese, English and Korean. On first run it follows the OS UI language
(Chinese → Chinese, Korean → Korean, everything else → English). Switch any time via the
**Language / 语言** submenu in either the floating card's or the tray's right-click menu — it applies
instantly, no restart, and the choice is remembered in
`%LOCALAPPDATA%\AiTokenMonitor\language.txt`.

## Build from source

Requires the stable .NET 10 SDK (`net10.0-windows`; pinned by `global.json`).

```powershell
git clone https://github.com/rotcst/AiTokenMonitor.git
cd AiTokenMonitor

# run the test suite
dotnet run --project CodexWeeklyMonitor.Tests\CodexWeeklyMonitor.Tests.csproj -c Release

# produce the self-contained single-file exe
dotnet publish CodexWeeklyMonitor\CodexWeeklyMonitor.csproj -c Release -r win-x64 `
  --self-contained true -p:PublishSingleFile=true `
  -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true
```

## License

[MIT](LICENSE).
