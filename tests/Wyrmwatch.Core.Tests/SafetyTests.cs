using Wyrmwatch.Core;
using System.IO.Compression;
using System.Text.Json;

namespace Wyrmwatch.Core.Tests;

public sealed class Fixture : IDisposable
{
    public string Root { get; } = Path.Combine(Path.GetTempPath(), "wyrmwatch-test-" + Guid.NewGuid().ToString("N"));
    public ServerProfile Profile { get; }
    public Fixture()
    {
        Profile = new() { InstallPath = Path.Combine(Root, "server"), BackupPath = Path.Combine(Root, "backups") };
        Directory.CreateDirectory(Path.Combine(Profile.SavedPath, "SaveGames"));
        Directory.CreateDirectory(Path.GetDirectoryName(Profile.ConfigPath)!);
        File.WriteAllText(Path.Combine(Profile.SavedPath, "SaveGames", "world.sav"), "precious world data");
        File.WriteAllText(Profile.ConfigPath, "[Unrelated]\nKeep=1\n[/Script/Dominion.DedicatedServerSettings]\nOwnerId=owner\nServerName=Friends\nDefaultWorldName=Home\nAdminPassword=test-only\nWorldPassword=\nPort=7777\n+AdminIds=keep-me\n");
    }
    public void Dispose()
    {
        var expected = Path.Combine(Path.GetTempPath(), Path.GetFileName(Root));
        if (SafePaths.Same(Root, expected) && Path.GetFileName(Root).StartsWith("wyrmwatch-test-")) Directory.Delete(Root, true);
    }
}

