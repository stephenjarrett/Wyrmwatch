using System.Collections.Concurrent;
using System.Diagnostics;
using Dragonwilds.Core;
using Dragonwilds.Windows;

namespace Wyrmwatch.Agent;

public sealed class ManagerHost
{
    public const string Version = "0.2.0";
    private readonly JsonStore store;
    private readonly IServerRuntime runtime;
    private readonly SteamClient steam;
    private readonly BackupEngine backups = new();
    private readonly MaintenanceService maintenance;
    private readonly SemaphoreSlim operations = new(1, 1);
    private readonly ConcurrentDictionary<string, ServerSnapshot> snapshots = new();
    private readonly ConcurrentQueue<string> log = new();
    private readonly object logLock = new();
    private readonly object grantsLock = new();
    private AgentParent? parent;
    private Task? scheduler;
    private bool stopping;
    public string? RemoteAddress { get; set; }
    public bool PersistentHost { get; set; }
    public bool Busy => operations.CurrentCount == 0 || maintenance.Busy;
    public ManagerHost(JsonStore store)
    {
        this.store = store;
        var helper = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "Dragonwilds.Signal.exe"));
        runtime = OperatingSystem.IsWindows() ? new WindowsRuntime(store, helper) : new LinuxRuntime(store);
        steam = new SteamClient(Path.Combine(store.DirectoryPath, "tools"));
        maintenance = new(runtime, steam, backups, store);
        maintenance.Log += WriteLog;
    }
    public ManagerSettings Settings => store.Read("settings.json", () => new ManagerSettings());
    public void Attach(AgentParent identity) => parent = identity;
    public bool TryPrepareShutdown()
    {
        if (!operations.Wait(0)) return false;
        stopping = true; // Retain the gate: no new operation may race shutdown.
        return true;
    }
    public void WriteLog(string text)
    {
        var line = $"{DateTimeOffset.Now:HH:mm:ss}  {text}";
        log.Enqueue(line); while (log.Count > 250) log.TryDequeue(out _);
        lock (logLock)
        {
            try
            {
                var path = Path.Combine(store.DirectoryPath, "activity.log");
                if (File.Exists(path) && new FileInfo(path).Length > 2_000_000) File.Move(path, path + ".1", true);
                File.AppendAllText(path, line + Environment.NewLine);
            }
            catch (IOException) { }
        }
    }
    public AgentStatus Status()
    {
        var settings = Settings;
        return new(Busy, Version, settings.Servers.Select(p => new ManagedServer(p.Id, p.Name,
            snapshots.GetValueOrDefault(p.Id, ServerSnapshot.Offline with { Accessible = false, Players = null, ActivityReason = "Waiting for the first observation." }),
            maintenance.Schedule(p.Id), steam.InstalledBuild(p))).ToList(), maintenance.History.Snapshot().Take(100).ToList(), log.ToList(), settings.BackgroundMode || PersistentHost, RemoteAddress, PersistentHost);
    }
    public ServerProfile Profile(string id) => Settings.Servers.SingleOrDefault(p => p.Id == id) ?? throw new KeyNotFoundException("Server connection not found.");
    public async Task<string> ExecuteAsync(string id, ServerAction request)
    {
        if (!await operations.WaitAsync(0)) throw new InvalidOperationException("Another operation is still running.");
        try
        {
            var p = Profile(id); p.Validate();
            if (request.Action == "restore" && request.Confirmation != p.Name) throw new ArgumentException("Confirm the server name before restoring.");
            return request.Action switch
            {
                "start" => await maintenance.StartAsync(p),
                "stop" => await maintenance.StopAsync(p),
                "restart" => await maintenance.StopAsync(p, true),
                "backup" => await maintenance.BackupAsync(p),
                "check" => await maintenance.CheckAsync(p),
                "update" => await maintenance.UpdateAsync(p),
                "install" => await maintenance.InstallAsync(p),
                "configuration" => await maintenance.SaveConfigurationAsync(p, request.Values ?? throw new ArgumentException("Missing configuration values.")),
                "restore" => await maintenance.RestoreAsync(p, Archive(p, request.Archive)),
                "verify" => await VerifyAsync(p, request.Archive),
                _ => throw new ArgumentException("Unknown operation.")
            };
        }
        finally { operations.Release(); }
    }
    private string Archive(ServerProfile profile, string? name) => backups.List(profile).SingleOrDefault(b => Path.GetFileName(b.Path) == name)?.Path ?? throw new ArgumentException("Choose a backup belonging to this server.");
    private async Task<string> VerifyAsync(ServerProfile profile, string? name)
    {
        await backups.VerifyAsync(Archive(profile, name), profile);
        return "Every archived file matches its recorded hash.";
    }
    public object BackupList(string id) => backups.List(Profile(id)).Select(b => new { Id = Path.GetFileName(b.Path), b.Manifest.Created, b.Manifest.Reason, b.Bytes, Files = b.Manifest.Files.Count }).ToArray();
    public async Task<ServerProfile> SaveProfileAsync(ServerProfile profile)
    {
        profile.Validate();
        await ChangeSettingsAsync(settings =>
        {
            var previous = settings.DisconnectedServers.FirstOrDefault(p => SafePaths.Same(p.InstallPath, profile.InstallPath));
            if (previous is not null && settings.Servers.All(p => p.Id != profile.Id)) profile = profile with { Id = previous.Id };
            if (settings.Servers.Any(p => p.Id != profile.Id && SafePaths.Same(p.InstallPath, profile.InstallPath))) throw new ArgumentException("This installation is already connected.");
            var existing = settings.Servers.SingleOrDefault(p => p.Id == profile.Id);
            var profiles = settings.Servers.Where(p => p.Id != profile.Id).ToList(); profiles.Add(profile);
            if (existing is null || existing.AutoUpdate != profile.AutoUpdate || existing.AutoBackup != profile.AutoBackup || existing.UpdateMinutes != profile.UpdateMinutes || existing.BackupHours != profile.BackupHours) maintenance.ResetSchedule(profile.Id);
            return settings with { Servers = profiles, DisconnectedServers = settings.DisconnectedServers.Where(p => p.Id != profile.Id).ToList(), SelectedServerId = profile.Id };
        });
        return profile;
    }
    public Task RemoveProfileAsync(string id) => ChangeSettingsAsync(settings =>
    {
        var profile = settings.Servers.SingleOrDefault(p => p.Id == id) ?? throw new KeyNotFoundException("Server connection not found.");
        return settings with { Servers = settings.Servers.Where(p => p.Id != id).ToList(), DisconnectedServers = [.. settings.DisconnectedServers.Where(p => p.Id != id), profile with { AutoUpdate = false, AutoBackup = false }], SelectedServerId = settings.SelectedServerId == id ? null : settings.SelectedServerId };
    });
    public Task SavePreferencesAsync(ManagerSettings preferences) => ChangeSettingsAsync(settings => settings with
    {
        Theme = preferences.Theme, CloseToTray = preferences.CloseToTray, LaunchAtLogin = preferences.LaunchAtLogin,
        BackgroundMode = preferences.BackgroundMode, Language = preferences.Language, CheckAppUpdates = preferences.CheckAppUpdates,
        SelectedServerId = preferences.SelectedServerId
    });
    private async Task ChangeSettingsAsync(Func<ManagerSettings, ManagerSettings> change)
    {
        if (!await operations.WaitAsync(0)) throw new InvalidOperationException("Wait for the current operation before editing connections or preferences.");
        try { store.Write("settings.json", change(Settings)); }
        finally { operations.Release(); }
    }
    public List<AccessGrant> Grants() { lock (grantsLock) return store.Read("access-grants.json", () => new List<AccessGrant>()); }
    public IssuedAccessGrant Issue(CreateAccessGrant request)
    {
        if (request.Role is not ("Viewer" or "Operator" or "Maintainer") || request.Days is < 1 or > 365 || string.IsNullOrWhiteSpace(request.Name) || request.Name.Length > 80) throw new ArgumentException("Choose a name, role, and lifetime from 1 to 365 days.");
        if (request.ServerId is not null) _ = Profile(request.ServerId);
        var secret = AccessPolicy.NewSecret();
        var grant = new AccessGrant(Guid.NewGuid().ToString("N"), request.Name, AccessPolicy.Hash(secret), request.Role, request.ServerId, DateTimeOffset.UtcNow.AddDays(request.Days));
        lock (grantsLock) { var grants = Grants(); grants.Add(grant); store.Write("access-grants.json", grants); WorkspaceLease.Protect(Path.Combine(store.DirectoryPath, "access-grants.json")); }
        return new(grant, secret);
    }
    public void Revoke(string id) { lock (grantsLock) store.Write("access-grants.json", Grants().Where(g => g.Id != id).ToList()); }
    public async Task MonitorAsync(Action stop, CancellationToken token)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(3));
        try
        {
            do
            {
                try
                {
                    var settings = Settings;
                    foreach (var profile in settings.Servers)
                    {
                        try { profile.Validate(); snapshots[profile.Id] = await maintenance.ObserveAsync(profile, token); }
                        catch (Exception error) when (error is not OperationCanceledException) { snapshots[profile.Id] = ServerSnapshot.Offline with { Accessible = false, Players = null, ActivityReason = error.Message }; }
                    }
                    if (!PersistentHost && !settings.BackgroundMode && parent is { } owner && !ParentAlive(owner) && TryPrepareShutdown()) { stop(); break; }
                    if (!stopping && settings.Servers.Any(p => p.AutoBackup || p.AutoUpdate) && (scheduler is null || scheduler.IsCompleted)) scheduler = ScheduleAsync(token);
                }
                catch (Exception error) when (error is not OperationCanceledException) { WriteLog("Background manager: " + error.Message); }
            } while (await timer.WaitForNextTickAsync(token));
        }
        catch (OperationCanceledException) { }
        if (scheduler is not null) try { await scheduler; } catch (OperationCanceledException) { }
    }
    private async Task ScheduleAsync(CancellationToken token)
    {
        if (!await operations.WaitAsync(0, token)) return;
        try { await maintenance.TickAsync(Settings.Servers, token); }
        catch (Exception error) when (error is not OperationCanceledException) { WriteLog("Automation: " + error.Message); }
        finally { operations.Release(); }
    }
    private static bool ParentAlive(AgentParent parent)
    {
        try { using var process = Process.GetProcessById(parent.Id); return ProcessLifetime.Token(process) == parent.StartToken; }
        catch (Exception error) when (error is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception or IOException or UnauthorizedAccessException) { return false; }
    }
}
