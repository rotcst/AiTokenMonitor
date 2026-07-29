# AI 토큰 모니터

[English](README.md) · [简体中文](README.zh-CN.md) · **한국어**

[![Release](https://img.shields.io/github/v/release/rotcst/AiTokenMonitor)](https://github.com/rotcst/AiTokenMonitor/releases/latest)
[![License: MIT](https://img.shields.io/badge/License-MIT-blue.svg)](LICENSE)
![Platform](https://img.shields.io/badge/platform-Windows%2010%2F11-lightgrey)

**Codex** 와 **Claude Code** 의 사용량을 한 화면에서 나란히 보여 주는 Windows 데스크톱 위젯입니다.
한도 창, 크레딧 잔액, 토큰 기록을 각 서비스의 공식 데이터에서 직접 읽으며, **CLI나 데스크톱 앱이
실행 중이 아니어도** 동작합니다.

> OpenAI · Anthropic 과 무관합니다. 이 도구들이 사용자 PC에 이미 기록해 둔 자격 증명과 세션 파일만
> 읽습니다.

## 다운로드

**[Releases 페이지](https://github.com/rotcst/AiTokenMonitor/releases/latest)** 에서 최신 단일 파일
`AiTokenMonitor.exe` 를 받으세요. 설치 불필요, 별도 런타임 불필요 — 자체 포함 실행 파일입니다.
더블클릭하면 메인 창을 표시한 뒤 시스템 트레이에 상주하며, 다시 실행하면 기존 창을 복원합니다.

## 기능

- **두 서비스, 한 창**: 하단 탭으로 Codex / Claude 전환.
- **추정이 아닌 실제 한도**: CLI가 쓰는 것과 같은 공식 엔드포인트를 읽습니다.
  - Claude: `GET https://api.anthropic.com/api/oauth/usage` (`/usage` 의 원본).
  - Codex: ChatGPT 백엔드 `wham/usage`, `wham/profiles/me`, `wham/rate-limit-reset-credits`.
- **5시간 · 주간 한도** + 정확한 재설정 시각과 남은 시간.
- **크레딧 잔액 · 사용 크레딧**, 모델별 주간 한도(Opus / Sonnet / GPT-5-Codex …), 지출 한도, 한도 알림.
- **게이지 오브 모드**: 카드를 더블클릭하면 레이싱 대시보드 형태의 작은 오브로 접히며, 하나의
  다이얼에 두 개의 바늘로 Codex와 Claude 사용량을 함께 표시합니다(5시간 창이 있으면 5시간, 없으면 주간).
  오브를 더블클릭하면 다시 펼쳐집니다.
- **토큰 기록**: 누적 · 오늘 · 최근 7일, 일별 막대 그래프, 스크롤 가능한 전체 목록.
- **현재 모델과 컨텍스트 사용량**: 로컬 세션 기록에서 읽으며 터미널 CLI와 데스크톱 앱 모두 지원.
- **3개 언어 UI**(简体中文 / English / 한국어): 시스템 언어에 따라 자동 선택, 우클릭 메뉴에서 즉시 전환.
- **네이티브 창 동작**: DWM 둥근 모서리, 최소화/복원 애니메이션, 작업 표시줄로 최소화, 트레이로 숨기기.
  실수로 닫지 않도록 종료는 트레이 메뉴에만 있습니다.

## 사용량을 읽는 방식

두 서비스 모두 클라이언트 실행 여부와 무관하게 동작합니다. 각 클라이언트가 로컬에 이미 기록하는
파일을 읽기 때문입니다.

| | 터미널 CLI | 데스크톱 앱 |
| --- | --- | --- |
| Claude 자격 증명 | `<CLAUDE_CONFIG_DIR 또는 ~/.claude>/.credentials.json` | `%APPDATA%\Claude\config.json` (OSCrypt 복호화) |
| Claude 세션 | `<CLAUDE_CONFIG_DIR 또는 ~/.claude>/projects/**/*.jsonl` | 같은 폴더 |
| Codex 자격 증명 | `<CODEX_HOME 또는 ~/.codex>/auth.json` | 같은 파일 |
| Codex 세션 | `<CODEX_HOME 또는 ~/.codex>/sessions/**` | + `archived_sessions/**` |

`CLAUDE_CONFIG_DIR` 와 `CODEX_HOME` 를 전 구간에서 존중하므로, 설정 폴더를 옮겨도 "한도는 읽혔는데
토큰은 비어 있는" 반쪽 상태가 생기지 않습니다.

Codex 는 공식 HTTP API 를 우선 사용하며, 저장된 토큰이 만료되면 `codex app-server` 를 띄우는 방식으로
폴백합니다(토큰 갱신은 CLI가 직접 수행). 카드의 상태 줄에 현재 어떤 경로인지 표시됩니다.

## 개인정보

- 액세스 토큰은 **메모리에서만** 사용되며, 오직 공식 사용량 엔드포인트 호출에만 쓰입니다. 디스크에
  저장하거나 외부로 전송하지 않습니다.
- 데스크톱 토큰 캐시는 현재 Windows 사용자 자신의 DPAPI 키로 복호화하며(데스크톱 앱과 동일한 방식),
  어떤 데이터도 PC 밖으로 나가지 않습니다.
- 세션 파싱은 메시지 ID · 타임스탬프 · `usage` 토큰 수만 추출하며, 프롬프트나 응답은 저장하지 않습니다.
- **Claude 선불 크레딧 잔액**(결제 페이지의 금액)은 **표시하지 않습니다**. Claude Code 가 쓰는 OAuth
  사용량 엔드포인트가 이를 제공하지 않으며, 읽으려면 전체 claude.ai 웹 세션이 필요한데 이 앱은 의도적으로
  건드리지 않습니다.

## 언어

UI 는 简体中文 · English · 한국어 를 제공합니다. 첫 실행 시 OS UI 언어를 따릅니다(중국어→중국어,
한국어→한국어, 그 외→영어). 이후 언제든 플로팅 카드나 트레이의 우클릭 메뉴에서 **언어 / Language**
하위 메뉴로 전환할 수 있으며, 즉시 적용되고 재시작이 필요 없으며 선택은
`%LOCALAPPDATA%\AiTokenMonitor\language.txt` 에 기억됩니다.

## 소스에서 빌드

안정적인 .NET 10 SDK가 필요합니다(`net10.0-windows` 대상, `global.json`으로 고정).

```powershell
git clone https://github.com/rotcst/AiTokenMonitor.git
cd AiTokenMonitor

# 테스트 실행
dotnet run --project CodexWeeklyMonitor.Tests\CodexWeeklyMonitor.Tests.csproj -c Release

# 자체 포함 단일 파일 exe 생성
dotnet publish CodexWeeklyMonitor\CodexWeeklyMonitor.csproj -c Release -r win-x64 `
  --self-contained true -p:PublishSingleFile=true `
  -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true
```

## 라이선스

[MIT](LICENSE).
