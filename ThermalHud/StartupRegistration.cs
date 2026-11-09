using System.IO;
using Microsoft.Win32;

namespace ThermalHud;

internal static class StartupRegistration
{
    public const string Argument = "--startup";
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string Entry = "ThermalHud";

    public static bool IsEnabled()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey);
        return key?.GetValue(Entry) is string { Length: > 0 };
    }

    public static void SetEnabled(bool enabled)
    {
        if (!enabled)
        {
            if (IsEnabled())
            {
                using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: true);
                key?.DeleteValue(Entry, throwOnMissingValue: false);
            }

            return;
        }

        string path = Environment.ProcessPath ?? "";
        if (!Path.IsPathFullyQualified(path)
            || string.Equals(Path.GetFileName(path), "dotnet.exe", StringComparison.OrdinalIgnoreCase))
            throw new IOException("Startup requires the Thermal HUD executable.");

        string command = $"\"{path}\" {Argument}";
        if (command.Length > 260)
            throw new IOException("The Windows startup command exceeds the Run key limit.");

        using (var existing = Registry.CurrentUser.OpenSubKey(RunKey))
        {
            if (string.Equals(existing?.GetValue(Entry) as string, command, StringComparison.OrdinalIgnoreCase))
                return;
        }

        using var run = Registry.CurrentUser.CreateSubKey(RunKey, writable: true);
        run.SetValue(Entry, command, RegistryValueKind.String);
    }
}
