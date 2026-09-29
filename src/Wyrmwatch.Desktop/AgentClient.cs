using System.Diagnostics;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Dragonwilds.Core;

namespace Wyrmwatch.Desktop;

public sealed class CachedHistory
{
    internal IReadOnlyList<OperationRecord> Records = [];
    public IReadOnlyList<OperationRecord> Snapshot() => Records;
}

public sealed class AgentClient(string workspace) : IDisposable
{
    private readonly JsonStore store = new(workspace);
    private readonly SemaphoreSlim connectionGate = new(1, 1);
    private HttpClient? http;
    private AgentEndpoint? endpoint;
    private int requests;
    private bool suspended;
    private string? lastLog;
    private AgentStatus status = new(false, "", [], [], [], false, null);
    public bool Busy => requests > 0 || status.Busy;
    public bool Background => status.Background;
    public bool PersistentHost => status.PersistentHost;
    public string? RemoteAddress => status.RemoteAddress;
    public CachedHistory History { get; } = new();
    public event Action<string>? Log;
    public event Action? Changed;
    public ScheduleState Schedule(string id) => status.Servers.FirstOrDefault(s => s.Id == id)?.Schedule ?? new();
    public async Task EnsureStartedAsync(CancellationToken token = default)
    {
        await connectionGate.WaitAsync(token);
        try
        {
            if (suspended) throw new IOException("The background manager is changing versions or shutting down.");
            if (http is not null && endpoint is not null && Alive(endpoint)) return;
            http?.Dispose(); http = null;
            AgentEndpoint? found = null;
            try { found = store.Read<AgentEndpoint?>("agent.json", () => null); } catch (IOException) { }
            if (found is null || !Alive(found))
            {
                var executable = Path.Combine(AppContext.BaseDirectory, "agent", OperatingSystem.IsWindows() ? "Wyrmwatch.Agent.exe" : "Wyrmwatch.Agent");
                if (!File.Exists(executable)) throw new IOException("The background manager is missing. Extract the entire Wyrmwatch package.");
                var start = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = Path.GetDirectoryName(executable)! };
                start.ArgumentList.Add("--workspace"); start.ArgumentList.Add(workspace);
                using var owner = Process.GetCurrentProcess();
                start.ArgumentList.Add("--parent"); start.ArgumentList.Add(owner.Id.ToString()); start.ArgumentList.Add(owner.StartTime.ToUniversalTime().Ticks.ToString());
                using var process = Process.Start(start) ?? throw new IOException("Could not start the background manager.");
                for (var attempt = 0; attempt < 100; attempt++)
                {
                    await Task.Delay(100, token);
                    try { found = store.Read<AgentEndpoint?>("agent.json", () => null); } catch (IOException) { }
                    if (found is not null && Alive(found)) break;
                    if (process.HasExited) throw new IOException("The background manager could not start. Check that the selected ports and workspace are available.");
                }
            }
            if (found is null || !Alive(found)) throw new IOException("The background manager did not become ready in time.");
            var address = new Uri(found.Address);
            if (address.Scheme != "http" || !address.IsLoopback || address.AbsolutePath != "/" || address.UserInfo.Length != 0) throw new IOException("The local manager endpoint is invalid.");
            endpoint = found;
            http = new HttpClient { BaseAddress = address, Timeout = TimeSpan.FromMinutes(45) };
            http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", found.Secret);
            using var current = Process.GetCurrentProcess();
            using var attached = await http.PostAsJsonAsync("admin/attach", new ProcessIdentity(current.Id, current.StartTime.ToUniversalTime(), Environment.ProcessPath ?? ""), token);
            await CheckAsync(attached, token);
        }
        finally { connectionGate.Release(); }
    }
    private static bool Alive(AgentEndpoint endpoint)
    {
        try { using var process = Process.GetProcessById(endpoint.ProcessId); return process.StartTime.ToUniversalTime().Ticks == endpoint.StartUtcTicks && process.ProcessName == "Wyrmwatch.Agent"; }
        catch (Exception error) when (error is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception) { return false; }
    }
    private static async Task CheckAsync(HttpResponseMessage response, CancellationToken token = default)
    {
        if (response.IsSuccessStatusCode) return;
        ActionResult? result = null;
        try { result = await response.Content.ReadFromJsonAsync<ActionResult>(token); } catch (JsonException) { }
        throw new IOException(result?.Message ?? $"Background manager returned {(int)response.StatusCode}.");
    }
    public async Task<T> GetAsync<T>(string route, CancellationToken token = default)
    {
        await EnsureStartedAsync(token);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token); deadline.CancelAfter(TimeSpan.FromSeconds(15));
        using var response = await http!.GetAsync(route, deadline.Token); await CheckAsync(response, deadline.Token);
        return await response.Content.ReadFromJsonAsync<T>(deadline.Token) ?? throw new IOException("The manager returned an empty response.");
    }
    public async Task SendAsync(string route, HttpMethod method, object? body = null)
    {
        await EnsureStartedAsync();
        using var request = new HttpRequestMessage(method, route);
        if (body is not null) request.Content = JsonContent.Create(body, body.GetType());
        using var response = await http!.SendAsync(request); await CheckAsync(response);
    }
    public async Task<T> PostAsync<T>(string route, object body)
    {
        await EnsureStartedAsync();
        using var response = await http!.PostAsync(route, JsonContent.Create(body, body.GetType())); await CheckAsync(response);
        return await response.Content.ReadFromJsonAsync<T>() ?? throw new IOException("The manager returned an empty response.");
    }
    public async Task RefreshAsync(CancellationToken token = default)
    {
        status = await GetAsync<AgentStatus>("api/status", token); History.Records = status.Operations;
        var start = lastLog is null ? Math.Max(0, status.Log.Count - 20) : status.Log.LastIndexOf(lastLog) + 1;
        foreach (var line in status.Log.Skip(start)) Log?.Invoke(line);
        lastLog = status.Log.LastOrDefault(); Changed?.Invoke();
    }
    public async Task<ServerSnapshot> ObserveAsync(ServerProfile profile, CancellationToken token = default)
    {
        await RefreshAsync(token);
        return status.Servers.FirstOrDefault(p => p.Id == profile.Id)?.State ?? ServerSnapshot.Offline with { Accessible = false, Players = null, ActivityReason = "Waiting for the background manager." };
    }
    private async Task<string> ActionAsync(ServerProfile profile, ServerAction action)
    {
        Interlocked.Increment(ref requests); Changed?.Invoke();
        try { return (await PostAsync<ActionResult>($"api/servers/{Uri.EscapeDataString(profile.Id)}/actions", action)).Message; }
        finally { Interlocked.Decrement(ref requests); try { await RefreshAsync(); } catch (IOException) { } Changed?.Invoke(); }
    }
    public Task<string> StartAsync(ServerProfile p) => ActionAsync(p, new("start"));
    public Task<string> StopAsync(ServerProfile p, bool restart = false) => ActionAsync(p, new(restart ? "restart" : "stop"));
    public Task<string> BackupAsync(ServerProfile p) => ActionAsync(p, new("backup"));
    public Task<string> CheckAsync(ServerProfile p) => ActionAsync(p, new("check"));
    public Task<string> UpdateAsync(ServerProfile p) => ActionAsync(p, new("update"));
    public Task<string> InstallAsync(ServerProfile p) => ActionAsync(p, new("install"));
    public Task<string> RestoreAsync(ServerProfile p, string archive) => ActionAsync(p, new("restore", Path.GetFileName(archive), Confirmation: p.Name));
    public Task<string> SaveConfigurationAsync(ServerProfile p, IReadOnlyDictionary<string, string> values) => ActionAsync(p, new("configuration", Values: values.ToDictionary()));
    public async Task<ServerProfile> SaveProfileAsync(ServerProfile p)
    {
        await EnsureStartedAsync();
        using var response = await http!.PutAsJsonAsync("admin/profiles", p); await CheckAsync(response);
        return await response.Content.ReadFromJsonAsync<ServerProfile>() ?? throw new IOException("The manager returned an empty connection.");
    }
    public Task SavePreferencesAsync(ManagerSettings settings) => SendAsync("admin/preferences", HttpMethod.Put, settings);
    public async Task StopAgentAsync()
    {
        await EnsureStartedAsync();
        await connectionGate.WaitAsync();
        try
        {
            suspended = true;
            using var response = await http!.PostAsync("admin/shutdown", null); await CheckAsync(response);
            var current = endpoint;
            for (var i = 0; current is not null && Alive(current) && i < 100; i++) await Task.Delay(100);
            if (current is not null && Alive(current)) throw new IOException("The background manager is still shutting down. It has not been force-stopped.");
            http?.Dispose(); http = null; endpoint = null;
        }
        catch { suspended = false; throw; }
        finally { connectionGate.Release(); }
    }
    public void Resume() => suspended = false;
    public void Dispose() { http?.Dispose(); connectionGate.Dispose(); }
}
