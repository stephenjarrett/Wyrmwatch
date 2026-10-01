using Wyrmwatch.Agent;
using Wyrmwatch.Core;

namespace Wyrmwatch.Core.Tests;

public class ServerCreationTests
{
    private static ServerCreationPlan Plan(Fixture f, string folder = "new-server", string owner = "owner-fixture", string? backupRoot = null) =>
        new("New server", "New world", owner, "admin-secret-fixture", "join-secret-fixture", 7777,
            f.Root, folder, backupRoot ?? Path.Combine(f.Root, "archives"));

    [Fact]
    public void PreviewRejectsInvalidInputsAndOverlapWithoutCreatingFolders()
    {
        using var f = new Fixture();
        var plan = Plan(f); plan.Validate([f.Profile]);
        Assert.False(Directory.Exists(plan.Profile.InstallPath));
        Assert.False(Directory.Exists(plan.Profile.BackupPath));
        Assert.Throws<ArgumentException>(() => Plan(f, owner: "").Validate([f.Profile]));
        Assert.Throws<ArgumentException>(() => Plan(f, backupRoot: f.Profile.SavedPath).Validate([f.Profile]));
        Assert.Throws<ArgumentException>(() => Plan(f, folder: "server").Validate([f.Profile]));
        foreach (var unsafeName in new[] { "..", "../server", "..\\server", "CON", "NUL.txt", "server.", "bad:name" })
            Assert.Throws<ArgumentException>(() => Plan(f, folder: unsafeName));
        Assert.Equal("server-con", ServerCreationPlan.SuggestFolderName("CON"));
        Assert.False(Directory.Exists(plan.Profile.InstallPath));
        Assert.False(Directory.Exists(plan.Profile.BackupPath));
    }

    [Fact]
    public async Task CreationInstallsAndConfiguresWithoutStartingOrChangingAnExistingWorld()
    {
        using var f = new Fixture();
        var original = Path.Combine(f.Profile.SavedPath, "SaveGames", "world.sav"); File.WriteAllText(original, "existing world");
        var store = new JsonStore(Path.Combine(f.Root, "workspace"));
        var steam = new Installer(); var runtime = new StoppedRuntime();
        var host = new ManagerHost(store, runtime, steam);
        await host.SaveProfileAsync(f.Profile);
        var plan = Plan(f); plan.Validate(host.Settings.Servers);
        var p = await host.SaveProfileAsync(plan.Profile);
        await host.ExecuteAsync(p.Id, new("install"));
        await host.ExecuteAsync(p.Id, new("configuration", Values: plan.Configuration.ToDictionary()));
        var configuration = GameConfiguration.Read(p.ConfigPath);
        foreach (var value in plan.Configuration) Assert.Equal(value.Value, configuration[value.Key]);
        Assert.Equal(1, steam.Calls); Assert.Equal(0, runtime.Starts); Assert.Equal(0, runtime.Stops);
        Assert.False(host.Profile(p.Id).AutoBackup); Assert.False(host.Profile(p.Id).AutoUpdate);
        Assert.Equal("existing world", File.ReadAllText(original));
        Assert.All(host.Status().Operations, operation => Assert.Equal("Succeeded", operation.Status));
        var log = File.ReadAllText(Path.Combine(store.DirectoryPath, "activity.log"));
        Assert.DoesNotContain("admin-secret-fixture", log); Assert.DoesNotContain("join-secret-fixture", log);
        Assert.False(Directory.Exists(Path.Combine(p.SavedPath, "SaveGames")));
    }

    [Fact]
    public async Task BackendRejectsFilesAddedAfterWizardReviewWithoutRunningInstaller()
    {
        using var f = new Fixture(); var steam = new Installer(); var runtime = new StoppedRuntime();
        var host = new ManagerHost(new(Path.Combine(f.Root, "workspace")), runtime, steam);
        var plan = Plan(f); plan.Validate([]);
        var p = await host.SaveProfileAsync(plan.Profile);
        Directory.CreateDirectory(p.InstallPath);
        var existing = Path.Combine(p.InstallPath, "existing.sav"); File.WriteAllText(existing, "keep world");
        await Assert.ThrowsAsync<IOException>(() => host.ExecuteAsync(p.Id, new("install")));
        Assert.Equal(0, steam.Calls); Assert.Equal(0, runtime.Starts);
        Assert.Equal("keep world", File.ReadAllText(existing)); Assert.False(File.Exists(p.ConfigPath));
    }

    private sealed class StoppedRuntime : IServerRuntime
    {
        public int Starts, Stops;
        public Task<ServerSnapshot> InspectAsync(ServerProfile p, CancellationToken token = default) => Task.FromResult(ServerSnapshot.Offline);
        public Task StartAsync(ServerProfile p, CancellationToken token = default) { Starts++; return Task.CompletedTask; }
        public Task StopAsync(ServerProfile p, CancellationToken token = default) { Stops++; return Task.CompletedTask; }
    }
    private sealed class Installer : ISteamClient
    {
        public int Calls;
        public string? InstalledBuild(ServerProfile p) => File.Exists(p.Launcher) ? "fixture-build" : null;
        public Task<BuildStatus> CheckAsync(ServerProfile p, Action<string> log, CancellationToken token = default) => throw new InvalidOperationException("Unexpected update check");
        public Task InstallAsync(ServerProfile p, bool repair, Action<string> log, CancellationToken token = default)
        {
            Calls++; Directory.CreateDirectory(p.InstallPath);
            File.WriteAllText(p.Launcher, "non-executable fixture"); return Task.CompletedTask;
        }
    }
}
