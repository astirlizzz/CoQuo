namespace CoQuo;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        using var singleton = new Mutex(true, @"Local\CoQuo.SingleInstance", out var firstInstance);
        if (!firstInstance) return;
        ApplicationConfiguration.Initialize();
        Application.Run(new TrayApplicationContext());
    }
}
