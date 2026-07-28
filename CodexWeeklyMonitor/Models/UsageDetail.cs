namespace CodexWeeklyMonitor.Models;

/// <summary>One label/value row in the expandable detail panel.</summary>
public sealed record UsageDetailItem(string Label, string Value)
{
    public string AutomationLabel => $"{Label}：{Value}";
}

/// <summary>A titled group of detail rows (账号 / 额度 / 用量 ...).</summary>
public sealed record UsageDetailSection(string Title, IReadOnlyList<UsageDetailItem> Items);
