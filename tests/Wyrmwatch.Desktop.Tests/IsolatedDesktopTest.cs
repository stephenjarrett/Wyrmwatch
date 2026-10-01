using Wyrmwatch.Desktop;

namespace Wyrmwatch.Desktop.Tests;

// The assembly intentionally serializes UI tests because Avalonia and Program have
// process-wide state. Serialization alone does not clean up state between tests.
public abstract class IsolatedDesktopTest : IDisposable
{
    private readonly bool demo = Program.Demo, headless = Program.HeadlessTest;
    private readonly bool minimized = Program.Minimized, applyStartup = Program.ApplyStartup;
    private readonly string directory = Program.DataDirectory;
    private readonly string isolatedDirectory = Path.Combine(Path.GetTempPath(), "wyrmwatch-ui-state-" + Guid.NewGuid().ToString("N"));
    private bool disposed;

    protected IsolatedDesktopTest()
    {
        Program.Demo = false;
        Program.HeadlessTest = true;
        Program.Minimized = Program.ApplyStartup = false;
        Program.DataDirectory = isolatedDirectory;
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        Program.Demo = demo; Program.HeadlessTest = headless;
        Program.Minimized = minimized; Program.ApplyStartup = applyStartup;
        Program.DataDirectory = directory;
        if (Directory.Exists(isolatedDirectory)) Directory.Delete(isolatedDirectory, true);
    }
}
