using System.Runtime.InteropServices;
using System.Windows;

namespace ThermalHud;

public partial class App : Application
{
    private Mutex? instance;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        bool backgroundLaunch = e.Args.Contains(StartupRegistration.Argument, StringComparer.OrdinalIgnoreCase);
        instance = new Mutex(false, @"Local\ThermalHud.98DE0F4E", out bool firstInstance);
        if (!firstInstance)
        {
            if (!backgroundLaunch)
            {
                IntPtr existing = FindWindow(null, ThermalHud.MainWindow.WindowTitle);
                if (existing != IntPtr.Zero)
                {
                    if (IsIconic(existing))
                        ShowWindowAsync(existing, 9); // SW_RESTORE

                    SetForegroundWindow(existing);
                }
            }

            Shutdown();
            return;
        }

        MainWindow = new MainWindow { ShowActivated = !backgroundLaunch };
        MainWindow.Show();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        instance?.Dispose();
        base.OnExit(e);
    }

    [DllImport("user32.dll", EntryPoint = "FindWindowW", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern IntPtr FindWindow(string? windowClass, string title);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr window);

    [DllImport("user32.dll")]
    private static extern bool IsIconic(IntPtr window);

    [DllImport("user32.dll")]
    private static extern bool ShowWindowAsync(IntPtr window, int command);
}
