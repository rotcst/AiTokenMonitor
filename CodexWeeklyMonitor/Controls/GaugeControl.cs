using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using Color = System.Windows.Media.Color;
using Point = System.Windows.Point;
using Size = System.Windows.Size;
using UserControl = System.Windows.Controls.UserControl;
using Path = System.Windows.Shapes.Path;
using Orientation = System.Windows.Controls.Orientation;
using HorizontalAlignment = System.Windows.HorizontalAlignment;
using VerticalAlignment = System.Windows.VerticalAlignment;
using FontFamily = System.Windows.Media.FontFamily;

namespace CodexWeeklyMonitor.Controls;

/// <summary>
/// A tachometer-style orb showing Codex and Claude usage at a glance. Each provider gets a filled
/// progress sweep (0 → used%) with a tapered pointer riding its own ring, plus a redline zone and a
/// digital readout — deliberately a car dashboard, not a clock face.
/// </summary>
/// <remarks>
/// The scale is a 250° sweep with the gap at the bottom. Two concentric rings (Codex outer, Claude
/// inner) keep the two values from colliding. Static chrome is built once; <see cref="Update"/> only
/// rewrites the coloured progress arcs, the two pointers, and the readouts.
/// </remarks>
public sealed class GaugeControl : UserControl
{
    private const double Scale = 1.5;
    internal const double ControlSize = 92 * Scale;
    internal const double ReadoutTop = 63 * Scale;
    private const double Size = ControlSize;
    private const double Center = Size / 2;
    private const double MinAngle = -125;
    private const double Sweep = 250;
    private const double RedlineStart = 85;

    private const double CodexRadius = 36 * Scale;
    private const double ClaudeRadius = 26 * Scale;
    private const double RingThickness = 4.2 * Scale;

    private static readonly Color CodexColor = Color.FromRgb(0x6C, 0xE0, 0x7A);
    private static readonly Color ClaudeColor = Color.FromRgb(0xF0, 0x9A, 0x6E);
    private static readonly Color TrackColor = Color.FromArgb(0x30, 0xFF, 0xFF, 0xFF);
    private static readonly Color RedlineColor = Color.FromRgb(0xF5, 0x45, 0x3C);

    private readonly Canvas _canvas = new() { Width = Size, Height = Size };
    private readonly TextBlock _codexReadout;
    private readonly TextBlock _claudeReadout;

    // Rebuilt on every Update() so the swept arc + pointer can change; kept to remove cleanly.
    private readonly List<UIElement> _dynamic = [];

    public GaugeControl()
    {
        Width = Size;
        Height = Size;

        // Background tracks for each ring.
        AddStatic(BuildArc(0, 100, CodexRadius, TrackColor, RingThickness));
        AddStatic(BuildArc(0, 100, ClaudeRadius, TrackColor, RingThickness));

        // Redline band on the outer scale so the danger zone always reads.
        AddStatic(BuildArc(RedlineStart, 100, CodexRadius, Color.FromArgb(0x55, RedlineColor.R, RedlineColor.G, RedlineColor.B), RingThickness));

        // Bold major ticks + numbers, subtle minor ticks — car-gauge cadence, not clock ticks.
        for (var pct = 0; pct <= 100; pct += 10)
        {
            var major = pct % 20 == 0;
            AddStatic(BuildTick(pct, major));
            if (major)
            {
                AddStatic(BuildNumber(pct));
            }
        }

        // Chrome-look hub.
        var hub = new Ellipse
        {
            Width = 10 * Scale,
            Height = 10 * Scale,
            Fill = new RadialGradientBrush(
                Color.FromRgb(0x5A, 0x63, 0x70),
                Color.FromRgb(0x20, 0x25, 0x2D))
            {
                GradientOrigin = new Point(0.35, 0.3),
                Center = new Point(0.35, 0.3),
            },
            Stroke = new SolidColorBrush(Color.FromArgb(0x66, 0xFF, 0xFF, 0xFF)),
            StrokeThickness = Scale,
        };
        Canvas.SetLeft(hub, Center - (5 * Scale));
        Canvas.SetTop(hub, Center - (5 * Scale));
        AddStatic(hub);

        // Digital readouts stacked in the bottom gap, colour-matched to their ring.
        _codexReadout = BuildReadout(CodexColor);
        _claudeReadout = BuildReadout(ClaudeColor);
        var legend = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center, Width = Size };
        legend.Children.Add(BuildLegendRow(CodexColor, "CODEX", _codexReadout));
        legend.Children.Add(BuildLegendRow(ClaudeColor, "CLAUDE", _claudeReadout));
        Canvas.SetLeft(legend, 0);
        Canvas.SetTop(legend, ReadoutTop);
        AddStatic(legend);

