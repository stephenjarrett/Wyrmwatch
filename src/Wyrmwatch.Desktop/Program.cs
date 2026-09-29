using Avalonia;

namespace Wyrmwatch.Desktop;

internal static class Program
{
    public static string DataDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Wyrmwatch");
    public static bool Demo;
    public static bool Minimized;
    public static bool HeadlessTest;
    public static bool ApplyStartup;
    private static Mutex? singleInstance;
    [STAThread]
    public static void Main(string[] args)
    {
        var parentIndex = Array.IndexOf(args, "--wait-for-parent");
        if (parentIndex >= 0 && parentIndex + 2 < args.Length && int.TryParse(args[parentIndex + 1], out var parentId) && long.TryParse(args[parentIndex + 2], out var parentStart))
        {
            try { using var parent = System.Diagnostics.Process.GetProcessById(parentId); if (parent.StartTime.ToUniversalTime().Ticks == parentStart && !parent.WaitForExit(30000)) return; }
            catch (Exception error) when (error is ArgumentException or InvalidOperationException) { }
        }
        ApplyStartup = args.Contains("--apply-startup");
        Demo = args.Contains("--demo"); Minimized = args.Contains("--minimized");
        var index = Array.IndexOf(args, "--data-dir");
        if (index >= 0 && index + 1 < args.Length) DataDirectory = Path.GetFullPath(args[index + 1]);
        if (Demo && index < 0) DataDirectory = Path.Combine(Path.GetTempPath(), "Wyrmwatch-demo");
        var hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(DataDirectory.ToUpperInvariant())))[..20];
        singleInstance = new Mutex(true, "Wyrmwatch-" + hash, out var created);
        if (!created) return;
        try { BuildAvaloniaApp().StartWithClassicDesktopLifetime(args); }
        finally { singleInstance.ReleaseMutex(); singleInstance.Dispose(); }
    }
    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<App>().UsePlatformDetect().WithInterFont().LogToTrace();
}
