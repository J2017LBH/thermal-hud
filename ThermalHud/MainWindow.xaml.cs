using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Media;
using System.Security;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace ThermalHud;

public partial class MainWindow : Window
{
    public const string WindowTitle = "Thermal HUD · 硬件温度监控";
    private static readonly TimeSpan MaximumHistory = TimeSpan.FromMinutes(15);
    private static readonly TimeSpan SoundCooldown = TimeSpan.FromSeconds(30);
    private const int MaximumSamples = 451;

    private readonly List<TemperatureSample> history = new(MaximumSamples);
    private readonly CancellationTokenSource stop = new();
    private readonly Brush cpuBrush;
    private readonly Brush gpuBrush;
    private readonly Brush fanBrush;
    private readonly Brush alertBrush;
    private HudSettings settings = HudSettings.Load();
    private SensorSnapshot? latestReading;
    private List<FanRow> visibleFans = [];
    private HashSet<string> activeAlerts = new(StringComparer.Ordinal);
    private long? lastSound;
    private Task? monitoring;

    public MainWindow()
    {
        InitializeComponent();
        cpuBrush = (Brush)FindResource("CpuBrush");
        gpuBrush = (Brush)FindResource("GpuBrush");
        fanBrush = (Brush)FindResource("FanBrush");
        alertBrush = (Brush)FindResource("AlertBrush");
        ApplyWindowMode();

        var workArea = SystemParameters.WorkArea;
        Left = Math.Max(workArea.Left, workArea.Right - Width - 16);
        Top = workArea.Top + 16;
    }

    private void StartMonitoring(object sender, RoutedEventArgs e)
    {
        monitoring ??= Task.Run(() => MonitorAsync(stop.Token));
    }

