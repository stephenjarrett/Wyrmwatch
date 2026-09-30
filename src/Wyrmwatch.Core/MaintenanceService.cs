namespace Wyrmwatch.Core;

public sealed class MaintenanceService(IServerRuntime runtime, ISteamClient steam, BackupEngine backups, JsonStore store, TimeProvider? clock = null)
{
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly object stateLock = new();
    private readonly Dictionary<string, (DateTimeOffset Since, DateTimeOffset Last, string ProcessKey)> emptySince = [];
    private DateTimeOffset Now => (clock ?? TimeProvider.System).GetUtcNow();
    private readonly Dictionary<string, ScheduleState> schedules = store.Read("schedules.json", () => new Dictionary<string, ScheduleState>());
    public OperationHistory History { get; } = new(store);
    public bool Busy { get; private set; }
    public event Action<string>? Log;
    public event Action? Changed;
    public ScheduleState Schedule(string id) { lock (stateLock) return schedules.GetValueOrDefault(id, new()); }
    public static bool InWindow(ServerProfile p, DateTimeOffset now)
    {
        if (!p.UseMaintenanceWindow) return true;
        var time = TimeOnly.FromDateTime(now.LocalDateTime);
        return p.WindowStart < p.WindowEnd ? time >= p.WindowStart && time < p.WindowEnd : time >= p.WindowStart || time < p.WindowEnd;
    }
    public async Task<ServerSnapshot> ObserveAsync(ServerProfile profile, CancellationToken token = default)
    {
        var state = await runtime.InspectAsync(profile, token);
        lock (stateLock)
        {
            if (state.Accessible && state.Running && state.Players == 0)
            {
                var key = string.Join(';', state.Processes.OrderBy(p => p.Id).Select(p => $"{p.Id}:{p.StartToken ?? p.StartUtc.Ticks.ToString()}"));
                if (emptySince.TryGetValue(profile.Id, out var prior) && prior.ProcessKey == key && Now - prior.Last < TimeSpan.FromSeconds(15))
                    emptySince[profile.Id] = (prior.Since, Now, key);
                else emptySince[profile.Id] = (Now, Now, key);
            }
            else emptySince.Remove(profile.Id);
        }
        return state;
    }
    public string? Deferral(ServerProfile profile, ServerSnapshot state, DateTimeOffset now)
    {
        if (!state.Accessible) return "Cannot verify server processes; maintenance deferred.";
        if (!InWindow(profile, now)) return "Waiting for the maintenance window.";
        if (!state.Running) return null;
        if (state.Players is null) return "Player activity is unknown; waiting for reliable activity information.";
        if (state.Players > 0) return $"Waiting for {state.Players} player(s) to leave.";
        lock (stateLock)
            if (!emptySince.TryGetValue(profile.Id, out var observation) || now - observation.Since < TimeSpan.FromSeconds(60) || now - observation.Last >= TimeSpan.FromSeconds(15)) return "Waiting for the server to remain empty for 60 seconds.";
        return null;
    }
    private void WriteLog(string message) { Log?.Invoke(message); }
    private async Task<string> ExecuteAsync(ServerProfile profile, string action, Func<Task<string>> body)
    {
        if (!await gate.WaitAsync(0)) throw new InvalidOperationException("Another server operation is still running.");
        var record = new OperationRecord(Guid.NewGuid().ToString("N"), profile.Id, profile.Name, action, DateTimeOffset.Now, null, "Running", "Starting…");
        try
        {
            using var lease = InstallationLease.Acquire(profile.InstallPath);
            profile.Validate(); Busy = true; History.Save(record); Changed?.Invoke(); WriteLog($"{profile.Name}: {action}");
            var result = await body();
            History.Save(record with { Finished = DateTimeOffset.Now, Status = result.StartsWith("Waiting", StringComparison.Ordinal) ? "Deferred" : "Succeeded", Detail = result });
            WriteLog(result); return result;
        }
        catch (Exception e)
        {
            History.Save(record with { Finished = DateTimeOffset.Now, Status = "Failed", Detail = e.Message }); WriteLog(e.Message); throw;
        }
        finally { Busy = false; gate.Release(); Changed?.Invoke(); }
    }
    public Task<string> BackupAsync(ServerProfile p) => ExecuteAsync(p, "Create backup", async () =>
    {
        var state = await runtime.InspectAsync(p);
        var result = await backups.CreateAsync(p, "Manual backup", state.Running); backups.Prune(p);
        return $"Verified backup saved: {Path.GetFileName(result.Path)}";
    });
    public Task<string> CheckAsync(ServerProfile p) => ExecuteAsync(p, "Check for updates", async () => (await steam.CheckAsync(p, WriteLog)).Summary);
    public Task<string> StartAsync(ServerProfile p) => ExecuteAsync(p, "Start server", async () =>
    {
        await runtime.StartAsync(p); return "Server process started. Watch the server log for world readiness.";
    });
    public Task<string> StopAsync(ServerProfile p, bool restart = false) => ExecuteAsync(p, restart ? "Restart server" : "Stop server", async () =>
    {
        var before = await runtime.InspectAsync(p);
        if (!before.Accessible) throw new IOException("Cannot verify server state.");
        if (!before.Running) return "The server is already stopped.";
        await backups.CreateAsync(p, restart ? "Before restart" : "Before stop", true);
        await runtime.StopAsync(p);
        await backups.CreateAsync(p, "After shutdown", false);
        if (restart) await runtime.StartAsync(p);
        backups.Prune(p); return restart ? "Server restarted." : "Server stopped and backed up.";
    });
    public Task<string> UpdateAsync(ServerProfile p, bool automatic = false) => ExecuteAsync(p, automatic ? "Automatic update" : "Update server", async () =>
    {
        var build = await steam.CheckAsync(p, WriteLog);
        if (build.Installed is null || build.Available is null) throw new IOException("Build information is incomplete. No server files were changed.");
        if (!build.UpdateAvailable) return build.Summary;
        var state = await ObserveAsync(p);
        var deferred = Deferral(p, state, Now);
        // Both manual and automatic updates respect the empty-server policy.
        if (deferred is not null) return "Waiting: " + deferred;
        await backups.CreateAsync(p, "Before update", state.Running);
        if (state.Running)
        {
            var latest = await ObserveAsync(p);
            deferred = Deferral(p, latest, Now);
            if (deferred is not null) return "Waiting: " + deferred;
            await runtime.StopAsync(p);
            try { await backups.CreateAsync(p, "Stopped before update", false); }
            catch { await runtime.StartAsync(p); throw; }
        }
        var stopped = await runtime.InspectAsync(p);
        if (!stopped.Accessible || stopped.Running) throw new IOException("The server is not confirmed stopped. Update cancelled.");
        await steam.InstallAsync(p, false, WriteLog);
        if (steam.InstalledBuild(p) != build.Available) throw new IOException("Installed build does not match the checked version. The server remains stopped for investigation.");
        if (state.Running) await runtime.StartAsync(p);
        backups.Prune(p); return $"Updated to build {build.Available}.";
    });
    public Task<string> InstallAsync(ServerProfile p) => ExecuteAsync(p, "Install server", async () =>
    {
        var state = await runtime.InspectAsync(p);
        if (!state.Accessible || state.Running) throw new IOException("Stop the server before installation.");
        if (File.Exists(p.Launcher)) throw new IOException("An installation already exists here. Connect it and use Update server instead.");
        if (Directory.Exists(p.InstallPath) && Directory.EnumerateFileSystemEntries(p.InstallPath).Any()) throw new IOException("New installations require an empty folder. Existing files were preserved.");
        await steam.InstallAsync(p, false, WriteLog); return "Server installed. Configure its owner, world and passwords before starting.";
    });
    public Task<string> SaveConfigurationAsync(ServerProfile p, IReadOnlyDictionary<string, string> values) => ExecuteAsync(p, "Write game configuration", async () =>
    {
        var state = await runtime.InspectAsync(p);
        if (!state.Accessible || state.Running) throw new IOException("Stop the server before changing its game configuration.");
        var original = File.Exists(p.ConfigPath) ? await File.ReadAllTextAsync(p.ConfigPath) : "";
        var merged = GameConfiguration.Merge(original, values);
        if (File.Exists(p.ConfigPath)) await backups.CreateAsync(p, "Before configuration change", false);
        state = await runtime.InspectAsync(p);
        if (!state.Accessible || state.Running) throw new IOException("The server started while preparing the change. Configuration was not changed.");
        if ((File.Exists(p.ConfigPath) ? await File.ReadAllTextAsync(p.ConfigPath) : "") != original) throw new IOException("Configuration changed while preparing the edit. Reload it before saving.");
        AtomicFile.Write(p.ConfigPath, merged); return "Game configuration saved. Previous settings are preserved in the backup.";
    });
    public Task<string> RestoreAsync(ServerProfile p, string archive) => ExecuteAsync(p, "Restore backup", async () =>
    {
        var recovery = await backups.RestoreAsync(p, archive, async () => { var state = await runtime.InspectAsync(p); return state.Accessible && !state.Running; });
        return $"Backup restored. Previous files retained in {recovery}. The server remains stopped.";
    });
    public async Task TickAsync(IReadOnlyList<ServerProfile> profiles, CancellationToken token = default)
    {
        if (Busy) return;
        foreach (var p in profiles)
        {
            token.ThrowIfCancellationRequested();
            await ObserveAsync(p, token);
            if (!p.AutoBackup && !p.AutoUpdate) continue;
            var state = Schedule(p.Id); var now = Now;
            state = state with { NextBackup = p.AutoBackup ? state.NextBackup ?? now.AddHours(p.BackupHours) : null, NextUpdate = p.AutoUpdate ? state.NextUpdate ?? now.AddMinutes(p.UpdateMinutes) : null };
            if (p.AutoBackup && state.NextBackup <= now)
            {
                try { await BackupAsync(p); state = state with { NextBackup = now.AddHours(p.BackupHours) }; }
                catch (Exception e) { WriteLog("Scheduled backup: " + e.Message); state = state with { NextBackup = now.AddMinutes(15) }; }
            }
            if (p.AutoUpdate && state.NextUpdate <= now)
            {
                try { var result = await UpdateAsync(p, true); state = state with { NextUpdate = now.AddMinutes(result.StartsWith("Waiting") ? 5 : p.UpdateMinutes) }; }
                catch (Exception e) { WriteLog("Scheduled update: " + e.Message); state = state with { NextUpdate = now.AddMinutes(15) }; }
            }
            lock (stateLock) { schedules[p.Id] = state; store.Write("schedules.json", schedules); }
        }
    }
    public void ResetSchedule(string id) { lock (stateLock) { schedules.Remove(id); store.Write("schedules.json", schedules); } }
}
