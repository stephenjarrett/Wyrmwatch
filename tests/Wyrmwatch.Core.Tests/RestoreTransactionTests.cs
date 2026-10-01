using Wyrmwatch.Core;

namespace Wyrmwatch.Core.Tests;

public class RestoreTransactionTests
{
    private sealed class RunningRuntime : IServerRuntime
    {
        public bool Running { get; private set; } = true;
        public Task<ServerSnapshot> InspectAsync(ServerProfile profile, CancellationToken token = default) => Task.FromResult(ServerSnapshot.Offline with { Running = Running });
        public Task StartAsync(ServerProfile profile, CancellationToken token = default) => throw new InvalidOperationException("Storage test must never start an engine.");
        public Task StopAsync(ServerProfile profile, CancellationToken token = default) { Running = false; return Task.CompletedTask; }
    }

    [Fact]
    public async Task PendingRestoreAllowsGracefulStopEvenWithMissingSaved()
    {
        using var f = new Fixture(); var clean = new BackupEngine(); var archive = await clean.CreateAsync(f.Profile, "Original", false);
        var interrupted = new BackupEngine { RestoreCheckpoint = _ => throw new BackupEngine.RestoreInterruptedException() };
        await Assert.ThrowsAsync<BackupEngine.RestoreInterruptedException>(() => interrupted.RestoreAsync(f.Profile, archive.Path, () => Task.FromResult(true)));
        Directory.Delete(f.Profile.SavedPath, true);
        var runtime = new RunningRuntime(); var service = new MaintenanceService(runtime, new UnusedSteam(), clean, new(f.Root));
        Assert.Contains("Recover", await service.StopAsync(f.Profile));
        Assert.False(runtime.Running); Assert.True(clean.HasPendingRestore(f.Profile));
        await Assert.ThrowsAsync<IOException>(() => service.StartAsync(f.Profile));
        await Assert.ThrowsAsync<IOException>(() => service.StopAsync(f.Profile, true));
    }