    private async Task MonitorAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var sensors = new SensorReader();
            while (!cancellationToken.IsCancellationRequested)
            {
                var reading = sensors.Read();
                var timestamp = DateTimeOffset.Now;
                int delay = await Dispatcher.InvokeAsync(() =>
                {
                    DisplayReading(reading, timestamp);
                    return settings.SampleSeconds;
                }, DispatcherPriority.Background, cancellationToken).Task.ConfigureAwait(false);
                await Task.Delay(TimeSpan.FromSeconds(delay), cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        catch (Exception error)
        {
            if (cancellationToken.IsCancellationRequested)
                return;

            Trace.TraceError("Thermal sensor polling stopped: {0}", error);
            await Dispatcher.InvokeAsync(() =>
            {
                latestReading = null;
                history.Add(new TemperatureSample(DateTimeOffset.Now, null, null));
                RefreshChart();
                CpuValue.Text = GpuValue.Text = "—";
                FanEmpty.Text = "暂无转速数据";
                UpdateTime.Text = "";
                SensorStatus.Text = "读取中断 · 可重开窗口";
                RefreshAlerts();
            }, DispatcherPriority.Background).Task.ConfigureAwait(false);
        }
    }

    private async void StopMonitoring(object? sender, EventArgs e)
    {
        stop.Cancel();
        try
        {
            if (monitoring is not null)
                await monitoring.ConfigureAwait(false);
        }
        catch (OperationCanceledException) { }
        finally
        {
            stop.Dispose();
        }
    }

    private void DisplayReading(SensorSnapshot reading, DateTimeOffset timestamp)
    {
        latestReading = reading;
        CpuValue.Text = FormatTemperature(reading.CpuTemperature);
        GpuValue.Text = FormatTemperature(reading.GpuTemperature);
        AutomationProperties.SetName(CpuValue, $"CPU 温度 {AccessibleTemperature(reading.CpuTemperature)}");
        AutomationProperties.SetName(GpuValue, $"GPU 温度 {AccessibleTemperature(reading.GpuTemperature)}");
        CpuVendor.Text = reading.CpuVendor;
        GpuVendor.Text = reading.GpuVendor;
        CpuValue.ToolTip = reading.CpuTemperature is null ? "当前无法读取 CPU 温度" : null;
        GpuValue.ToolTip = reading.GpuTemperatureSource ?? "当前无法读取 GPU 温度";
        FanEmpty.Text = "暂无转速数据";

        var missing = new List<string>(3);
        if (reading.CpuTemperature is null) missing.Add("CPU");
        if (reading.GpuTemperature is null) missing.Add("GPU");
        if (reading.Fans.Length == 0) missing.Add("风扇");
        SensorStatus.Text = missing.Count == 0
            ? "传感器在线"
            : $"未读到 {string.Join(" / ", missing)}";
        UpdateTime.Text = $"更新 {timestamp:HH:mm:ss}";

        if (history.Count > 0 && timestamp < history[^1].Time)
            history.Clear();
        var cutoff = timestamp - MaximumHistory;
        var expired = history.FindIndex(sample => sample.Time >= cutoff);
        if (expired < 0)
            history.Clear();
        else if (expired > 0)
            history.RemoveRange(0, expired);
        if (history.Count == MaximumSamples)
            history.RemoveAt(0);
        history.Add(new TemperatureSample(timestamp, reading.CpuTemperature, reading.GpuTemperature));
        RefreshChart();
        RefreshAlerts();
    }

    private static string FormatTemperature(float? temperature) =>
        temperature?.ToString("0", CultureInfo.CurrentCulture) ?? "—";

    private static string AccessibleTemperature(float? temperature) =>
        temperature is null ? "暂无数据" : $"{FormatTemperature(temperature)} 摄氏度";

    private void RefreshAlerts()
    {
        var currentAlerts = new HashSet<string>(StringComparer.Ordinal);
        var messages = new List<string>();
        var cpuWarning = latestReading?.CpuTemperature is float cpu
            && settings.CpuLimit is int cpuLimit && cpu >= cpuLimit;
        var gpuWarning = latestReading?.GpuTemperature is float gpu
            && settings.GpuLimit is int gpuLimit && gpu >= gpuLimit;
        if (cpuWarning)
        {
            currentAlerts.Add("CPU");
            messages.Add($"CPU ≥ {settings.CpuLimit} °C");
        }

        if (gpuWarning)
        {
            currentAlerts.Add("GPU");
            messages.Add($"GPU ≥ {settings.GpuLimit} °C");
        }

        CpuValue.Foreground = cpuWarning ? alertBrush : cpuBrush;
        GpuValue.Foreground = gpuWarning ? alertBrush : gpuBrush;

        var fanRows = new List<FanRow>();
        foreach (var fan in latestReading?.Fans ?? [])
        {
            var fanWarning = settings.FanLimit is int fanLimit && fan.Rpm < fanLimit;
            if (fanWarning)
            {
                currentAlerts.Add("Fan/" + fan.Name);
                messages.Add($"{fan.Name} < {settings.FanLimit} RPM");
            }

            fanRows.Add(new FanRow(fan.Name, fan.Rpm.ToString("N0", CultureInfo.CurrentCulture),
                fanWarning ? alertBrush : fanBrush));
        }

        if (!visibleFans.SequenceEqual(fanRows))
        {
            visibleFans = fanRows;
            FanList.ItemsSource = visibleFans;
        }

        FanEmpty.Visibility = fanRows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        var message = messages.Count == 0 ? "" : $"告警 · {string.Join("   /   ", messages)}";
        AlertBanner.Visibility = messages.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        if (AlertText.Text != message)
        {
            AlertText.Text = message;
            if (messages.Count > 0)
                UIElementAutomationPeer.CreatePeerForElement(AlertText)?.RaiseAutomationEvent(AutomationEvents.LiveRegionChanged);
        }

        if (settings.PlaySound
            && currentAlerts.Except(activeAlerts).Any()
            && (lastSound is null || Stopwatch.GetElapsedTime(lastSound.Value) >= SoundCooldown))
        {
            SystemSounds.Exclamation.Play();
            lastSound = Stopwatch.GetTimestamp();
        }

        activeAlerts = currentAlerts;
    }

    private void ChangeChartRange(object sender, SelectionChangedEventArgs e) => RefreshChart();

    private void RefreshChart()
    {
        if (HistoryChart is null)
            return;

        var minutes = ChartRange.SelectedIndex switch { 1 => 5, 2 => 15, _ => 1 };
        HistoryChart.SetSamples(history, TimeSpan.FromMinutes(minutes));
    }

    private void DragHeader(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed)
            DragMove();
    }

