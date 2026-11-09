using System;
using System.Collections.Generic;
using System.Globalization;
using System.Windows;
using System.Windows.Media;

namespace ThermalHud;

public readonly record struct TemperatureSample(DateTimeOffset Time, double? Cpu, double? Gpu);

public sealed class TemperatureChart : FrameworkElement
{
    private static readonly TimeSpan MaximumContinuousGap = TimeSpan.FromSeconds(20);
    private static readonly Typeface AxisFont = new("Consolas");
    private static readonly Typeface CaptionFont = new("Microsoft YaHei UI");
    private static readonly Brush PlotBrush = FrozenBrush(0xFF0B1420);
    private static readonly Brush AxisBrush = FrozenBrush(0xFFA2B3C6);
    private static readonly Pen GridPen = FrozenPen(FrozenBrush(0xFF243345), 1);
    private static readonly Pen CpuGlow = FrozenPen(FrozenBrush(0x2454ECDC), 5);
    private static readonly Pen GpuGlow = FrozenPen(FrozenBrush(0x24B7A4FF), 5);

    private IReadOnlyList<TemperatureSample> _samples = Array.Empty<TemperatureSample>();
    private TimeSpan _window = TimeSpan.FromMinutes(2);

    public void SetSamples(IReadOnlyList<TemperatureSample> samples, TimeSpan window)
    {
        ArgumentNullException.ThrowIfNull(samples);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(window, TimeSpan.Zero);
        _samples = samples;
        _window = window;
        InvalidateVisual();
    }

    protected override void OnRender(DrawingContext drawing)
    {
        base.OnRender(drawing);
        if (ActualWidth < 80 || ActualHeight < 58)
        {
            return;
        }

        var plot = new Rect(33, 10, ActualWidth - 42, ActualHeight - 32);
        var end = _samples.Count == 0 ? DateTimeOffset.Now : _samples[^1].Time;
        var start = end - _window;
        var min = 30d;
        var max = 90d;
        var hasData = false;
        foreach (var sample in _samples)
        {
            if (sample.Time >= start && sample.Time <= end)
            {
                Include(sample.Cpu);
                Include(sample.Gpu);
            }
        }

        var step = Math.Ceiling((max - min) / 20) * 5;
        min = Math.Floor(min / step) * step;
        max = Math.Ceiling(max / step) * step;
        var pixelsPerDip = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        drawing.DrawRoundedRectangle(PlotBrush, null, plot, 3, 3);

        for (var temperature = min; temperature <= max; temperature += step)
        {
            var y = PositionY(temperature);
            drawing.DrawLine(GridPen, new Point(plot.Left, y), new Point(plot.Right, y));
            var label = Format($"{temperature:0}°", AxisFont, 10);
            drawing.DrawText(label, new Point(plot.Left - 6 - label.Width, y - label.Height / 2));
        }

        for (var division = 0; division <= 4; division++)
        {
            var x = plot.Left + plot.Width * division / 4;
            drawing.DrawLine(GridPen, new Point(x, plot.Top), new Point(x, plot.Bottom));
        }

        var ago = Format(WindowLabel(_window), CaptionFont, 10);
        var now = Format("现在", CaptionFont, 10);
        drawing.DrawText(ago, new Point(plot.Left, plot.Bottom + 6));
        drawing.DrawText(now, new Point(plot.Right - now.Width, plot.Bottom + 6));

        if (!hasData)
        {
            var empty = Format("暂无温度数据", CaptionFont, 12);
            drawing.DrawText(empty, new Point(
                plot.Left + (plot.Width - empty.Width) / 2,
                plot.Top + (plot.Height - empty.Height) / 2));
            return;
        }

        var cpuBrush = (Brush?)TryFindResource("CpuBrush") ?? FrozenBrush(0xFF54ECDC);
        var gpuBrush = (Brush?)TryFindResource("GpuBrush") ?? FrozenBrush(0xFFB7A4FF);
        var clip = plot;
        clip.Inflate(4, 4);
        drawing.PushClip(new RectangleGeometry(clip));
        DrawSeries(cpu: true, cpuBrush, CpuGlow, null);
        DrawSeries(cpu: false, gpuBrush, GpuGlow, DashStyles.Dash);
        drawing.Pop();

        void Include(double? value)
        {
            if (value is double temperature && double.IsFinite(temperature))
            {
                hasData = true;
                min = Math.Min(min, temperature);
                max = Math.Max(max, temperature);
            }
        }

        double PositionY(double value) => plot.Bottom - (value - min) / (max - min) * plot.Height;

        FormattedText Format(string text, Typeface font, double size) =>
            new(text, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, font, size, AxisBrush, pixelsPerDip);

        void DrawSeries(bool cpu, Brush brush, Pen glow, DashStyle? dash)
        {
            var line = new StreamGeometry();
            var isolated = new List<Point>();
            Point? latest = null;
            Point? previous = null;
            DateTimeOffset? previousTime = null;
            var hasSegment = false;

            using (var path = line.Open())
            {
                foreach (var sample in _samples)
                {
                    if (sample.Time < start || sample.Time > end)
                    {
                        continue;
                    }

                    var value = cpu ? sample.Cpu : sample.Gpu;
                    if (previousTime is { } time && sample.Time - time > MaximumContinuousGap)
                        BreakSeries();

                    if (value is double temperature && double.IsFinite(temperature))
                    {
                        var x = plot.Left + (sample.Time - start).TotalSeconds / _window.TotalSeconds * plot.Width;
                        var point = new Point(x, PositionY(temperature));
                        if (previous is null)
                        {
                            path.BeginFigure(point, false, false);
                        }
                        else
                        {
                            path.LineTo(point, true, true);
                            hasSegment = true;
                        }

                        previous = point;
                        previousTime = sample.Time;
                    }
                    else
                    {
                        BreakSeries();
                    }

                    latest = previous;
                }
            }

            line.Freeze();
            drawing.DrawGeometry(null, glow, line);
            drawing.DrawGeometry(null, FrozenPen(brush, 1.7, dash), line);
            foreach (var point in isolated)
            {
                drawing.DrawEllipse(brush, null, point, 1.5, 1.5);
            }

            if (latest is Point endPoint)
            {
                drawing.DrawEllipse(PlotBrush, FrozenPen(brush, 1.4), endPoint, 2.7, 2.7);
            }

            void BreakSeries()
            {
                if (!hasSegment && previous is Point single)
                    isolated.Add(single);

                previous = null;
                previousTime = null;
                hasSegment = false;
            }
        }
    }

    private static string WindowLabel(TimeSpan window)
    {
        if (window.TotalSeconds % 60 == 0)
        {
            return $"{window.TotalMinutes:0} 分钟前";
        }

        return $"{window.TotalSeconds:0} 秒前";
    }

    private static SolidColorBrush FrozenBrush(uint color)
    {
        var brush = new SolidColorBrush(Color.FromArgb(
            (byte)(color >> 24), (byte)(color >> 16), (byte)(color >> 8), (byte)color));
        brush.Freeze();
        return brush;
    }

    private static Pen FrozenPen(Brush brush, double thickness, DashStyle? dash = null)
    {
        var pen = new Pen(brush, thickness)
        {
            StartLineCap = PenLineCap.Round,
            EndLineCap = PenLineCap.Round,
            LineJoin = PenLineJoin.Round,
            DashStyle = dash ?? DashStyles.Solid
        };
        if (pen.CanFreeze)
        {
            pen.Freeze();
        }

        return pen;
    }
}
