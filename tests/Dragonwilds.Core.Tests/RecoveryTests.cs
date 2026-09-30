using Dragonwilds.Core;

namespace Dragonwilds.Core.Tests;

public class RecoveryTests
{
    [Fact]
    public async Task CancelledBackupRemovesPartialAndPreservesExistingArchives()
    {
        using var f = new Fixture(); var engine = new BackupEngine();
        var prior = await engine.CreateAsync(f.Profile, "Keep", false);
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => engine.CreateAsync(f.Profile, "Cancelled", false, cancelled.Token));
        Assert.Equal(prior.Path, Assert.Single(engine.List(f.Profile)).Path);
        await engine.VerifyAsync(prior.Path, f.Profile);
        Assert.Empty(Directory.GetFiles(f.Profile.BackupPath, "*.partial"));
    }

    [Fact]
    public async Task FailedBackupDoesNotPrunePreviousRecoveryPoints()
    {
        using var f = new Fixture(); var p = f.Profile with { RetainBackups = 1 }; var engine = new BackupEngine();
        await engine.CreateAsync(p, "First", false); await engine.CreateAsync(p, "Second", false);
        using (var locked = new FileStream(Path.Combine(p.SavedPath, "SaveGames", "world.sav"), FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            var service = new MaintenanceService(new OfflineRuntime(), new UnusedSteam(), engine, new(f.Root));
            await Assert.ThrowsAsync<IOException>(() => service.BackupAsync(p));
        }
        Assert.Equal(2, engine.List(p).Count); Assert.Empty(Directory.GetFiles(p.BackupPath, "*.partial"));
        foreach (var archive in engine.List(p)) await engine.VerifyAsync(archive.Path, p);
        Assert.Equal("precious world data", File.ReadAllText(Path.Combine(p.SavedPath, "SaveGames", "world.sav")));
    }

    [Fact]
    public async Task CancelledRestoreAfterPreparationLeavesActiveWorldUnchanged()
    {
        using var f = new Fixture(); var engine = new BackupEngine(); var archive = await engine.CreateAsync(f.Profile, "Older", false);
        var world = Path.Combine(f.Profile.SavedPath, "SaveGames", "world.sav"); File.WriteAllText(world, "latest progress");
        using var cancel = new CancellationTokenSource(); var checks = 0;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => engine.RestoreAsync(f.Profile, archive.Path, () =>
        {
            if (++checks == 2) cancel.Cancel(); return Task.FromResult(true);
        }, cancel.Token));
        Assert.Equal(2, checks); Assert.Equal("latest progress", File.ReadAllText(world));
        Assert.Contains(engine.List(f.Profile), b => b.Manifest.Reason == "Before restore");
    }

    [Fact]
    public async Task FailureDuringSecondFolderSwapRollsBackTheFirstFolder()
    {
        using var f = new Fixture(); var engine = new BackupEngine(); var archive = await engine.CreateAsync(f.Profile, "Older", false);
        var world = Path.Combine(f.Profile.SavedPath, "SaveGames", "world.sav"); File.WriteAllText(world, "latest progress");
        var config = File.ReadAllBytes(f.Profile.ConfigPath); var checks = 0;
        await Assert.ThrowsAsync<IOException>(() => engine.RestoreAsync(f.Profile, archive.Path, () =>
        {
            if (++checks == 2)
            {
                // A filesystem collision forces a real move failure after SaveGames has been swapped.
                var recovery = Directory.GetDirectories(Path.GetDirectoryName(f.Profile.SavedPath)!, ".recovery-*").Single();
                File.WriteAllText(Path.Combine(recovery, "Config"), "occupied destination");
            }
            return Task.FromResult(true);
        }));
        Assert.Equal("latest progress", File.ReadAllText(world)); Assert.Equal(config, File.ReadAllBytes(f.Profile.ConfigPath));
        foreach (var backup in engine.List(f.Profile)) await engine.VerifyAsync(backup.Path, f.Profile);
    }
}

internal class OfflineRuntime : IServerRuntime
{
    public virtual Task<ServerSnapshot> InspectAsync(ServerProfile profile, CancellationToken token = default) => Task.FromResult(ServerSnapshot.Offline);
    public Task StartAsync(ServerProfile profile, CancellationToken token = default) => throw new InvalidOperationException("Unexpected start in a storage test.");
    public Task StopAsync(ServerProfile profile, CancellationToken token = default) => throw new InvalidOperationException("Unexpected stop in a storage test.");
}
internal class UnusedSteam : ISteamClient
{
    public string? InstalledBuild(ServerProfile profile) => "100";
    public virtual Task<BuildStatus> CheckAsync(ServerProfile profile, Action<string> log, CancellationToken token = default) => throw new InvalidOperationException("Unexpected network operation in a test.");
    public Task InstallAsync(ServerProfile profile, bool repair, Action<string> log, CancellationToken token = default) => throw new InvalidOperationException("Unexpected installation in a test.");
}
