using Dragonwilds.Core;

namespace Dragonwilds.Core.Tests;

public class MaintenanceTests
{
    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now = DateTimeOffset.UtcNow;
        public override DateTimeOffset GetUtcNow() => Now;
    }
    private sealed class Runtime : IServerRuntime
    {
        public ServerSnapshot State = ServerSnapshot.Offline;
        public int Starts, Stops; public bool FailStop;
        public Task<ServerSnapshot> InspectAsync(ServerProfile p, CancellationToken t = default) => Task.FromResult(State);
        public Task StartAsync(ServerProfile p, CancellationToken t = default) { Starts++; State = State with { Running = true }; return Task.CompletedTask; }
        public Task StopAsync(ServerProfile p, CancellationToken t = default) { Stops++; if (FailStop) throw new IOException("Shutdown refused"); State = ServerSnapshot.Offline; return Task.CompletedTask; }
    }
    private sealed class Steam : ISteamClient
    {
        public string? Installed = "100", Available = "200"; public int Installs; public bool FailInstall;
        public string? InstalledBuild(ServerProfile p) => Installed;
        public Task<BuildStatus> CheckAsync(ServerProfile p, Action<string> log, CancellationToken t = default) => Task.FromResult(new BuildStatus(Installed, Available));
        public Task InstallAsync(ServerProfile p, bool repair, Action<string> log, CancellationToken t = default) { Installs++; if (FailInstall) throw new IOException("Network failure"); Installed = Available; return Task.CompletedTask; }
    }
    [Theory] [InlineData(null)] [InlineData(1)] [InlineData(5)]
    public async Task PlayersOrUnknownActivityDeferWithoutChangingServer(int? players)
    {
        using var f = new Fixture(); var runtime = new Runtime { State = ServerSnapshot.Offline with { Running = true, Players = players } }; var steam = new Steam();
        var service = new MaintenanceService(runtime, steam, new(), new(f.Root));
        Assert.StartsWith("Waiting", await service.UpdateAsync(f.Profile, true)); Assert.Equal(0, runtime.Stops); Assert.Equal(0, steam.Installs);
    }
    [Fact] public async Task UnknownInstalledBuildNeverTriggersInstall()
    {
        using var f = new Fixture(); var runtime = new Runtime(); var steam = new Steam { Installed = null };
        var service = new MaintenanceService(runtime, steam, new(), new(f.Root));
        await Assert.ThrowsAsync<IOException>(() => service.UpdateAsync(f.Profile, true)); Assert.Equal(0, steam.Installs);
    }
    [Fact] public async Task EmptyWindowRequiresContinuousObservationAndResetsAfterSleep()
    {
        using var f = new Fixture(); var clock = new Clock(); var runtime = new Runtime { State = ServerSnapshot.Offline with { Running = true } };
        var service = new MaintenanceService(runtime, new Steam(), new(), new(f.Root), clock);
        await service.ObserveAsync(f.Profile); Assert.NotNull(service.Deferral(f.Profile, runtime.State, clock.Now));
        for (var i = 0; i < 12; i++) { clock.Now = clock.Now.AddSeconds(5); await service.ObserveAsync(f.Profile); }
        Assert.Null(service.Deferral(f.Profile, runtime.State, clock.Now));
        clock.Now = clock.Now.AddMinutes(20); await service.ObserveAsync(f.Profile);
        Assert.NotNull(service.Deferral(f.Profile, runtime.State, clock.Now));
    }
    [Fact] public async Task OfflineUpdateBacksUpAndPreservesStoppedState()
    {
        using var f = new Fixture(); var runtime = new Runtime(); var steam = new Steam(); var engine = new BackupEngine();
        var service = new MaintenanceService(runtime, steam, engine, new(f.Root));
        Assert.Contains("200", await service.UpdateAsync(f.Profile)); Assert.Equal(1, steam.Installs); Assert.Equal(0, runtime.Starts); Assert.Single(engine.List(f.Profile));
    }
    [Fact] public async Task EmptyWindowUsesStableProcessIdentityButResetsForAReplacementProcess()
    {
        using var f = new Fixture(); var clock = new Clock(); var runtime = new Runtime { State = ServerSnapshot.Offline with { Running = true } };
        var service = new MaintenanceService(runtime, new Steam(), new(), new(f.Root), clock);
        for (var i = 0; i <= 12; i++)
        {
            runtime.State = runtime.State with { Processes = [new(42, DateTime.UtcNow.AddTicks(i), f.Profile.Launcher, "same-kernel-start")] };
            await service.ObserveAsync(f.Profile); clock.Now = clock.Now.AddSeconds(5);
        }
        Assert.Null(service.Deferral(f.Profile, runtime.State, clock.Now));
        runtime.State = runtime.State with { Processes = [new(42, DateTime.UtcNow, f.Profile.Launcher, "new-kernel-start")] };
        await service.ObserveAsync(f.Profile);
        Assert.NotNull(service.Deferral(f.Profile, runtime.State, clock.Now));
    }
    [Fact] public async Task OnlineUpdateTakesStoppedBackupAndRestartsAfterVerification()
    {
        using var f = new Fixture(); var clock = new Clock(); var runtime = new Runtime { State = ServerSnapshot.Offline with { Running = true } }; var steam = new Steam(); var engine = new BackupEngine();
        var service = new MaintenanceService(runtime, steam, engine, new(f.Root), clock);
        for (var i = 0; i <= 12; i++) { await service.ObserveAsync(f.Profile); clock.Now = clock.Now.AddSeconds(5); }
        await service.UpdateAsync(f.Profile, true);
        Assert.Equal(1, runtime.Stops); Assert.Equal(1, runtime.Starts); Assert.Equal(2, engine.List(f.Profile).Count);
    }
    [Fact] public async Task BackupFailurePreventsUpdate()
    {
        using var f = new Fixture(); var runtime = new Runtime(); var steam = new Steam(); var service = new MaintenanceService(runtime, steam, new(), new(f.Root));
        var empty = f.Profile with { InstallPath = Path.Combine(f.Root, "empty") };
        await Assert.ThrowsAsync<IOException>(() => service.UpdateAsync(empty)); Assert.Equal(0, steam.Installs); Assert.Equal(0, runtime.Stops);
    }
    [Fact] public async Task ShutdownFailurePreventsUpdate()
    {
        using var f = new Fixture(); var clock = new Clock(); var runtime = new Runtime { State = ServerSnapshot.Offline with { Running = true }, FailStop = true }; var steam = new Steam();
        var service = new MaintenanceService(runtime, steam, new(), new(f.Root), clock);
        for (var i = 0; i <= 12; i++) { await service.ObserveAsync(f.Profile); clock.Now = clock.Now.AddSeconds(5); }
        await Assert.ThrowsAsync<IOException>(() => service.UpdateAsync(f.Profile)); Assert.Equal(0, steam.Installs);
    }
    [Fact] public async Task InstallFailureLeavesServerStopped()
    {
        using var f = new Fixture(); var clock = new Clock(); var runtime = new Runtime { State = ServerSnapshot.Offline with { Running = true } }; var steam = new Steam { FailInstall = true };
        var service = new MaintenanceService(runtime, steam, new(), new(f.Root), clock);
        for (var i = 0; i <= 12; i++) { await service.ObserveAsync(f.Profile); clock.Now = clock.Now.AddSeconds(5); }
        await Assert.ThrowsAsync<IOException>(() => service.UpdateAsync(f.Profile)); Assert.False(runtime.State.Running); Assert.Equal(0, runtime.Starts);
    }
    [Fact] public async Task ConfigurationWriteRefusesOnlineServer()
    {
        using var f = new Fixture(); var runtime = new Runtime { State = ServerSnapshot.Offline with { Running = true } };
        var service = new MaintenanceService(runtime, new Steam(), new(), new(f.Root)); var original = File.ReadAllText(f.Profile.ConfigPath);
        await Assert.ThrowsAsync<IOException>(() => service.SaveConfigurationAsync(f.Profile, GameConfiguration.Read(f.Profile.ConfigPath))); Assert.Equal(original, File.ReadAllText(f.Profile.ConfigPath));
    }
    [Fact] public async Task SchedulerPersistsDeadlinesAndRunsMissedBackupOnce()
    {
        using var f = new Fixture(); var clock = new Clock(); var p = f.Profile with { AutoBackup = true, BackupHours = 1 }; var engine = new BackupEngine();
        var service = new MaintenanceService(new Runtime(), new Steam(), engine, new(f.Root), clock);
        await service.TickAsync([p]); var due = service.Schedule(p.Id).NextBackup;
        clock.Now = clock.Now.AddDays(2);
        var reopened = new MaintenanceService(new Runtime(), new Steam(), engine, new(f.Root), clock);
        Assert.Equal(due, reopened.Schedule(p.Id).NextBackup);
        await reopened.TickAsync([p]); await reopened.TickAsync([p]); Assert.Single(engine.List(p));
    }
}
