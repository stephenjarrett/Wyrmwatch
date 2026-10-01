using Wyrmwatch.Agent;
using Wyrmwatch.Core;

namespace Wyrmwatch.Core.Tests;

public class AgentRestoreRecoveryTests
{
    private sealed class Runtime : IServerRuntime
    {
        public int Starts { get; private set; }
        public Task<ServerSnapshot> InspectAsync(ServerProfile p, CancellationToken token = default) => Task.FromResult(ServerSnapshot.Offline);
        public Task StartAsync(ServerProfile p, CancellationToken token = default) { Starts++; return Task.CompletedTask; }
        public Task StopAsync(ServerProfile p, CancellationToken token = default) => Task.CompletedTask;
    }

    [Fact]
    public async Task AgentExposesRecoveryRequiresConfirmationAndUnblocksOnlyAfterVerification()
    {
        using var f = new Fixture(); var baseline = new BackupEngine();
        var archive = await baseline.CreateAsync(f.Profile, "Original", false);
        var world = Path.Combine(f.Profile.SavedPath, "SaveGames", "world.sav");
        File.WriteAllText(world, "current world");
        var interrupted = new BackupEngine { RestoreCheckpoint = step => { if (step == "Installed:SaveGames") throw new BackupEngine.RestoreInterruptedException(); } };
        await Assert.ThrowsAsync<BackupEngine.RestoreInterruptedException>(() => interrupted.RestoreAsync(f.Profile, archive.Path, () => Task.FromResult(true)));
        var store = new JsonStore(Path.Combine(f.Root, "workspace"));
        store.Write("settings.json", new ManagerSettings { Servers = [f.Profile] });
        var runtime = new Runtime(); var host = new ManagerHost(store, runtime, new UnusedSteam());
        Assert.True(Assert.Single(host.Status().Servers).RecoveryRequired);
        await Assert.ThrowsAsync<IOException>(() => host.ExecuteAsync(f.Profile.Id, new("start")));
        Assert.Equal(0, runtime.Starts);
        await Assert.ThrowsAsync<ArgumentException>(() => host.ExecuteAsync(f.Profile.Id, new("recover", Confirmation: "wrong server")));
        Assert.True(baseline.HasPendingRestore(f.Profile));
        await Assert.ThrowsAsync<IOException>(() => host.SaveProfileAsync(f.Profile with { DataPath = Path.Combine(f.Root, "alternate saves") }));
        var message = await host.ExecuteAsync(f.Profile.Id, new("recover", Confirmation: f.Profile.Name));
        Assert.Contains("rolled back", message);
        Assert.Equal("current world", File.ReadAllText(world));
        Assert.False(Assert.Single(new ManagerHost(store, runtime, new UnusedSteam()).Status().Servers).RecoveryRequired);
        await host.ExecuteAsync(f.Profile.Id, new("start"));
        Assert.Equal(1, runtime.Starts);
    }
}
