using Wyrmwatch.Core;
using Wyrmwatch.Agent;

namespace Wyrmwatch.Core.Tests;

public class PersistenceRaceTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void PreviouslyObservedDocumentCannotFallBackWhenFileOrDirectoryDisappears(bool directoryDisappears)
    {
        using var fixture = new Fixture();
        var writer = new JsonStore(Path.Combine(fixture.Root, "workspace"));
        writer.Write("settings.json", new ManagerSettings { Servers = [fixture.Profile], BackgroundMode = true });
        var reader = new JsonStore(writer.DirectoryPath);
        Assert.True(reader.Read("settings.json", () => new ManagerSettings()).BackgroundMode);
        var path = directoryDisappears ? writer.DirectoryPath : Path.Combine(writer.DirectoryPath, "settings.json");
        var held = path + ".fixture-held";
        try
        {
            if (directoryDisappears) Directory.Move(path, held); else File.Move(path, held);
            Assert.Throws<IOException>(() => reader.Read("settings.json", () => new ManagerSettings()));
            Assert.Throws<IOException>(() => writer.Read("settings.json", () => new ManagerSettings()));
        }
        finally { if (directoryDisappears) Directory.Move(held, path); else File.Move(held, path); }
        Assert.True(reader.Read("settings.json", () => new ManagerSettings()).BackgroundMode);
        Assert.Equal(fixture.Profile.Id, Assert.Single(reader.Read("settings.json", () => new ManagerSettings()).Servers).Id);
    }

    [Fact]
    public void NewWorkspaceStillUsesDefaultsForNeverCreatedFiles()
    {
        using var fixture = new Fixture();
        var store = new JsonStore(Path.Combine(fixture.Root, "new-workspace"));
        Assert.False(store.Read("settings.json", () => new ManagerSettings()).BackgroundMode);
        store.Write("settings.json", new ManagerSettings { BackgroundMode = true });
        Assert.Empty(store.Read("operations.json", () => new List<OperationRecord>()));
        Assert.True(store.Read("settings.json", () => new ManagerSettings()).BackgroundMode);
    }

    [Fact]
    public async Task TransientMissingPreferencesNeverTurnsBackgroundModeOffOrStopsManager()
    {
        using var fixture = new Fixture();
        var store = new JsonStore(Path.Combine(fixture.Root, "workspace"));
        store.Write("settings.json", new ManagerSettings { Servers = [fixture.Profile], BackgroundMode = true });
        var manager = new ManagerHost(store, new OfflineRuntime(), new UnusedSteam());
        manager.Attach(new AgentParent(int.MaxValue, "missing"));
        Assert.True(manager.Settings.BackgroundMode); // The manager has observed existing preferences.
        var path = Path.Combine(store.DirectoryPath, "settings.json");
        var held = path + ".fixture-held";
        var stopped = false;
        using var cancel = new CancellationTokenSource();
        Task? monitor = null;
        try
        {
            File.Move(path, held); // Disposable replacement gap, longer than the bounded reader retry.
            monitor = manager.MonitorAsync(() => stopped = true, cancel.Token);
            await Task.Delay(100);
        }
        finally
        {
            if (File.Exists(held)) File.Move(held, path);
            cancel.Cancel();
            if (monitor is not null) await monitor;
        }
        Assert.False(stopped, "A transient preference gap was interpreted as disabled background mode and stopped the manager.");
        Assert.True(manager.Settings.BackgroundMode);
        Assert.Equal(fixture.Profile.Id, Assert.Single(manager.Settings.Servers).Id);
    }

    [Fact]
    public async Task PreferencesRemainReadableWhileAtomicallyReplaced()
    {
        using var fixture = new Fixture(); var store = new JsonStore(Path.Combine(fixture.Root, "workspace"));
        store.Write("settings.json", new ManagerSettings { Servers = [fixture.Profile] });
        await Task.WhenAll(Task.Run(() =>
        {
            for (var i = 0; i < 100; i++) store.Write("settings.json", new ManagerSettings { Servers = [fixture.Profile], Theme = i % 2 == 0 ? "Dark" : "Light" });
        }), Task.Run(() =>
        {
            for (var i = 0; i < 250; i++) Assert.Equal(fixture.Profile.Id, Assert.Single(store.Read("settings.json", () => new ManagerSettings()).Servers).Id);
        }));
    }
}
