using System.ComponentModel;
using System.Diagnostics;
using Wyrmwatch.Core;
using Wyrmwatch.Platform;

namespace Wyrmwatch.Core.Tests;

[Collection(NativeProcessCollection.Name)]
public class LinuxInspectionRaceTests
{
    [Theory]
    [InlineData("exit", true)]
    [InlineData("permission", false)]
    [InlineData("io", false)]
    public async Task ProcessExitAfterEnumerationIsDistinctFromUnreadableLiveProcess(string fault, bool accessible)
    {
        if (!OperatingSystem.IsLinux()) return;
        var root = Path.Combine(Path.GetTempPath(), "wyrmwatch-inspection-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var executable = Path.Combine(root, "RSDragonwildsFixture");
        File.Copy("/bin/sleep", executable);
        File.SetUnixFileMode(executable, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        using var actor = Process.Start(new ProcessStartInfo(executable) { UseShellExecute = false, ArgumentList = { "20" } })!;
        var reached = false;
        try
        {
            var profile = new ServerProfile { InstallPath = Path.Combine(root, "never-started"), BackupPath = Path.Combine(root, "backups") };
            var runtime = new LinuxRuntime(new JsonStore(Path.Combine(root, "workspace")))
            {
                BeforeProcessInspection = process =>
                {
                    if (process.Id != actor.Id) return;
                    reached = true;
                    if (fault == "exit") { actor.Kill(); actor.WaitForExit(); }
                    else if (fault == "permission") throw new Win32Exception(13, "Disposable permission-denial fixture.");
                    else throw new IOException("Disposable unreadable live-process fixture.");
                }
            };
            var state = await runtime.InspectAsync(profile);
            Assert.True(reached); Assert.False(state.Running); Assert.Equal(accessible, state.Accessible);
            if (fault != "exit") Assert.False(actor.HasExited);
        }
        finally
        {
            // Only the exact process launched from this unique temporary fixture.
            if (!actor.HasExited) { actor.Kill(); await actor.WaitForExitAsync(); }
            Directory.Delete(root, true);
        }
    }
}