        Content = _canvas;
        Update(null, null);
    }

    /// <summary>Redraws each ring's progress sweep + pointer and the readouts.</summary>
    public void Update(int? codexPercent, int? claudePercent)
    {
        foreach (var element in _dynamic)
        {
            _canvas.Children.Remove(element);
        }

        _dynamic.Clear();

        DrawProvider(codexPercent, CodexRadius, CodexColor, 32 * Scale);
        DrawProvider(claudePercent, ClaudeRadius, ClaudeColor, 22 * Scale);

        _codexReadout.Text = FormatPercent(codexPercent);
        _claudeReadout.Text = FormatPercent(claudePercent);
    }

    private void DrawProvider(int? percent, double radius, Color color, double pointerLength)
    {
        if (percent is not { } value)
        {
            return;
        }

        value = Math.Clamp(value, 0, 100);
        if (value > 0)
        {
            // Filled progress sweep — the part that makes it read as a gauge rather than a clock.
            var swept = BuildArc(0, value, radius, color, RingThickness);
            AddDynamic(swept);
        }

        AddDynamic(BuildPointer(value, color, pointerLength));
    }

    private static double AngleFor(double percent) => MinAngle + (Sweep * percent / 100.0);

    private static Point PointFor(double percent, double radius)
    {
        var radians = AngleFor(percent) * Math.PI / 180.0;
        return new Point(
            Center + (radius * Math.Sin(radians)),
            Center - (radius * Math.Cos(radians)));
    }

    private static Path BuildArc(double startPercent, double endPercent, double radius, Color color, double thickness)
    {
        var figure = new PathFigure { StartPoint = PointFor(startPercent, radius) };
        figure.Segments.Add(new ArcSegment
        {
            Point = PointFor(endPercent, radius),
            Size = new Size(radius, radius),
            SweepDirection = SweepDirection.Clockwise,
            IsLargeArc = (endPercent - startPercent) * Sweep / 100.0 > 180,
        });

        return new Path
        {
            Stroke = new SolidColorBrush(color),
            StrokeThickness = thickness,
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round,
            Data = new PathGeometry([figure]),
        };
    }

    private static Line BuildTick(double percent, bool major)
    {
        var outer = PointFor(percent, CodexRadius + RingThickness / 2 + (1.5 * Scale));
        var inner = PointFor(percent, CodexRadius + RingThickness / 2 + ((major ? 5.5 : 3) * Scale));
        return new Line
        {
            X1 = outer.X,
            Y1 = outer.Y,
            X2 = inner.X,
            Y2 = inner.Y,
            Stroke = new SolidColorBrush(percent >= RedlineStart
                ? RedlineColor
                : Color.FromArgb(major ? (byte)0xCC : (byte)0x66, 0xC6, 0xD0, 0xDA)),
            StrokeThickness = (major ? 1.2 : 0.7) * Scale,
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round,
        };
    }

    private static TextBlock BuildNumber(double percent)
    {
        var text = new TextBlock
        {
            Text = ((int)percent).ToString(),
            FontFamily = new FontFamily("Cascadia Mono, Consolas, monospace"),
            FontSize = 4.5 * Scale,
            FontWeight = FontWeights.SemiBold,
            Foreground = new SolidColorBrush(Color.FromRgb(0x8C, 0x98, 0xA6)),
        };
        text.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        var at = PointFor(percent, CodexRadius + RingThickness / 2 + (9 * Scale));
        Canvas.SetLeft(text, at.X - text.DesiredSize.Width / 2);
        Canvas.SetTop(text, at.Y - text.DesiredSize.Height / 2);
        return text;
    }

    /// <summary>A tapered tach pointer: wide near the hub, sharp at the tip, with a short counterweight tail.</summary>
    private static Shape BuildPointer(double percent, Color color, double length)
    {
        var polygon = new Polygon
        {
            Points =
            [
                new Point(Center, Center - length),   // tip
                new Point(Center - (2.2 * Scale), Center - Scale),       // left shoulder
                new Point(Center - (1.5 * Scale), Center + (5 * Scale)), // tail left
                new Point(Center + (1.5 * Scale), Center + (5 * Scale)), // tail right
                new Point(Center + (2.2 * Scale), Center - Scale),       // right shoulder
            ],
            Fill = new LinearGradientBrush(
                Color.FromArgb(0xFF, color.R, color.G, color.B),
                Color.FromArgb(0xB0, color.R, color.G, color.B),
                90),
            Stroke = new SolidColorBrush(Color.FromArgb(0x40, 0x00, 0x00, 0x00)),
            StrokeThickness = 0.4 * Scale,
            RenderTransform = new RotateTransform(AngleFor(percent), Center, Center),
        };
        polygon.Effect = new System.Windows.Media.Effects.DropShadowEffect
        {
            BlurRadius = 3 * Scale,
            ShadowDepth = 0,
            Opacity = 0.5,
            Color = color,
        };
        return polygon;
    }

    private static TextBlock BuildReadout(Color color) => new()
    {
        Text = "--",
        FontFamily = new FontFamily("Cascadia Mono, Consolas, monospace"),
        FontSize = 6.5 * Scale,
        FontWeight = FontWeights.Bold,
        Foreground = new SolidColorBrush(color),
        VerticalAlignment = VerticalAlignment.Center,
        Margin = new Thickness(2.5 * Scale, 0, 0, 0),
        MinWidth = 17 * Scale,
    };

    private static UIElement BuildLegendRow(Color color, string name, TextBlock readout)
    {
        var row = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 0, 0, Scale),
        };
        row.Children.Add(new TextBlock
        {
            Text = name,
            FontFamily = new FontFamily("Cascadia Mono, Consolas, monospace"),
            FontSize = 4.8 * Scale,
            FontWeight = FontWeights.SemiBold,
            Foreground = new SolidColorBrush(Color.FromArgb(0xB0, color.R, color.G, color.B)),
            VerticalAlignment = VerticalAlignment.Center,
        });
        row.Children.Add(readout);
        return row;
    }

    private static string FormatPercent(int? percent) => percent is null ? "--" : $"{percent}%";

    private void AddStatic(UIElement element) => _canvas.Children.Add(element);

    private void AddDynamic(UIElement element)
    {
        _canvas.Children.Add(element);
        _dynamic.Add(element);
    }
}