    [Fact]
    public async Task ManyFileInventoryCanRecoverItsOwnJournal()
    {
        using var f = new Fixture(); var clean = new BackupEngine();
        for (var index = 0; index < 300; index++) File.WriteAllText(Path.Combine(f.Profile.SavedPath, "SaveGames", $"world-{index:D4}.sav"), "synthetic fixture");
        var archive = await clean.CreateAsync(f.Profile, "Many files", false);
        var interrupted = new BackupEngine { RestoreCheckpoint = step => { if (step == "Installed:Config") throw new BackupEngine.RestoreInterruptedException(); } };
        await Assert.ThrowsAsync<BackupEngine.RestoreInterruptedException>(() => interrupted.RestoreAsync(f.Profile, archive.Path, () => Task.FromResult(true)));
        var journal = Directory.GetFiles(Path.GetDirectoryName(f.Profile.SavedPath)!, ".wyrmwatch-restore-*.json").Single();
        Assert.True(new FileInfo(journal).Length > 64_000);
        await clean.RecoverInterruptedRestoreAsync(f.Profile, () => Task.FromResult(true));
        Assert.False(clean.HasPendingRestore(f.Profile));
        Assert.Equal(301, Directory.GetFiles(Path.Combine(f.Profile.SavedPath, "SaveGames")).Length);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RestoreRecoversMissingOrEmptySaved(bool createEmpty)
    {
        using var f = new Fixture(); var engine = new BackupEngine();
        var archive = await engine.CreateAsync(f.Profile, "Original", false);
        Directory.Delete(f.Profile.SavedPath, true);
        if (createEmpty) Directory.CreateDirectory(f.Profile.SavedPath);
        await engine.RestoreAsync(f.Profile, archive.Path, () => Task.FromResult(true));
        Assert.Equal("precious world data", File.ReadAllText(Path.Combine(f.Profile.SavedPath, "SaveGames", "world.sav")));
        Assert.False(engine.HasPendingRestore(f.Profile));
    }

    [Fact]
    public async Task ConfigOnlyRestoreExplicitlyPreservesWorlds()
    {
        using var f = new Fixture(); var engine = new BackupEngine();
        Directory.Delete(Path.Combine(f.Profile.SavedPath, "SaveGames"), true);
        var archive = await engine.CreateAsync(f.Profile, "Config only", false);
        Assert.False(BackupEngine.Coverage(archive.Manifest).IncludesWorld);
        Directory.CreateDirectory(Path.Combine(f.Profile.SavedPath, "SaveGames"));
        var world = Path.Combine(f.Profile.SavedPath, "SaveGames", "world.sav"); File.WriteAllText(world, "new world");
        File.WriteAllText(f.Profile.ConfigPath, "changed config");
        await engine.RestoreAsync(f.Profile, archive.Path, () => Task.FromResult(true));
        Assert.Equal("new world", File.ReadAllText(world));
        Assert.Contains("OwnerId=owner", File.ReadAllText(f.Profile.ConfigPath));
    }

    [Fact]
    public async Task WorldRestoreRetainsAndClearsConfigAbsentFromArchive()
    {
        using var f = new Fixture(); var engine = new BackupEngine();
        Directory.Delete(Path.Combine(f.Profile.SavedPath, "Config"), true);
        var archive = await engine.CreateAsync(f.Profile, "World only", false);
        Assert.True(BackupEngine.Coverage(archive.Manifest).IncludesWorld);
        Directory.CreateDirectory(Path.GetDirectoryName(f.Profile.ConfigPath)!);
        File.WriteAllText(f.Profile.ConfigPath, "newer config");
        var recovery = await engine.RestoreAsync(f.Profile, archive.Path, () => Task.FromResult(true));
        Assert.False(Directory.Exists(Path.Combine(f.Profile.SavedPath, "Config")));
        Assert.Equal("newer config", File.ReadAllText(Path.Combine(recovery, Path.GetRelativePath(f.Profile.SavedPath, f.Profile.ConfigPath))));
    }

    [Theory]
    [InlineData("Journal")]
    [InlineData("Retained:SaveGames")]
    [InlineData("Installed:SaveGames")]
    [InlineData("Retained:Config")]
    [InlineData("Installed:Config")]
    [InlineData("Committed")]
    public async Task EveryInterruptedSwapBlocksOperationsUntilExplicitIdempotentRecovery(string checkpoint)
    {
        using var f = new Fixture(); var clean = new BackupEngine();
        var archive = await clean.CreateAsync(f.Profile, "Original", false);
        var world = Path.Combine(f.Profile.SavedPath, "SaveGames", "world.sav"); File.WriteAllText(world, "latest world");
        var originalConfig = File.ReadAllText(f.Profile.ConfigPath); File.WriteAllText(f.Profile.ConfigPath, "latest config");
        var interrupted = new BackupEngine { RestoreCheckpoint = step => { if (step == checkpoint) throw new BackupEngine.RestoreInterruptedException(); } };
        await Assert.ThrowsAsync<BackupEngine.RestoreInterruptedException>(() => interrupted.RestoreAsync(f.Profile, archive.Path, () => Task.FromResult(true)));
        Assert.True(clean.HasPendingRestore(f.Profile));
        var service = new MaintenanceService(new OfflineRuntime(), new UnusedSteam(), clean, new(f.Root));
        await Assert.ThrowsAsync<IOException>(() => service.StartAsync(f.Profile));
        await Assert.ThrowsAsync<IOException>(() => service.BackupAsync(f.Profile));
        await Assert.ThrowsAsync<IOException>(() => service.SaveConfigurationAsync(f.Profile, new Dictionary<string, string>()));
        await Assert.ThrowsAsync<IOException>(() => clean.RecoverInterruptedRestoreAsync(f.Profile, () => Task.FromResult(false)));
        await service.RecoverRestoreAsync(f.Profile);
        Assert.False(clean.HasPendingRestore(f.Profile));
        Assert.Equal(checkpoint == "Committed" ? "precious world data" : "latest world", File.ReadAllText(world));
        Assert.Equal(checkpoint == "Committed" ? originalConfig : "latest config", File.ReadAllText(f.Profile.ConfigPath));
        Assert.Contains("No interrupted restore", await service.RecoverRestoreAsync(f.Profile));
    }

    [Fact]
    public async Task CorruptJournalBlocksWithoutMutatingFiles()
    {
        using var f = new Fixture(); var clean = new BackupEngine(); var archive = await clean.CreateAsync(f.Profile, "Original", false);
        var interrupted = new BackupEngine { RestoreCheckpoint = _ => throw new BackupEngine.RestoreInterruptedException() };
        await Assert.ThrowsAsync<BackupEngine.RestoreInterruptedException>(() => interrupted.RestoreAsync(f.Profile, archive.Path, () => Task.FromResult(true)));
        var path = Directory.GetFiles(Path.GetDirectoryName(f.Profile.SavedPath)!, ".wyrmwatch-restore-*.json").Single();
        File.WriteAllText(path, "broken");
        Assert.True(clean.HasPendingRestore(f.Profile));
        await Assert.ThrowsAsync<IOException>(() => clean.RecoverInterruptedRestoreAsync(f.Profile, () => Task.FromResult(true)));
        Assert.True(clean.HasPendingRestore(f.Profile));
        Assert.Equal("precious world data", File.ReadAllText(Path.Combine(f.Profile.SavedPath, "SaveGames", "world.sav")));
    }

    [Theory]
    [InlineData("Stage", null)]
    [InlineData("Previous", null)]
    [InlineData("SavedPath", null)]
    [InlineData("Stage", "/")]
    public async Task InvalidJournalPathsFailClosed(string field, string? value)
    {
        using var f = new Fixture(); var clean = new BackupEngine(); var archive = await clean.CreateAsync(f.Profile, "Original", false);
        var interrupted = new BackupEngine { RestoreCheckpoint = _ => throw new BackupEngine.RestoreInterruptedException() };
        await Assert.ThrowsAsync<BackupEngine.RestoreInterruptedException>(() => interrupted.RestoreAsync(f.Profile, archive.Path, () => Task.FromResult(true)));
        var path = Directory.GetFiles(Path.GetDirectoryName(f.Profile.SavedPath)!, ".wyrmwatch-restore-*.json").Single();
        var document = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(path))!.AsObject(); document[field] = value;
        File.WriteAllText(path, document.ToJsonString());
        await Assert.ThrowsAsync<IOException>(() => clean.RecoverInterruptedRestoreAsync(f.Profile, () => Task.FromResult(true)));
        Assert.True(clean.HasPendingRestore(f.Profile));
        Assert.Equal("precious world data", File.ReadAllText(Path.Combine(f.Profile.SavedPath, "SaveGames", "world.sav")));
    }

