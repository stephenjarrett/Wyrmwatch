using Wyrmwatch.Core;
using Wyrmwatch.Agent;

namespace Wyrmwatch.Core.Tests;

public class SingleServerTests
{
    private sealed class Runtime : IServerRuntime
    {
        public Dictionary<string, ServerSnapshot> States { get; } = [];
        public List<string> Starts { get; } = [];
        public List<string> Stops { get; } = [];
        public Task<ServerSnapshot> InspectAsync(ServerProfile p, CancellationToken token = default) => Task.FromResult(States.GetValueOrDefault(p.Id, ServerSnapshot.Offline));
        public Task StartAsync(ServerProfile p, CancellationToken token = default) { Starts.Add(p.Id); States[p.Id] = ServerSnapshot.Offline with { Running = true }; return Task.CompletedTask; }
        public Task StopAsync(ServerProfile p, CancellationToken token = default) { Stops.Add(p.Id); States[p.Id] = ServerSnapshot.Offline; return Task.CompletedTask; }
    }

    [Fact]
    public async Task SelectingAnotherServerDoesNotSwitchAndDisconnectCannotBypassStartGuard()
    {
        using var a = new Fixture(); using var b = new Fixture();
        var runtime = new Runtime(); var host = new ManagerHost(new(a.Root), runtime, new UnusedSteam());
        await host.SaveProfileAsync(a.Profile); await host.SaveProfileAsync(b.Profile);
        await host.ExecuteAsync(a.Profile.Id, new("start"));
        await host.SavePreferencesAsync(host.Settings with { SelectedServerId = b.Profile.Id });
        Assert.Equal([a.Profile.Id], runtime.Starts); Assert.Empty(runtime.Stops);
        await Assert.ThrowsAsync<InvalidOperationException>(() => host.ExecuteAsync(b.Profile.Id, new("start")));
        await Assert.ThrowsAsync<IOException>(() => host.SaveProfileAsync(a.Profile with { InstallPath = Path.Combine(a.Root, "relocated") }));
        await host.RemoveProfileAsync(a.Profile.Id);
        await Assert.ThrowsAsync<InvalidOperationException>(() => host.ExecuteAsync(b.Profile.Id, new("start")));
        Assert.Equal([a.Profile.Id], runtime.Starts); Assert.Empty(runtime.Stops);
        await host.SaveProfileAsync(a.Profile);
        await host.ExecuteAsync(a.Profile.Id, new("stop"));
        await host.ExecuteAsync(b.Profile.Id, new("start"));
        Assert.Equal([a.Profile.Id, b.Profile.Id], runtime.Starts); Assert.Equal([a.Profile.Id], runtime.Stops);
    }

    [Fact]
    public async Task UnknownStateOfAnotherServerBlocksLaunchWithoutChangingEitherWorld()
    {
        using var a = new Fixture(); using var b = new Fixture();
        var runtime = new Runtime(); runtime.States[a.Profile.Id] = ServerSnapshot.Offline with { Accessible = false, Players = null };
        var host = new ManagerHost(new(a.Root), runtime, new UnusedSteam());
        await host.SaveProfileAsync(a.Profile); await host.SaveProfileAsync(b.Profile);
        await Assert.ThrowsAsync<IOException>(() => host.ExecuteAsync(b.Profile.Id, new("start")));
        Assert.Empty(runtime.Starts); Assert.Empty(runtime.Stops); Assert.DoesNotContain(host.Status().Operations, o => o.Status == "Succeeded");
    }

    [Fact]
    public async Task MaintenanceRestartCannotLaunchAlongsideAnotherServer()
    {
        using var a = new Fixture(); using var b = new Fixture(); var runtime = new Runtime();
        runtime.States[a.Profile.Id] = runtime.States[b.Profile.Id] = ServerSnapshot.Offline with { Running = true };
        var service = new MaintenanceService(new SingleServerRuntime(runtime, () => [a.Profile, b.Profile]), new UnusedSteam(), new(), new(a.Root));
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.StopAsync(a.Profile, restart: true));
        Assert.Empty(runtime.Starts); Assert.Equal([a.Profile.Id], runtime.Stops); Assert.True(runtime.States[b.Profile.Id].Running);
    }
}
