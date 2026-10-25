using System.Text;
using System.Text.Json.Serialization;

namespace CoQuo;

[JsonConverter(typeof(JsonStringEnumConverter))]
internal enum LogLevel { Verbose, Info, Warning, Error }

/// <summary>
/// Small diagnostic log in %LOCALAPPDATA%\CoQuo. Only warnings and errors by default; routine events (every poll)
/// need Verbose (<c>--verbose</c> or "LogLevel" in settings.json). Never holds secrets and never exceeds
/// two files of <see cref="MaxBytes"/>: the full file is moved over coquo.log.1.
/// </summary>
internal static class Log
{
    private const long MaxBytes = 512 * 1024;
    private static readonly object Gate = new();
    private static readonly string Path = System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CoQuo", "coquo.log");

    public static LogLevel Level { get; set; } = LogLevel.Warning;

    public static bool Enabled(LogLevel level) => level >= Level;

    public static void Write(LogLevel level, string message)
    {
        if (!Enabled(level)) return;
        try
        {
            var line = $"{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss zzz} {level,-7} {message.ReplaceLineEndings(" ")}{Environment.NewLine}";
            lock (Gate)
            {
                Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
                var info = new FileInfo(Path);
                if (info.Exists && info.Length >= MaxBytes) File.Move(Path, Path + ".1", overwrite: true);
                File.AppendAllText(Path, line, Encoding.UTF8);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* logging must never break the app */ }
    }
}
