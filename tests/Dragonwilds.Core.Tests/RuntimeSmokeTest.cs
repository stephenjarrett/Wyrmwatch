using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using Dragonwilds.Core;
using Dragonwilds.Windows;

namespace Dragonwilds.Core.Tests;

public class RuntimeSmokeTest
{
    private static IServerRuntime Runtime(string root) => OperatingSystem.IsWindows()
        ? new WindowsRuntime(new(root), Path.Combine(AppContext.BaseDirectory, "Dragonwilds.Signal.exe")) : new LinuxRuntime(new(root));

    [Fact]
    public async Task OwnedDisposableConsoleReceivesGracefulShutdown()
    {
        await using var fixture = new DisposableServer(); var runtime = Runtime(fixture.Root);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(25));
        await runtime.StartAsync(fixture.Profile, timeout.Token);
        Assert.True((await runtime.InspectAsync(fixture.Profile)).Running);
        await runtime.StopAsync(fixture.Profile, timeout.Token);
        Assert.Equal("graceful", File.ReadAllText(Path.Combine(fixture.Profile.InstallPath, "shutdown.requested")));
        Assert.False((await runtime.InspectAsync(fixture.Profile)).Running);
    }

    [Fact]
    public async Task TwoRunningServersKeepProcessesBackupsAndRestoresIsolatedAfterManagerRestart()
    {
        await using var first = new DisposableServer(); await using var second = new DisposableServer(first.Profile.Port);
        ServerConnections.Validate(first.Profile, [second.Profile]);
        var runtime = Runtime(first.Root);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(35));
        await runtime.StartAsync(first.Profile, timeout.Token); await runtime.StartAsync(second.Profile, timeout.Token);
        var a = await runtime.InspectAsync(first.Profile); var b = await runtime.InspectAsync(second.Profile);
        Assert.True(a.Running && b.Running);
        Assert.Empty(a.Processes.Select(p => p.Id).Intersect(b.Processes.Select(p => p.Id)));
        var secondIdentity = Assert.Single(b.Processes);
        var untouched = File.ReadAllBytes(second.World);
        var engine = new BackupEngine(); var recovery = await engine.CreateAsync(first.Profile, "Test", true);
        File.WriteAllText(first.World, "new progress");
        runtime = Runtime(first.Root);
        await runtime.StopAsync(first.Profile, timeout.Token);
        await engine.RestoreAsync(first.Profile, recovery.Path, async () => !(await runtime.InspectAsync(first.Profile)).Running);
        Assert.Equal("precious world data", File.ReadAllText(first.World));
        b = await runtime.InspectAsync(second.Profile);
        Assert.True(b.Running); Assert.Equal(secondIdentity.Id, Assert.Single(b.Processes).Id);
        Assert.Equal(untouched, File.ReadAllBytes(second.World)); Assert.Empty(engine.List(second.Profile));
        Assert.False(File.Exists(Path.Combine(second.Profile.InstallPath, "shutdown.requested")));
        await runtime.StopAsync(second.Profile, timeout.Token);
        Assert.Equal("graceful", File.ReadAllText(Path.Combine(second.Profile.InstallPath, "shutdown.requested")));
    }

    private sealed class DisposableServer : IAsyncDisposable
    {
        private readonly Fixture fixture = new();
        public string Root => fixture.Root;
        public ServerProfile Profile { get; }
        public string World => Path.Combine(Profile.SavedPath, "SaveGames", "world.sav");
        public DisposableServer(int excludedPort = 0)
        {
            int port;
            do { using var reservation = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0)); port = ((IPEndPoint)reservation.Client.LocalEndPoint!).Port; } while (port == excludedPort);
            Profile = fixture.Profile with { Port = port, LauncherPath = Path.Combine(fixture.Profile.InstallPath, OperatingSystem.IsWindows() ? "RSDragonwildsServer.exe" : "RSDragonwildsServer") };
            foreach (var file in Directory.GetFiles(Path.Combine(AppContext.BaseDirectory, "fixture"))) File.Copy(file, Path.Combine(Profile.InstallPath, Path.GetFileName(file)));
            if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(Profile.Launcher, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            File.WriteAllText(Profile.ConfigPath, File.ReadAllText(Profile.ConfigPath).Replace("Port=7777", "Port=" + port));
        }
        public async ValueTask DisposeAsync()
        {
            if (!SafePaths.Within(Profile.Launcher, Root) || !SafePaths.Within(Root, Path.GetTempPath())) throw new IOException("Invalid fixture cleanup path.");
            // Cleanup can touch only an exact executable path copied into this temporary fixture.
            foreach (var process in Process.GetProcesses())
                using (process)
                    try
                    {
                        if (!process.ProcessName.StartsWith("RSDragonwilds", StringComparison.Ordinal)) continue;
                        if (process.MainModule?.FileName is { } path && SafePaths.Same(path, Profile.Launcher))
                        { process.Kill(); await process.WaitForExitAsync(); }
                    }
                    catch (InvalidOperationException) { }
                    catch (System.ComponentModel.Win32Exception) { }
            fixture.Dispose();
        }
    }
}
