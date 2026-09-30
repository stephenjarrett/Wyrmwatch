using System.Text;
using Wyrmwatch.Agent;
using Wyrmwatch.Core;

namespace Wyrmwatch.Core.Tests;

public class WorldProvisionTests
{
    private sealed class Runtime : IServerRuntime
    {
        public string? RunningId { get; set; }
        public int Starts { get; private set; }
        public Task<ServerSnapshot> InspectAsync(ServerProfile p, CancellationToken token = default) => Task.FromResult(ServerSnapshot.Offline with { Running = p.Id == RunningId });
        public Task StartAsync(ServerProfile p, CancellationToken token = default) { Starts++; return Task.CompletedTask; }
        public Task StopAsync(ServerProfile p, CancellationToken token = default) => throw new InvalidOperationException("Never stop a source server automatically.");
    }
    private sealed class Installer : ISteamClient
    {
        public int Calls; public bool Fail; public Action? AfterDownload;
        public string? InstalledBuild(ServerProfile p) => "fixture-build";
        public Task<BuildStatus> CheckAsync(ServerProfile p, Action<string> log, CancellationToken token = default) => throw new InvalidOperationException();
        public Task InstallAsync(ServerProfile p, bool repair, Action<string> log, CancellationToken token = default)
        {
            Calls++; Directory.CreateDirectory(p.InstallPath); File.WriteAllText(p.Launcher, "synthetic launcher only");
            if (Fail) throw new IOException("Synthetic interrupted download");
            AfterDownload?.Invoke(); return Task.CompletedTask;
        }
    }
    private static void Save(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var body = new MemoryStream(); using var writer = new BinaryWriter(body, Encoding.UTF8, true);
        foreach (var name in new[] { "INFO", "GLOB" }) { writer.Write(Encoding.ASCII.GetBytes(name)); writer.Write(4); writer.Write(1); }
        using var output = new BinaryWriter(File.Create(path)); output.Write(Encoding.ASCII.GetBytes("SAVE")); output.Write((int)body.Length); output.Write(body.ToArray());
    }
    private static ServerCreationPlan Plan(Fixture f, string name = "Imported Friends") => new(name, "Fallback world", "synthetic-owner", "synthetic-admin", "", 7777, Path.Combine(f.Root, "managed servers"), ServerCreationPlan.SuggestFolderName(name), Path.Combine(f.Root, "managed backups"));
    private static WorldImportRequest Request(ServerCreationPlan p, WorldImportPlan source, bool stopped = true) => new(p.Profile, p.Configuration.ToDictionary(), source, stopped);

