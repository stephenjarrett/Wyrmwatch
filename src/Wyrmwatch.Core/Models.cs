namespace Wyrmwatch.Core;

public sealed record ServerProfile
{
    public string Id { get; init; } = Guid.NewGuid().ToString("N");
    public string Name { get; init; } = "My Dragonwilds server";
    public string InstallPath { get; init; } = "";
    public string BackupPath { get; init; } = "";
    public string DataPath { get; init; } = "";
    public string LauncherPath { get; init; } = "";
    public int Port { get; init; } = 7777;
    public bool AutoUpdate { get; init; }
    public int UpdateMinutes { get; init; } = 60;
    public bool AutoBackup { get; init; }
    public int BackupHours { get; init; } = 6;
    public int RetainBackups { get; init; } = 20;
    public bool UseMaintenanceWindow { get; init; }
    public TimeOnly WindowStart { get; init; } = new(3, 0);
    public TimeOnly WindowEnd { get; init; } = new(5, 0);
    public string Launcher => string.IsNullOrEmpty(LauncherPath) ? Path.Combine(InstallPath, OperatingSystem.IsWindows() ? "RSDragonwildsServer.exe" : "RSDragonwildsServer.sh") : LauncherPath;
    public string SavedPath => string.IsNullOrWhiteSpace(DataPath) ? Path.Combine(InstallPath, "RSDragonwilds", "Saved") : DataPath;
    public string ConfigPath => Path.Combine(SavedPath, "Config", OperatingSystem.IsWindows() ? "WindowsServer" : "Linux", "DedicatedServer.ini");
    public string LogPath => Path.Combine(SavedPath, "Logs", "RSDragonwilds.log");
    public void Validate()
    {
        if (string.IsNullOrEmpty(Id) || Id.Length > 80 || Id.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not ('-' or '_'))) throw new ArgumentException("The server connection ID is invalid.");
        if (string.IsNullOrWhiteSpace(Name)) throw new ArgumentException("Give your server a name.");
        if (string.IsNullOrWhiteSpace(InstallPath) || !Path.IsPathFullyQualified(InstallPath)) throw new ArgumentException("Choose an absolute server installation folder.");
        var root = Path.GetPathRoot(Path.GetFullPath(InstallPath));
        if (SafePaths.Same(root!, InstallPath)) throw new ArgumentException("Choose a dedicated folder, not the root of a drive.");
        SafePaths.NoLinks(InstallPath);
        if (!SafePaths.Within(Launcher, InstallPath)) throw new ArgumentException("The launcher must be inside the server installation.");
        if (string.IsNullOrWhiteSpace(BackupPath) || !Path.IsPathFullyQualified(BackupPath)) throw new ArgumentException("Choose an absolute backup folder.");
        if (SafePaths.Within(BackupPath, InstallPath) || SafePaths.Same(BackupPath, InstallPath)) throw new ArgumentException("Store backups outside the server installation.");
        SafePaths.NoLinks(BackupPath);
        if (!Path.IsPathFullyQualified(SavedPath) || SafePaths.Same(SavedPath, Path.GetPathRoot(SavedPath)!)) throw new ArgumentException("Choose a dedicated save-data folder.");
        if (SafePaths.Within(BackupPath, SavedPath) || SafePaths.Same(BackupPath, SavedPath)) throw new ArgumentException("Store backups outside the save-data folder.");
        SafePaths.NoLinks(SavedPath);
        if (Port is < 1024 or > 65535) throw new ArgumentException("Choose a port between 1024 and 65535.");
        if (UpdateMinutes is < 5 or > 10080 || BackupHours is < 1 or > 720 || RetainBackups is < 1 or > 1000) throw new ArgumentException("Check the schedule intervals and backup retention.");
        if (UseMaintenanceWindow && WindowStart == WindowEnd) throw new ArgumentException("The maintenance window must have different start and end times.");
    }
}

public sealed record ManagerSettings
{
    public List<ServerProfile> Servers { get; init; } = [];
    public List<ServerProfile> DisconnectedServers { get; init; } = [];
    public string? SelectedServerId { get; init; }
    public string Theme { get; init; } = "Dark";
    public bool CloseToTray { get; init; } = true;
    public bool LaunchAtLogin { get; init; }
    public bool BackgroundMode { get; init; }
    public string Language { get; init; } = "en";
    public bool CheckAppUpdates { get; init; }
}

public sealed record ServerSnapshot(bool Running, bool Accessible, int? Players, string ActivityReason,
    double CpuPercent, long MemoryBytes, TimeSpan Uptime, IReadOnlyList<ProcessIdentity> Processes)
{
    public static ServerSnapshot Offline => new(false, true, 0, "Server is stopped", 0, 0, TimeSpan.Zero, []);
}
public sealed record ProcessIdentity(int Id, DateTime StartUtc, string Path, string? StartToken = null);
public sealed record BuildStatus(string? Installed, string? Available)
{
    public bool UpdateAvailable => Installed is not null && Available is not null && Installed != Available;
    public string Summary => Installed is null || Available is null ? "Build information unavailable" : UpdateAvailable ? $"Update available · {Installed} → {Available}" : $"Up to date · build {Installed}";
}
public sealed record OperationRecord(string Id, string ServerId, string ServerName, string Action, DateTimeOffset Started,
    DateTimeOffset? Finished, string Status, string Detail);
public sealed record ScheduleState(DateTimeOffset? NextUpdate = null, DateTimeOffset? NextBackup = null);
public sealed record BackupFile(string Entry, long Length, string Sha256, DateTime LastWriteUtc);
public sealed record BackupManifest(int Format, string Product, string Installation, string ProfileId, string DataRoot, DateTimeOffset Created,
    string Reason, bool WasRunning, List<BackupFile> Files);
public sealed record BackupInfo(string Path, BackupManifest Manifest, long Bytes);

public interface IServerRuntime
{
    Task<ServerSnapshot> InspectAsync(ServerProfile profile, CancellationToken token = default);
    Task StartAsync(ServerProfile profile, CancellationToken token = default);
    Task StopAsync(ServerProfile profile, CancellationToken token = default);
}
public interface ISteamClient
{
    string? InstalledBuild(ServerProfile profile);
    Task<BuildStatus> CheckAsync(ServerProfile profile, Action<string> log, CancellationToken token = default);
    Task InstallAsync(ServerProfile profile, bool repair, Action<string> log, CancellationToken token = default);
}