    private void CloseWindow(object sender, RoutedEventArgs e) => Close();

    private void ToggleSettings(object sender, RoutedEventArgs e)
    {
        if (SettingsPanel.Visibility == Visibility.Visible)
        {
            ShowDashboard();
            return;
        }

        CpuLimit.Text = settings.CpuLimit?.ToString(CultureInfo.CurrentCulture) ?? "";
        GpuLimit.Text = settings.GpuLimit?.ToString(CultureInfo.CurrentCulture) ?? "";
        FanLimit.Text = settings.FanLimit?.ToString(CultureInfo.CurrentCulture) ?? "";
        SampleRate.SelectedIndex = settings.SampleSeconds switch { 5 => 1, 10 => 2, _ => 0 };
        AlertSound.IsChecked = settings.PlaySound;
        KeepOnTop.IsChecked = settings.KeepOnTop;
        SettingsError.Visibility = Visibility.Collapsed;
        try
        {
            LaunchOnLogin.IsChecked = StartupRegistration.IsEnabled();
            LaunchOnLogin.IsEnabled = true;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or SecurityException)
        {
            LaunchOnLogin.IsChecked = false;
            LaunchOnLogin.IsEnabled = false;
            ShowSettingsError("无法读取当前账户的自启动设置。");
        }

        Dashboard.Visibility = Visibility.Collapsed;
        SettingsPanel.Visibility = Visibility.Visible;
        CpuLimit.Focus();
        CpuLimit.SelectAll();
    }

    private void SaveSettings(object sender, RoutedEventArgs e)
    {
        if (!TryReadLimit(CpuLimit, 125, "CPU 高温", out var cpu)
            || !TryReadLimit(GpuLimit, 125, "GPU 高温", out var gpu)
            || !TryReadLimit(FanLimit, 30000, "风扇低转速", out var fan))
            return;

        var updated = new HudSettings
        {
            CpuLimit = cpu,
            GpuLimit = gpu,
            FanLimit = fan,
            SampleSeconds = SampleRate.SelectedIndex switch { 1 => 5, 2 => 10, _ => 2 },
            PlaySound = AlertSound.IsChecked == true,
            KeepOnTop = KeepOnTop.IsChecked == true
        };

        try { updated.Save(); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            ShowSettingsError("无法保存到本机。请检查本地文件夹的写入权限。");
            return;
        }

        settings = updated;
        ApplyWindowMode();
        RefreshAlerts();
        try
        {
            if (LaunchOnLogin.IsEnabled)
                StartupRegistration.SetEnabled(LaunchOnLogin.IsChecked == true);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or SecurityException)
        {
            ShowSettingsError("监控设置已保存。无法更新自启动，请检查程序路径或账户权限。");
            return;
        }

        ShowDashboard();
    }

    private bool TryReadLimit(TextBox box, int maximum, string label, out int? limit)
    {
        var value = box.Text.Trim();
        limit = null;
        if (value.Length == 0)
            return true;
        if (int.TryParse(value, out var number) && number >= 1 && number <= maximum)
        {
            limit = number;
            return true;
        }

        ShowSettingsError($"{label}阈值请输入 1 至 {maximum} 的整数，或留空。");
        box.Focus();
        box.SelectAll();
        return false;
    }

    private void ShowSettingsError(string message)
    {
        SettingsError.Text = message;
        SettingsError.Visibility = Visibility.Visible;
    }

    private void ShowDashboard()
    {
        SettingsPanel.Visibility = Visibility.Collapsed;
        Dashboard.Visibility = Visibility.Visible;
        SettingsButton.Focus();
    }

    private void ApplyWindowMode()
    {
        Topmost = settings.KeepOnTop;
        WindowMode.Text = Topmost ? "只读硬件监控 · 置顶" : "只读硬件监控";
    }

    private sealed record FanRow(string Name, string Value, Brush Brush);
}
