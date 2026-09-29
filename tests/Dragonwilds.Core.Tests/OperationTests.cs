using Dragonwilds.Core;
using Wyrmwatch.Agent;

namespace Dragonwilds.Core.Tests;

public class OperationTests
{
    private sealed class PausedSteam : UnusedSteam
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override async Task<BuildStatus> CheckAsync(ServerProfile profile, Action<string> log, CancellationToken token = default)
        { Entered.SetResult(); await Release.Task.WaitAsync(TimeSpan.FromSeconds(10), token); return new("100", "100"); }
    }

    [Fact]
    public async Task ActiveOperationBlocksManualWorkSchedulingAndShutdownThenReleasesGate()
    {
        using var f = new Fixture(); var store = new JsonStore(f.Root); var steam = new PausedSteam();
        var p = f.Profile with { AutoBackup = true }; store.Write("settings.json", new ManagerSettings { Servers = [p] });
        store.Write("schedules.json", new Dictionary<string, ScheduleState> { [p.Id] = new(NextBackup: DateTimeOffset.UtcNow.AddMinutes(-1)) });
        var host = new ManagerHost(store, new OfflineRuntime(), steam);
        var checking = host.ExecuteAsync(p.Id, new("check"));
        await steam.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        using var cancel = new CancellationTokenSource();
        var monitor = host.MonitorAsync(() => throw new Exception("Unexpected shutdown"), cancel.Token);
        try
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() => host.ExecuteAsync(p.Id, new("backup")));
            await Assert.ThrowsAsync<InvalidOperationException>(() => host.SaveProfileAsync(p with { Name = "busy edit" }));
            Assert.False(host.TryPrepareShutdown()); Assert.Empty(new BackupEngine().List(p));
        }
        finally { cancel.Cancel(); steam.Release.TrySetResult(); await monitor; await checking; }
        await host.ExecuteAsync(p.Id, new("backup")); Assert.Single(new BackupEngine().List(p));
        Assert.True(host.TryPrepareShutdown());
        await Assert.ThrowsAsync<InvalidOperationException>(() => host.ExecuteAsync(p.Id, new("backup")));
    }

    [Fact]
    public async Task LegacyConflictingProfilesCannotRunScheduledBackups()
    {
        using var a = new Fixture(); using var b = new Fixture(); var store = new JsonStore(a.Root);
        var profiles = new[] { a.Profile with { AutoBackup = true }, b.Profile with { AutoBackup = true, DataPath = a.Profile.SavedPath } };
        store.Write("settings.json", new ManagerSettings { Servers = profiles.ToList() });
        store.Write("schedules.json", profiles.ToDictionary(p => p.Id, _ => new ScheduleState(NextBackup: DateTimeOffset.UtcNow.AddMinutes(-1))));
        var host = new ManagerHost(store, new OfflineRuntime(), new UnusedSteam()); using var cancel = new CancellationTokenSource();
        var monitor = host.MonitorAsync(() => { }, cancel.Token);
        cancel.Cancel(); await monitor;
        Assert.All(host.Status().Servers, s => { Assert.False(s.State.Accessible); Assert.Contains("save-data", s.State.ActivityReason); });
        Assert.All(profiles, p => Assert.Empty(new BackupEngine().List(p)));
    }
}
