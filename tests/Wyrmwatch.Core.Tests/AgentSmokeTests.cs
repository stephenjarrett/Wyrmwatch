using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Wyrmwatch.Core;

namespace Wyrmwatch.Core.Tests;

[Collection(NativeProcessCollection.Name)]
public class AgentSmokeTests
{
    [Fact]
    public async Task LocalApiRequiresHostSecretAndRejectsUntrustedOrigins()
    {
        await using var agent = await AgentFixture.StartAsync();
        using var anonymous = new HttpClient { BaseAddress = agent.Admin.BaseAddress };
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("api/status")).StatusCode);
        using var wrongKey = agent.Client(AccessPolicy.NewSecret());
        Assert.Equal(HttpStatusCode.Unauthorized, (await wrongKey.GetAsync("admin/settings")).StatusCode);
        agent.Admin.DefaultRequestHeaders.Add("Origin", "https://untrusted.example");
        Assert.Equal(HttpStatusCode.Forbidden, (await agent.Admin.GetAsync("api/status")).StatusCode);
        agent.Admin.DefaultRequestHeaders.Remove("Origin");
        Assert.Equal(2, (await agent.Admin.GetFromJsonAsync<AgentStatus>("api/status"))!.Servers.Count);
        Assert.Equal("untouched", File.ReadAllText(agent.Sentinel));
    }

    [Fact]
    public async Task LegacyRemoteSettingsAreIgnoredAndDisconnectPreservesFiles()
    {
        await using var agent = await AgentFixture.StartAsync(new { Enabled = true, Port = 8843 });
        Assert.Equal(HttpStatusCode.NotFound, (await agent.Admin.GetAsync("admin/remote")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await agent.Admin.GetAsync("admin/access")).StatusCode);
        Assert.Null((await agent.Admin.GetFromJsonAsync<AgentStatus>("api/status"))!.RemoteAddress);
        Assert.True(agent.Admin.BaseAddress!.IsLoopback);
        Assert.Contains("8843", File.ReadAllText(Path.Combine(agent.Root, "remote.json")));
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
        await using var agent = await AgentFixture.StartAsync(prepare: fixture =>
        {
            fixture.First = fixture.First with { AutoBackup = true };
            var store = new JsonStore(fixture.Root);
            store.Write("settings.json", new ManagerSettings { Servers = [fixture.First, fixture.Second] });
            store.Write("schedules.json", new Dictionary<string, ScheduleState> { [fixture.First.Id] = new(NextBackup: DateTimeOffset.UtcNow.AddSeconds(6)) });
        });
        using var second = agent.StartProcess();
        var secondOutput = second.StandardOutput.ReadToEndAsync();
        var secondError = second.StandardError.ReadToEndAsync();
        var secondExited = second.WaitForExit(10000);
        if (!secondExited) { second.Kill(); await second.WaitForExitAsync(); } // Only this disposable duplicate agent.
        Assert.True(secondExited, $"Duplicate fixture agent did not exit: {await secondOutput}\n{await secondError}");
        Assert.Equal(1, second.ExitCode);
        Assert.Contains("A background manager already owns this workspace", await secondError);
        Assert.DoesNotContain("Unhandled exception", await secondError);
        await EnsureSuccessWithBodyAsync(await agent.Admin.PutAsJsonAsync("admin/preferences", new ManagerSettings { BackgroundMode = true }));
        Assert.True((await agent.Admin.PostAsJsonAsync("admin/attach", new AgentParent(int.MaxValue, "missing"))).IsSuccessStatusCode);
        var backups = new BackupEngine();
        using (var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(25)))
            try
            {
                while (backups.List(agent.First).Count == 0) await Task.Delay(200, deadline.Token);
            }
            catch (OperationCanceledException) when (deadline.IsCancellationRequested)
            {
                var activity = Path.Combine(agent.Root, "activity.log");
                var schedules = Path.Combine(agent.Root, "schedules.json");
                Assert.Fail($"Scheduled backup timed out; agent exited={agent.Process.HasExited}. " +
                    $"Schedules: {(File.Exists(schedules) ? File.ReadAllText(schedules) : "missing")}\n" +
                    $"Activity: {(File.Exists(activity) ? File.ReadAllText(activity) : "missing")}");
            }
        Assert.False(agent.Process.HasExited); Assert.True((await agent.Admin.GetFromJsonAsync<AgentStatus>("api/status"))!.Background);
        var backup = Assert.Single(backups.List(agent.First)); await backups.VerifyAsync(backup.Path, agent.First);
        DateTimeOffset? next;
        using (var completed = new CancellationTokenSource(TimeSpan.FromSeconds(15)))
            while (true)
            {
                var status = (await agent.Admin.GetFromJsonAsync<AgentStatus>("api/status", completed.Token))!;
                next = status.Servers.Single(s => s.Id == agent.First.Id).Schedule.NextBackup;
                // The archive is published before the scheduler persists its next deadline.
                if (!status.Busy && next > DateTimeOffset.UtcNow) break;
                await Task.Delay(100, completed.Token);
            }
        await agent.RestartAsync(); await Task.Delay(3500);
        Assert.Equal(next, (await agent.Admin.GetFromJsonAsync<AgentStatus>("api/status"))!.Servers.Single(s => s.Id == agent.First.Id).Schedule.NextBackup);
        Assert.Single(backups.List(agent.First));
        Assert.True((await agent.Admin.PostAsJsonAsync("admin/attach", new AgentParent(int.MaxValue, "missing"))).IsSuccessStatusCode);
        Assert.True((await agent.Admin.PutAsJsonAsync("admin/preferences", new ManagerSettings { BackgroundMode = false })).IsSuccessStatusCode);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(12)); await agent.Process.WaitForExitAsync(timeout.Token);
        Assert.Equal(0, agent.Process.ExitCode);
        Assert.Equal("untouched", File.ReadAllText(agent.Sentinel));
        Assert.Equal(2, new JsonStore(agent.Root).Read("settings.json", () => new ManagerSettings()).Servers.Count);
    }

    [Fact]
    public async Task LocalBackupAndRestoreRequireConfirmedNameAndPreserveOtherServer()
    {
        await using var agent = await AgentFixture.StartAsync();
        var otherWorld = Path.Combine(agent.Second.SavedPath, "SaveGames", "other.sav");
        Directory.CreateDirectory(Path.GetDirectoryName(otherWorld)!); File.WriteAllText(otherWorld, "other world");
        await EnsureSuccessWithBodyAsync(await agent.Admin.PostAsJsonAsync($"api/servers/{agent.First.Id}/actions", new ServerAction("backup")));
        var backup = Assert.Single(new BackupEngine().List(agent.First));
        var restore = new ServerAction("restore", Path.GetFileName(backup.Path), Confirmation: agent.First.Name);
        File.WriteAllText(agent.Sentinel, "new progress");
        Assert.Equal(HttpStatusCode.BadRequest, (await agent.Admin.PostAsJsonAsync($"api/servers/{agent.First.Id}/actions", restore with { Confirmation = "wrong name" })).StatusCode);
        Assert.Equal("new progress", File.ReadAllText(agent.Sentinel));
        await EnsureSuccessWithBodyAsync(await agent.Admin.PostAsJsonAsync($"api/servers/{agent.First.Id}/actions", new ServerAction("verify", Path.GetFileName(backup.Path))));
        await EnsureSuccessWithBodyAsync(await agent.Admin.PostAsJsonAsync($"api/servers/{agent.First.Id}/actions", restore));
        Assert.Equal("untouched", File.ReadAllText(agent.Sentinel)); Assert.Equal("other world", File.ReadAllText(otherWorld));
        Assert.Empty(new BackupEngine().List(agent.Second));
        var status = await agent.Admin.GetFromJsonAsync<AgentStatus>("api/status");
        Assert.All(status!.Operations, o => Assert.Equal(agent.First.Id, o.ServerId));
    }

    [Fact]
    public async Task ImportEndpointIsOwnerOnlyAndDisablesAutomationWithoutChangingFiles()
    {
        await using var agent = await AgentFixture.StartAsync();
        using var f = new Fixture(); File.WriteAllText(f.Profile.Launcher, "fixture");
        File.WriteAllText(f.Profile.ConfigPath, File.ReadAllText(f.Profile.ConfigPath).Replace("Port=7777", "Port=7780"));
        var profile = ExistingServerImport.Inspect(f.Profile.Launcher, f.Profile.SavedPath, f.Profile.BackupPath);
        var config = File.ReadAllBytes(profile.ConfigPath);
        using var anonymous = new HttpClient { BaseAddress = agent.Admin.BaseAddress };
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.PostAsJsonAsync("admin/import", profile)).StatusCode);
        var result = await agent.Admin.PostAsJsonAsync("admin/import", profile with { AutoBackup = true, AutoUpdate = true }); result.EnsureSuccessStatusCode();
        var imported = await result.Content.ReadFromJsonAsync<ServerProfile>();
        Assert.False(imported!.AutoBackup); Assert.False(imported.AutoUpdate); Assert.Equal(7780, imported.Port);
        Assert.Equal(config, File.ReadAllBytes(profile.ConfigPath)); Assert.Empty(new BackupEngine().List(profile));
    }

    private static async Task EnsureSuccessWithBodyAsync(HttpResponseMessage response)
    {
        using (response)
            Assert.True(response.IsSuccessStatusCode,
                $"{response.RequestMessage?.RequestUri}: HTTP {(int)response.StatusCode} {response.StatusCode}; {await response.Content.ReadAsStringAsync()}");
    }

    private sealed class AgentFixture : IAsyncDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "wyrmwatch-agent-test-" + Guid.NewGuid().ToString("N"));
        public ServerProfile First { get; set; } = null!;
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
        public static async Task<AgentFixture> StartAsync(object? remote = null, Action<AgentFixture>? prepare = null)
        {
            var fixture = new AgentFixture(); Directory.CreateDirectory(fixture.Root);
            fixture.First = new() { Id = "first", Name = "First", InstallPath = Path.Combine(fixture.Root, "first"), BackupPath = Path.Combine(fixture.Root, "backups-first") };
            fixture.Second = new() { Id = "second", Name = "Second", InstallPath = Path.Combine(fixture.Root, "second"), BackupPath = Path.Combine(fixture.Root, "backups-second"), Port = 7778 };
            Directory.CreateDirectory(Path.GetDirectoryName(fixture.Sentinel)!); File.WriteAllText(fixture.Sentinel, "untouched");
            new JsonStore(fixture.Root).Write("settings.json", new ManagerSettings { Servers = [fixture.First, fixture.Second] });
            if (remote is not null) new JsonStore(fixture.Root).Write("remote.json", remote);
            prepare?.Invoke(fixture);
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
        public async Task RestartAsync()
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            while (true)
            {
                using var response = await Admin.PostAsync("admin/shutdown", null, timeout.Token);
                if (response.StatusCode != HttpStatusCode.Conflict) { response.EnsureSuccessStatusCode(); break; }
                // A new scheduler tick may acquire the operation gate after an idle observation.
                await Task.Delay(100, timeout.Token);
            }
            await Process.WaitForExitAsync(timeout.Token);
            Process.Dispose(); Admin.Dispose(); Process = StartProcess();
            while (true)
            {
                if (Process.HasExited) throw new IOException(await Process.StandardError.ReadToEndAsync());
                var endpoint = new JsonStore(Root).Read<AgentEndpoint?>("agent.json", () => null);
                if (endpoint?.ProcessId == Process.Id)
                {
                    Admin = new HttpClient { BaseAddress = new Uri(endpoint.Address), Timeout = TimeSpan.FromSeconds(15) };
                    Admin.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", endpoint.Secret); return;
                }
                await Task.Delay(100, timeout.Token);
            }
        }
        public async ValueTask DisposeAsync()
        {
            if (Process is not null && !Process.HasExited)
            {
                if (Admin is not null)
                    for (var i = 0; i < 20 && !Process.HasExited; i++) { try { using var response = await Admin.PostAsync("admin/shutdown", null); if (response.IsSuccessStatusCode) break; } catch (Exception error) when (error is HttpRequestException or ObjectDisposedException) { break; } await Task.Delay(100); }
                if (!Process.WaitForExit(5000)) Process.Kill(); // Only the disposable agent started by this fixture.
                await Process.WaitForExitAsync();
            }
            Process?.Dispose(); Admin?.Dispose();
            if (SafePaths.Within(Root, Path.GetTempPath()) && Path.GetFileName(Root).StartsWith("wyrmwatch-agent-test-", StringComparison.Ordinal) && Directory.Exists(Root)) Directory.Delete(Root, true);
        }
    }
}
