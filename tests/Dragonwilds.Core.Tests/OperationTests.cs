using Dragonwilds.Core;
using Wyrmwatch.Agent;

namespace Dragonwilds.Core.Tests;

public class OperationTests
{
    [Fact]
    public void InstallationLeasesExcludeDuplicateWorkAndReleaseAfterDisposal()
    {
        using var fixture = new Fixture();
        var temporaryRoot = Path.Combine(fixture.Root, "locks");
        using (InstallationLease.Acquire(fixture.Profile.InstallPath, temporaryRoot))
        {
            Assert.Throws<IOException>(() => InstallationLease.Acquire(fixture.Profile.InstallPath, temporaryRoot));
            using var unrelated = InstallationLease.Acquire(Path.Combine(fixture.Root, "other-server"), temporaryRoot);
        }
        using var reacquired = InstallationLease.Acquire(fixture.Profile.InstallPath, temporaryRoot);
    }

    [Fact]
    public void UnixLeasesWorkWithAnUnwritableLegacyDirectoryAndReportPermissionFailures()
    {
        if (OperatingSystem.IsWindows()) return;
        using var fixture = new Fixture();
        var temporaryRoot = Path.Combine(fixture.Root, "locks");
        var legacy = Path.Combine(temporaryRoot, "Wyrmwatch-operation-locks");
        Directory.CreateDirectory(legacy);
        File.SetUnixFileMode(legacy, UnixFileMode.UserRead | UnixFileMode.UserExecute);
        string? lockDirectory = null;
        try
        {
            using (var lease = InstallationLease.Acquire(fixture.Profile.InstallPath, temporaryRoot))
            {
                lockDirectory = Path.GetDirectoryName(lease.Name)!;
                Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute, File.GetUnixFileMode(lockDirectory));
            }
            File.SetUnixFileMode(lockDirectory, UnixFileMode.None);
            // Root bypasses filesystem permissions; CI and desktop users exercise this branch.
            try { using var probe = File.Create(Path.Combine(lockDirectory, "permission-probe")); }
            catch (UnauthorizedAccessException)
            {
                var error = Assert.Throws<IOException>(() => InstallationLease.Acquire(fixture.Profile.InstallPath, temporaryRoot));
                Assert.Contains("same account", error.Message);
            }
            using var unrelated = InstallationLease.Acquire(Path.Combine(fixture.Root, "other-server"), temporaryRoot);
        }
        finally
        {
            File.SetUnixFileMode(legacy, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            if (lockDirectory is not null) File.SetUnixFileMode(lockDirectory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
    }

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
