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
        // System sleep can be a multicall binary whose behavior depends on its
        // filename. Use our own actor, with an explicit readiness handshake.
        foreach (var file in Directory.GetFiles(Path.Combine(AppContext.BaseDirectory, "fixture")))
            File.Copy(file, Path.Combine(root, Path.GetFileName(file)));
        var executable = Path.Combine(root, "RSDragonwildsServer");
        File.SetUnixFileMode(executable, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        using var actor = Process.Start(new ProcessStartInfo(executable)
        {
            UseShellExecute = false, RedirectStandardError = true, ArgumentList = { "-Port=0" }
        })!;
        var reached = false;
        try
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            while (!File.Exists(Path.Combine(root, "server.ready")))
            {
                if (actor.HasExited)
                    throw new IOException($"Disposable inspection fixture exited with code {actor.ExitCode} before readiness: {await actor.StandardError.ReadToEndAsync()}");
                await Task.Delay(10, deadline.Token);
            }
            actor.Refresh();
            Assert.False(actor.HasExited);
            Assert.StartsWith("RSDragonwilds", actor.ProcessName);
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
