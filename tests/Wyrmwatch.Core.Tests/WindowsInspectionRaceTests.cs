using System.ComponentModel;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using Wyrmwatch.Core;
using Wyrmwatch.Platform;

namespace Wyrmwatch.Core.Tests;

public class WindowsInspectionRaceTests
{
    [Theory]
    [InlineData("exit", true)]
    [InlineData("exit-permission", true)]
    [InlineData("exit-io", true)]
    [InlineData("permission", false)]
    [InlineData("io", false)]
    [InlineData("invalid", false)]
    [InlineData("argument", false)]
    [InlineData("unknown", false)]
    public async Task ProcessExitAfterEnumerationIsDistinctFromUnreadableLiveProcess(string fault, bool accessible)
    {
        if (!OperatingSystem.IsWindows()) return;
        await using var fixture = await InspectionFixture.StartAsync();
        var reached = false;
        var runtime = new WindowsRuntime(new JsonStore(Path.Combine(fixture.Root, "workspace")), "unused")
        {
            BeforeProcessInspection = process =>
            {
                if (process.Id != fixture.Actor.Id) return;
                reached = true;
                if (fault.StartsWith("exit", StringComparison.Ordinal)) { fixture.Actor.Kill(); fixture.Actor.WaitForExit(); }
                if (fault.Contains("permission", StringComparison.Ordinal)) throw new Win32Exception(5, "Disposable permission-denial fixture.");
                if (fault.Contains("io", StringComparison.Ordinal)) throw new IOException("Disposable unreadable process fixture.");
                if (fault == "invalid") throw new InvalidOperationException("Disposable unavailable process state fixture.");
                if (fault == "argument") throw new ArgumentException("Disposable unavailable process state fixture.");
                if (fault == "unknown") { process.Dispose(); throw new Win32Exception(5, "Disposable unconfirmable process state fixture."); }
            }
        };
        var state = await runtime.InspectAsync(fixture.UnrelatedProfile);
        Assert.True(reached); Assert.False(state.Running); Assert.Equal(accessible, state.Accessible);
        Assert.Empty(state.Processes);
        if (!fault.StartsWith("exit", StringComparison.Ordinal)) Assert.False(fixture.Actor.HasExited);
        Assert.False(File.Exists(Path.Combine(fixture.Root, "workspace", "owned-processes.json")));
    }

    [Fact]
    public async Task LiveSameNameProcessAtAnotherPathIsNeitherAdoptedNorStopped()
    {
        if (!OperatingSystem.IsWindows()) return;
        await using var fixture = await InspectionFixture.StartAsync();
        var store = new JsonStore(Path.Combine(fixture.Root, "workspace"));
        var runtime = new WindowsRuntime(store, "unused");
        var state = await runtime.InspectAsync(fixture.UnrelatedProfile);
        Assert.True(state.Accessible); Assert.False(state.Running); Assert.Empty(state.Processes);
        await runtime.StopAsync(fixture.UnrelatedProfile);
        Assert.False(fixture.Actor.HasExited);
        var manualProfile = fixture.UnrelatedProfile with { InstallPath = fixture.Root };
        var manual = await runtime.InspectAsync(manualProfile);
        Assert.True(manual.Accessible); Assert.True(manual.Running);
        Assert.Equal(fixture.Actor.Id, Assert.Single(manual.Processes).Id);
        IServerRuntime externalRuntime = runtime;
        var refusal = await Assert.ThrowsAsync<IOException>(() => externalRuntime.StopAsync(manualProfile));
        Assert.Contains("started outside this manager", refusal.Message);
        Assert.False(fixture.Actor.HasExited);
        Assert.False(File.Exists(Path.Combine(store.DirectoryPath, "owned-processes.json")));
    }

    private sealed class InspectionFixture : IAsyncDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "wyrmwatch-windows-inspection-test-" + Guid.NewGuid().ToString("N"));
        public Process Actor { get; private set; } = null!;
        public ServerProfile UnrelatedProfile => new() { InstallPath = Path.Combine(Root, "never-started"), BackupPath = Path.Combine(Root, "backups") };
        public static async Task<InspectionFixture> StartAsync()
        {
            var fixture = new InspectionFixture();
            Directory.CreateDirectory(fixture.Root);
            try
            {
                foreach (var file in Directory.GetFiles(Path.Combine(AppContext.BaseDirectory, "fixture"))) File.Copy(file, Path.Combine(fixture.Root, Path.GetFileName(file)));
                using var reservation = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
                var port = ((IPEndPoint)reservation.Client.LocalEndPoint!).Port;
                reservation.Dispose();
                var start = new ProcessStartInfo(Path.Combine(fixture.Root, "RSDragonwildsServer.exe")) { UseShellExecute = false, CreateNoWindow = true };
                start.ArgumentList.Add("-Port=" + port);
                fixture.Actor = Process.Start(start)!;
                using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                while (!File.Exists(Path.Combine(fixture.Root, "server.ready")))
                {
                    Assert.False(fixture.Actor.HasExited);
                    await Task.Delay(10, deadline.Token);
                }
                Assert.True(SafePaths.Same(fixture.Actor.MainModule!.FileName!, start.FileName));
                return fixture;
            }
            catch { await fixture.DisposeAsync(); throw; }
        }
        public async ValueTask DisposeAsync()
        {
            // Only the actor launched from this unique exact fixture path is terminated.
            if (Actor is not null)
            {
                if (!Actor.HasExited) { Actor.Kill(); await Actor.WaitForExitAsync(); }
                Actor.Dispose();
            }
            var expected = Path.Combine(Path.GetTempPath(), Path.GetFileName(Root));
            if (!SafePaths.Same(Root, expected) || !Path.GetFileName(Root).StartsWith("wyrmwatch-windows-inspection-test-", StringComparison.Ordinal)) throw new IOException("Invalid fixture cleanup path.");
            for (var attempt = 0; Directory.Exists(Root); attempt++)
                try { Directory.Delete(Root, true); }
                catch (Exception error) when (attempt < 20 && error is IOException or UnauthorizedAccessException)
                { await Task.Delay(25); } // Windows can release image mappings just after exit is signalled.
        }
    }
}
