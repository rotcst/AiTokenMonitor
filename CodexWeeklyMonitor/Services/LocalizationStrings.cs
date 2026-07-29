namespace CodexWeeklyMonitor.Services;

/// <summary>
/// The full string catalogue. Each entry is <c>[Chinese, English, Korean]</c>, matching the order
/// of <see cref="AppLanguage"/>. Format placeholders use <c>{0}</c>, <c>{1}</c>, ... .
/// </summary>
internal static class Strings
{
    public static readonly IReadOnlyDictionary<string, string?[]> Table = new Dictionary<string, string?[]>
    {
        // App / header
        ["app.title"] = ["AI TOKEN 用量监控", "AI Token Monitor", "AI 토큰 모니터"],
        ["header.details"] = ["详情", "Details", "세부정보"],
        ["header.history"] = ["历史", "History", "기록"],

        // Provider tabs / automation
        ["tab.codex"] = ["切换到 Codex 用量", "Switch to Codex usage", "Codex 사용량으로 전환"],
        ["tab.claude"] = ["切换到 Claude 用量", "Switch to Claude usage", "Claude 사용량으로 전환"],
        ["header.toggleDetails"] = ["展开或收起详情", "Toggle details", "세부정보 펼치기/접기"],
        ["header.toggleHistory"] = ["展开或收起逐日 Token 历史", "Toggle daily token history", "일별 토큰 기록 펼치기/접기"],
        ["header.refresh"] = ["立即刷新", "Refresh now", "지금 새로고침"],
        ["header.minimize"] = ["最小化到任务栏", "Minimize to taskbar", "작업 표시줄로 최소화"],
        ["header.hide"] = ["隐藏到系统托盘", "Hide to system tray", "시스템 트레이로 숨기기"],

        // Connection status
        ["conn.connecting"] = ["正在连接", "Connecting", "연결 중"],
        ["conn.reading"] = ["正在读取", "Reading", "읽는 중"],
        ["conn.readingClaude"] = ["正在读取 Claude", "Reading Claude", "Claude 읽는 중"],
        ["conn.live"] = ["实时监控中", "Live", "실시간 모니터링"],
        ["conn.liveWithSource"] = ["实时监控中 · {0}", "Live · {0}", "실시간 · {0}"],
        ["conn.readingCodex"] = ["正在读取 Codex", "Reading Codex", "Codex 읽는 중"],
        ["conn.codexFailed"] = ["Codex 连接失败", "Codex connection failed", "Codex 연결 실패"],
        ["conn.codexLocal"] = ["Codex 本机统计", "Codex local stats", "Codex 로컬 통계"],
        ["conn.claudeLive"] = ["Claude 实时监控中", "Claude live", "Claude 실시간"],
        ["conn.claudeUpdateFailed"] = ["Claude 更新暂时失败", "Claude update failed", "Claude 업데이트 실패"],
        ["conn.readingClaudeQuota"] = ["正在读取 Claude 额度", "Reading Claude quota", "Claude 한도 읽는 중"],
        ["conn.claudeQuotaFailed"] = ["Claude 额度读取失败", "Claude quota read failed", "Claude 한도 읽기 실패"],
        ["conn.connectFailed"] = ["连接失败", "Connection failed", "연결 실패"],
        ["conn.updateFailed"] = ["更新暂时失败", "Update failed", "업데이트 실패"],

        // Rate-window cards
        ["card.fiveHour"] = ["5 小时额度", "5-hour limit", "5시간 한도"],
        ["card.weekly"] = ["周额度", "Weekly limit", "주간 한도"],
        ["card.used"] = ["已用 {0}%", "{0}% used", "{0}% 사용"],
        ["card.notProvided"] = ["未提供", "Not provided", "제공 안 됨"],
        ["card.waitingData"] = ["等待数据", "Waiting for data", "데이터 대기 중"],
        ["card.waitingCodex"] = ["等待 Codex 数据", "Waiting for Codex", "Codex 대기 중"],
        ["card.waitingClaude"] = ["等待 Claude 额度", "Waiting for Claude quota", "Claude 한도 대기 중"],
        ["card.quotaUnavailable"] = ["额度不可用", "Quota unavailable", "한도 사용 불가"],
        ["card.noWindow"] = ["当前计划无窗口", "No window on this plan", "이 요금제에는 창 없음"],
        ["reset.unknown"] = ["重置时间未知", "Reset time unknown", "재설정 시간 미상"],

        // Balance / secondary cards
        ["card.balance"] = ["充值余额", "Credit balance", "충전 잔액"],
        ["card.resetCredits"] = ["可用重置次数", "Reset credits", "재설정 크레딧"],
        ["card.lifetimeTokens"] = ["累计 TOKEN", "Lifetime tokens", "누적 토큰"],
        ["card.localLifetimeTokens"] = ["本机累计 TOKEN", "Local lifetime tokens", "로컬 누적 토큰"],
        ["card.latestDay"] = ["最近一天", "Latest day", "최근 하루"],
        ["card.dayUsage"] = ["{0} 用量", "{0} usage", "{0} 사용량"],
        ["card.sevenDay"] = ["近 7 天", "Last 7 days", "최근 7일"],
        ["card.dailyToken"] = ["逐日 TOKEN", "Daily tokens", "일별 토큰"],
        ["card.usageCredit"] = ["用量额度", "Usage credits", "사용 크레딧"],
        ["card.creditBalanceAutoReload"] = ["点数余额 · 自动充值", "Credit balance · Auto-reload", "크레딧 잔액 · 자동 충전"],
        ["card.creditBalance"] = ["点数余额", "Credit balance", "크레딧 잔액"],
        ["card.notEnabled"] = ["未开通", "Not enabled", "미사용"],
        ["card.enabled"] = ["已开通", "Enabled", "사용 중"],
        ["card.context"] = ["上下文占用", "Context used", "컨텍스트 사용"],
        ["card.plan"] = ["订阅计划", "Plan", "요금제"],
        ["credit.unlimited"] = ["不限", "Unlimited", "무제한"],
        ["credit.available"] = ["可用", "Available", "사용 가능"],

        // Token status line
        ["status.readingCodexToken"] = ["正在读取 Codex Token", "Reading Codex tokens", "Codex 토큰 읽는 중"],
        ["status.errWithLocal"] = ["{0} · 已显示本机 Token", "{0} · showing local tokens", "{0} · 로컬 토큰 표시 중"],
        ["status.codexNoHistory"] = ["当前 Codex 版本未提供 Token 历史", "This Codex version has no token history", "이 Codex 버전은 토큰 기록을 제공하지 않음"],
        ["status.localRealtime"] = ["今日本机实时估算 · 历史来自官方账本", "Today estimated live locally · official history", "오늘 로컬 실시간 추정 · 공식 기록"],
        ["status.historyDelay"] = ["历史截至 {0} · 服务端延迟 {1} 天", "History to {0} · server {1}d behind", "기록 {0}까지 · 서버 {1}일 지연"],
        ["status.tokenRefresh"] = ["Token 每 60 秒刷新", "Tokens refresh every 60s", "토큰 60초마다 새로고침"],
        ["status.tokenUnavailable"] = ["Token 历史暂不可用", "Token history unavailable", "토큰 기록 사용 불가"],
        ["status.readingToken"] = ["读取 Token 历史…", "Reading token history…", "토큰 기록 읽는 중…"],
        ["status.noDailyToken"] = ["暂无逐日 Token 数据", "No daily token data", "일별 토큰 데이터 없음"],
        ["status.claudeThrottledWithData"] = ["接口限流 · 数据为 {0} · {1}后重试", "Rate limited · data from {0} · retry in {1}", "요청 제한 · 데이터 {0} · {1} 후 재시도"],
        ["status.claudeThrottled"] = ["接口限流 · {0}后重试", "Rate limited · retry in {0}", "요청 제한 · {0} 후 재시도"],
        ["status.claudeStale"] = ["额度暂未刷新 · {0}", "Quota not refreshed · {0}", "한도 미갱신 · {0}"],
        ["status.claudeOfficial"] = ["额度来自 Claude 官方接口 · Token 为本机统计", "Quota from Claude's API · tokens counted locally", "한도는 Claude 공식 API · 토큰은 로컬 집계"],
        ["status.claudeNotDetected"] = ["未检测到 Claude · 安装并登录 Claude Code 后可用", "Claude not found · install and sign in to Claude Code", "Claude 없음 · Claude Code 설치·로그인 필요"],
        ["retry.minutes"] = ["{0} 分钟", "{0} min", "{0}분"],
        ["retry.seconds"] = ["{0} 秒", "{0} sec", "{0}초"],

        // Time-stamped status
        ["time.updated"] = ["{0} 更新", "Updated {0}", "{0} 업데이트"],
        ["time.failed"] = ["{0} 失败", "{0} failed", "{0} 실패"],
        ["time.check"] = ["{0} 检查", "Checked {0}", "{0} 확인"],

        // Data sources
        ["source.official"] = ["官方接口", "Official API", "공식 API"],
        ["source.appServer"] = ["本机 app-server", "Local app-server", "로컬 app-server"],
        ["source.officialUsage"] = ["官方用量接口", "Official usage API", "공식 사용량 API"],
        ["source.localSession"] = ["本机会话记录", "Local session logs", "로컬 세션 기록"],
        ["source.statusBridge"] = ["状态栏桥接", "Status-line bridge", "상태줄 브리지"],

        // Context menu
        ["menu.refresh"] = ["立即刷新", "Refresh now", "지금 새로고침"],
        ["menu.expandDetails"] = ["展开详情", "Show details", "세부정보 표시"],
        ["menu.collapseDetails"] = ["收起详情", "Hide details", "세부정보 숨기기"],
        ["menu.expandHistory"] = ["展开逐日 Token 历史", "Show daily token history", "일별 토큰 기록 표시"],
        ["menu.collapseHistory"] = ["收起逐日 Token 历史", "Hide daily token history", "일별 토큰 기록 숨기기"],
        ["menu.topmost"] = ["始终置顶", "Always on top", "항상 위"],
        ["menu.minimizeTaskbar"] = ["最小化到任务栏", "Minimize to taskbar", "작업 표시줄로 최소화"],
        ["menu.hideTray"] = ["隐藏到系统托盘", "Hide to tray", "트레이로 숨기기"],
        ["menu.showWindow"] = ["显示主窗口", "Show window", "창 표시"],
        ["menu.exit"] = ["退出程序", "Exit", "종료"],
        ["menu.language"] = ["语言 / Language", "Language / 语言", "언어 / Language"],
        ["menu.gauge"] = ["切换到液面悬浮球", "Switch to liquid orb", "액체 오브로 전환"],

        // Gauge / orb mode
        ["gauge.restoreHint"] = ["双击恢复主窗口", "Double-click to restore", "더블클릭하여 복원"],

        // Tray status lines
        ["tray.weeklyUnknown"] = ["{0}：周额度未知", "{0}: weekly unknown", "{0}: 주간 한도 미상"],
        ["tray.weeklyUsed"] = ["{0}：周额度已用 {1}%", "{0}: weekly {1}% used", "{0}: 주간 {1}% 사용"],

        // Header button caption + arrow ({0} = ▾ / ▴)
        ["header.detailsArrow"] = ["详情 {0}", "Details {0}", "세부 {0}"],
        ["header.historyArrow"] = ["历史 {0}", "History {0}", "기록 {0}"],

        // History panel
        ["history.title"] = ["{0} TOKEN 历史", "{0} token history", "{0} 토큰 기록"],
        ["history.colDate"] = ["日期", "Date", "날짜"],
        ["history.colShare"] = ["占比", "Share", "비율"],
        ["history.colToken"] = ["TOKEN", "Tokens", "토큰"],
        ["history.noDaily"] = ["暂无逐日数据", "No daily data", "일별 데이터 없음"],
        ["history.subtitle"] = ["{0} 天 · 截至 {1}", "{0} days · to {1}", "{0}일 · {1}까지"],

        // Detail-panel section titles
        ["sec.account"] = ["账号", "Account", "계정"],
        ["sec.quota"] = ["额度", "Quota", "한도"],
        ["sec.balance"] = ["余额", "Balance", "잔액"],
        ["sec.usageStats"] = ["用量统计", "Usage stats", "사용 통계"],
        ["sec.wallet"] = ["点数余额", "Credit balance", "크레딧 잔액"],
        ["sec.usageCredit"] = ["用量额度（本期消耗）", "Usage credits (this period)", "사용 크레딧 (이번 기간)"],
        ["sec.session"] = ["当前会话", "Current session", "현재 세션"],

        // Detail-panel labels
        ["lbl.status"] = ["状态", "Status", "상태"],
        ["lbl.source"] = ["数据来源", "Source", "데이터 출처"],
        ["lbl.account"] = ["账号", "Account", "계정"],
        ["lbl.nickname"] = ["昵称", "Name", "이름"],
        ["lbl.plan"] = ["订阅计划", "Plan", "요금제"],
        ["lbl.available"] = ["当前可用", "Available now", "현재 사용 가능"],
        ["lbl.updateTime"] = ["更新时间", "Updated", "업데이트 시각"],
        ["lbl.limitTitle"] = ["限额提示", "Limit notice", "한도 알림"],
        ["lbl.limitDesc"] = ["限额说明", "Limit detail", "한도 설명"],
        ["lbl.spendCap"] = ["支出上限", "Spend cap", "지출 한도"],
        ["lbl.balance"] = ["余额", "Balance", "잔액"],
        ["lbl.hasBalance"] = ["是否有余额", "Has balance", "잔액 있음"],
        ["lbl.overageCap"] = ["超额上限", "Overage cap", "초과 한도"],
        ["lbl.approxLocalMsg"] = ["约可用本地消息", "Approx. local messages", "로컬 메시지 예상"],
        ["lbl.approxCloudMsg"] = ["约可用云端消息", "Approx. cloud messages", "클라우드 메시지 예상"],
        ["lbl.resetCredits"] = ["可用重置次数", "Reset credits", "재설정 크레딧"],
        ["lbl.lifetimeTokens"] = ["累计 Token", "Lifetime tokens", "누적 토큰"],
        ["lbl.peakDaily"] = ["单日峰值", "Peak daily", "일일 최고"],
        ["lbl.longestTurn"] = ["最长单轮时长", "Longest turn", "최장 턴"],
        ["lbl.currentStreak"] = ["当前连续天数", "Current streak", "현재 연속일"],
        ["lbl.longestStreak"] = ["最长连续天数", "Longest streak", "최장 연속일"],
        ["lbl.historyDays"] = ["历史天数", "History days", "기록 일수"],
        ["lbl.historyUntil"] = ["历史截至", "History until", "기록 종료일"],
        ["lbl.totalThreads"] = ["会话总数", "Total threads", "총 스레드"],
        ["lbl.fastMode"] = ["Fast 模式占比", "Fast-mode share", "Fast 모드 비율"],
        ["lbl.skillsUsed"] = ["技能使用次数", "Skill uses", "스킬 사용 횟수"],
        ["lbl.uniqueSkills"] = ["使用过的技能", "Unique skills", "사용 스킬 수"],
        ["lbl.mostEffort"] = ["最常用推理强度", "Top reasoning effort", "주 추론 강도"],
        ["lbl.statsNote"] = ["统计说明", "Stats note", "통계 참고"],
        ["lbl.localLifetimeTokens"] = ["本机累计 Token", "Local lifetime tokens", "로컬 누적 토큰"],
        ["lbl.statsScope"] = ["统计范围", "Stats scope", "통계 범위"],
        ["lbl.currentBalance"] = ["当前余额", "Current balance", "현재 잔액"],
        ["lbl.autoReload"] = ["自动充值", "Auto-reload", "자동 충전"],
        ["lbl.reloadTrigger"] = ["充值触发额", "Reload trigger", "충전 기준액"],
        ["lbl.reloadAmount"] = ["每次充值", "Reload amount", "충전 금액"],
        ["lbl.canPurchase"] = ["可自助购买", "Can self-purchase", "직접 구매 가능"],
        ["lbl.isEnabled"] = ["是否开通", "Enabled", "사용 여부"],
        ["lbl.periodUsed"] = ["本期已用", "Used this period", "이번 기간 사용"],
        ["lbl.periodCap"] = ["本期上限", "Cap this period", "이번 기간 한도"],
        ["lbl.usedPercent"] = ["已用比例", "Used %", "사용 비율"],
        ["lbl.disabledReason"] = ["停用原因", "Disabled reason", "비활성 사유"],
        ["lbl.currentModel"] = ["当前模型", "Current model", "현재 모델"],
        ["lbl.context"] = ["上下文占用", "Context used", "컨텍스트 사용"],
        ["lbl.effort"] = ["推理强度", "Reasoning effort", "추론 강도"],
        ["lbl.ccVersion"] = ["Claude Code 版本", "Claude Code version", "Claude Code 버전"],
        ["lbl.lastTurn"] = ["最近一轮", "Last turn", "최근 턴"],

        // Detail-panel values
        ["val.yes"] = ["是", "Yes", "예"],
        ["val.no"] = ["否", "No", "아니요"],
        ["val.exhausted"] = ["否（额度已用尽）", "No (quota exhausted)", "아니요 (한도 소진)"],
        ["val.capReached"] = ["已达上限", "Cap reached", "한도 도달"],
        ["val.capNotReached"] = ["未达上限", "Under cap", "한도 미달"],
        ["val.exhaustedShort"] = ["已用尽", "Exhausted", "소진"],
        ["val.on"] = ["已开启", "On", "켜짐"],
        ["val.off"] = ["未开启", "Off", "꺼짐"],
        ["val.enabledShort"] = ["已开通", "Enabled", "사용 중"],
        ["val.notEnabledShort"] = ["未开通", "Not enabled", "미사용"],
        ["val.statsScopeClaude"] = ["仅本机会话记录，含缓存读写", "Local session logs only, incl. cache I/O", "로컬 세션 기록만, 캐시 입출력 포함"],
        ["val.noRecentSession"] = ["近 30 分钟没有本机会话活动", "No local session in the last 30 min", "최근 30분간 로컬 세션 없음"],
        ["detail.reading"] = ["正在读取", "Reading", "읽는 중"],
        ["detail.normal"] = ["正常", "OK", "정상"],
        ["detail.noData"] = ["暂无可显示的数据", "No data to show", "표시할 데이터 없음"],

        // Composite windows / countdowns / units
        ["window.usedRemaining"] = ["已用 {0}% · 剩余 {1}%", "{0}% used · {1}% left", "{0}% 사용 · {1}% 남음"],
        ["window.reset"] = ["{0} 重置", "resets {0}", "{0} 재설정"],
        ["countdown.imminent"] = ["即将重置", "resetting soon", "곧 재설정"],
        ["countdown.daysHours"] = ["还有 {0} 天 {1} 小时", "{0}d {1}h left", "{0}일 {1}시간 남음"],
        ["countdown.hoursMinutes"] = ["还有 {0} 小时 {1} 分", "{0}h {1}m left", "{0}시간 {1}분 남음"],
        ["countdown.minutes"] = ["还有 {0} 分钟", "{0}m left", "{0}분 남음"],
        ["range.pair"] = ["{0} – {1} 条", "{0}–{1} msgs", "{0}–{1}개"],
        ["range.single"] = ["{0} 条", "{0} msgs", "{0}개"],
        ["unit.days"] = ["{0} 天", "{0} days", "{0}일"],
        ["unit.count"] = ["{0} 个", "{0}", "{0}개"],
        ["unit.times"] = ["{0} 次", "{0}×", "{0}회"],
        ["unit.kinds"] = ["{0} 种", "{0} kinds", "{0}종"],
        ["duration.hoursMin"] = ["{0} 小时 {1} 分", "{0}h {1}m", "{0}시간 {1}분"],
        ["duration.minSec"] = ["{0} 分 {1} 秒", "{0}m {1}s", "{0}분 {1}초"],
        ["duration.sec"] = ["{0} 秒", "{0}s", "{0}초"],

        // Date formats (.NET custom format strings, per language)
        ["fmt.monthDay"] = ["M月d日", "MMM d", "M월 d일"],
        ["fmt.monthDayTime"] = ["M月d日 HH:mm", "MMM d HH:mm", "M월 d일 HH:mm"],

        // Service errors — Claude
        ["err.claude.noCred"] = ["未找到 Claude 登录凭据，请先登录 Claude Code 或 Claude 桌面端。", "No Claude credentials found. Sign in to Claude Code or the Claude desktop app.", "Claude 자격 증명을 찾을 수 없습니다. Claude Code 또는 데스크톱 앱에 로그인하세요."],
        ["err.claude.invalid"] = ["Claude 登录凭据无效或已失效，请重新登录。", "Claude credentials are invalid or expired. Sign in again.", "Claude 자격 증명이 유효하지 않거나 만료되었습니다. 다시 로그인하세요."],
        ["err.claude.expired"] = ["Claude 登录凭据已过期，请在 Claude Code 中重新登录。", "Claude credentials expired. Sign in again in Claude Code.", "Claude 자격 증명이 만료되었습니다. Claude Code에서 다시 로그인하세요."],
        ["err.claude.timeout"] = ["读取 Claude 额度超时。", "Timed out reading Claude quota.", "Claude 한도 읽기 시간 초과."],
        ["err.claude.network"] = ["无法连接 Claude 额度接口。", "Can't reach the Claude quota API.", "Claude 한도 API에 연결할 수 없습니다."],
        ["err.claude.throttled"] = ["Claude 额度接口限流中，已自动降低刷新频率。", "Claude quota API is rate-limiting; refresh slowed automatically.", "Claude 한도 API 요청 제한 중, 자동으로 새로고침 속도 낮춤."],
        ["err.claude.http"] = ["Claude 额度接口返回 {0}。", "Claude quota API returned {0}.", "Claude 한도 API가 {0} 반환."],
        ["err.claude.parse"] = ["无法解析 Claude 额度数据。", "Couldn't parse Claude quota data.", "Claude 한도 데이터를 해석할 수 없습니다."],

        // Service errors — Codex
        ["err.codex.noCred"] = ["未找到 Codex 登录凭据，请先登录 Codex。", "No Codex credentials found. Sign in to Codex.", "Codex 자격 증명을 찾을 수 없습니다. Codex에 로그인하세요."],
        ["err.codex.expired"] = ["Codex 登录凭据已过期，需要 Codex CLI 续期。", "Codex credentials expired; the Codex CLI needs to refresh them.", "Codex 자격 증명이 만료됨; Codex CLI가 갱신해야 합니다."],
        ["err.codex.parseQuota"] = ["无法解析 Codex 额度数据。", "Couldn't parse Codex quota data.", "Codex 한도 데이터를 해석할 수 없습니다."],
        ["err.codex.parseStats"] = ["无法解析 Codex 账号统计。", "Couldn't parse Codex account stats.", "Codex 계정 통계를 해석할 수 없습니다."],
        ["err.codex.statsUnavailable"] = ["Codex 账号统计暂不可用。", "Codex account stats unavailable.", "Codex 계정 통계를 사용할 수 없습니다."],
        ["err.codex.timeout"] = ["读取 Codex 额度超时。", "Timed out reading Codex quota.", "Codex 한도 읽기 시간 초과."],
        ["err.codex.network"] = ["无法连接 Codex 额度接口。", "Can't reach the Codex quota API.", "Codex 한도 API에 연결할 수 없습니다."],
        ["err.codex.invalid"] = ["Codex 登录凭据无效或已失效。", "Codex credentials are invalid or expired.", "Codex 자격 증명이 유효하지 않거나 만료되었습니다."],
        ["err.codex.throttled"] = ["Codex 额度接口限流中，已自动降低刷新频率。", "Codex quota API is rate-limiting; refresh slowed automatically.", "Codex 한도 API 요청 제한 중, 자동으로 새로고침 속도 낮춤."],
        ["err.codex.http"] = ["Codex 额度接口返回 {0}。", "Codex quota API returned {0}.", "Codex 한도 API가 {0} 반환."],
        ["err.codex.notFound"] = ["未找到 codex.exe。请先安装或更新 Codex CLI，或设置 CODEX_EXE 环境变量。", "codex.exe not found. Install/update the Codex CLI or set CODEX_EXE.", "codex.exe를 찾을 수 없습니다. Codex CLI를 설치·업데이트하거나 CODEX_EXE를 설정하세요."],
        ["err.codex.startFailed"] = ["无法启动 Codex app-server。", "Couldn't start the Codex app-server.", "Codex app-server를 시작할 수 없습니다."],

        // Friendly Codex errors (shown in the card)
        ["friendly.codexNotFound"] = ["未找到 Codex CLI 或桌面端", "Codex CLI or desktop app not found", "Codex CLI 또는 데스크톱 앱을 찾을 수 없음"],
        ["friendly.codexLogin"] = ["请先在 Codex 中登录", "Sign in to Codex first", "먼저 Codex에 로그인하세요"],
        ["friendly.codexStart"] = ["无法启动 Codex CLI", "Couldn't start the Codex CLI", "Codex CLI를 시작할 수 없음"],
        ["friendly.usageUnavailable"] = ["用量暂时不可用", "Usage temporarily unavailable", "사용량을 일시적으로 사용할 수 없음"],
    };
}
