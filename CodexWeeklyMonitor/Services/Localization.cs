using System.Globalization;

namespace CodexWeeklyMonitor.Services;

public enum AppLanguage
{
    Chinese,
    English,
    Korean,
}

/// <summary>
/// App-wide string catalogue and current-language state. UI and services both call
/// <see cref="T(string, object[])"/>; the window subscribes to <see cref="Changed"/> to re-render.
/// </summary>
/// <remarks>
/// Language follows the OS on first run (Chinese and Korean systems get their own language, every
/// other system gets English) and is then remembered per user. Strings live in one table rather
/// than per-culture .resx files so a translator can see all three side by side.
/// </remarks>
public static class Loc
{
    private static readonly object Gate = new();
    private static readonly string StorePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "AiTokenMonitor",
        "language.txt");

    private static AppLanguage _current = LoadOrDetect();

    public static event EventHandler? Changed;

    public static AppLanguage Current
    {
        get
        {
            lock (Gate)
            {
                return _current;
            }
        }
    }

    public static IReadOnlyList<AppLanguage> All { get; } =
        [AppLanguage.Chinese, AppLanguage.English, AppLanguage.Korean];

    public static string DisplayName(AppLanguage language) => language switch
    {
        AppLanguage.Chinese => "简体中文",
        AppLanguage.Korean => "한국어",
        _ => "English",
    };

    public static void SetLanguage(AppLanguage language)
    {
        lock (Gate)
        {
            if (_current == language)
            {
                return;
            }

            _current = language;
        }

        Save(language);
        Changed?.Invoke(null, EventArgs.Empty);
    }

    /// <summary>Looks up <paramref name="key"/> in the current language, then formats any args.</summary>
    public static string T(string key, params object?[] args)
    {
        var text = Resolve(key, Current);
        if (args.Length == 0)
        {
            return text;
        }

        try
        {
            return string.Format(CultureInfo.CurrentCulture, text, args);
        }
        catch (FormatException)
        {
            return text;
        }
    }

    private static string Resolve(string key, AppLanguage language)
    {
        if (!Strings.Table.TryGetValue(key, out var translations))
        {
            return key;
        }

        var index = (int)language;
        var value = index < translations.Length ? translations[index] : null;
        // Fall back to English, then Chinese, so a missing translation never shows a blank.
        return value
               ?? translations.ElementAtOrDefault((int)AppLanguage.English)
               ?? translations[0]
               ?? key;
    }

    private static CultureInfo CultureFor(AppLanguage language) => language switch
    {
        AppLanguage.Chinese => CultureInfo.GetCultureInfo("zh-CN"),
        AppLanguage.Korean => CultureInfo.GetCultureInfo("ko-KR"),
        _ => CultureInfo.GetCultureInfo("en-US"),
    };

    /// <summary>Localized "month day" (e.g. 7月28日 / Jul 28 / 7월 28일).</summary>
    public static string MonthDay(DateTimeOffset value) =>
        value.ToLocalTime().ToString(T("fmt.monthDay"), CultureFor(Current));

    public static string MonthDay(DateOnly value) =>
        value.ToDateTime(TimeOnly.MinValue).ToString(T("fmt.monthDay"), CultureFor(Current));

    /// <summary>Localized "month day HH:mm".</summary>
    public static string MonthDayTime(DateTimeOffset value) =>
        value.ToLocalTime().ToString(T("fmt.monthDayTime"), CultureFor(Current));

    internal static AppLanguage DetectFromSystem()
    {
        var culture = CultureInfo.CurrentUICulture.TwoLetterISOLanguageName;
        return culture switch
        {
            "zh" => AppLanguage.Chinese,
            "ko" => AppLanguage.Korean,
            _ => AppLanguage.English,
        };
    }

    private static AppLanguage LoadOrDetect()
    {
        try
        {
            if (File.Exists(StorePath) &&
                Enum.TryParse<AppLanguage>(File.ReadAllText(StorePath).Trim(), out var stored))
            {
                return stored;
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Fall through to detection.
        }

        return DetectFromSystem();
    }

    private static void Save(AppLanguage language)
    {
        try
        {
            var directory = Path.GetDirectoryName(StorePath);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            File.WriteAllText(StorePath, language.ToString());
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Persisting the choice is best-effort; the app still switches for this session.
        }
    }
}
