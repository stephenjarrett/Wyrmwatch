using Wyrmwatch.Core;

namespace Wyrmwatch.Core.Tests;

public class PersistenceRaceTests
{
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
