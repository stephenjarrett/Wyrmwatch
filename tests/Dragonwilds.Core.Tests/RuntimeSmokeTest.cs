using System.Diagnostics;
using Dragonwilds.Core;
using Dragonwilds.Windows;

namespace Dragonwilds.Core.Tests;

public class RuntimeSmokeTest
{
    [Fact]
    public async Task OwnedDisposableConsoleReceivesGracefulShutdown()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var fixture = new Fixture();
        foreach (var file in Directory.GetFiles(Path.Combine(AppContext.BaseDirectory, "fixture"))) File.Copy(file, Path.Combine(fixture.Profile.InstallPath, Path.GetFileName(file)));
        var runtime = new WindowsRuntime(new(fixture.Root), Path.Combine(AppContext.BaseDirectory, "Dragonwilds.Signal.exe"));
        try
        {
            await runtime.StartAsync(fixture.Profile);
            Assert.True((await runtime.InspectAsync(fixture.Profile)).Running);
            await runtime.StopAsync(fixture.Profile);
            Assert.Equal("graceful", File.ReadAllText(Path.Combine(fixture.Profile.InstallPath, "shutdown.requested")));
            Assert.False((await runtime.InspectAsync(fixture.Profile)).Running);
        }
        finally
        {
            // Clean up only the executable copied into this test's unique temporary fixture.
            foreach (var process in Process.GetProcessesByName("RSDragonwildsServer"))
                using (process)
                    try { if (SafePaths.Same(process.MainModule!.FileName!, fixture.Profile.Launcher)) { process.Kill(); await process.WaitForExitAsync(); } }
                    catch (InvalidOperationException) { }
        }
    }
}
