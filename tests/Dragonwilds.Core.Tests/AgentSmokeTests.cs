using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Dragonwilds.Core;

namespace Dragonwilds.Core.Tests;

public class AgentSmokeTests
{
    [Fact]
    public async Task AccessKeysAreScopedRevocableAndNeverAuthorizeOwnerSettings()
    {
        await using var agent = await AgentFixture.StartAsync();
        using var anonymous = new HttpClient { BaseAddress = agent.Admin.BaseAddress };
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("api/status")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await anonymous.GetAsync("/")).StatusCode);
        using var page = await anonymous.GetAsync("app.js"); Assert.Contains("default-src 'self'", page.Headers.GetValues("Content-Security-Policy").Single());
        var issued = await (await agent.Admin.PostAsJsonAsync("admin/access", new CreateAccessGrant("Viewer", "Viewer", agent.First.Id))).Content.ReadFromJsonAsync<IssuedAccessGrant>();
        Assert.NotNull(issued);
        using var guest = agent.Client(issued.Secret);
        var status = await guest.GetFromJsonAsync<AgentStatus>("api/status"); Assert.Single(status!.Servers); Assert.Equal(agent.First.Id, status.Servers[0].Id); Assert.Empty(status.Servers[0].Actions);
        Assert.Equal(HttpStatusCode.Forbidden, (await guest.PostAsJsonAsync($"api/servers/{agent.First.Id}/actions", new ServerAction("start"))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await guest.GetAsync($"api/servers/{agent.Second.Id}/backups")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await guest.GetAsync("admin/settings")).StatusCode);
        guest.DefaultRequestHeaders.Add("Origin", "https://untrusted.example");
        Assert.Equal(HttpStatusCode.Forbidden, (await guest.GetAsync("api/status")).StatusCode); guest.DefaultRequestHeaders.Remove("Origin");
        Assert.DoesNotContain(issued.Secret, File.ReadAllText(Path.Combine(agent.Root, "access-grants.json")));
        Assert.True((await agent.Admin.DeleteAsync("admin/access/" + issued.Grant.Id)).IsSuccessStatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await guest.GetAsync("api/status")).StatusCode);
        Assert.Empty((await agent.Admin.GetFromJsonAsync<AgentStatus>("api/status"))!.Operations);
        Assert.Equal("untouched", File.ReadAllText(agent.Sentinel));
    }

    [Fact]
    public async Task InvalidRemoteSettingsKeepLocalManagementAvailableAndDisconnectPreservesFiles()
    {
        await using var agent = await AgentFixture.StartAsync(new RemoteSettings(true, 0));
        var remote = await agent.Admin.GetFromJsonAsync<RemoteInfo>("admin/remote");
        Assert.NotNull(remote!.Warning); Assert.Null(remote.Address);
        Assert.True((await agent.Admin.DeleteAsync("admin/profiles/" + agent.First.Id)).IsSuccessStatusCode);
        Assert.Equal("untouched", File.ReadAllText(agent.Sentinel));
        Assert.Single((await agent.Admin.GetFromJsonAsync<ManagerSettings>("admin/settings"))!.Servers);
        var reconnected = await (await agent.Admin.PutAsJsonAsync("admin/profiles", agent.First with { Id = "new-connection" })).Content.ReadFromJsonAsync<ServerProfile>();
        Assert.Equal(agent.First.Id, reconnected!.Id); Assert.False(reconnected.AutoUpdate); Assert.False(reconnected.AutoBackup);
        Assert.Equal("untouched", File.ReadAllText(agent.Sentinel));
    }

    [Fact]
    public async Task BackgroundModeSurvivesDesktopExitAndDisabledModeClosesWithoutChangingSaves()
    {
        await using var agent = await AgentFixture.StartAsync();
        using var second = agent.StartProcess();
        Assert.True(second.WaitForExit(10000)); Assert.NotEqual(0, second.ExitCode);
        Assert.True((await agent.Admin.PutAsJsonAsync("admin/preferences", new ManagerSettings { BackgroundMode = true })).IsSuccessStatusCode);
        Assert.True((await agent.Admin.PostAsJsonAsync("admin/attach", new AgentParent(int.MaxValue, "missing"))).IsSuccessStatusCode);
        await Task.Delay(3500);
        Assert.False(agent.Process.HasExited); Assert.True((await agent.Admin.GetFromJsonAsync<AgentStatus>("api/status"))!.Background);
        Assert.True((await agent.Admin.PutAsJsonAsync("admin/preferences", new ManagerSettings { BackgroundMode = false })).IsSuccessStatusCode);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(12)); await agent.Process.WaitForExitAsync(timeout.Token);
        Assert.Equal(0, agent.Process.ExitCode);
        Assert.Equal("untouched", File.ReadAllText(agent.Sentinel));
        Assert.Equal(2, new JsonStore(agent.Root).Read("settings.json", () => new ManagerSettings()).Servers.Count);
    }

    private sealed class AgentFixture : IAsyncDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "wyrmwatch-agent-test-" + Guid.NewGuid().ToString("N"));
        public ServerProfile First { get; private set; } = null!;
        public ServerProfile Second { get; private set; } = null!;
        public string Sentinel => Path.Combine(First.SavedPath, "SaveGames", "sentinel.sav");
        public Process Process { get; private set; } = null!;
        public HttpClient Admin { get; private set; } = null!;
        public HttpClient Client(string secret)
        {
            var client = new HttpClient { BaseAddress = Admin.BaseAddress, Timeout = TimeSpan.FromSeconds(15) };
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", secret); return client;
        }
        public Process StartProcess()
        {
            var host = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH");
            if (string.IsNullOrWhiteSpace(host) || !File.Exists(host)) host = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(typeof(object).Assembly.Location)!, "../../..", OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet"));
            var start = new ProcessStartInfo(host) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
            start.ArgumentList.Add(Path.Combine(AppContext.BaseDirectory, "agent", "Wyrmwatch.Agent.dll"));
            using var owner = System.Diagnostics.Process.GetCurrentProcess();
            foreach (var argument in new[] { "--workspace", Root, "--parent", owner.Id.ToString(), ProcessLifetime.Token(owner) }) start.ArgumentList.Add(argument);
            return System.Diagnostics.Process.Start(start)!;
        }
        public static async Task<AgentFixture> StartAsync(RemoteSettings? remote = null)
        {
            var fixture = new AgentFixture(); Directory.CreateDirectory(fixture.Root);
            fixture.First = new() { Id = "first", Name = "First", InstallPath = Path.Combine(fixture.Root, "first"), BackupPath = Path.Combine(fixture.Root, "backups-first") };
            fixture.Second = new() { Id = "second", Name = "Second", InstallPath = Path.Combine(fixture.Root, "second"), BackupPath = Path.Combine(fixture.Root, "backups-second") };
            Directory.CreateDirectory(Path.GetDirectoryName(fixture.Sentinel)!); File.WriteAllText(fixture.Sentinel, "untouched");
            new JsonStore(fixture.Root).Write("settings.json", new ManagerSettings { Servers = [fixture.First, fixture.Second] });
            if (remote is not null) new JsonStore(fixture.Root).Write("remote.json", remote);
            fixture.Process = fixture.StartProcess();
            try
            {
                AgentEndpoint? endpoint = null;
                for (var i = 0; i < 150; i++)
                {
                    if (fixture.Process.HasExited) throw new IOException(await fixture.Process.StandardError.ReadToEndAsync());
                    endpoint = new JsonStore(fixture.Root).Read<AgentEndpoint?>("agent.json", () => null);
                    if (endpoint is not null) break; await Task.Delay(100);
                }
                Assert.NotNull(endpoint);
                fixture.Admin = new HttpClient { BaseAddress = new Uri(endpoint.Address), Timeout = TimeSpan.FromSeconds(15) };
                fixture.Admin.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", endpoint.Secret); return fixture;
            }
            catch { await fixture.DisposeAsync(); throw; }
        }
        public async ValueTask DisposeAsync()
        {
            if (Process is not null && !Process.HasExited)
            {
                if (Admin is not null)
                    for (var i = 0; i < 20 && !Process.HasExited; i++) { try { using var response = await Admin.PostAsync("admin/shutdown", null); if (response.IsSuccessStatusCode) break; } catch (HttpRequestException) { break; } await Task.Delay(100); }
                if (!Process.WaitForExit(5000)) Process.Kill(); // Only the disposable agent started by this fixture.
                await Process.WaitForExitAsync();
            }
            Process?.Dispose(); Admin?.Dispose();
            if (SafePaths.Within(Root, Path.GetTempPath()) && Path.GetFileName(Root).StartsWith("wyrmwatch-agent-test-", StringComparison.Ordinal) && Directory.Exists(Root)) Directory.Delete(Root, true);
        }
    }
}
