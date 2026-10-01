using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using Wyrmwatch.Core;

namespace Wyrmwatch.Desktop;

internal enum WorkspacePage { Servers, ServerSettings, Resources, Automation, Backups, Activity, AppSettings, Help, AppUpdates }

public sealed class ServerRow(ServerProfile profile) : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;
    public string Id => profile.Id;
    public string Name => profile.Name;
    public string Path => profile.InstallPath;
    public string StatusIcon => Status switch { "Running" => "●", "Stopped" => "○", "Needs attention" or "Recovery required" => "!", _ => "…" };
    public string Status { get; private set; } = "Checking…";
    public string Metrics { get; private set; } = "Waiting for the manager";
    public void Replace(ServerProfile value) { if (profile == value) return; profile = value; PropertyChanged?.Invoke(this, new(null)); }
    public void Update(ServerSnapshot? state, bool recoveryRequired = false)
    {
        var status = recoveryRequired ? "Recovery required" : state is null ? "Checking…" : !state.Accessible ? "Needs attention" : state.Running ? "Running" : "Stopped";
        var metrics = recoveryRequired ? "Interrupted restore — recover it from Backups before starting." : state is null ? "Waiting for the manager" : !state.Accessible ? state.ActivityReason : !state.Running ? $"UDP {profile.Port} · {(File.Exists(profile.Launcher) ? "Ready to start" : "Launcher missing")}" : $"{state.Players?.ToString() ?? "?"} players · CPU {state.CpuPercent:0.0}% · {state.MemoryBytes / 1073741824d:0.0} GB";
        if (Status == status && Metrics == metrics) return;
        Status = status; Metrics = metrics; PropertyChanged?.Invoke(this, new(null));
    }
}

