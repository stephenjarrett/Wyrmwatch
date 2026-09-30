using Wyrmwatch.Core;
using Wyrmwatch.Agent;

namespace Wyrmwatch.Core.Tests;

public class ConnectionTests
{
    [Theory]
    [InlineData("same saves")]
    [InlineData("nested saves")]
    [InlineData("parent saves")]
    [InlineData("nested install")]
    [InlineData("backup in other saves")]
    [InlineData("saves in other backups")]
    public async Task ConflictingConnectionsAreRejectedWithoutChangingSettingsOrWorld(string conflict)
    {
        using var a = new Fixture(); using var b = new Fixture();
        var second = b.Profile with { Port = 7778 };
        second = conflict switch
        {
            "same saves" => second with { DataPath = a.Profile.SavedPath },
            "nested saves" => second with { DataPath = Path.Combine(a.Profile.SavedPath, "nested") },
            "parent saves" => second with { DataPath = Path.GetDirectoryName(a.Profile.SavedPath)! },
            "nested install" => second with { InstallPath = Path.Combine(a.Profile.InstallPath, "nested") },
            "backup in other saves" => second with { BackupPath = Path.Combine(a.Profile.SavedPath, "backups") },
            _ => second with { DataPath = Path.Combine(a.Profile.BackupPath, "saves") }
        };
        var store = new JsonStore(a.Root); store.Write("settings.json", new ManagerSettings { Servers = [a.Profile] });
        var before = File.ReadAllBytes(Path.Combine(a.Root, "settings.json"));
        await Assert.ThrowsAsync<ArgumentException>(() => new ManagerHost(store).SaveProfileAsync(second));
        Assert.Equal(before, File.ReadAllBytes(Path.Combine(a.Root, "settings.json")));
        Assert.Equal("precious world data", File.ReadAllText(Path.Combine(a.Profile.SavedPath, "SaveGames", "world.sav")));
    }

    [Fact]
    public async Task SharedPortAndBackupDestinationAreAllowedForSavedServers()
    {
        using var a = new Fixture(); using var b = new Fixture();
        var host = new ManagerHost(new(a.Root));
        await host.SaveProfileAsync(a.Profile);
        await host.SaveProfileAsync(b.Profile with { BackupPath = a.Profile.BackupPath });
        await host.SaveProfileAsync(a.Profile with { Name = "Updated name" });
        Assert.Equal(2, host.Settings.Servers.Count);
        Assert.All(host.Settings.Servers, p => Assert.Equal(7777, p.Port));
    }

    [Fact]
    public async Task OverlappingSaveFolderIsRejectedBeforeAnyGameWriteOrBackup()
    {
        using var a = new Fixture(); using var b = new Fixture();
        var store = new JsonStore(a.Root);
        store.Write("settings.json", new ManagerSettings { Servers = [a.Profile with { DataPath = b.Profile.SavedPath }, b.Profile] });
        var host = new ManagerHost(store);
        var original = File.ReadAllBytes(b.Profile.ConfigPath);
        var values = GameConfiguration.Read(b.Profile.ConfigPath); values["Port"] = "7778";
        await Assert.ThrowsAsync<ArgumentException>(() => host.ExecuteAsync(a.Profile.Id, new("configuration", Values: values)));
        Assert.Equal(original, File.ReadAllBytes(b.Profile.ConfigPath)); Assert.Empty(new BackupEngine().List(a.Profile));
    }

    [Fact]
    public async Task ImportReadsExternalSaveFolderAndReconnectsWithoutChangingFiles()
    {
        using var f = new Fixture(); var backup = new BackupEngine();
        var external = Path.Combine(f.Root, "existing-world"); Directory.Move(f.Profile.SavedPath, external);
        File.WriteAllText(f.Profile.Launcher, "fixture launcher only");
        var profile = ExistingServerImport.Inspect(f.Profile.Launcher, external, f.Profile.BackupPath);
        Assert.Equal("Friends", profile.Name); Assert.Equal(7777, profile.Port);
        var before = Directory.GetFiles(f.Root, "*", SearchOption.AllDirectories).ToDictionary(p => p, File.ReadAllBytes);
        var host = new ManagerHost(new(Path.Combine(f.Root, "workspace")));
        profile = await host.ImportProfileAsync(profile with { AutoBackup = true, AutoUpdate = true });
        Assert.False(profile.AutoBackup); Assert.False(profile.AutoUpdate); Assert.Empty(host.Status().Operations);
        foreach (var (path, data) in before) Assert.Equal(data, File.ReadAllBytes(path));
        var recovery = await backup.CreateAsync(profile, "Test", false);
        await host.RemoveProfileAsync(profile.Id);
        var connected = await host.ImportProfileAsync(profile with { Id = "reconnected" });
        Assert.Equal(profile.Id, connected.Id); Assert.Equal(recovery.Path, Assert.Single(backup.List(connected)).Path);
        Assert.Equal(external, connected.SavedPath);
    }
}