    [Theory]
    [InlineData("RecoveryRetained:SaveGames")]
    [InlineData("RecoveryRestored:SaveGames")]
    [InlineData("RecoveryRetained:Config")]
    [InlineData("RecoveryRestored:Config")]
    public async Task InterruptedRollbackCanResumeWithoutLosingEitherState(string checkpoint)
    {
        using var f = new Fixture(); var clean = new BackupEngine(); var archive = await clean.CreateAsync(f.Profile, "Original", false);
        var world = Path.Combine(f.Profile.SavedPath, "SaveGames", "world.sav"); File.WriteAllText(world, "latest world");
        File.WriteAllText(f.Profile.ConfigPath, "latest config");
        var interrupted = new BackupEngine { RestoreCheckpoint = step => { if (step == "Installed:Config") throw new BackupEngine.RestoreInterruptedException(); } };
        await Assert.ThrowsAsync<BackupEngine.RestoreInterruptedException>(() => interrupted.RestoreAsync(f.Profile, archive.Path, () => Task.FromResult(true)));
        var recovery = new BackupEngine { RestoreCheckpoint = step => { if (step == checkpoint) throw new BackupEngine.RestoreInterruptedException(); } };
        await Assert.ThrowsAsync<BackupEngine.RestoreInterruptedException>(() => recovery.RecoverInterruptedRestoreAsync(f.Profile, () => Task.FromResult(true)));
        Assert.True(clean.HasPendingRestore(f.Profile));
        await clean.RecoverInterruptedRestoreAsync(f.Profile, () => Task.FromResult(true));
        Assert.False(clean.HasPendingRestore(f.Profile));
        Assert.Equal("latest world", File.ReadAllText(world)); Assert.Equal("latest config", File.ReadAllText(f.Profile.ConfigPath));
        var retained = Directory.GetDirectories(Path.GetDirectoryName(f.Profile.SavedPath)!, ".recovery-*").Single();
        Assert.Equal("precious world data", File.ReadAllText(Path.Combine(retained, "interrupted-SaveGames", "world.sav")));
    }

