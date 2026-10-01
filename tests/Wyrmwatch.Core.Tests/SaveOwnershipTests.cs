using Wyrmwatch.Agent;
using Wyrmwatch.Core;

namespace Wyrmwatch.Core.Tests;

public class SaveOwnershipTests
{
    private sealed class Runtime : IServerRuntime
    {
        public string? RunningId { get; set; }
        public Task<ServerSnapshot> InspectAsync(ServerProfile p, CancellationToken token = default) => Task.FromResult(ServerSnapshot.Offline with { Running = p.Id == RunningId });
        public Task StartAsync(ServerProfile p, CancellationToken token = default) => throw new InvalidOperationException("Fixture must never start a game.");
        public Task StopAsync(ServerProfile p, CancellationToken token = default) => throw new InvalidOperationException("Fixture must never stop a game.");
    }

    [Theory]
    [InlineData("same")]
    [InlineData("alias")]
    [InlineData("nested")]
    [InlineData("backup")]
    public async Task DisconnectedRunningConnectionStillReservesItsData(string kind)
    {
        using var a = new Fixture(); using var b = new Fixture();
        var runtime = new Runtime { RunningId = a.Profile.Id };
        var host = new ManagerHost(new(a.Root), runtime, new UnusedSteam());
        await host.SaveProfileAsync(a.Profile);
        await host.RemoveProfileAsync(a.Profile.Id);
        var original = File.ReadAllBytes(a.Profile.ConfigPath);
        var second = kind switch
        {
            "alias" => b.Profile with { DataPath = Path.Combine(a.Profile.SavedPath, ".") },
            "nested" => b.Profile with { DataPath = Path.Combine(a.Profile.SavedPath, "nested") },
            "backup" => b.Profile with { BackupPath = Path.Combine(a.Profile.SavedPath, "backups") },
            _ => b.Profile with { DataPath = a.Profile.SavedPath }
        };
        await Assert.ThrowsAsync<ArgumentException>(() => host.SaveProfileAsync(second));
        Assert.Empty(host.Settings.Servers);
        Assert.Equal(a.Profile.Id, Assert.Single(host.Settings.DisconnectedServers).Id);
        Assert.Equal(original, File.ReadAllBytes(a.Profile.ConfigPath));
    }

    [Fact]
    public async Task ImportedDifferentInstallCannotWriteDisconnectedSaves()
    {
        using var a = new Fixture(); using var b = new Fixture();
        File.WriteAllText(b.Profile.Launcher, "fixture only");
        var host = new ManagerHost(new(a.Root), new Runtime { RunningId = a.Profile.Id }, new UnusedSteam());
        await host.SaveProfileAsync(a.Profile); await host.RemoveProfileAsync(a.Profile.Id);
        var original = File.ReadAllBytes(a.Profile.ConfigPath);
        await Assert.ThrowsAsync<ArgumentException>(() => host.ImportProfileAsync(b.Profile with { DataPath = a.Profile.SavedPath }));
        Assert.Equal(original, File.ReadAllBytes(a.Profile.ConfigPath));
    }

    [Theory]
    [InlineData("configuration")]
    [InlineData("start")]
    [InlineData("backup")]
    [InlineData("install")]
    public async Task ExistingConflictingSettingsCannotBypassExecutionGuard(string action)
    {
        using var a = new Fixture(); using var b = new Fixture();
        var store = new JsonStore(a.Root);
        var second = b.Profile with { DataPath = a.Profile.SavedPath };
        store.Write("settings.json", new ManagerSettings { Servers = [second], DisconnectedServers = [a.Profile] });
        var host = new ManagerHost(store, new Runtime { RunningId = a.Profile.Id }, new UnusedSteam());
        var original = File.ReadAllBytes(a.Profile.ConfigPath);
        await Assert.ThrowsAsync<ArgumentException>(() => host.ExecuteAsync(second.Id, new(action, Values: GameConfiguration.Read(a.Profile.ConfigPath))));
        Assert.Equal(original, File.ReadAllBytes(a.Profile.ConfigPath));
        Assert.Empty(new BackupEngine().List(second));
    }

    [Fact]
    public async Task ReconnectingRunningServerIsReadOnlyButCannotChangeSaveOwnership()
    {
        using var a = new Fixture(); using var b = new Fixture();
        var host = new ManagerHost(new(a.Root), new Runtime { RunningId = a.Profile.Id }, new UnusedSteam());
        await host.SaveProfileAsync(a.Profile); await host.RemoveProfileAsync(a.Profile.Id);
        await Assert.ThrowsAsync<IOException>(() => host.SaveProfileAsync(a.Profile with { DataPath = b.Profile.SavedPath }));
        await Assert.ThrowsAsync<IOException>(() => host.SaveProfileAsync(b.Profile with { Id = a.Profile.Id }));
        var reconnected = await host.SaveProfileAsync(a.Profile with { Id = "new-request-id" });
        Assert.Equal(a.Profile.Id, reconnected.Id);
        Assert.Empty(host.Settings.DisconnectedServers);
    }

    [Fact]
    public void IndependentWorkspacesCannotMutateTheSameSaveTreeConcurrently()
    {
        using var a = new Fixture(); using var b = new Fixture();
        using (ServerOperationLease.Acquire(a.Profile))
            Assert.Throws<IOException>(() => ServerOperationLease.Acquire(b.Profile with { DataPath = a.Profile.SavedPath }));
        using var released = ServerOperationLease.Acquire(b.Profile with { DataPath = a.Profile.SavedPath });
    }

    [Fact]
    public async Task AutomationSkipsConnectedProfilesConflictingWithDisconnectedData()
    {
        using var a = new Fixture(); using var b = new Fixture();
        var second = b.Profile with { DataPath = a.Profile.SavedPath, AutoBackup = true };
        var store = new JsonStore(a.Root);
        store.Write("settings.json", new ManagerSettings { Servers = [second], DisconnectedServers = [a.Profile] });
        store.Write("schedules.json", new Dictionary<string, ScheduleState> { [second.Id] = new(NextBackup: DateTimeOffset.UtcNow.AddHours(-1)) });
        var host = new ManagerHost(store, new Runtime { RunningId = a.Profile.Id }, new UnusedSteam());
        using var token = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));
        await host.MonitorAsync(() => { }, token.Token);
        Assert.Empty(new BackupEngine().List(second));
        Assert.False(Assert.Single(host.Status().Servers).State.Accessible);
    }
}
