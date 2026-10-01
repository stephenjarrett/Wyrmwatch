using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using Wyrmwatch.Core;
using Wyrmwatch.Platform;
using Wyrmwatch.Agent;

namespace Wyrmwatch.Core.Tests;

[Collection(NativeProcessCollection.Name)]
public class RuntimeSmokeTest
{
    private static IServerRuntime Runtime(string root) => OperatingSystem.IsWindows()
        ? new WindowsRuntime(new(root), Path.Combine(AppContext.BaseDirectory, "Wyrmwatch.Signal.exe")) : new LinuxRuntime(new(root));

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
    public async Task SavedServersSwitchOnlyAfterShutdownAndReuseTheSamePort()
    {
        await using var first = new DisposableServer(); await using var second = new DisposableServer(first.Profile.Port);
        ServerConnections.Validate(first.Profile, [second.Profile]);
        Assert.Equal(first.Profile.Port, second.Profile.Port);
        var store = new JsonStore(first.Root); var raw = Runtime(first.Root);
        var host = new ManagerHost(store, raw, new UnusedSteam());
        await host.SaveProfileAsync(first.Profile); await host.SaveProfileAsync(second.Profile);
        await host.ExecuteAsync(first.Profile.Id, new("start"));
        var a = await raw.InspectAsync(first.Profile); Assert.True(a.Running);
        var firstIdentity = Assert.Single(a.Processes);
        await Assert.ThrowsAsync<InvalidOperationException>(() => host.ExecuteAsync(second.Profile.Id, new("start")));
        Assert.Equal(firstIdentity.Id, Assert.Single((await raw.InspectAsync(first.Profile)).Processes).Id);
        Assert.False((await raw.InspectAsync(second.Profile)).Running);
        var untouched = File.ReadAllBytes(second.World);
        var engine = new BackupEngine(); var recovery = await engine.CreateAsync(first.Profile, "Test", true);
        File.WriteAllText(first.World, "new progress");
        raw = Runtime(first.Root); host = new ManagerHost(store, raw, new UnusedSteam());
        await Assert.ThrowsAsync<InvalidOperationException>(() => host.ExecuteAsync(second.Profile.Id, new("start")));
        await host.ExecuteAsync(first.Profile.Id, new("stop"));
        await host.ExecuteAsync(first.Profile.Id, new("restore", Path.GetFileName(recovery.Path), Confirmation: first.Profile.Name));
        Assert.Equal("precious world data", File.ReadAllText(first.World));
        Assert.Equal(untouched, File.ReadAllBytes(second.World)); Assert.Empty(engine.List(second.Profile));
        await host.ExecuteAsync(second.Profile.Id, new("start"));
        Assert.True((await raw.InspectAsync(second.Profile)).Running); Assert.False((await raw.InspectAsync(first.Profile)).Running);
        await host.ExecuteAsync(second.Profile.Id, new("stop"));
        Assert.Equal("graceful", File.ReadAllText(Path.Combine(second.Profile.InstallPath, "shutdown.requested")));
    }

    private sealed class DisposableServer : IAsyncDisposable
    {
        private readonly Fixture fixture = new();
        public string Root => fixture.Root;
        public ServerProfile Profile { get; }
        public string World => Path.Combine(Profile.SavedPath, "SaveGames", "world.sav");
        public DisposableServer(int sharedPort = 0)
        {
            using var reservation = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
            var port = sharedPort == 0 ? ((IPEndPoint)reservation.Client.LocalEndPoint!).Port : sharedPort;
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
