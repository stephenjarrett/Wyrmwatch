using System.Collections.Concurrent;
using System.Diagnostics;
using Wyrmwatch.Core;
using Wyrmwatch.Platform;

namespace Wyrmwatch.Agent;

public sealed class ManagerHost
{
    public const string Version = "0.2.3";
    private readonly JsonStore store;
    private readonly IServerRuntime runtime;
    private readonly ISteamClient steam;
    private readonly BackupEngine backups = new();
    private readonly MaintenanceService maintenance;
    private readonly SemaphoreSlim operations = new(1, 1);
    private readonly ConcurrentDictionary<string, ServerSnapshot> snapshots = new();
    private readonly ConcurrentQueue<string> log = new();
    private readonly object logLock = new();
    private AgentParent? parent;
    private Task? scheduler;
    private bool stopping;
    public bool PersistentHost { get; set; }
    public bool Busy => operations.CurrentCount == 0 || maintenance.Busy;
    public ManagerHost(JsonStore store, IServerRuntime? runtime = null, ISteamClient? steam = null)
    {
        this.store = store;
        var helper = Path.Combine(AppContext.BaseDirectory, "Wyrmwatch.Signal.exe");
        if (!File.Exists(helper)) helper = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "Wyrmwatch.Signal.exe"));
        this.runtime = new SingleServerRuntime(runtime ?? (OperatingSystem.IsWindows() ? new WindowsRuntime(store, helper) : new LinuxRuntime(store)), () =>
        {
            var settings = Settings;
            // A disconnected running server must still prevent a second launch.
            return settings.Servers.Concat(settings.DisconnectedServers).DistinctBy(p => p.Id).ToArray();
        });
        this.steam = steam ?? new SteamClient(Path.Combine(store.DirectoryPath, "tools"));
        maintenance = new(this.runtime, this.steam, backups, store);
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
            maintenance.Schedule(p.Id), steam.InstalledBuild(p)) { RecoveryRequired = backups.HasPendingRestore(p) }).ToList(), maintenance.History.Snapshot().Take(100).ToList(), log.ToList(), settings.BackgroundMode || PersistentHost, null, PersistentHost);
    }
    public ServerProfile Profile(string id) => Settings.Servers.SingleOrDefault(p => p.Id == id) ?? throw new KeyNotFoundException("Server connection not found.");
    public async Task<string> ExecuteAsync(string id, ServerAction request)
    {
        if (!await operations.WaitAsync(0)) throw new InvalidOperationException("Another operation is still running.");
        ServerProfile? observed = null;
        try
        {
            var p = Profile(id);
            observed = p;
            if (request.Action == "configuration" && request.Values?.TryGetValue("Port", out var portText) == true)
            {
                if (!int.TryParse(portText, out var port)) throw new ArgumentException("Invalid port.");
                p = p with { Port = port };
            }
            var settings = Settings;
            ServerConnections.Validate(p, settings.Servers.Concat(settings.DisconnectedServers));
            if (request.Action is "restore" or "recover" && request.Confirmation != p.Name) throw new ArgumentException("Confirm the server name before restoring or recovering.");
            return request.Action switch
            {
                "start" => await maintenance.StartAsync(p),
                "stop" => await maintenance.StopAsync(p),
                "restart" => await maintenance.StopAsync(p, true),
                "backup" => await maintenance.BackupAsync(p),
                "check" => await maintenance.CheckAsync(p),
                "update" => await maintenance.UpdateAsync(p),
                "install" => await maintenance.InstallAsync(p),
                "configuration" => await SaveConfigurationAsync(p, request.Values ?? throw new ArgumentException("Missing configuration values.")),
                "restore" => await maintenance.RestoreAsync(p, Archive(p, request.Archive)),
                "recover" => await maintenance.RecoverRestoreAsync(p),
                "verify" => await VerifyAsync(p, request.Archive),
                _ => throw new ArgumentException("Unknown operation.")
            };
        }
        finally
        {
            if (observed is not null)
                try { snapshots[id] = await maintenance.ObserveAsync(observed); }
                catch (Exception error) { snapshots[id] = ServerSnapshot.Offline with { Accessible = false, Players = null, ActivityReason = error.Message }; }
            operations.Release();
        }
    }
    private async Task<string> SaveConfigurationAsync(ServerProfile profile, IReadOnlyDictionary<string, string> values)
    {
        var result = await maintenance.SaveConfigurationAsync(profile, values);
        var settings = Settings;
        store.Write("settings.json", settings with { Servers = settings.Servers.Select(p => p.Id == profile.Id ? profile : p).ToList() });
        return result;
    }

    public Task<ServerProfile> ImportProfileAsync(ServerProfile profile)
    {
        var inspected = ExistingServerImport.Inspect(profile.Launcher, profile.SavedPath, profile.BackupPath);
        if (inspected.Port != profile.Port) throw new ArgumentException("The server port changed during import. Review the connection again.");
        return SaveProfileAsync(profile with { AutoBackup = false, AutoUpdate = false });
    }
    public Task<ServerProfile> CreateServerAsync(CreateServerRequest request) => ProvisionServerAsync(request, null);
    public Task<ServerProfile> ImportWorldAsync(WorldImportRequest request)
    {
        if (!request.SourceStoppedConfirmed) throw new ArgumentException("Close the source game or server and confirm it is stopped before importing its world.");
        if (request.Source is null) throw new ArgumentException("Choose and review a world save before importing.");
        return ProvisionServerAsync(new(request.Profile, request.Configuration), request.Source);
    }
    private async Task CheckImportSourceAsync(WorldImportPlan source)
    {
        var current = await WorldImport.InspectAsync(source.SourcePath);
        if (current != source) throw new IOException("The source world changed after review. Close its game or server and review the save again.");
        var settings = Settings;
        foreach (var owner in settings.Servers.Concat(settings.DisconnectedServers).Where(p => SafePaths.Within(source.SourcePath, p.SavedPath)))
        {
            backups.EnsureNoPendingRestore(owner);
            var state = await runtime.InspectAsync(owner);
            if (!state.Accessible || state.Running) throw new IOException("Stop the source server before importing its world. Its files were preserved.");
        }
    }
    private async Task<ServerProfile> ProvisionServerAsync(CreateServerRequest request, WorldImportPlan? source)
    {
        if (!await operations.WaitAsync(0)) throw new InvalidOperationException("Another server operation is still running.");
        var profile = request.Profile with { AutoBackup = false, AutoUpdate = false };
        string? staging = null;
        var published = false; var registered = false;
        try
        {
            ServerConnections.Validate(profile, Settings.Servers.Concat(Settings.DisconnectedServers));
            if (Settings.Servers.Concat(Settings.DisconnectedServers).Any(p => string.Equals(p.Name, profile.Name, StringComparison.OrdinalIgnoreCase)))
                throw new ArgumentException("A server with this name is already saved. Choose a different name for the new server.");
            if (Settings.Servers.Concat(Settings.DisconnectedServers).Any(p => p.Id == profile.Id)) throw new ArgumentException("This server connection ID is already saved. Reconnect the existing server instead.");
            if (!string.IsNullOrEmpty(profile.DataPath) || !string.IsNullOrEmpty(profile.LauncherPath)) throw new ArgumentException("New servers must use their own installation and save folders.");
            var values = request.Configuration;
            if (!values.TryGetValue("Port", out var port) || port != profile.Port.ToString()) throw new ArgumentException("The game port does not match the reviewed setup.");
            var configuration = GameConfiguration.Merge("", values);
            var sourceOwner = source is null ? null : Settings.Servers.Concat(Settings.DisconnectedServers).FirstOrDefault(p => SafePaths.Within(source.SourcePath, p.SavedPath));
            using var sourceLease = sourceOwner is null ? null : ServerOperationLease.Acquire(sourceOwner);
            if (source is not null) await CheckImportSourceAsync(source);
            using var lease = InstallationLease.Acquire(profile.InstallPath);
            if (File.Exists(profile.InstallPath) || Directory.Exists(profile.InstallPath)) throw new IOException("Choose a new server folder name. The target already exists; no existing files were changed.");
            var state = await runtime.InspectAsync(profile);
            if (!state.Accessible || state.Running) throw new IOException("Cannot confirm this installation is stopped.");
            staging = profile.InstallPath + ".setup-" + Guid.NewGuid().ToString("N");
            var stagedProfile = profile with { InstallPath = staging };
            ServerConnections.Validate(stagedProfile, Settings.Servers.Concat(Settings.DisconnectedServers));
            WriteLog($"{profile.Name}: downloading new server into {staging}");
            await steam.InstallAsync(stagedProfile, false, WriteLog);
            SafePaths.NoLinks(stagedProfile.Launcher);
            if (!File.Exists(stagedProfile.Launcher)) throw new IOException("The download did not produce the server launcher.");
            configuration = GameConfiguration.Merge(File.Exists(stagedProfile.ConfigPath) ? await File.ReadAllTextAsync(stagedProfile.ConfigPath) : "", values);
            AtomicFile.Write(stagedProfile.ConfigPath, configuration);
            if (source is not null)
            {
                await CheckImportSourceAsync(source);
                WriteLog($"{profile.Name}: copying reviewed world; source files are preserved.");
                await WorldImport.CopyAsync(source, Path.Combine(stagedProfile.SavedPath, "SaveGames"));
            }
            SafePaths.NoLinks(profile.InstallPath);
            // Move fails if another app created the target. Never replace an existing folder.
            Directory.Move(staging, profile.InstallPath);
            published = true;
            var settings = Settings;
            store.Write("settings.json", settings with { Servers = [.. settings.Servers, profile], SelectedServerId = profile.Id });
            registered = true;
            snapshots[profile.Id] = ServerSnapshot.Offline;
            WriteLog($"{profile.Name}: {(source is null ? "created" : "world imported")} and configured; stopped, automation off.");
            return profile;
        }
        catch (Exception error)
        {
            var detail = published && !registered
                ? $" Completed setup files were preserved at {profile.InstallPath}, but this workspace did not save the server connection. Do not retry into that existing folder. Inspect the retained setup before choosing a different new server folder/name for another setup attempt."
                : staging is not null && Directory.Exists(staging) ? $" Downloaded files were preserved at {staging}. You can retry with the same new server name." : "";
            WriteLog("Create server failed: " + error.Message + detail);
            throw new IOException(error.Message + detail, error);
        }
        finally { operations.Release(); }
    }
    private string Archive(ServerProfile profile, string? name) => backups.List(profile).SingleOrDefault(b => Path.GetFileName(b.Path) == name)?.Path ?? throw new ArgumentException("Choose a backup belonging to this server.");
    private async Task<string> VerifyAsync(ServerProfile profile, string? name)
    {
        await backups.VerifyAsync(Archive(profile, name), profile);
        return "Every archived file matches its recorded hash.";
    }
    public object BackupList(string id) => backups.List(Profile(id)).Select(b => new { Id = Path.GetFileName(b.Path), b.Manifest.Created, b.Manifest.Reason, b.Bytes, Files = b.Manifest.Files.Count, Coverage = BackupEngine.Coverage(b.Manifest).Description, IncludesWorld = BackupEngine.Coverage(b.Manifest).IncludesWorld }).ToArray();
    public async Task<ServerProfile> SaveProfileAsync(ServerProfile profile)
    {
        profile.Validate();
        if (!await operations.WaitAsync(0)) throw new InvalidOperationException("Wait for the current operation before editing connections or preferences.");
        try
        {
            var settings = Settings;
            var previous = settings.DisconnectedServers.FirstOrDefault(p => SafePaths.Same(p.InstallPath, profile.InstallPath));
            if (previous is not null && settings.Servers.All(p => p.Id != profile.Id)) profile = profile with { Id = previous.Id };
            ServerConnections.Validate(profile, settings.Servers.Concat(settings.DisconnectedServers));
            var existing = settings.Servers.Concat(settings.DisconnectedServers).SingleOrDefault(p => p.Id == profile.Id);
            if (existing is not null && (!SafePaths.Same(existing.InstallPath, profile.InstallPath) || !SafePaths.Same(existing.Launcher, profile.Launcher) || !SafePaths.Same(existing.SavedPath, profile.SavedPath)))
            {
                backups.EnsureNoPendingRestore(existing);
                var state = await runtime.InspectAsync(existing);
                if (!state.Accessible || state.Running) throw new IOException("Stop this server before changing its installation, launcher or save-data folder.");
            }
            var profiles = settings.Servers.Where(p => p.Id != profile.Id).ToList(); profiles.Add(profile);
            if (existing is null || existing.AutoUpdate != profile.AutoUpdate || existing.AutoBackup != profile.AutoBackup || existing.UpdateMinutes != profile.UpdateMinutes || existing.BackupHours != profile.BackupHours) maintenance.ResetSchedule(profile.Id);
            store.Write("settings.json", settings with { Servers = profiles, DisconnectedServers = settings.DisconnectedServers.Where(p => p.Id != profile.Id).ToList(), SelectedServerId = profile.Id });
        }
        finally { operations.Release(); }
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
        BackgroundMode = preferences.BackgroundMode, Language = preferences.Language,
        SelectedServerId = preferences.SelectedServerId
    });
    private async Task ChangeSettingsAsync(Func<ManagerSettings, ManagerSettings> change)
    {
        if (!await operations.WaitAsync(0)) throw new InvalidOperationException("Wait for the current operation before editing connections or preferences.");
        try { store.Write("settings.json", change(Settings)); }
        finally { operations.Release(); }
    }
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
                        try { ServerConnections.Validate(profile, settings.Servers.Concat(settings.DisconnectedServers)); snapshots[profile.Id] = await maintenance.ObserveAsync(profile, token); }
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
        try
        {
            var settings = Settings;
            var profiles = settings.Servers;
            var safe = profiles.Where(p =>
            {
                try { ServerConnections.Validate(p, profiles.Concat(settings.DisconnectedServers)); return true; }
                catch (ArgumentException) { return false; }
                catch (IOException) { return false; }
            }).ToArray();
            await maintenance.TickAsync(safe, token);
        }
        catch (Exception error) when (error is not OperationCanceledException) { WriteLog("Automation: " + error.Message); }
        finally { operations.Release(); }
    }
    private static bool ParentAlive(AgentParent parent)
    {
        try { using var process = Process.GetProcessById(parent.Id); return ProcessLifetime.Token(process) == parent.StartToken; }
        catch (Exception error) when (error is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception or IOException or UnauthorizedAccessException) { return false; }
    }
}
