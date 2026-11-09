using System.IO;
using System.Text.Json;

namespace ThermalHud;

internal sealed record HudSettings
{
    private static readonly string SettingsPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "ThermalHud",
        "settings.json");

    public int? CpuLimit { get; init; } = 90;
    public int? GpuLimit { get; init; } = 85;
    public int? FanLimit { get; init; }
    public int SampleSeconds { get; init; } = 2;
    public bool PlaySound { get; init; }
    public bool KeepOnTop { get; init; } = true;

    public static HudSettings Load()
    {
        try
        {
            using var file = File.OpenRead(SettingsPath);
            var settings = JsonSerializer.Deserialize<HudSettings>(file);
            if (settings is not null
                && settings.CpuLimit is null or (>= 1 and <= 125)
                && settings.GpuLimit is null or (>= 1 and <= 125)
                && settings.FanLimit is null or (>= 1 and <= 30000)
                && settings.SampleSeconds is 2 or 5 or 10)
            {
                return settings;
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException)
        {
            // Missing, inaccessible, and malformed settings fall back to defaults.
        }

        return new HudSettings();
    }

    public void Save()
    {
        var directory = Path.GetDirectoryName(SettingsPath)!;
        Directory.CreateDirectory(directory);
        var temporaryPath = Path.Combine(directory, $"settings-{Guid.NewGuid():N}.tmp");
        try
        {
            using (var file = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write))
                JsonSerializer.Serialize(file, this);

            File.Move(temporaryPath, SettingsPath, overwrite: true);
        }
        finally
        {
            try { File.Delete(temporaryPath); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
        }
    }
}
