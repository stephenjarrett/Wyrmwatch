using Avalonia;
using Avalonia.Media;

namespace Wyrmwatch.Desktop;

internal static class Program
{
    public static string DataDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Wyrmwatch");
    public static bool Demo;
    public static bool Minimized;
    public static bool HeadlessTest;
    public static bool ApplyStartup;
    public static string? PreviousExecutable;
    internal static string? UpdateOutcomeMessage;
    private static Mutex? singleInstance;
    [STAThread]
    public static void Main(string[] args)
    {
        if (InstallerLifecycle.TryRun(args, out var installerResult)) { Environment.ExitCode = installerResult; return; }
        var parentIndex = Array.IndexOf(args, "--wait-for-parent");
        if (parentIndex >= 0 && parentIndex + 2 < args.Length && int.TryParse(args[parentIndex + 1], out var parentId))
        {
            try { using var parent = System.Diagnostics.Process.GetProcessById(parentId); if (Wyrmwatch.Core.ProcessLifetime.Token(parent) == args[parentIndex + 2] && !parent.WaitForExit(30000)) return; }
            catch (Exception error) when (error is ArgumentException or InvalidOperationException) { }
        }
        ApplyStartup = args.Contains("--apply-startup");
        var previousIndex = Array.IndexOf(args, "--previous-executable");
        PreviousExecutable = previousIndex >= 0 && previousIndex + 1 < args.Length ? Path.GetFullPath(args[previousIndex + 1]) : null;
        Demo = args.Contains("--demo"); Minimized = args.Contains("--minimized");
        var index = Array.IndexOf(args, "--data-dir");
        if (index >= 0 && index + 1 < args.Length) DataDirectory = Path.GetFullPath(args[index + 1]);
        if (Demo && index < 0) DataDirectory = Path.Combine(Path.GetTempPath(), "Wyrmwatch-demo");
        if (OperatingSystem.IsWindows())
        {
            var outcomeIndex = Array.IndexOf(args, "--installation-result");
            if (outcomeIndex >= 0 && outcomeIndex + 1 < args.Length) UpdateOutcomeMessage = InstallerLifecycle.Outcome(args[outcomeIndex + 1], AppContext.BaseDirectory);
            _ = Task.Run(() => InstallerLifecycle.CleanupCompleted(AppContext.BaseDirectory));
        }
        var hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(DataDirectory.ToUpperInvariant())))[..20];
        singleInstance = new Mutex(true, "Wyrmwatch-" + hash, out var created);
        if (!created) return;
        try { BuildAvaloniaApp().StartWithClassicDesktopLifetime(args); }
        finally { singleInstance.ReleaseMutex(); singleInstance.Dispose(); }
    }
    internal static AppBuilder ConfigureFonts(AppBuilder builder) => builder.WithInterFont()
        .With(new FontManagerOptions { DefaultFamilyName = "fonts:Inter#Inter" });
    public static AppBuilder BuildAvaloniaApp() => ConfigureFonts(AppBuilder.Configure<App>().UsePlatformDetect()).LogToTrace();
}
