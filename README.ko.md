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
`AiTokenMonitor-x.y.z.exe` 를 받으세요. 설치 불필요, 별도 런타임 불필요 — 자체 포함 실행 파일입니다.
더블클릭하면 메인 창을 표시한 뒤 시스템 트레이에 상주하며, 다시 실행하면 기존 창을 복원합니다.

## 기능

- **두 서비스, 한 창**: 하단 탭으로 Codex / Claude 전환.
- **추정이 아닌 실제 한도**: CLI가 쓰는 것과 같은 공식 데이터 경로를 읽습니다.
  - Claude: `GET https://api.anthropic.com/api/oauth/usage` (`/usage` 의 원본).
  - Codex: 주요 한도 창은 `codex app-server`의 `account/rateLimits/read`를 사용하고, ChatGPT
    백엔드 `wham/usage`, `wham/profiles/me`, `wham/rate-limit-reset-credits`로 세부 정보를 보완합니다.
- **5시간 · 주간 한도** + 정확한 재설정 시각과 남은 시간.
- **크레딧 잔액 · 사용 크레딧**, 모델별 주간 한도(Opus / Sonnet / GPT-5-Codex …), 지출 한도, 한도 알림.
- **액체 오브 모드**: 메인 창의 우클릭 메뉴에서 작은 오브로 전환합니다. 좌우 수조는 각각 Codex와
  Claude이며 수위는 각 서비스의 **남은 한도**입니다(가득 차면 100%, 바닥이면 0%). 왼쪽 또는 오른쪽을
  클릭하면 해당 서비스의 5시간/주간 한도를 독립적으로 전환합니다. 서비스 제목은 백분율 위에, 선택한
  기간과 재설정 카운트다운은 아래에 표시됩니다. 오브의 우클릭 메뉴에서 메인 창으로 돌아갈 수 있습니다.
- **토큰 기록**: 누적 · 오늘 · 최근 7일, 일별 막대 그래프, 스크롤 가능한 전체 목록.
- **현재 모델과 컨텍스트 사용량**: 로컬 세션 기록에서 읽으며 터미널 CLI와 데스크톱 앱 모두 지원.
- **3개 언어 UI**(简体中文 / English / 한국어): 시스템 언어에 따라 자동 선택, 우클릭 메뉴에서 즉시 전환.
- **Windows 시작 시 실행**: 메인 창·트레이·오브 우클릭 메뉴에서 켜고 끕니다. 현재 사용자
  `HKCU\...\CurrentVersion\Run` 키에만 등록하므로 관리자 권한도, 설치 프로그램도, 컴퓨터 전체에 남는
  흔적도 없습니다. 로그인 시 실행되는 경우 창을 표시하거나 포커스를 빼앗지 않고 바로 트레이로 들어가며,
  이식형 EXE를 옮겨도 다음 실행에서 등록 경로를 새 위치로 고쳐 씁니다.
- **온라인 업데이트**: 시작 시 최신 GitHub 정식 Release를 확인하고 이후 6시간마다 다시 확인합니다.
  Windows 시작 시 실행되어 몇 주 동안 트레이에 머물러도 새 릴리스를 놓치지 않습니다. 새 버전은 즉시
  알리고, "나중에"를 누른 버전은 24시간 동안 조용히 있다가 더 새로운 버전이 나오면 다시 알립니다.
  메인 창·트레이·오브 우클릭 메뉴에서 수동 확인도 가능합니다. 확인 후 EXE를 다운로드하고 크기와
  SHA-256을 검증한 뒤 기존 파일을 교체하고 재시작합니다. GitHub 토큰은 필요 없습니다.
- **네이티브 창 동작**: DWM 둥근 모서리, 최소화/복원 애니메이션, 작업 표시줄로 최소화, 트레이로 숨기기.
  실수로 닫지 않도록 종료는 트레이 메뉴에만 있습니다.

메인 제목 옆에 현재 버전이 표시됩니다. 메인 창과 액체 오브는 각자의 화면 위치를 따로 기억하므로 전환해도
서로의 좌표를 덮어쓰지 않습니다.

