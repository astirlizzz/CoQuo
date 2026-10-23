namespace CoQuo;

internal static class CodexLocator
{
    // Windows can install Codex as a native EXE or an npm .cmd shim.
    public static string? Find()
    {
        var dirs = (Environment.GetEnvironmentVariable("PATH") ?? "")
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .Select(p => p.Trim().Trim('"')).ToList();
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        if (!string.IsNullOrWhiteSpace(appData)) dirs.Add(Path.Combine(appData, "npm"));
        // An explicit path avoids guessing which installation the user wants.
        var configured = Environment.GetEnvironmentVariable("COQUO_CODEX_PATH");
        if (!string.IsNullOrWhiteSpace(configured) && File.Exists(configured)) return configured;
        foreach (var dir in dirs)
        {
            foreach (var filename in new[] { "codex.exe", "codex.cmd", "codex.bat" })
            {
                try
                {
                    var candidate = Path.Combine(dir, filename);
                    if (File.Exists(candidate)) return candidate;
                }
                catch (ArgumentException) { /* invalid PATH entry */ }
            }
        }
        return null;
    }
}
