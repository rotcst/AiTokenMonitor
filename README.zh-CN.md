# AI Token 用量监控

[English](README.md) · **简体中文** · [한국어](README.ko.md)

[![Release](https://img.shields.io/github/v/release/rotcst/AiTokenMonitor)](https://github.com/rotcst/AiTokenMonitor/releases/latest)
[![License: MIT](https://img.shields.io/badge/License-MIT-blue.svg)](LICENSE)
![Platform](https://img.shields.io/badge/platform-Windows%2010%2F11-lightgrey)

一个 Windows 桌面悬浮小窗，在同一界面并排监控 **Codex** 与 **Claude Code** 的用量——额度窗口、
充值余额、Token 历史——数据直接来自各自官方来源，**不论 CLI 或桌面端是否在运行**都能读到。

> 与 OpenAI、Anthropic 无关联。只读取这两个工具在你本机已经写好的凭据和会话文件。

## 下载

到 **[Releases 页面](https://github.com/rotcst/AiTokenMonitor/releases/latest)** 下载最新的单文件
`AiTokenMonitor.exe`。免安装、无需另装运行时——自包含单文件，双击后直接显示主窗口并驻留在系统托盘；
重复启动会唤醒已有窗口。

## 功能

- **两个 Provider，一个窗口**：底部标签在 Codex / Claude 之间切换。
- **真实额度，不靠估算**：读取与 CLI 相同的官方接口——
  - Claude：`GET https://api.anthropic.com/api/oauth/usage`（即 `/usage` 背后的数据源）。
  - Codex：ChatGPT 后端 `wham/usage`、`wham/profiles/me`、`wham/rate-limit-reset-credits`。
- **5 小时与周额度**，含精确重置时间和倒计时。
- **充值余额、用量额度**、分模型周额度（Opus / Sonnet / GPT-5-Codex …）、支出上限、限额提示文案。
- **Token 历史**：累计、当天、近 7 天、逐日柱状图，以及可滚动的完整列表。
- **当前模型与上下文占用**，从本机会话记录读取——终端 CLI 和桌面端都适用。
- **三语界面**（简体中文 / English / 한국어），按系统语言自动选择，可在任一右键菜单里随时切换。
- **原生窗口行为**：DWM 系统圆角、最小化/恢复动画、最小化到任务栏、隐藏到托盘；退出只在托盘菜单，
  避免误关。

## 用量数据从哪来

两侧都不依赖客户端是否运行，因为读的是各客户端本来就会写在本机的文件：

| | 终端 CLI | 桌面端 |
| --- | --- | --- |
| Claude 凭据 | `<CLAUDE_CONFIG_DIR 或 ~/.claude>/.credentials.json` | `%APPDATA%\Claude\config.json`（OSCrypt 解密） |
| Claude 会话 | `<CLAUDE_CONFIG_DIR 或 ~/.claude>/projects/**/*.jsonl` | 同一目录 |
| Codex 凭据 | `<CODEX_HOME 或 ~/.codex>/auth.json` | 同一文件 |
| Codex 会话 | `<CODEX_HOME 或 ~/.codex>/sessions/**` | 追加 `archived_sessions/**` |

`CLAUDE_CONFIG_DIR` 和 `CODEX_HOME` 全程生效，改过配置目录也不会出现「额度读到了、Token 却是空的」
这种半通状态。

Codex 优先走官方 HTTP 接口；若本机存的令牌过期，则回退到拉起 `codex app-server`（由 CLI 自己续期）。
卡片状态行会标明当前走的是哪条路。

## 隐私

- 访问令牌**只在内存中使用**，仅用于请求官方用量接口，绝不落盘、也不发往任何第三方。
- 桌面端令牌缓存用当前 Windows 用户自己的 DPAPI 密钥解密（和桌面端同一套机制），数据不出本机。
- 会话解析只提取消息 ID、时间戳和 `usage` Token 字段，不保存任何提示词或回复。
- **Claude 预付点数余额**（计费页上的那个数字）**不显示**：Claude Code 用的 OAuth 用量接口并不返回它，
  要读它需要你完整的 claude.ai 网页会话，本程序刻意不去碰。

## 语言

界面提供简体中文、English、한국어。首次运行按系统 UI 语言选择（中文→中文、韩文→韩文、其余→英文）。
之后可在悬浮窗或托盘的右键菜单里，通过 **语言 / Language** 子菜单随时切换——即时生效、无需重启，
选择记录在 `%LOCALAPPDATA%\AiTokenMonitor\language.txt`。

## 从源码构建

需要 .NET SDK（目标框架 net8.0-windows；仓库可用 .NET 8/9/10 SDK 构建）。

```powershell
git clone https://github.com/rotcst/AiTokenMonitor.git
cd AiTokenMonitor

# 运行测试
dotnet run --project CodexWeeklyMonitor.Tests\CodexWeeklyMonitor.Tests.csproj -c Release

# 生成自包含单文件 exe
dotnet publish CodexWeeklyMonitor\CodexWeeklyMonitor.csproj -c Release -r win-x64 `
  --self-contained true -p:PublishSingleFile=true `
  -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true
```

## 许可证

[MIT](LICENSE)。