## 온라인 업데이트

앱은 저장소의 **최신 정식 GitHub Release**만 읽으며 초안과 프리릴리스는 무시합니다. Release에 버전과
일치하는 `AiTokenMonitor-x.y.z.exe`(`AiTokenMonitor.exe`도 호환용으로 허용)와 유효한 GitHub
`sha256:` 다이제스트가 있을 때만 설치할 수 있습니다. 자동으로 설치하지 않으며 **지금 업데이트**를 선택한
뒤에만 다운로드와 재시작을 시작합니다.

포터블 EXE가 있는 폴더는 현재 Windows 사용자가 쓸 수 있어야 합니다. 관리자 권한이 필요한 폴더에 두었다면
Releases 페이지에서 새 버전을 직접 다운로드하세요.

## 사용량을 읽는 방식

두 서비스 모두 클라이언트 실행 여부와 무관하게 동작합니다. 각 클라이언트가 로컬에 이미 기록하는
파일을 읽기 때문입니다.

| | 터미널 CLI | 데스크톱 앱 |
| --- | --- | --- |
| Claude 자격 증명 | `<CLAUDE_CONFIG_DIR 또는 ~/.claude>/.credentials.json` | `%APPDATA%\Claude\config.json` 또는 Microsoft Store `%LOCALAPPDATA%\Packages\Claude_*\LocalCache\Roaming\Claude\config.json` (OSCrypt 복호화) |
| Claude 세션 | `<CLAUDE_CONFIG_DIR 또는 ~/.claude>/projects/**/*.jsonl` | 같은 폴더 |
| Codex 자격 증명 | `<CODEX_HOME 또는 ~/.codex>/auth.json` | 같은 파일 |
| Codex 세션 | `<CODEX_HOME 또는 ~/.codex>/sessions/**` | + `archived_sessions/**` |

`CLAUDE_CONFIG_DIR` 와 `CODEX_HOME` 를 전 구간에서 존중하므로, 설정 폴더를 옮겨도 "한도는 읽혔는데
토큰은 비어 있는" 반쪽 상태가 생기지 않습니다.

Codex 는 지원되는 `codex app-server` 한도 스냅샷을 주요 창에 사용하고 공식 HTTP 엔드포인트의
세부 정보를 합칩니다. app-server 를 사용할 수 없으면 HTTP 스냅샷으로 폴백합니다. 새로고침에
실패하면 마지막 성공 값을 유지하되 이전 데이터임을 명확히 표시합니다.

**한도가 갱신되는 시점**: 양쪽 모두 폴링 간격을 줄이는 대신 이벤트 기반으로 동작합니다. Codex 는
app-server 가 보내는 `account/rateLimits/updated` 알림을 따릅니다. Claude 사용량 엔드포인트에는
그런 채널이 없으므로 세션 기록을 신호로 씁니다. 한 턴이 `*.jsonl` 에 기록되는 순간이 바로 한도가
움직인 시점이고, 감시자가 1초 안에 이를 보고 즉시 읽어옵니다. 따라서 **쓰는 동안에는** 숫자가
초 단위로 최신이고, **쓰지 않는 동안에는** 요청이 아예 나가지 않습니다. 여기에 3분 주기의 정기
폴링이 보조로 붙고(창이 재설정되면 한도가 저절로 바뀝니다), 이벤트 기반 읽기 사이에는 20초의
하한이 있습니다. 요청 제한 상황에서는 모두 물러나며, 카드에 "요청 제한 · N 후 재시도"로 표시됩니다.

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
  -p:IncludeNativeLibrariesForSelfExtract=true
```

온라인 업데이트와 호환되는 Release를 만들 때는 태그와 프로젝트 버전을 맞추고(예: `v1.9.9`),
`AiTokenMonitor-1.9.9.exe`를 업로드하세요. 클라이언트는 GitHub가 생성한 SHA-256을 검증합니다.

## 라이선스

[MIT](LICENSE).