    [Fact]
    public async Task ImportProvisionsOneWorldAndPreservesSourceAndCompanionsWithoutStarting()
    {
        using var f = new Fixture(); var source = Path.Combine(f.Root, "source", "Original World.sav"); Save(source);
        File.WriteAllText(source + ".backup", "source backup"); var bytes = File.ReadAllBytes(source);
        var plan = Plan(f); var inspected = await WorldImport.InspectAsync(source); var runtime = new Runtime();
        var host = new ManagerHost(new(Path.Combine(f.Root, "workspace")), runtime, new Installer());
        var p = await host.ImportWorldAsync(Request(plan, inspected));
        Assert.Equal(bytes, File.ReadAllBytes(source)); Assert.Equal("source backup", File.ReadAllText(source + ".backup"));
        Assert.Equal(bytes, File.ReadAllBytes(Assert.Single(Directory.GetFiles(Path.Combine(p.SavedPath, "SaveGames")))));
        Assert.Equal("synthetic-owner", GameConfiguration.Read(p.ConfigPath)["OwnerId"]);
        Assert.False(p.AutoBackup); Assert.False(p.AutoUpdate); Assert.Equal(0, runtime.Starts);
        var log = File.ReadAllText(Path.Combine(f.Root, "workspace", "activity.log"));
        Assert.DoesNotContain("synthetic-admin", log); Assert.DoesNotContain("synthetic-owner", log);
    }
    [Fact]
    public async Task SourceStoppedConfirmationAndKnownRunningSourceBlockBeforeDownload()
    {
        using var f = new Fixture(); var source = Path.Combine(f.Profile.SavedPath, "SaveGames", "Selected.sav"); Save(source);
        var installer = new Installer(); var host = new ManagerHost(new(Path.Combine(f.Root, "workspace")), new Runtime { RunningId = f.Profile.Id }, installer);
        await host.SaveProfileAsync(f.Profile); await host.RemoveProfileAsync(f.Profile.Id);
        var request = Request(Plan(f), await WorldImport.InspectAsync(source));
        await Assert.ThrowsAsync<ArgumentException>(() => host.ImportWorldAsync(request with { SourceStoppedConfirmed = false }));
        await Assert.ThrowsAsync<IOException>(() => host.ImportWorldAsync(request));
        Assert.Equal(0, installer.Calls); Assert.Empty(host.Settings.Servers);
    }
    [Fact]
    public async Task SourceChangedDuringDownloadBlocksPublicationAndCanBeReviewedAndRetried()
    {
        using var f = new Fixture(); var source = Path.Combine(f.Root, "source", "Selected.sav"); Save(source);
        var installer = new Installer(); var host = new ManagerHost(new(Path.Combine(f.Root, "workspace")), new Runtime(), installer); var plan = Plan(f);
        var reviewed = await WorldImport.InspectAsync(source);
        installer.AfterDownload = () => { var bytes = File.ReadAllBytes(source); bytes[^1] = 2; File.WriteAllBytes(source, bytes); };
        await Assert.ThrowsAsync<IOException>(() => host.ImportWorldAsync(Request(plan, reviewed)));
        Assert.False(Directory.Exists(plan.Profile.InstallPath)); Assert.Empty(host.Settings.Servers);
        installer.AfterDownload = null;
        var p = await host.ImportWorldAsync(Request(plan, await WorldImport.InspectAsync(source)));
        Assert.Equal(File.ReadAllBytes(source), File.ReadAllBytes(Path.Combine(p.SavedPath, "SaveGames", "Selected.sav")));
    }
    [Fact]
    public async Task InterruptedDownloadRetryNeverOverwritesAnExistingDestination()
    {
        using var f = new Fixture(); var source = Path.Combine(f.Root, "source", "Selected.sav"); Save(source);
        var installer = new Installer { Fail = true }; var host = new ManagerHost(new(Path.Combine(f.Root, "workspace")), new Runtime(), installer); var plan = Plan(f);
        var request = Request(plan, await WorldImport.InspectAsync(source));
        await Assert.ThrowsAsync<IOException>(() => host.ImportWorldAsync(request));
        Assert.False(Directory.Exists(plan.Profile.InstallPath)); Assert.Empty(host.Settings.Servers);
        installer.Fail = false; await host.ImportWorldAsync(request);
        var original = File.ReadAllBytes(Path.Combine(plan.Profile.SavedPath, "SaveGames", "Selected.sav"));
        await Assert.ThrowsAsync<IOException>(() => host.ImportWorldAsync(request with { Profile = plan.Profile with { Name = "Another name" } }));
        Assert.Equal(original, File.ReadAllBytes(Path.Combine(plan.Profile.SavedPath, "SaveGames", "Selected.sav"))); Assert.Equal(2, installer.Calls);
    }
    [Fact]
    public async Task DuplicateServerNameIsRejectedEvenWithDifferentManagedFolder()
    {
        using var f = new Fixture(); var installer = new Installer(); var host = new ManagerHost(new(Path.Combine(f.Root, "workspace")), new Runtime(), installer);
        await host.SaveProfileAsync(f.Profile);
        var p = Plan(f, f.Profile.Name); Assert.Throws<ArgumentException>(() => p.Validate(host.Settings.Servers));
        await Assert.ThrowsAsync<IOException>(() => host.CreateServerAsync(new(p.Profile, p.Configuration.ToDictionary())));
        Assert.Equal(0, installer.Calls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CreateAndWorldImportCannotReuseDisconnectedServerIdentity(bool importWorld)
    {
        using var f = new Fixture();
        var installer = new Installer(); var runtime = new Runtime { RunningId = f.Profile.Id };
        var workspace = Path.Combine(f.Root, "workspace");
        var host = new ManagerHost(new(workspace), runtime, installer);
        await host.SaveProfileAsync(f.Profile); await host.RemoveProfileAsync(f.Profile.Id);
        var settingsBefore = File.ReadAllBytes(Path.Combine(workspace, "settings.json"));
        var world = Path.Combine(f.Profile.SavedPath, "SaveGames", "world.sav");
        var worldBefore = File.ReadAllBytes(world); var configBefore = File.ReadAllBytes(f.Profile.ConfigPath);
        var source = Path.Combine(f.Root, "source", "Selected.sav"); Save(source);
        var reviewed = await WorldImport.InspectAsync(source);
        var plan = Plan(f, "A different server name");
        var collision = plan.Profile with { Id = f.Profile.Id };
        if (importWorld)
            await Assert.ThrowsAsync<IOException>(() => host.ImportWorldAsync(new(collision, plan.Configuration.ToDictionary(), reviewed, true)));
        else
            await Assert.ThrowsAsync<IOException>(() => host.CreateServerAsync(new(collision, plan.Configuration.ToDictionary())));
        Assert.Equal(0, installer.Calls); Assert.Equal(0, runtime.Starts);
        Assert.Empty(host.Settings.Servers); Assert.Equal(f.Profile.Id, Assert.Single(host.Settings.DisconnectedServers).Id);
        Assert.Equal(settingsBefore, File.ReadAllBytes(Path.Combine(workspace, "settings.json")));
        Assert.Equal(worldBefore, File.ReadAllBytes(world)); Assert.Equal(configBefore, File.ReadAllBytes(f.Profile.ConfigPath));
        Assert.False(Directory.Exists(collision.InstallPath));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task WorkspaceRegistrationFailureRetainsPublishedSetupAndExplainsRetryLimit(bool importWorld)
    {
        using var f = new Fixture(); var workspace = Path.Combine(f.Root, "workspace");
        var installer = new Installer(); var runtime = new Runtime();
        var host = new ManagerHost(new(workspace), runtime, installer);
        await host.SaveProfileAsync(f.Profile);
        var settingsBefore = File.ReadAllBytes(Path.Combine(workspace, "settings.json"));
        var oldWorld = Path.Combine(f.Profile.SavedPath, "SaveGames", "world.sav");
        var worldBefore = File.ReadAllBytes(oldWorld); var configBefore = File.ReadAllBytes(f.Profile.ConfigPath);
        var source = Path.Combine(f.Root, "source", "Selected.sav"); Save(source);
        var sourceBefore = File.ReadAllBytes(source); var reviewed = await WorldImport.InspectAsync(source);
        var plan = Plan(f);
        // Force atomic settings replacement to fail after folder publication, without
        // locking reads or altering the existing workspace document.
        installer.AfterDownload = () => Directory.CreateDirectory(Path.Combine(workspace, "settings.json.bak"));
        Task<ServerProfile> Submit() => importWorld ? host.ImportWorldAsync(Request(plan, reviewed)) : host.CreateServerAsync(new(plan.Profile, plan.Configuration.ToDictionary()));
        var error = await Assert.ThrowsAsync<IOException>(Submit);
        Assert.Contains("Completed setup files were preserved at " + plan.Profile.InstallPath, error.Message);
        Assert.Contains("did not save the server connection", error.Message);
        Assert.Contains("Do not retry into that existing folder", error.Message);
        Assert.True(File.Exists(plan.Profile.Launcher)); Assert.True(File.Exists(plan.Profile.ConfigPath));
        if (importWorld) Assert.Equal(sourceBefore, File.ReadAllBytes(Path.Combine(plan.Profile.SavedPath, "SaveGames", "Selected.sav")));
        Assert.Equal(f.Profile.Id, Assert.Single(host.Settings.Servers).Id);
        Assert.Equal(settingsBefore, File.ReadAllBytes(Path.Combine(workspace, "settings.json")));
        Assert.Equal(worldBefore, File.ReadAllBytes(oldWorld)); Assert.Equal(configBefore, File.ReadAllBytes(f.Profile.ConfigPath));
        Assert.Equal(sourceBefore, File.ReadAllBytes(source)); Assert.Equal(0, runtime.Starts);
        await Assert.ThrowsAsync<IOException>(Submit); Assert.Equal(1, installer.Calls);
    }
}