public sealed record BackupRow(BackupInfo Info)
{
    public bool IncludesWorld => BackupEngine.Coverage(Info.Manifest).IncludesWorld;
    public string Coverage => BackupEngine.Coverage(Info.Manifest).Description;
    public string Title => Info.Manifest.Created.ToLocalTime().ToString("MMM d, yyyy · h:mm tt");
    public string Detail => $"{Info.Manifest.Reason} · {Info.Manifest.Files.Count} files · {(Info.Manifest.WasRunning ? "Live snapshot" : "Stopped snapshot")}";
    public string Size => $"{Info.Bytes / 1024d / 1024d:0.0} MB";
}
public sealed record OperationRow(OperationRecord Record)
{
    public string Title => $"{Record.Action}  ·  {Record.Status}";
    public string Detail => $"{Record.Started.LocalDateTime:MMM d, h:mm tt} · {Record.ServerName}\n{Record.Detail}";
}
public sealed class WorkspaceModel : INotifyPropertyChanged
{
    public WorkspaceModel()
    {
        Profiles.CollectionChanged += (_, _) => { Refresh(nameof(NoServers)); Refresh(nameof(ServerCount)); };
        Backups.CollectionChanged += (_, _) => { Refresh(nameof(NoBackups)); Refresh(nameof(CanCreateFirstBackup)); };
    }
    public event PropertyChangedEventHandler? PropertyChanged;
    public void Refresh([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new(name));
    private void Set<T>(ref T field, T value, [CallerMemberName] string? name = null) { if (EqualityComparer<T>.Default.Equals(field, value)) return; field = value; Refresh(name); }
    public ObservableCollection<ServerProfile> Profiles { get; } = [];
    public ObservableCollection<ServerRow> ServerRows { get; } = [];
    private ServerRow? selectedRow;
    public ServerRow? SelectedRow { get => selectedRow; set => Set(ref selectedRow, value); }
    public string ServerCount => $"{Profiles.Count} saved server{(Profiles.Count == 1 ? "" : "s")}";
    public bool CanManage => !Busy && !Program.Demo;
    public void SyncRows()
    {
        foreach (var row in ServerRows.Where(r => Profiles.All(p => p.Id != r.Id)).ToArray()) ServerRows.Remove(row);
        foreach (var profile in Profiles)
        {
            var row = ServerRows.FirstOrDefault(r => r.Id == profile.Id);
            if (row is null) ServerRows.Add(new(profile)); else row.Replace(profile);
        }
        SelectedRow = ServerRows.FirstOrDefault(r => r.Id == SelectedProfile?.Id);
        Refresh(nameof(ServerCount));
    }
    public ObservableCollection<BackupRow> Backups { get; } = [];
    public void SyncBackups(IReadOnlyList<BackupInfo> backups)
    {
        // Archives are immutable. Keep existing rows so refreshes preserve selection.
        for (var i = 0; i < backups.Count; i++)
        {
            var existing = Backups.FirstOrDefault(row => SafePaths.Same(row.Info.Path, backups[i].Path));
            if (existing is null) Backups.Insert(i, new(backups[i]));
            else if (Backups.IndexOf(existing) != i) Backups.Move(Backups.IndexOf(existing), i);
        }
        while (Backups.Count > backups.Count) Backups.RemoveAt(Backups.Count - 1);
    }
    public ObservableCollection<OperationRow> Operations { get; } = [];
    private ServerProfile? selectedProfile;
    public ServerProfile? SelectedProfile
    {
        get => selectedProfile;
        set
        {
            if (selectedProfile?.Id != value?.Id)
            {
                serverRunning = null; hasObservation = false; inspectionUnknown = false; refreshFailed = false;
                refreshing = value is not null; inspectionReason = ""; resourceSamples = 0; backupSelected = false;
                lastRefresh = null;
            }
            Set(ref selectedProfile, value); Refresh(null);
        }
    }
    public bool HasServer => SelectedProfile is not null;
    public bool NoServer => !HasServer;
    public string ServerName => SelectedProfile?.Name ?? "No server connected";
    public string ServerPath => SelectedProfile?.InstallPath ?? "";
    private bool recoveryPending;
    public bool RecoveryPending { get => recoveryPending; set { Set(ref recoveryPending, value); RefreshActions(); } }
    public string RecoveryHint => "An interrupted restore needs recovery. Start, configuration changes and updates are blocked. Open Backups and recover the interrupted restore while the server is stopped; retained files remain available.";
    private bool busy;
    public bool Busy { get => busy || OperationActive; set { Set(ref busy, value); RefreshActions(); } }
    public bool CanAct => HasServer && !Busy && !Program.Demo && !RecoveryPending;
    public bool CanRecover => HasServer && !Busy && !Program.Demo && RecoveryPending && ServerRunning == false;
    private bool? serverRunning;
    public bool? ServerRunning
    {
        get => serverRunning;
        set
        {
            if (serverRunning != value && value is not null) resourceSamples = 0;
            Set(ref serverRunning, value); RefreshActions(); RefreshObservation();
        }
    }
    private IReadOnlyList<ManagedServer> observedServers = [];
    public ManagedServer? OtherRunningServer => observedServers.FirstOrDefault(s => s.Id != SelectedProfile?.Id && s.State.Running);
    private bool OtherServerStateUnknown => observedServers.Any(s => s.Id != SelectedProfile?.Id && !s.State.Accessible);
    public void UpdateStartPrerequisites(IEnumerable<ManagedServer> servers)
    {
        observedServers = servers.ToArray(); RefreshActions();
    }
    public bool CanStart => CanAct && ServerRunning == false && OtherRunningServer is null && !OtherServerStateUnknown;
    public bool CanStop => HasServer && !Busy && !Program.Demo && ServerRunning == true;
    public bool CanRestart => CanAct && ServerRunning == true;
    public bool CanRestore => CanAct && ServerRunning == false;
    private bool backupSelected;
    public bool BackupSelected { get => backupSelected; set { Set(ref backupSelected, value); RefreshActions(); } }
    public bool CanRestoreSelected => CanRestore && BackupSelected;
    public bool CanVerifySelected => HasServer && !Busy && !Program.Demo && BackupSelected;
    public bool NoServers => Profiles.Count == 0;
    public bool NoBackups => HasServer && Backups.Count == 0;
    public bool CanCreateFirstBackup => NoBackups && CanAct;
    public bool ShowStart => ServerRunning != true;
    public bool ShowStop => ServerRunning == true;
    private bool installedBuildKnown;
    public bool InstalledBuildKnown { get => installedBuildKnown; set { Set(ref installedBuildKnown, value); RefreshActions(); } }
    public bool CanUpdate => CanAct && InstalledBuildKnown;
    public string UpdateHint => !HasServer ? "Choose a server before checking its game update." : Program.Demo ? "Server actions are disabled in the demo workspace." : Busy ? "Wait for the current operation to finish before applying a game update." : RecoveryPending ? "Recover the interrupted restore in Backups before applying a game update." : InstalledBuildKnown ? "Game updates require a verified backup and no connected players. Unknown player activity defers the update." : "Apply game update is unavailable until the installed Steam build is known. Check for updates to review build information; no server files are changed by the check.";
    public string StartHint => !HasServer ? "Choose or create a server before starting." : Program.Demo ? "Server actions are disabled in the demo workspace." : Busy ? "Wait for the current operation to finish before starting." : RecoveryPending ? "Recover the interrupted restore in Backups before starting this server." : ServerRunning == false && OtherRunningServer is { } running ? $"{running.Name} is running. Open that server and stop it before starting this connection. Selecting a server does not stop it." : ServerRunning == false && OtherServerStateUnknown ? "Another saved server cannot be inspected. Check its state before starting this connection." : ServerRunning switch
    {
        true => "This server is already running.",
        false => "Start launches this connection. Verify the world in game, then create a backup.",
        _ => ServerStateHint
    };
    public string StartHelpDestination => !HasServer ? "Servers" : Busy || Program.Demo ? "Activity" : RecoveryPending ? "Backups" : ServerRunning is null ? "Refresh" : ServerRunning == false && OtherRunningServer is not null ? "RunningServer" : ServerRunning == false && OtherServerStateUnknown ? "Refresh" : "Activity";
    public string StartHelpText => StartHelpDestination switch { "Servers" => "Choose a server", "Backups" => "Open recovery in Backups", "RunningServer" => "Open " + OtherRunningServer!.Name, "Refresh" => InspectionUnknown ? "Retry server check" : "Check server state", _ => "Open Activity" };
    public string RestoreHint => !HasServer ? "Choose a server before restoring a backup." : Program.Demo ? "Server actions are disabled in the demo workspace." : Busy ? "Wait for the current operation to finish before restoring." : RecoveryPending ? "Recover the interrupted restore here before restoring another backup." : ServerRunning is null ? ServerStateHint : ServerRunning == true ? "Stop this server before restoring a backup." : !BackupSelected ? "Select a backup to review and restore." : "Restore the selected backup after reviewing its coverage. The server remains stopped.";
    public string RestoreHelpDestination => !HasServer ? "Servers" : Busy || Program.Demo ? "Activity" : RecoveryPending ? "Backups" : ServerRunning is null ? "Refresh" : ServerRunning == true ? "Stop" : "SelectBackup";
    public string RestoreHelpText => RestoreHelpDestination switch { "Servers" => "Choose a server", "Backups" => "Recover here", "Refresh" => InspectionUnknown ? "Retry server check" : "Check server state", "Stop" => "Stop server", "SelectBackup" => "Select a backup", _ => "Open Activity" };
    public string UpdateHelpDestination => !HasServer ? "Servers" : Busy || Program.Demo ? "Activity" : RecoveryPending ? "Backups" : !InstalledBuildKnown ? "CheckUpdates" : "Activity";
    public string UpdateHelpText => UpdateHelpDestination switch { "Servers" => "Choose a server", "Backups" => "Open recovery in Backups", "CheckUpdates" => "Check game update", _ => "Open Activity" };
    public bool CanConfigure => CanStart;
    public string ConfigurationHint => !HasServer ? "Choose a server to edit its settings." : RecoveryPending ? RecoveryHint : ServerRunning switch
    {
        true => "Stop this server before saving game configuration. You can review and edit the fields now.",
        false => "This server is stopped. Existing settings are backed up before changes are saved.",
        _ => "Waiting for a verified server state before game configuration can be saved."
    };
    public string ActionHint => !HasServer ? "Choose a server to see available actions." : Busy ? "An operation is in progress. Wait for it to finish before changing this server." : RecoveryPending ? RecoveryHint : ServerRunning switch
    {
        true => "Stop and Restart request a graceful shutdown after a backup. Connected players will be disconnected.",
        false => "The server is stopped. Start launches this connection; Stop and Restart become available when it is running.",
        _ => "Waiting for a verified server state. Start, Stop and configuration changes are unavailable until the check finishes."
    };
    private void RefreshActions()
    {
        foreach (var name in new[] { nameof(CanAct), nameof(CanStart), nameof(CanStop), nameof(CanRestart), nameof(CanRestore), nameof(CanConfigure), nameof(CanManage), nameof(CanRecover), nameof(CanUpdate), nameof(UpdateHint), nameof(ConfigurationHint), nameof(ActionHint), nameof(CanRestoreSelected), nameof(CanVerifySelected), nameof(CanCreateFirstBackup), nameof(ShowStart), nameof(ShowStop), nameof(StartHint), nameof(StartHelpText), nameof(StartHelpDestination), nameof(RestoreHint), nameof(RestoreHelpText), nameof(RestoreHelpDestination), nameof(UpdateHelpText), nameof(UpdateHelpDestination), nameof(StatusChipText), nameof(StatusChipIcon) }) Refresh(name);
    }
    private bool refreshing, hasObservation, inspectionUnknown, refreshFailed;
    private string inspectionReason = "";
    private DateTimeOffset? lastRefresh;
    private int resourceSamples;
    public bool Refreshing => HasServer && refreshing;
    public bool FirstLoading => HasServer && !hasObservation && refreshing;
    public bool StateStale => HasServer && lastRefresh is not null && (refreshing || refreshFailed);
    public bool InspectionUnknown => HasServer && inspectionUnknown;
    public string ServerStateHint => !HasServer ? "Choose a server to check its state." : InspectionUnknown ? "Server inspection is unavailable. Retry the server check or open Help & diagnostics. Start, Stop and configuration changes require a verified state." : FirstLoading ? "Checking this server for the first time. Start, Stop and configuration changes require a verified state." : StateStale ? "Refreshing server state. Displayed readings are from the last check." : ServerRunning is null ? "Checking server state. Start, Stop and configuration changes require a verified state." : lastRefresh is { } checkedAt ? $"Server state checked at {checkedAt.LocalDateTime:h:mm:ss tt}." : "Server state verified.";
    public string InspectionDetail => inspectionReason;
    public bool ShowResourceEmpty => !HasServer || ServerRunning != true || resourceSamples < 2;
    public string ResourcesHint => !HasServer ? "Choose a server to see resource usage." : InspectionUnknown || ServerRunning is null && !FirstLoading ? "Resource usage is unavailable until server inspection succeeds. Any retained readings are from an earlier check." : FirstLoading ? "Checking server state before collecting resource samples." : ServerRunning == false ? "Server stopped. Resource samples will begin after you start it." : resourceSamples < 2 ? "Collecting samples. The chart appears after two running-server readings." : StateStale ? "Showing samples from the last successful check while server state refreshes." : "Live resource samples from the running server.";
    public string StatusChipText => !HasServer ? "Not connected" : RecoveryPending ? "Recovery required" : InspectionUnknown ? "Inspection unavailable" : ServerRunning switch { true => "Running", false => "Stopped", _ => "Checking" };
    public string StatusChipIcon => !HasServer ? "○" : RecoveryPending || InspectionUnknown ? "!" : ServerRunning switch { true => "●", false => "○", _ => "…" };
    public void BeginRefresh()
    {
        if (!HasServer) return;
        refreshing = true; RefreshObservation();
    }
    public void CompleteRefresh(bool accessible, string? reason = null, DateTimeOffset? now = null)
    {
        if (!HasServer) return;
        refreshing = false; hasObservation = true; inspectionUnknown = !accessible; refreshFailed = !accessible;
        inspectionReason = accessible ? "" : reason ?? "Server inspection did not return an accessible state.";
        if (accessible) lastRefresh = now ?? DateTimeOffset.Now; else ServerRunning = null;
        RefreshObservation(); RefreshActions();
    }
    public void FailRefresh(string reason)
    {
        if (!HasServer) return;
        refreshing = false; inspectionUnknown = true; refreshFailed = true; inspectionReason = reason;
        ServerRunning = null; RefreshObservation(); RefreshActions();
    }
    public void RecordResourceSample()
    {
        if (ServerRunning != true || InspectionUnknown) return;
        resourceSamples = Math.Min(2, resourceSamples + 1); RefreshObservation();
    }
    public void ResetResourceSamples() { resourceSamples = 0; RefreshObservation(); }
    private void RefreshObservation()
    {
        foreach (var name in new[] { nameof(Refreshing), nameof(FirstLoading), nameof(StateStale), nameof(InspectionUnknown), nameof(ServerStateHint), nameof(InspectionDetail), nameof(ShowResourceEmpty), nameof(ResourcesHint), nameof(StatusChipText), nameof(StatusChipIcon), nameof(StartHint), nameof(StartHelpText), nameof(RestoreHint), nameof(RestoreHelpText) }) Refresh(name);
    }

    private bool operationActive;
    private string operationTitle = "", operationServer = "", operationPhase = "", operationLatestActivity = "", lastCompletion = "";
    private DateTimeOffset operationStarted, operationUpdated, operationClock;
    public bool OperationActive => operationActive;
    public string OperationTitle => operationTitle;
    public string OperationServer => operationServer;
    public string OperationPhase => operationPhase;
    public string OperationLatestActivity => operationLatestActivity;
    public string OperationElapsed
    {
        get
        {
            var duration = operationClock < operationStarted ? TimeSpan.Zero : operationClock - operationStarted;
            return duration.TotalHours >= 1 ? $"Elapsed {(int)duration.TotalHours}h {duration.Minutes:00}m {duration.Seconds:00}s" : $"Elapsed {(int)duration.TotalMinutes}m {duration.Seconds:00}s";
        }
    }
    public string OperationLastUpdate => $"Last activity at {operationUpdated.LocalDateTime:h:mm:ss tt}";
    public bool HasLastCompletion => lastCompletion.Length > 0;
    public string LastCompletion => lastCompletion;
    public void BeginOperation(string title, string? serverName = null, string phase = "Waiting for manager", DateTimeOffset? now = null)
    {
        operationActive = true; operationTitle = title; operationServer = serverName ?? ServerName;
        operationPhase = phase; operationLatestActivity = "Waiting for an activity update.";
        operationStarted = operationUpdated = operationClock = now ?? DateTimeOffset.Now;
        Busy = true; RefreshOperation(); Announce($"{operationTitle}. {operationServer}. {operationPhase}.");
    }
    public void UpdateOperationPhase(string phase, string? detail = null, DateTimeOffset? now = null)
    {
        if (!OperationActive) return;
        var changedPhase = operationPhase != phase;
        operationPhase = phase;
        if (!string.IsNullOrWhiteSpace(detail)) operationLatestActivity = detail;
        operationUpdated = operationClock = now ?? DateTimeOffset.Now;
        RefreshOperation(); if (changedPhase) Announce($"{operationTitle}. {operationPhase}.");
    }
    public void FinishOperation(string? summary, bool succeeded, DateTimeOffset? now = null, string? outcome = null)
    {
        if (!OperationActive) return;
        operationClock = now ?? DateTimeOffset.Now;
        operationActive = false; operationPhase = outcome is "Finished" or "Interrupted" or "Deferred" or "State unverified" ? outcome : !succeeded ? "Failed" : summary?.StartsWith("Waiting", StringComparison.Ordinal) == true ? "Deferred" : "Completed";
        if (!string.IsNullOrWhiteSpace(summary)) { operationLatestActivity = summary; operationUpdated = operationClock; }
        lastCompletion = $"Last operation: {(operationPhase == "Completed" ? "✓" : operationPhase == "Failed" ? "!" : "•")} {operationTitle} · {operationServer} · {operationPhase} at {operationClock.LocalDateTime:MMM d, h:mm tt}";
        Busy = false; RefreshOperation(); Refresh(nameof(LastCompletion)); Refresh(nameof(HasLastCompletion));
        Announce($"{operationTitle}. {operationPhase}. {summary}");
    }
    public void TickOperation(DateTimeOffset? now = null)
    {
        if (!OperationActive) return;
        operationClock = now ?? DateTimeOffset.Now; Refresh(nameof(OperationElapsed));
    }
    private void RefreshOperation()
    {
        foreach (var name in new[] { nameof(OperationActive), nameof(OperationTitle), nameof(OperationServer), nameof(OperationPhase), nameof(OperationLatestActivity), nameof(OperationElapsed), nameof(OperationLastUpdate) }) Refresh(name);
    }

    private string errorSummary = "", errorDetail = "", errorActionText = "", errorDestination = "", announcement = "";
    public bool HasError => errorSummary.Length > 0;
    public string ErrorSummary => errorSummary;
    public string ErrorDetail => errorDetail;
    public string ErrorActionText => errorActionText;
    public string ErrorDestination => errorDestination;
    public string Announcement => announcement;
    public void Announce(string message) { Set(ref announcement, message, nameof(Announcement)); }
    private string? errorOwner;
    public void ReportError(string summary, string detail, string destination = "Activity", string actionText = "Open Activity", string? owner = null)
    {
        errorOwner = owner;
        errorSummary = summary; errorDetail = detail; errorDestination = destination; errorActionText = actionText;
        RefreshError(); Announce($"{summary}. {actionText}.");
    }
    public void ClearError()
    {
        errorOwner = null;
        errorSummary = errorDetail = errorDestination = errorActionText = ""; RefreshError();
    }
    public void ClearErrorFor(string owner)
    {
        if (errorOwner == owner) ClearError();
    }
    private void RefreshError()
    {
        foreach (var name in new[] { nameof(HasError), nameof(ErrorSummary), nameof(ErrorDetail), nameof(ErrorActionText), nameof(ErrorDestination) }) Refresh(name);
    }
    private bool showServerPicker = true;
    public bool ShowServerPicker { get => showServerPicker; set => Set(ref showServerPicker, value); }
    private string pageTitle = "Servers";
    public string PageTitle { get => pageTitle; set => Set(ref pageTitle, value); }
    private string status = "Not connected";
    public string Status { get => status; set => Set(ref status, value); }
    private string players = "-", cpu = "-", memory = "-", uptime = "-", diskFree = "-", build = "Not checked yet", backup = "No recovery points yet", automation = "Automation is off", notice = "Ready. Create a server or import a world to get started.", activity = "", diagnostics = "Select a server, then refresh diagnostics.", nextUpdate = "Not scheduled", nextBackup = "Not scheduled";
    public string Players { get => players; set => Set(ref players, value); }
    public string Cpu { get => cpu; set => Set(ref cpu, value); }
    public string Memory { get => memory; set => Set(ref memory, value); }
    public string Uptime { get => uptime; set => Set(ref uptime, value); }
    public string DiskFree { get => diskFree; set => Set(ref diskFree, value); }
    public string BuildSummary { get => build; set => Set(ref build, value); }
    public string BackupSummary { get => backup; set => Set(ref backup, value); }
    public string AutomationSummary { get => automation; set => Set(ref automation, value); }
    public string Notice { get => notice; set { Set(ref notice, value); if (!OperationActive) Announce(value); } }
    public string Activity { get => activity; set => Set(ref activity, value); }
    public string Diagnostics { get => diagnostics; set => Set(ref diagnostics, value); }
    public string NextUpdate { get => nextUpdate; set => Set(ref nextUpdate, value); }
    public string NextBackup { get => nextBackup; set => Set(ref nextBackup, value); }
}
