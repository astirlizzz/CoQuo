using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Win32;

namespace CoQuo;

[JsonConverter(typeof(JsonStringEnumConverter))]
internal enum TrayDisplay { FiveHour, Weekly, Hidden }

internal sealed class Settings
{
    public int RefreshSeconds { get; set; } = 60;
    public TrayDisplay CodexDisplay { get; set; } = TrayDisplay.Weekly;
    public TrayDisplay ClaudeDisplay { get; set; } = TrayDisplay.FiveHour;
    public int IconSize { get; set; } = 16;

    // Show/hide flags of earlier versions: read once to migrate, never written back.
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? ShowCodex { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? ShowClaude { get; set; }

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
                    if (result.ShowCodex == false) result.CodexDisplay = TrayDisplay.Hidden;
                    if (result.ShowClaude == false) result.ClaudeDisplay = TrayDisplay.Hidden;
                    result.ShowCodex = result.ShowClaude = null;
                    if (!Enum.IsDefined(result.CodexDisplay)) result.CodexDisplay = TrayDisplay.Weekly;
                    if (!Enum.IsDefined(result.ClaudeDisplay)) result.ClaudeDisplay = TrayDisplay.FiveHour;
                    // At least one icon must stay visible: it is the only way to reach the menu.
                    if (result.CodexDisplay == TrayDisplay.Hidden && result.ClaudeDisplay == TrayDisplay.Hidden)
                        result.CodexDisplay = TrayDisplay.Weekly;
                    if (result.IconSize is not (16 or 20 or 24 or 28 or 32)) result.IconSize = 16;
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
