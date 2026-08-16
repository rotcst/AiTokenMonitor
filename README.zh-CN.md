# AI Token 用量监控

[English](README.md) · **简体中文** · [한국어](README.ko.md)

[![Release](https://img.shields.io/github/v/release/rotcst/AiTokenMonitor)](https://github.com/rotcst/AiTokenMonitor/releases/latest)
[![License: MIT](https://img.shields.io/badge/License-MIT-blue.svg)](LICENSE)
![Platform](https://img.shields.io/badge/platform-Windows%2010%2F11-lightgrey)

一个 Windows 桌面悬浮小窗，在同一界面并排监控 **Codex** 与 **Claude Code** 的用量——额度窗口、
充值余额、Token 历史——数据直接来自各自官方来源，**不论 CLI 或桌面端是否在运行**都能读到。

> 与 OpenAI、Anthropic 无关联。只读取这两个工具在你本机已经写好的凭据和会话文件。

## 下载

到 **[Releases 页面](https://github.com/rotcst/AiTokenMonitor/releases/latest)** 下载最新的版本化单文件
`AiTokenMonitor-x.y.z.exe`。免安装、无需另装运行时——自包含单文件，双击后直接显示主窗口并驻留在系统托盘；
重复启动会唤醒已有窗口。

## 功能

- **两个 Provider，一个窗口**：底部标签在 Codex / Claude 之间切换。
- **真实额度，不靠估算**：读取与 CLI 相同的官方数据面——
  - Claude：`GET https://api.anthropic.com/api/oauth/usage`（即 `/usage` 背后的数据源）。
  - Codex：头部额度采用 `codex app-server` 的 `account/rateLimits/read`，并用 ChatGPT 后端
    `wham/usage`、`wham/profiles/me`、`wham/rate-limit-reset-credits` 补充详情。
- **5 小时与周额度**，含精确重置时间和倒计时。
- **充值余额、用量额度**、分模型周额度（Opus / Sonnet / GPT-5-Codex …）、支出上限、限额提示文案。
- **液面悬浮球**：从主窗口右键菜单切换到小悬浮球，左右两个水舱分别是 Codex 与 Claude，水位＝各自的
  **剩余额度**（满舱 100%、见底 0%）。单击左侧或右侧可分别切换该服务的 5 小时/周额度；服务标题位于
  百分比上方，当前窗口和重置倒计时位于下方。通过悬浮球右键菜单可恢复主窗口。
- **Token 历史**：累计、当天、近 7 天、逐日柱状图，以及可滚动的完整列表。
- **当前模型与上下文占用**，从本机会话记录读取——终端 CLI 和桌面端都适用。
- **三语界面**（简体中文 / English / 한국어），按系统语言自动选择，可在任一右键菜单里随时切换。
- **开机自动启动**：主窗口、系统托盘、悬浮球三个右键菜单都能开关。写在当前用户的
  `HKCU\...\CurrentVersion\Run` 下，不需要管理员权限、不需要安装程序，也不会在全机范围留下任何东西。
  开机那次启动直接进托盘，不显示窗口、不抢焦点；便携 EXE 换了位置后，下次运行会自动把启动项指回新路径，
  而不是无声失效。
- **在线更新**：启动后自动检查 GitHub 最新正式版；也可从主窗口、系统托盘或悬浮球右键菜单手动检查。
  确认更新后会自动下载、核对文件大小与 SHA-256、替换原 EXE 并重启，整个过程不需要 GitHub Token。
- **原生窗口行为**：DWM 系统圆角、最小化/恢复动画、最小化到任务栏、隐藏到托盘；退出只在托盘菜单，
  避免误关。

主窗口标题旁会显示当前版本号。主窗口与悬浮球分别保存自己的屏幕位置，来回切换不会互相覆盖坐标。

## 在线更新

程序只读取 `rotcst/AiTokenMonitor` 的 GitHub **最新正式 Release**（忽略草稿和预发布版）。只有 Release
中存在匹配版本的 `AiTokenMonitor-x.y.z.exe`（兼容无版本后缀的 `AiTokenMonitor.exe`），并且 GitHub
提供有效的 `sha256:` 摘要时才允许安装。安装前会明确询问；选择“立即更新”后才下载和重启。

便携程序所在目录必须对当前 Windows 用户可写。如果 EXE 被放在需要管理员权限的目录，自动替换会失败，
此时可从 Releases 页面手动下载新版。

## 用量数据从哪来

两侧都不依赖客户端是否运行，因为读的是各客户端本来就会写在本机的文件：

| | 终端 CLI | 桌面端 |
| --- | --- | --- |
| Claude 凭据 | `<CLAUDE_CONFIG_DIR 或 ~/.claude>/.credentials.json` | `%APPDATA%\Claude\config.json`，或 Microsoft Store 版 `%LOCALAPPDATA%\Packages\Claude_*\LocalCache\Roaming\Claude\config.json`（OSCrypt 解密） |
| Claude 会话 | `<CLAUDE_CONFIG_DIR 或 ~/.claude>/projects/**/*.jsonl` | 同一目录 |
| Codex 凭据 | `<CODEX_HOME 或 ~/.codex>/auth.json` | 同一文件 |
| Codex 会话 | `<CODEX_HOME 或 ~/.codex>/sessions/**` | 追加 `archived_sessions/**` |

`CLAUDE_CONFIG_DIR` 和 `CODEX_HOME` 全程生效，改过配置目录也不会出现「额度读到了、Token 却是空的」
这种半通状态。

Codex 以受支持的 `codex app-server` 额度快照作为头部窗口，并用官方 HTTP 接口补充账号详情；
若 app-server 不可用，则回退到 HTTP 快照。刷新失败时会保留最后一次成功数据，但明确标记为旧数据，
不会继续伪装成实时值。

**额度什么时候更新**：两边都是事件驱动，而不是靠缩短轮询间隔。Codex 走 app-server 推送的
`account/rateLimits/updated` 通知；Claude 的用量接口没有推送通道，改用会话记录做信号——一个回合
写进 `*.jsonl` 时，也正是额度发生变化的时刻，监控器在一秒内就能看到并立即拉取。因此**用的时候**
数字是秒级新鲜的，**不用的时候**一个请求都不会发出去。此外还有一条三分钟的常规轮询兜底（窗口重置
时额度会自己跳回去），以及两次事件拉取之间 20 秒的下限。被限流时一律退让，界面会直接显示
「接口限流 · N 后重试」。

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

需要稳定版 .NET 10 SDK（目标框架 `net10.0-windows`，由 `global.json` 固定）。

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

发布供在线更新识别的 Release 时，Tag 与项目版本应一致（例如 `v1.9.9`），资产命名为
`AiTokenMonitor-1.9.9.exe`。GitHub 生成的 SHA-256 摘要会被客户端用于安装前校验。

## 许可证

[MIT](LICENSE)。
