using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text.Json;
using Wyrmwatch.Core;
using Wyrmwatch.Platform;

namespace Wyrmwatch.Core.Tests;

[Collection(NativeProcessCollection.Name)]
public class DetachedKeeperLifecycleTests
{
    [Fact]
    public async Task RunningGameAllowsApplicationReplacementAndRollback()
    {
        if (!OperatingSystem.IsWindows()) return;
        await using var f = new Fixture();
        await f.Runtime.StartAsync(f.Profile);
        var before = Assert.Single((await f.Runtime.InspectAsync(f.Profile)).Processes);
        InstallationProcesses.RefuseLegacyOwnershipHelper(f.App);
        var app = f.App;
        var manifest = Directory.GetFiles(f.App, "*", SearchOption.AllDirectories)
            .Where(path => Path.GetRelativePath(app, path) != AppInstallationTransaction.ManifestName).ToDictionary(
            path => Path.GetRelativePath(app, path).Replace('\\', '/'), path => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))));
        File.WriteAllBytes(Path.Combine(f.App, AppInstallationTransaction.ManifestName), JsonSerializer.SerializeToUtf8Bytes(manifest));
        foreach (var path in Directory.GetFiles(f.App, "*", SearchOption.AllDirectories))
        {
            var destination = Path.Combine(f.Incoming, Path.GetRelativePath(f.App, path));
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!); File.Copy(path, destination);
        }
        File.WriteAllText(Path.Combine(f.Incoming, "README.md"), "new application bytes");
        manifest["README.md"] = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(Path.Combine(f.Incoming, "README.md"))));
        File.WriteAllBytes(Path.Combine(f.Incoming, AppInstallationTransaction.ManifestName), JsonSerializer.SerializeToUtf8Bytes(manifest));
        AppInstallationTransaction.Prepare(f.App, f.Incoming, f.Journal, "0.2.8");
        // Delete and recreate every app-owned file: a helper using this runtime
        // would lock these files and fail this operation while the game runs.
        foreach (var path in Directory.GetFiles(f.App, "*", SearchOption.AllDirectories)) File.Delete(path);
        foreach (var path in Directory.GetFiles(f.Incoming, "*", SearchOption.AllDirectories)) File.Copy(path, Path.Combine(f.App, Path.GetRelativePath(f.Incoming, path)));
        AppInstallationTransaction.VerifyInstalled(f.Journal);
        AppInstallationTransaction.Restore(f.Journal);
        Assert.Equal("old application bytes", File.ReadAllText(Path.Combine(f.App, "README.md")));
        var after = Assert.Single((await f.Runtime.InspectAsync(f.Profile)).Processes);
        Assert.Equal(before.Id, after.Id); Assert.Equal(before.Path, after.Path); Assert.Equal(before.StartUtc, after.StartUtc);
        Assert.Equal("world remains unchanged", File.ReadAllText(f.World));
        Assert.False(File.Exists(Path.Combine(f.Profile.InstallPath, "shutdown.requested")));
    }

    [Fact]
    public async Task RunningLegacyHelperRefusesMigrationWithoutClosingAnyProcess()
    {
        if (!OperatingSystem.IsWindows()) return;
        await using var f = new Fixture();
        await f.Runtime.StartAsync(f.Profile);
        var before = Assert.Single((await f.Runtime.InspectAsync(f.Profile)).Processes);
        var jobs = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllBytes(Path.Combine(f.Root, "owned-jobs.json")))!;
        var ready = Path.Combine(f.Root, "legacy-helper-ready");
        var start = new ProcessStartInfo(Path.Combine(f.App, "Wyrmwatch.Signal.exe")) { UseShellExecute = false, CreateNoWindow = true };
        foreach (var value in new[] { "--keep-job", jobs[f.Profile.Id], ready }) start.ArgumentList.Add(value);
        using var legacy = Process.Start(start)!;
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (!File.Exists(ready)) { Assert.False(legacy.HasExited); await Task.Delay(20, deadline.Token); }
        var app = f.App;
        var error = Assert.Throws<IOException>(() => InstallationProcesses.RefuseLegacyOwnershipHelper(app));
        Assert.Equal("LegacyOwnershipHelper", error.Data["InstallerCondition"]);
        Assert.False(legacy.HasExited);
        Assert.Equal(before, Assert.Single((await f.Runtime.InspectAsync(f.Profile)).Processes));
        Assert.False(File.Exists(Path.Combine(f.Profile.InstallPath, "shutdown.requested")));
        await f.Runtime.StopAsync(f.Profile); // Explicit cleanup of this synthetic server only.
        Assert.True(legacy.WaitForExit(5000));
        InstallationProcesses.RefuseLegacyOwnershipHelper(f.App);
    }

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private sealed class Fixture : IAsyncDisposable
    {
        public string Root { get; }
        public string App => Path.Combine(Root, "app");
        public string Incoming => Path.Combine(Root, "incoming");
        public string Journal { get; } = Path.Combine(Path.GetTempPath(), "wyrmwatch-install-" + Guid.NewGuid().ToString("N"));
        public ServerProfile Profile { get; }
        public WindowsRuntime Runtime { get; }
        public string World => Path.Combine(Profile.SavedPath, "SaveGames", "world.sav");
        public Fixture()
        {
            var root = Path.Combine(Path.GetTempPath(), "wyrmwatch-keeper-lifecycle-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root); Root = WindowsPhysicalPath.ExistingDirectory(root);
            Directory.CreateDirectory(App); Directory.CreateDirectory(Incoming);
            // Optional local packaging check: read the supplied package, then
            // mutate only a new disposable copy. Never execute its desktop.
            if (Environment.GetEnvironmentVariable("WYRM_WATCH_TEST_PUBLISHED_APP") is { Length: > 0 } published)
            {
                published = Path.GetFullPath(published); SafePaths.NoLinks(published);
                foreach (var path in Directory.GetFiles(published, "*", SearchOption.AllDirectories))
                {
                    SafePaths.NoLinks(path);
                    var destination = Path.Combine(App, Path.GetRelativePath(published, path));
                    Directory.CreateDirectory(Path.GetDirectoryName(destination)!); File.Copy(path, destination);
                }
            }
            else
            {
                Directory.CreateDirectory(Path.Combine(App, "agent"));
                foreach (var path in Directory.GetFiles(AppContext.BaseDirectory, "Wyrmwatch.Signal*")) File.Copy(path, Path.Combine(App, Path.GetFileName(path)));
                foreach (var name in new[] { "Wyrmwatch.Core.dll", "Wyrmwatch.Platform.dll" }) File.Copy(Path.Combine(AppContext.BaseDirectory, name), Path.Combine(App, name));
                foreach (var name in new[] { "Wyrmwatch.exe", "Wyrmwatch.dll", "Wyrmwatch.runtimeconfig.json", "agent/Wyrmwatch.Agent.exe" }) File.WriteAllText(Path.Combine(App, name), "unexecuted app manifest fixture");
            }
            File.WriteAllText(Path.Combine(App, "README.md"), "old application bytes");
            var game = Path.Combine(Root, "game"); Directory.CreateDirectory(game);
            foreach (var path in Directory.GetFiles(Path.Combine(AppContext.BaseDirectory, "fixture"))) File.Copy(path, Path.Combine(game, Path.GetFileName(path)));
            using var reservation = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
            Profile = new ServerProfile { Id = "detached-lifecycle", InstallPath = game, BackupPath = Path.Combine(Root, "backups"), Port = ((IPEndPoint)reservation.Client.LocalEndPoint!).Port };
            Directory.CreateDirectory(Path.GetDirectoryName(Profile.ConfigPath)!); Directory.CreateDirectory(Path.GetDirectoryName(World)!);
            File.WriteAllText(World, "world remains unchanged");
            File.WriteAllText(Profile.ConfigPath, "[/Script/Dominion.DedicatedServerSettings]\nOwnerId=dummy\nServerName=Dummy\nDefaultWorldName=Dummy\nAdminPassword=synthetic-only\nPort=" + Profile.Port + "\n");
            Runtime = new WindowsRuntime(new(Root), Path.Combine(App, "Wyrmwatch.Signal.exe"));
        }
        public async ValueTask DisposeAsync()
        {
            // Use the intact test runner's helper even if a replacement assertion
            // left the disposable app incomplete. Ownership is still reverified.
            var cleanup = new WindowsRuntime(new(Root), Path.Combine(AppContext.BaseDirectory, "Wyrmwatch.Signal.exe"));
            if ((await cleanup.InspectAsync(Profile)).Running) await cleanup.StopAsync(Profile);
            foreach (var keeper in Process.GetProcessesByName("keep"))
                using (keeper)
                    try { if (keeper.MainModule?.FileName is { } path && SafePaths.Within(path, Root)) Assert.True(keeper.WaitForExit(5000)); }
                    catch (InvalidOperationException) { }
            if (Directory.Exists(Journal)) Directory.Delete(Journal, true);
            Directory.Delete(Root, true);
        }
    }
}
