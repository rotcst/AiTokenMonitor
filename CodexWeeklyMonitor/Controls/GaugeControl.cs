using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using System.Windows.Threading;
using CodexWeeklyMonitor.Models;
using Color = System.Windows.Media.Color;
using FontFamily = System.Windows.Media.FontFamily;
using HorizontalAlignment = System.Windows.HorizontalAlignment;
using LinearGradientBrush = System.Windows.Media.LinearGradientBrush;
using Path = System.Windows.Shapes.Path;
using Point = System.Windows.Point;
using RadialGradientBrush = System.Windows.Media.RadialGradientBrush;
using Rectangle = System.Windows.Shapes.Rectangle;
using UserControl = System.Windows.Controls.UserControl;
using VerticalAlignment = System.Windows.VerticalAlignment;
using Brushes = System.Windows.Media.Brushes;
using Cursors = System.Windows.Input.Cursors;

namespace CodexWeeklyMonitor.Controls;

internal enum GaugeProvider
{
    Codex,
    Claude,
}

internal enum GaugeQuotaPeriod
{
    FiveHour,
    Weekly,
}

/// <summary>
/// The orb's liquid dashboard: one circle split into two chambers — Codex on the left, Claude on the
/// right — each a draining tank whose water level is the provider's <em>remaining</em> quota. A full
/// tank reads 100% (fresh), a dry tank 0% (exhausted), and the readout always matches the water line.
/// </summary>
/// <remarks>
/// The whole control is clipped to a circle; a thin seam splits it. Each chamber clips its own liquid
/// so waves never bleed across the divider. The surface is two scrolling sine bands (a slow, faint
/// back swell and a quicker front ripple) whose horizontal drift runs forever on their own storyboards.
/// <see cref="Update"/> supplies both quota windows. Clicking either chamber switches that provider
/// independently between its 5-hour and weekly windows.
/// </remarks>
public sealed class GaugeControl : UserControl
{
    /// <summary>Diameter of the liquid disc; it sits just inside the orb bezel the window paints.</summary>
    internal const double Diameter = 116;
    private const double Radius = Diameter / 2;

    private static readonly Color CodexColor = Color.FromRgb(0x6C, 0xE0, 0x7A);
    private static readonly Color ClaudeColor = Color.FromRgb(0xF0, 0x9A, 0x6E);

    private readonly Func<DateTimeOffset> _clock;
    private readonly DispatcherTimer _countdownTimer;
    private readonly Chamber _codex;
    private readonly Chamber _claude;
    private RateLimitWindow? _codexFiveHour;
    private RateLimitWindow? _codexWeekly;
    private RateLimitWindow? _claudeFiveHour;
    private RateLimitWindow? _claudeWeekly;
    private GaugeQuotaPeriod _codexPeriod = GaugeQuotaPeriod.FiveHour;
    private GaugeQuotaPeriod _claudePeriod = GaugeQuotaPeriod.FiveHour;
    private bool _codexPeriodInitialized;
    private bool _claudePeriodInitialized;

    public GaugeControl()
        : this(() => DateTimeOffset.Now)
    {
    }

    internal GaugeControl(Func<DateTimeOffset> clock)
    {
        _clock = clock;
        Width = Diameter;
        Height = Diameter;

        var root = new Grid
        {
            Width = Diameter,
            Height = Diameter,
            ClipToBounds = true,
            Clip = new EllipseGeometry(new Point(Radius, Radius), Radius, Radius),
        };

        _codex = new Chamber(CodexColor, "CODEX", isLeft: true);
        _claude = new Chamber(ClaudeColor, "CLAUDE", isLeft: false);
        root.Children.Add(_codex.Root);
        root.Children.Add(_claude.Root);
        root.Children.Add(BuildDivider());
        root.Children.Add(BuildGlassHighlight());

        Content = root;
        _countdownTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromSeconds(10),
        };
        _countdownTimer.Tick += (_, _) => RefreshCountdowns();
        Loaded += (_, _) => _countdownTimer.Start();
        Unloaded += (_, _) => _countdownTimer.Stop();

