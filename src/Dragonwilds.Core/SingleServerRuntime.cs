namespace Dragonwilds.Core;

// All starts, including maintenance restarts, use fresh observations of saved connections.
public sealed class SingleServerRuntime(IServerRuntime inner, Func<IReadOnlyList<ServerProfile>> connections) : IServerRuntime
{
    private readonly SemaphoreSlim starts = new(1, 1);
    public Task<ServerSnapshot> InspectAsync(ServerProfile profile, CancellationToken token = default) => inner.InspectAsync(profile, token);
    public Task StopAsync(ServerProfile profile, CancellationToken token = default) => inner.StopAsync(profile, token);

    public async Task StartAsync(ServerProfile profile, CancellationToken token = default)
    {
        await starts.WaitAsync(token);
        try
        {
            foreach (var other in connections().Where(p => p.Id != profile.Id))
            {
                other.Validate();
                var state = await inner.InspectAsync(other, token);
                if (!state.Accessible) throw new IOException("Cannot confirm that another saved server is stopped. Check its status before starting this server.");
                if (state.Running) throw new InvalidOperationException("Another saved server is running. Stop it before starting this server. Selecting a different entry does not stop the active server.");
            }
            await inner.StartAsync(profile, token);
        }
        finally { starts.Release(); }
    }
}