    [Fact]
    public async Task MissingOriginalKeepsRecoveryBlocked()
    {
        using var f = new Fixture(); var clean = new BackupEngine(); var archive = await clean.CreateAsync(f.Profile, "Original", false);
        var interrupted = new BackupEngine { RestoreCheckpoint = step => { if (step == "Retained:SaveGames") throw new BackupEngine.RestoreInterruptedException(); } };
        await Assert.ThrowsAsync<BackupEngine.RestoreInterruptedException>(() => interrupted.RestoreAsync(f.Profile, archive.Path, () => Task.FromResult(true)));
        var retained = Directory.GetDirectories(Path.GetDirectoryName(f.Profile.SavedPath)!, ".recovery-*").Single();
        Directory.Delete(Path.Combine(retained, "SaveGames"), true);
        await Assert.ThrowsAsync<IOException>(() => clean.RecoverInterruptedRestoreAsync(f.Profile, () => Task.FromResult(true)));
        Assert.True(clean.HasPendingRestore(f.Profile));
    }

    [Fact]
    public async Task ChangedCommittedWorldKeepsRecoveryBlocked()
    {
        using var f = new Fixture(); var clean = new BackupEngine(); var archive = await clean.CreateAsync(f.Profile, "Original", false);
        var interrupted = new BackupEngine { RestoreCheckpoint = step => { if (step == "Committed") throw new BackupEngine.RestoreInterruptedException(); } };
        await Assert.ThrowsAsync<BackupEngine.RestoreInterruptedException>(() => interrupted.RestoreAsync(f.Profile, archive.Path, () => Task.FromResult(true)));
        var world = Path.Combine(f.Profile.SavedPath, "SaveGames", "world.sav"); File.WriteAllText(world, "external change");
        await Assert.ThrowsAsync<IOException>(() => clean.RecoverInterruptedRestoreAsync(f.Profile, () => Task.FromResult(true)));
        Assert.True(clean.HasPendingRestore(f.Profile)); Assert.Equal("external change", File.ReadAllText(world));
    }

    [Fact]
    public async Task MissingPrimaryJournalFallsBackToRetainedDocumentAndNeverFailsOpen()
    {
        using var f = new Fixture(); var clean = new BackupEngine(); var archive = await clean.CreateAsync(f.Profile, "Original", false);
        var world = Path.Combine(f.Profile.SavedPath, "SaveGames", "world.sav"); File.WriteAllText(world, "latest world");
        var interrupted = new BackupEngine { RestoreCheckpoint = step => { if (step == "Committed") throw new BackupEngine.RestoreInterruptedException(); } };
        await Assert.ThrowsAsync<BackupEngine.RestoreInterruptedException>(() => interrupted.RestoreAsync(f.Profile, archive.Path, () => Task.FromResult(true)));
        var path = Directory.GetFiles(Path.GetDirectoryName(f.Profile.SavedPath)!, ".wyrmwatch-restore-*.json").Single();
        Assert.True(File.Exists(path + ".bak")); File.Delete(path);
        Assert.True(clean.HasPendingRestore(f.Profile));
        Assert.Throws<IOException>(() => clean.EnsureNoPendingRestore(f.Profile));
        await clean.RecoverInterruptedRestoreAsync(f.Profile, () => Task.FromResult(true));
        Assert.Equal("latest world", File.ReadAllText(world));
        Assert.False(clean.HasPendingRestore(f.Profile)); Assert.False(File.Exists(path + ".bak"));
    }

    [Theory]
    [InlineData("Installed:SaveGames")]
    [InlineData("Installed:Config")]
    public async Task MissingBaselineRollbackRetainsInstalledFilesAndReturnsToEmptyState(string checkpoint)
    {
        using var f = new Fixture(); var clean = new BackupEngine(); var archive = await clean.CreateAsync(f.Profile, "Original", false);
        Directory.Delete(f.Profile.SavedPath, true);
        var interrupted = new BackupEngine { RestoreCheckpoint = step => { if (step == checkpoint) throw new BackupEngine.RestoreInterruptedException(); } };
        await Assert.ThrowsAsync<BackupEngine.RestoreInterruptedException>(() => interrupted.RestoreAsync(f.Profile, archive.Path, () => Task.FromResult(true)));
        await clean.RecoverInterruptedRestoreAsync(f.Profile, () => Task.FromResult(true));
        Assert.False(clean.HasPendingRestore(f.Profile)); Assert.Empty(Directory.GetFileSystemEntries(f.Profile.SavedPath));
        var retained = Directory.GetDirectories(Path.GetDirectoryName(f.Profile.SavedPath)!, ".recovery-*").Single();
        Assert.Equal("precious world data", File.ReadAllText(Path.Combine(retained, "interrupted-SaveGames", "world.sav")));
    }
}

