using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using Wyrmwatch.Core;
using Wyrmwatch.Platform;

namespace Wyrmwatch.Core.Tests;

public class LauncherHandoffTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public async Task LauncherExitRetainsOwnershipAndGracefulShutdownAfterManagerRestart(bool shellExec, bool cancelBeforeFirstInspection)
    {
        if (!OperatingSystem.IsWindows() && !OperatingSystem.IsLinux()) return;
        if (OperatingSystem.IsWindows() && shellExec) return;
        var fixture = new Fixture();
        using var socket = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        var port = ((IPEndPoint)socket.Client.LocalEndPoint!).Port; socket.Close();
        var install = fixture.Profile.InstallPath;
        var shipping = OperatingSystem.IsWindows() ? Path.Combine(install, "RSDragonwilds", "Binaries", "Win64") : Path.Combine(install, "RSDragonwilds", "Binaries", "Linux");
        Directory.CreateDirectory(shipping);
        foreach (var file in Directory.GetFiles(Path.Combine(AppContext.BaseDirectory, "fixture")))
        {
            File.Copy(file, Path.Combine(install, Path.GetFileName(file)));
            File.Copy(file, Path.Combine(shipping, Path.GetFileName(file)));
        }
        var executable = Path.Combine(shipping, OperatingSystem.IsWindows() ? "RSDragonwildsServer-Win64-Shipping.exe" : "RSDragonwildsServer");
        var launcher = Path.Combine(install, OperatingSystem.IsWindows() ? "RSDragonwildsServer.exe" : "RSDragonwildsServer.sh");
        if (OperatingSystem.IsWindows())
        {
            File.Copy(Path.Combine(shipping, "RSDragonwildsServer.exe"), executable);
            File.WriteAllText(Path.Combine(install, "launcher-handoff"), "fixture");
        }
        else
        {
            File.SetUnixFileMode(executable, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            // Positional arguments keep temporary paths opaque to the shell.
            File.WriteAllText(launcher, $"#!/bin/bash\n{(shellExec ? "exec " : "") }\"$(dirname \"$0\")/RSDragonwilds/Binaries/Linux/RSDragonwildsServer\" \"$@\"{(shellExec ? "" : " &\nexit 0")}\n");
            File.SetUnixFileMode(launcher, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
        var profile = fixture.Profile with { LauncherPath = launcher, Port = port };
        File.WriteAllText(profile.ConfigPath, File.ReadAllText(profile.ConfigPath).Replace("Port=7777", "Port=" + port));
        IServerRuntime MakeRuntime()
        {
            IServerRuntime runtime = OperatingSystem.IsWindows() ? new WindowsRuntime(new(fixture.Root), Path.Combine(AppContext.BaseDirectory, "Wyrmwatch.Signal.exe")) : new LinuxRuntime(new(fixture.Root));
            return runtime;
        }
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(25));
        var completed = false;
        try
        {
            var first = MakeRuntime();
            if (cancelBeforeFirstInspection)
            {
                using var cancel = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token);
                var starting = first.StartAsync(profile, cancel.Token);
                while (!File.Exists(Path.Combine(shipping, "server.ready")))
                {
                    if (starting.IsFaulted) await starting;
                    await Task.Delay(10, timeout.Token);
                }
                cancel.Cancel();
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => starting);
                // No inspection has registered the shipping child. A replacement runtime
                // must recover ownership using the keeper/session, not a saved child PID.
                var registered = new JsonStore(fixture.Root).Read("owned-processes.json", () => new Dictionary<string, List<ProcessIdentity>>());
                Assert.DoesNotContain(registered[profile.Id], identity => SafePaths.Same(identity.Path, executable));
            }
            else
            {
                await first.StartAsync(profile, timeout.Token);
                var started = await first.InspectAsync(profile, timeout.Token);
                Assert.True(started.Running); Assert.Single(started.Processes);
                Assert.Equal(executable, started.Processes[0].Path);
            }
            // Reopen persisted ownership, without relying on the first runtime's in-memory registry.
            var restarted = MakeRuntime();
            Assert.True((await restarted.InspectAsync(profile, timeout.Token)).Running);
            await restarted.StopAsync(profile, timeout.Token);
            Assert.Equal("graceful", File.ReadAllText(Path.Combine(shipping, "shutdown.requested")));
            Assert.False((await restarted.InspectAsync(profile, timeout.Token)).Running);
            // An unrelated later process at the exact same installation path is never adopted.
            var externalStart = new ProcessStartInfo(executable) { UseShellExecute = false };
            externalStart.ArgumentList.Add("-Port=" + port);
            using var external = Process.Start(externalStart)!;
            await Task.Delay(500, timeout.Token);
            Assert.True((await restarted.InspectAsync(profile, timeout.Token)).Running);
            var rejected = await Assert.ThrowsAsync<IOException>(() => restarted.StopAsync(profile, timeout.Token));
            Assert.Contains("started", rejected.Message);
            Assert.False(external.HasExited);
            completed = true;
        }
        catch (Exception error) { Console.WriteLine(error); throw; }
        finally
        {
            // Failure cleanup is confined to exact disposable fixture executable identities.
            foreach (var process in Process.GetProcesses())
                using (process)
                    try
                    {
                        if (!process.ProcessName.StartsWith("RSDragonwilds", StringComparison.Ordinal)) continue;
                        if (process.MainModule?.FileName is { } path && SafePaths.Same(path, executable))
                        { process.Kill(); await process.WaitForExitAsync(); }
                    }
                    catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception or ArgumentException) { }
            // Give the query-only job keeper time to observe that its disposable job is empty.
            if (OperatingSystem.IsWindows()) await Task.Delay(250);
            try { fixture.Dispose(); }
            catch (Exception error) when (!completed && error is IOException or UnauthorizedAccessException) { Console.WriteLine("Disposable cleanup: " + error.Message); }
        }
    }
}