        Update(null, null, null, null);
    }

    /// <summary>Supplies both quota windows for each provider without changing the user's selection.</summary>
    public void Update(
        RateLimitWindow? codexFiveHour,
        RateLimitWindow? codexWeekly,
        RateLimitWindow? claudeFiveHour,
        RateLimitWindow? claudeWeekly)
    {
        _codexFiveHour = codexFiveHour;
        _codexWeekly = codexWeekly;
        _claudeFiveHour = claudeFiveHour;
        _claudeWeekly = claudeWeekly;

        InitializePeriod(
            ref _codexPeriod,
            ref _codexPeriodInitialized,
            codexFiveHour,
            codexWeekly);
        InitializePeriod(
            ref _claudePeriod,
            ref _claudePeriodInitialized,
            claudeFiveHour,
            claudeWeekly);
        RefreshReadouts();
    }

    internal static string FormatPercent(int? percent) => percent is null ? "--" : $"{percent}%";

    internal GaugeQuotaPeriod CodexPeriod => _codexPeriod;

    internal GaugeQuotaPeriod ClaudePeriod => _claudePeriod;

    internal string CodexTitleText => _codex.TitleText;

    internal string ClaudeTitleText => _claude.TitleText;

    internal string CodexPercentText => _codex.PercentText;

    internal string ClaudePercentText => _claude.PercentText;

    internal string CodexResetText => _codex.ResetText;

    internal string ClaudeResetText => _claude.ResetText;

    internal void ToggleProvider(GaugeProvider provider)
    {
        if (provider == GaugeProvider.Codex)
        {
            _codexPeriod = Toggle(_codexPeriod);
            _codexPeriodInitialized = true;
        }
        else
        {
            _claudePeriod = Toggle(_claudePeriod);
            _claudePeriodInitialized = true;
        }

        RefreshReadouts();
    }

    internal bool ToggleProviderAt(Point point)
    {
        if (ProviderAt(point) is not { } provider)
        {
            return false;
        }

        ToggleProvider(provider);
        return true;
    }

    internal static GaugeProvider? ProviderAt(Point point)
    {
        var offsetX = point.X - Radius;
        var offsetY = point.Y - Radius;
        if ((offsetX * offsetX) + (offsetY * offsetY) > Radius * Radius)
        {
            return null;
        }

        return point.X < Radius ? GaugeProvider.Codex : GaugeProvider.Claude;
    }

    internal void RefreshCountdowns()
    {
        var now = _clock();
        _codex.RefreshCountdown(now);
        _claude.RefreshCountdown(now);
    }

    internal static string FormatResetCountdown(DateTimeOffset? resetsAt, DateTimeOffset now)
    {
        if (resetsAt is null)
        {
            return "--";
        }

        var remaining = resetsAt.Value - now;
        if (remaining <= TimeSpan.Zero)
        {
            return "0m";
        }

        if (remaining.TotalDays >= 1)
        {
            return $"{(int)remaining.TotalDays}d {remaining.Hours}h";
        }

        return remaining.TotalHours >= 1
            ? $"{(int)remaining.TotalHours}h {remaining.Minutes}m"
            : $"{Math.Max(1, (int)remaining.TotalMinutes)}m";
    }

    private void RefreshReadouts()
    {
        var now = _clock();
        _codex.SetWindow(SelectWindow(_codexFiveHour, _codexWeekly, _codexPeriod), _codexPeriod, now);
        _claude.SetWindow(SelectWindow(_claudeFiveHour, _claudeWeekly, _claudePeriod), _claudePeriod, now);
    }

    private static void InitializePeriod(
        ref GaugeQuotaPeriod period,
        ref bool initialized,
        RateLimitWindow? fiveHour,
        RateLimitWindow? weekly)
    {
        if (initialized || (fiveHour is null && weekly is null))
        {
            return;
        }

        period = fiveHour is not null ? GaugeQuotaPeriod.FiveHour : GaugeQuotaPeriod.Weekly;
        initialized = true;
    }

    private static RateLimitWindow? SelectWindow(
        RateLimitWindow? fiveHour,
        RateLimitWindow? weekly,
        GaugeQuotaPeriod period) =>
        period == GaugeQuotaPeriod.FiveHour ? fiveHour : weekly;

    private static GaugeQuotaPeriod Toggle(GaugeQuotaPeriod period) =>
        period == GaugeQuotaPeriod.FiveHour ? GaugeQuotaPeriod.Weekly : GaugeQuotaPeriod.FiveHour;

    private static string PeriodLabel(GaugeQuotaPeriod period) =>
        period == GaugeQuotaPeriod.FiveHour ? "5H" : "W";

    // A faint vertical seam between the two tanks, fading out top and bottom so it reads as a divider,
    // not a hard line drawn across the glass.
    private static UIElement BuildDivider() => new Rectangle
    {
        Width = 1.1,
        HorizontalAlignment = HorizontalAlignment.Center,
        VerticalAlignment = VerticalAlignment.Stretch,
        Margin = new Thickness(0, Radius * 0.3, 0, Radius * 0.3),
        IsHitTestVisible = false,
        Fill = new LinearGradientBrush(
            new GradientStopCollection
            {
                new GradientStop(Color.FromArgb(0x00, 0xFF, 0xFF, 0xFF), 0),
                new GradientStop(Color.FromArgb(0x33, 0xFF, 0xFF, 0xFF), 0.5),
                new GradientStop(Color.FromArgb(0x00, 0xFF, 0xFF, 0xFF), 1),
            },
            new Point(0.5, 0),
            new Point(0.5, 1)),
    };

    // A soft light spot in the upper-left sells the "glass sphere" read over the flat liquid.
    private static UIElement BuildGlassHighlight() => new Ellipse
    {
        Width = Diameter * 0.6,
        Height = Diameter * 0.4,
        HorizontalAlignment = HorizontalAlignment.Left,
        VerticalAlignment = VerticalAlignment.Top,
        Margin = new Thickness(Diameter * 0.1, Diameter * 0.06, 0, 0),
        IsHitTestVisible = false,
        Fill = new RadialGradientBrush(
            new GradientStopCollection
            {
                new GradientStop(Color.FromArgb(0x2E, 0xFF, 0xFF, 0xFF), 0),
                new GradientStop(Color.FromArgb(0x00, 0xFF, 0xFF, 0xFF), 1),
            })
        {
            GradientOrigin = new Point(0.4, 0.35),
            Center = new Point(0.4, 0.35),
        },
    };

    /// <summary>One half of the orb: a clipped tank with layered scrolling waves and a readout.</summary>
    private sealed class Chamber
    {
        private const double Width = Radius;              // each tank spans half the disc
        private const double TileWidth = Radius * 3;      // wide enough that a scrolled wave never gaps
        private static readonly FontFamily MonoFont = new("Cascadia Mono, Consolas, monospace");

        private readonly List<TranslateTransform> _levelTransforms = [];
        private readonly TextBlock _title;
        private readonly TextBlock _readout;
        private readonly TextBlock _reset;
        private DateTimeOffset? _resetsAt;
        private GaugeQuotaPeriod _period;

        public Chamber(Color color, string name, bool isLeft)
        {
            Root = new Grid
            {
                Width = Width,
                Height = Diameter,
                ClipToBounds = true,
                HorizontalAlignment = isLeft ? HorizontalAlignment.Left : HorizontalAlignment.Right,
                Background = Brushes.Transparent,
                Cursor = Cursors.Hand,
            };

            // Liquid lives in its own canvas so the wave paths can be freely positioned and scrolled.
            var liquid = new Canvas { ClipToBounds = true, IsHitTestVisible = false };
            // Back swell: taller, slower, fainter. Front ripple: shorter, quicker, brighter.
            liquid.Children.Add(BuildWave(color, alpha: 0x3A, amplitude: 3.4, wavelength: Width, seconds: 6.5, phase: 0));
            liquid.Children.Add(BuildWave(color, alpha: 0x5E, amplitude: 2.5, wavelength: Width * 0.62, seconds: 4.3, phase: Math.PI * 0.6));
            Root.Children.Add(liquid);

            // Provider above, remaining quota in the centre, reset countdown below. The compact
            // period prefix on the countdown makes the independently selected window unambiguous.
            _title = new TextBlock
            {
                Text = name,
                FontFamily = MonoFont,
                FontSize = 7.5,
                FontWeight = FontWeights.SemiBold,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, -39, 0, 0),
                IsHitTestVisible = false,
                Foreground = new SolidColorBrush(Color.FromArgb(0xB4, color.R, color.G, color.B)),
            };
            _readout = new TextBlock
            {
                Text = "--",
                FontFamily = MonoFont,
                FontSize = 17,
                FontWeight = FontWeights.Bold,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 0, 0),
                IsHitTestVisible = false,
                Foreground = new SolidColorBrush(Lighten(color)),
                Effect = new System.Windows.Media.Effects.DropShadowEffect
                {
                    BlurRadius = 4,
                    ShadowDepth = 0,
                    Opacity = 0.9,
                    Color = Color.FromRgb(0, 0, 0),
                },
            };
            _reset = new TextBlock
            {
                Text = "5H · --",
                Width = Width - 4,
                FontFamily = MonoFont,
                FontSize = 7,
                FontWeight = FontWeights.SemiBold,
                TextAlignment = TextAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 39, 0, 0),
                IsHitTestVisible = false,
                Foreground = new SolidColorBrush(Color.FromArgb(0xC8, 0xF3, 0xF6, 0xFA)),
                Effect = new System.Windows.Media.Effects.DropShadowEffect
                {
                    BlurRadius = 3,
                    ShadowDepth = 0,
                    Opacity = 0.8,
                    Color = Color.FromRgb(0, 0, 0),
                },
            };
            Root.Children.Add(_title);
            Root.Children.Add(_readout);
            Root.Children.Add(_reset);
        }

        public Grid Root { get; }

        public string TitleText => _title.Text;

        public string PercentText => _readout.Text;

        public string ResetText => _reset.Text;

        /// <summary>Switches the displayed window, then slides the water line to its remaining quota.</summary>
        public void SetWindow(
            RateLimitWindow? window,
            GaugeQuotaPeriod period,
            DateTimeOffset now)
        {
            _period = period;
            _resetsAt = window?.ResetsAt;
            SetLevel(window?.RemainingPercent);
            RefreshCountdown(now);
        }

        public void RefreshCountdown(DateTimeOffset now)
        {
            _reset.Text = $"{PeriodLabel(_period)} · {FormatResetCountdown(_resetsAt, now)}";
        }

        private void SetLevel(int? remaining)
        {
            _readout.Text = FormatPercent(remaining);

            // Water line measured from the top: full quota → line at the top (y = 0); empty → bottom.
            var fraction = remaining is { } value ? Math.Clamp(value, 0, 100) / 100.0 : 0;
            var lineY = Diameter * (1 - fraction);

            var slide = new DoubleAnimation(lineY, TimeSpan.FromMilliseconds(650))
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseInOut },
            };
            foreach (var transform in _levelTransforms)
            {
                transform.BeginAnimation(TranslateTransform.YProperty, slide);
            }
        }

        // A filled band whose top edge is a sine wave. It scrolls horizontally forever; SetLevel drives
        // its vertical offset. The geometry reaches far below the disc so a low water line still fills.
        private UIElement BuildWave(Color color, byte alpha, double amplitude, double wavelength, double seconds, double phase)
        {
            var figure = new PathFigure { StartPoint = new Point(0, WaveAt(0, amplitude, wavelength, phase)) };
            var edge = new PolyLineSegment();
            const int steps = 48;
            for (var i = 1; i <= steps; i++)
            {
                var x = TileWidth * i / steps;
                edge.Points.Add(new Point(x, WaveAt(x, amplitude, wavelength, phase)));
            }

            edge.Points.Add(new Point(TileWidth, Diameter * 2));
            edge.Points.Add(new Point(0, Diameter * 2));
            figure.Segments.Add(edge);
            figure.IsClosed = true;

            var levelTransform = new TranslateTransform(0, Diameter);   // starts empty; SetLevel raises it
            _levelTransforms.Add(levelTransform);
            var scrollTransform = new TranslateTransform(0, 0);

            var path = new Path
            {
                Fill = new SolidColorBrush(Color.FromArgb(alpha, color.R, color.G, color.B)),
                IsHitTestVisible = false,
                Data = new PathGeometry([figure]),
                RenderTransform = new TransformGroup { Children = { scrollTransform, levelTransform } },
            };
            // Centre the wide tile over the narrow chamber so both scroll directions stay covered.
            Canvas.SetLeft(path, -(TileWidth - Width) / 2);

            var drift = new DoubleAnimation(0, -wavelength, TimeSpan.FromSeconds(seconds))
            {
                RepeatBehavior = RepeatBehavior.Forever,
            };
            scrollTransform.BeginAnimation(TranslateTransform.XProperty, drift);
            return path;
        }

        private static double WaveAt(double x, double amplitude, double wavelength, double phase) =>
            amplitude * Math.Sin((2 * Math.PI * x / wavelength) + phase);

        // Pull the readout colour toward white so the digits pop against their own tinted water.
        private static Color Lighten(Color color) => Color.FromRgb(
            (byte)(color.R + ((255 - color.R) * 0.45)),
            (byte)(color.G + ((255 - color.G) * 0.45)),
            (byte)(color.B + ((255 - color.B) * 0.45)));
    }
}
