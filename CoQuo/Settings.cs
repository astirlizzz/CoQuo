using System.Text.Json;
using Microsoft.Win32;

namespace CoQuo;

internal sealed class Settings
{
    public int RefreshSeconds { get; set; } = 60;
    public bool ShowCodex { get; set; } = true;
    public bool ShowClaude { get; set; } = true;

    private static string SettingsPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "CoQuo", "settings.json");

    public static Settings Load()
    {
        try
        {
            if (File.Exists(SettingsPath))
            {
                var result = JsonSerializer.Deserialize<Settings>(File.ReadAllText(SettingsPath));
                if (result is not null && result.RefreshSeconds is 60 or 120 or 300 or 600)
                {
                    // At least one icon must stay visible: it is the only way to reach the menu.
                    if (!result.ShowCodex && !result.ShowClaude) result.ShowCodex = true;
                    return result;
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { }
        return new Settings();
    }

    public void Save()
    {
        var dir = Path.GetDirectoryName(SettingsPath)!;
        Directory.CreateDirectory(dir);
        var tmp = SettingsPath + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        File.Move(tmp, SettingsPath, overwrite: true);
    }

    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string Name = "CoQuo";

    public static bool IsAutoStartEnabled()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey);
        var value = key?.GetValue(Name) as string;
        var currentPath = Environment.ProcessPath;
        return currentPath is not null && string.Equals(value, $"\"{currentPath}\"", StringComparison.OrdinalIgnoreCase);
    }

    public static void SetAutoStart(bool enabled)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKey);
        if (enabled)
        {
            var path = Environment.ProcessPath ?? throw new InvalidOperationException("Cannot locate executable");
            key.SetValue(Name, $"\"{path}\"", RegistryValueKind.String);
        }
        else key.DeleteValue(Name, throwOnMissingValue: false);
    }
}