public class SafetyTests
{
    [Fact] public async Task BackupRoundTripPreservesWorldAndManifest()
    {
        using var f = new Fixture(); var engine = new BackupEngine();
        var backup = await engine.CreateAsync(f.Profile, "Test", false);
        var verified = await engine.VerifyAsync(backup.Path, f.Profile);
        Assert.Equal(2, verified.Files.Count); Assert.Single(engine.List(f.Profile));
        Assert.Empty(Directory.GetFiles(f.Profile.BackupPath, "*.partial"));
    }
    [Fact] public async Task TamperedArchiveFailsVerification()
    {
        using var f = new Fixture(); var engine = new BackupEngine(); var backup = await engine.CreateAsync(f.Profile, "Test", false);
        using (var zip = ZipFile.Open(backup.Path, ZipArchiveMode.Update))
        { zip.GetEntry("SaveGames/world.sav")!.Delete(); using var writer = new StreamWriter(zip.CreateEntry("SaveGames/world.sav").Open()); writer.Write("corrupted"); }
        await Assert.ThrowsAsync<IOException>(() => engine.VerifyAsync(backup.Path, f.Profile));
    }
    [Fact] public async Task RetentionLeavesUnrelatedAndOtherProfileFiles()
    {
        using var f = new Fixture(); var engine = new BackupEngine(); var p = f.Profile with { RetainBackups = 1 };
        var old = await engine.CreateAsync(p, "First", false); var newer = await engine.CreateAsync(p, "Second", false);
        var unrelated = Path.Combine(p.BackupPath, "manual.zip"); File.WriteAllText(unrelated, "keep");
        var other = await engine.CreateAsync(p with { Id = Guid.NewGuid().ToString("N") }, "Other", false);
        engine.Prune(p);
        Assert.False(File.Exists(old.Path)); Assert.True(File.Exists(newer.Path)); Assert.True(File.Exists(unrelated)); Assert.True(File.Exists(other.Path));
    }
    [Fact] public async Task RestoreRetainsPreviousStateAndRemovesNewerSavesFromActiveFolder()
    {
        using var f = new Fixture(); var engine = new BackupEngine(); var backup = await engine.CreateAsync(f.Profile, "Original", false);
        var world = Path.Combine(f.Profile.SavedPath, "SaveGames", "world.sav");
        File.WriteAllText(world, "newer progress"); File.WriteAllText(Path.Combine(Path.GetDirectoryName(world)!, "newer.sav"), "another world");
        var recovery = await engine.RestoreAsync(f.Profile, backup.Path, () => Task.FromResult(true));
        Assert.Equal("precious world data", File.ReadAllText(world));
        Assert.Equal("newer progress", File.ReadAllText(Path.Combine(recovery, "SaveGames", "world.sav")));
        Assert.True(File.Exists(Path.Combine(recovery, "SaveGames", "newer.sav")));
        Assert.False(File.Exists(Path.Combine(Path.GetDirectoryName(world)!, "newer.sav")));
    }
    [Fact] public async Task RestoreRefusesRunningServerWithoutChangingFiles()
    {
        using var f = new Fixture(); var engine = new BackupEngine(); var backup = await engine.CreateAsync(f.Profile, "Original", false);
        await Assert.ThrowsAsync<IOException>(() => engine.RestoreAsync(f.Profile, backup.Path, () => Task.FromResult(false)));
        Assert.Equal("precious world data", File.ReadAllText(Path.Combine(f.Profile.SavedPath, "SaveGames", "world.sav")));
    }
    [Fact] public async Task RestoreRechecksServerAfterStaging()
    {
        using var f = new Fixture(); var engine = new BackupEngine(); var backup = await engine.CreateAsync(f.Profile, "Original", false); var calls = 0;
        await Assert.ThrowsAsync<IOException>(() => engine.RestoreAsync(f.Profile, backup.Path, () => Task.FromResult(++calls == 1)));
        Assert.Equal("precious world data", File.ReadAllText(Path.Combine(f.Profile.SavedPath, "SaveGames", "world.sav")));
    }
    [Fact] public async Task WrongServerBackupIsRejected()
    {
        using var f = new Fixture(); var engine = new BackupEngine(); var backup = await engine.CreateAsync(f.Profile, "Original", false);
        await Assert.ThrowsAsync<IOException>(() => engine.VerifyAsync(backup.Path, f.Profile with { Id = "another" }));
    }
    [Theory] [InlineData("../outside.sav")] [InlineData("SaveGames/../../outside")] [InlineData("C:/Windows/data")] [InlineData("/etc/passwd")] [InlineData("SaveGames/file:stream")]
    public void ArchivePathsCannotEscapeRoot(string relative)
    {
        using var f = new Fixture(); Assert.Throws<IOException>(() => SafePaths.Resolve(f.Profile.SavedPath, relative));
    }
    [Fact] public void BackupsCannotBeInsideInstallation()
    {
        using var f = new Fixture(); Assert.Throws<ArgumentException>(() => (f.Profile with { BackupPath = Path.Combine(f.Profile.InstallPath, "backups") }).Validate());
    }
    [Fact] public async Task EmptyBackupCannotSucceedOrPrune()
    {
        using var f = new Fixture(); var engine = new BackupEngine(); var other = f.Profile with { InstallPath = Path.Combine(f.Root, "empty") };
        await Assert.ThrowsAsync<IOException>(() => engine.CreateAsync(other, "Empty", false));
    }
    [Fact] public void InvalidPreferencesArePreserved()
    {
        using var f = new Fixture(); var store = new JsonStore(f.Root); var path = Path.Combine(f.Root, "settings.json"); File.WriteAllText(path, "{ damaged");
        Assert.Throws<IOException>(() => store.Read("settings.json", () => new ManagerSettings())); Assert.Equal("{ damaged", File.ReadAllText(path));
    }
    [Fact] public void AtomicWriteKeepsPreviousVersion()
    {
        using var f = new Fixture(); var file = Path.Combine(f.Root, "state.json"); AtomicFile.Write(file, "one"); AtomicFile.Write(file, "two");
        Assert.Equal("two", File.ReadAllText(file)); Assert.Equal("one", File.ReadAllText(file + ".bak"));
    }
    [Fact] public void ConfigMergePreservesOtherSectionsAdminListsAndComments()
    {
        using var f = new Fixture(); var original = File.ReadAllText(f.Profile.ConfigPath) + "; important comment\n"; var values = GameConfiguration.ReadText(original); values["ServerName"] = "New name";
        var merged = GameConfiguration.Merge(original, values);
        Assert.Contains("Keep=1", merged); Assert.Contains("+AdminIds=keep-me", merged); Assert.Contains("; important comment", merged); Assert.Contains("ServerName=New name", merged);
        Assert.Equal(merged, GameConfiguration.Merge(merged, values));
    }
    [Fact] public void ConfigRejectsLineInjection()
    {
        using var f = new Fixture(); var values = GameConfiguration.Read(f.Profile.ConfigPath); values["ServerName"] = "Oops\nAdminPassword=changed";
        Assert.Throws<ArgumentException>(() => GameConfiguration.Merge("", values));
    }
    [Fact] public void PublicBuildDoesNotConfuseBetaWithPublic()
    {
        const string data = "noise \"4019830\" { \"depots\" { \"branches\" { \"beta\" { \"buildid\" \"999\" } \"public\" { \"buildid\" \"123\" } } } }";
        Assert.Equal("123", SteamMetadata.PublicBuild(data)); Assert.Null(SteamMetadata.PublicBuild("unavailable"));
    }
    [Theory] [InlineData(23, true)] [InlineData(1, true)] [InlineData(4, false)] [InlineData(22, true)] [InlineData(2, false)]
    public void MaintenanceWindowCanCrossMidnight(int hour, bool expected)
    {
        var p = new ServerProfile { UseMaintenanceWindow = true, WindowStart = new(22, 0), WindowEnd = new(2, 0) };
        Assert.Equal(expected, MaintenanceService.InWindow(p, new DateTimeOffset(DateTime.Today.AddHours(hour))));
    }
}
