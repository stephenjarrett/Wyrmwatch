using System.Text;
using Wyrmwatch.Agent;
using Wyrmwatch.Core;

namespace Wyrmwatch.Core.Tests;

public class ManagedSetupRecoveryTests
{
    private sealed class Lab : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "wyrmwatch-setup-recovery-" + Guid.NewGuid().ToString("N"));
        public JsonStore Store => new(Path.Combine(Root, "workspace"));
        public ServerProfile Profile => new() { Id = "prepared-fixture", Name = "Prepared world", InstallPath = Path.Combine(Root, "managed", "prepared-world"), BackupPath = Path.Combine(Root, "backups", "prepared-world") };
        public Runtime Runtime { get; } = new();
        public Installer Steam { get; } = new();
        public Lab() => Store.Write("settings.json", new ManagerSettings());
        public ManagerHost Host() => new(Store, Runtime, Steam);
        public Dictionary<string, string> Configuration => new() { ["ServerName"] = Profile.Name, ["OwnerId"] = "synthetic-owner", ["DefaultWorldName"] = "Fallback", ["AdminPassword"] = "synthetic-admin", ["WorldPassword"] = "", ["Port"] = "7777" };
        public async Task<ServerProfile> Published(bool import)
        {
            var staged = Profile with { InstallPath = Profile.InstallPath + ".setup-" + Guid.NewGuid().ToString("N") };
            Directory.CreateDirectory(staged.InstallPath); File.WriteAllText(staged.Launcher, "synthetic launcher, never run");
            AtomicFile.Write(staged.ConfigPath, GameConfiguration.Merge("", Configuration));
            WorldImportPlan? source = null;
            if (import)
            {
                var path = Path.Combine(Root, "source", "Selected.sav"); WriteSave(path); source = await WorldImport.InspectAsync(path);
                await WorldImport.CopyAsync(source, Path.Combine(staged.SavedPath, "SaveGames"));
            }
            await ManagedSetupRecovery.WriteAsync(Profile, staged, source, Store);
            Directory.Move(staged.InstallPath, Profile.InstallPath); return Profile;
        }
        public void Dispose()
        {
            if (!SafePaths.Within(Root, Path.GetTempPath()) || !Path.GetFileName(Root).StartsWith("wyrmwatch-setup-recovery-")) throw new IOException("Unsafe fixture cleanup");
            Directory.Delete(Root, true);
        }
    }
    private sealed class Runtime : IServerRuntime
    {
        public bool Running; public bool Accessible = true; public int Starts;
        public Task<ServerSnapshot> InspectAsync(ServerProfile p, CancellationToken token = default) => Task.FromResult(ServerSnapshot.Offline with { Running = Running, Accessible = Accessible });
        public Task StartAsync(ServerProfile p, CancellationToken token = default) { Starts++; return Task.CompletedTask; }
        public Task StopAsync(ServerProfile p, CancellationToken token = default) => throw new IOException("Never stop a fixture automatically.");
    }
    private sealed class Installer : ISteamClient
    {
        public int Calls; public Action? AfterDownload;
        public string? InstalledBuild(ServerProfile p) => "fixture-build";
        public Task<BuildStatus> CheckAsync(ServerProfile p, Action<string> log, CancellationToken token = default) => throw new IOException("No check during recovery");
        public Task InstallAsync(ServerProfile p, bool repair, Action<string> log, CancellationToken token = default)
        { Calls++; Directory.CreateDirectory(p.InstallPath); File.WriteAllText(p.Launcher, "synthetic launcher, never run"); AfterDownload?.Invoke(); return Task.CompletedTask; }
    }
    private static void WriteSave(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var body = new MemoryStream(); using var writer = new BinaryWriter(body, Encoding.UTF8, true);
        writer.Write(Encoding.ASCII.GetBytes("INFO")); writer.Write(4); writer.Write(1);
        writer.Write(Encoding.ASCII.GetBytes("GLOB")); writer.Write(12); writer.Write(new byte[12]);
        using var output = new BinaryWriter(File.Create(path)); output.Write(Encoding.ASCII.GetBytes("SAVE")); output.Write((int)body.Length); output.Write(body.ToArray());
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ReopenedManagerRegistersVerifiedSnapshotWithoutDownloadCopyOrStart(bool import)
    {
        using var f = new Lab(); var p = await f.Published(import); var config = File.ReadAllBytes(p.ConfigPath);
        var world = Path.Combine(p.SavedPath, "SaveGames", "Selected.sav"); var bytes = import ? File.ReadAllBytes(world) : [];
        if (import) File.Delete(Path.Combine(f.Root, "source", "Selected.sav"));
        var host = f.Host(); var candidate = Assert.Single(host.PreparedSetups()); Assert.Equal(p.Id, candidate.OriginalId);
        var reviewed = await host.ReviewSetupAsync(new(p.InstallPath));
        var connected = await host.ResumeSetupAsync(new(p.InstallPath, reviewed.ReceiptToken));
        Assert.Equal(p.Id, connected.Id); Assert.Single(f.Host().Settings.Servers); Assert.Empty(host.PreparedSetups());
        Assert.Equal(config, File.ReadAllBytes(p.ConfigPath)); if (import) Assert.Equal(bytes, File.ReadAllBytes(world));
        Assert.False(connected.AutoBackup); Assert.False(connected.AutoUpdate); Assert.Equal(0, f.Steam.Calls); Assert.Equal(0, f.Runtime.Starts);
        Assert.False(ManagedSetupRecovery.HasReceipt(p.InstallPath));
        await Assert.ThrowsAsync<IOException>(() => host.ResumeSetupAsync(new(p.InstallPath, reviewed.ReceiptToken)));
        Assert.Single(host.Settings.Servers);
    }
    [Fact]
    public async Task ActualRegistrationFailureCanResumeAfterWorkspaceReopens()
    {
        using var f = new Lab(); f.Steam.AfterDownload = () => Directory.CreateDirectory(Path.Combine(f.Store.DirectoryPath, "settings.json.bak"));
        await Assert.ThrowsAsync<IOException>(() => f.Host().CreateServerAsync(new(f.Profile, f.Configuration)));
        Assert.Empty(f.Host().Settings.Servers); Assert.True(ManagedSetupRecovery.HasReceipt(f.Profile.InstallPath));
        Directory.Delete(Path.Combine(f.Store.DirectoryPath, "settings.json.bak"));
        var host = f.Host(); var reviewed = await host.ReviewSetupAsync(new(f.Profile.InstallPath));
        await host.ResumeSetupAsync(new(f.Profile.InstallPath, reviewed.ReceiptToken));
        Assert.Single(host.Settings.Servers); Assert.Equal(1, f.Steam.Calls); Assert.Equal(0, f.Runtime.Starts);
    }
    [Theory]
    [InlineData("world")]
    [InlineData("configuration")]
    [InlineData("launcher")]
    [InlineData("extra-save")]
    [InlineData("receipt")]
    public async Task ChangedPreparedFilesRejectWithoutRegistrationOrOverwrite(string change)
    {
        using var f = new Lab(); var p = await f.Published(true); var host = f.Host(); var reviewed = await host.ReviewSetupAsync(new(p.InstallPath));
        var path = change switch { "world" => Path.Combine(p.SavedPath, "SaveGames", "Selected.sav"), "configuration" => p.ConfigPath, "launcher" => p.Launcher, "extra-save" => Path.Combine(p.SavedPath, "SaveGames", "Unrecorded.sav"), _ => Path.Combine(p.InstallPath, ManagedSetupRecovery.ReceiptFileName) };
        File.AppendAllText(path, "modified"); var changed = File.ReadAllBytes(path);
        await Assert.ThrowsAsync<IOException>(() => host.ResumeSetupAsync(new(p.InstallPath, reviewed.ReceiptToken)));
        Assert.Empty(host.Settings.Servers); Assert.Equal(changed, File.ReadAllBytes(path)); Assert.Equal(0, f.Steam.Calls); Assert.Equal(0, f.Runtime.Starts);
    }
    [Fact]
    public async Task UnknownDirectoryAndCopiedReceiptAreNeverAdopted()
    {
        using var f = new Lab(); var p = await f.Published(false);
        var stranger = new JsonStore(Path.Combine(f.Root, "other-workspace"));
        await Assert.ThrowsAsync<IOException>(() => ManagedSetupRecovery.VerifyAsync(p.InstallPath, stranger));
        var copy = Path.Combine(f.Root, "unknown"); Directory.CreateDirectory(copy);
        File.Copy(Path.Combine(p.InstallPath, ManagedSetupRecovery.ReceiptFileName), Path.Combine(copy, ManagedSetupRecovery.ReceiptFileName));
        await Assert.ThrowsAsync<IOException>(() => f.Host().ReviewSetupAsync(new(copy)));
        Assert.Empty(f.Host().Settings.Servers);
    }
    [Theory]
    [InlineData(true, true)]
    [InlineData(false, false)]
    public async Task RunningOrUnknownPreparedServerRemainsUnregistered(bool running, bool accessible)
    {
        using var f = new Lab(); var p = await f.Published(false); f.Runtime.Running = running; f.Runtime.Accessible = accessible;
        var host = f.Host(); var review = await host.ReviewSetupAsync(new(p.InstallPath));
        await Assert.ThrowsAsync<IOException>(() => host.ResumeSetupAsync(new(p.InstallPath, review.ReceiptToken)));
        Assert.Empty(host.Settings.Servers); Assert.True(ManagedSetupRecovery.HasReceipt(p.InstallPath));
    }
    [Theory]
    [InlineData("id")]
    [InlineData("name")]
    [InlineData("overlap")]
    public async Task ExistingDisconnectedIdentityNameAndPathsRemainReserved(string collision)
    {
        using var f = new Lab(); var p = await f.Published(false); var known = p with { Id = "other-id", Name = "Other name", InstallPath = Path.Combine(f.Root, "other-install"), BackupPath = Path.Combine(f.Root, "other-backups") };
        known = collision switch { "id" => known with { Id = p.Id }, "name" => known with { Name = p.Name }, _ => known with { DataPath = p.SavedPath } };
        f.Store.Write("settings.json", new ManagerSettings { DisconnectedServers = [known] });
        var before = File.ReadAllBytes(Path.Combine(f.Store.DirectoryPath, "settings.json")); var host = f.Host(); var review = await host.ReviewSetupAsync(new(p.InstallPath));
        await Assert.ThrowsAnyAsync<Exception>(() => host.ResumeSetupAsync(new(p.InstallPath, review.ReceiptToken)));
        Assert.Equal(before, File.ReadAllBytes(Path.Combine(f.Store.DirectoryPath, "settings.json"))); Assert.Empty(host.Settings.Servers);
    }
    [Fact]
    public async Task StaleUnpublishedReceiptDoesNotBlockLaterVerifiedRetry()
    {
        using var f = new Lab(); var old = f.Profile with { InstallPath = f.Profile.InstallPath + ".setup-old" };
        Directory.CreateDirectory(old.InstallPath); File.WriteAllText(old.Launcher, "old synthetic"); AtomicFile.Write(old.ConfigPath, GameConfiguration.Merge("", f.Configuration));
        await ManagedSetupRecovery.WriteAsync(f.Profile, old, null, f.Store);
        var p = await f.Published(false); var review = await ManagedSetupRecovery.VerifyAsync(p.InstallPath, f.Store);
        Assert.Equal(p.Id, review.Profile.Id); Assert.Single(f.Host().PreparedSetups()); Assert.True(File.Exists(old.Launcher));
        await f.Host().ResumeSetupAsync(new(p.InstallPath, review.ReceiptToken)); Assert.True(File.Exists(old.Launcher));
    }
    [Fact]
    public async Task WrongReviewTokenAndCancellationPreserveThePreparedSetup()
    {
        using var f = new Lab(); var p = await f.Published(false); var host = f.Host();
        await Assert.ThrowsAsync<IOException>(() => host.ResumeSetupAsync(new(p.InstallPath, new string('0', 64))));
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => host.ReviewSetupAsync(new(p.InstallPath), cancelled.Token));
        Assert.Empty(host.Settings.Servers); Assert.True(ManagedSetupRecovery.HasReceipt(p.InstallPath));
    }
}
