namespace CoQuo;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        using var singleton = new Mutex(true, @"Local\CoQuo.SingleInstance", out var firstInstance);
        if (!firstInstance) return;
        // Either this switch or "LogLevel" in settings.json (applied by the context) can make the log more verbose.
        if (args.Contains("--verbose", StringComparer.OrdinalIgnoreCase)) Log.Level = LogLevel.Verbose;
        ApplicationConfiguration.Initialize();
        Application.Run(new TrayApplicationContext());
    }
}
