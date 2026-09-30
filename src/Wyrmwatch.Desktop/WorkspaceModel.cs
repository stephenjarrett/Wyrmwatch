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
    public ObservableCollection<OperationRow> Operations { get; } = [];
    private ServerProfile? selectedProfile;
    public ServerProfile? SelectedProfile { get => selectedProfile; set { if (selectedProfile?.Id != value?.Id) serverRunning = null; Set(ref selectedProfile, value); Refresh(null); } }
    public bool HasServer => SelectedProfile is not null;
    public bool NoServer => !HasServer;
    public string ServerName => SelectedProfile?.Name ?? "No server connected";
    public string ServerPath => SelectedProfile?.InstallPath ?? "";
    private bool recoveryPending;
    public bool RecoveryPending { get => recoveryPending; set { Set(ref recoveryPending, value); RefreshActions(); } }
    public string RecoveryHint => "An interrupted restore needs recovery. Start, configuration changes and updates are blocked. Open Backups and recover the interrupted restore while the server is stopped; retained files remain available.";
    private bool busy;
    public bool Busy { get => busy; set { Set(ref busy, value); RefreshActions(); } }
    public bool CanAct => HasServer && !Busy && !Program.Demo && !RecoveryPending;
    public bool CanRecover => HasServer && !Busy && !Program.Demo && RecoveryPending && ServerRunning == false;
    private bool? serverRunning;
    public bool? ServerRunning { get => serverRunning; set { Set(ref serverRunning, value); RefreshActions(); } }
    public bool CanStart => CanAct && ServerRunning == false;
    public bool CanStop => HasServer && !Busy && !Program.Demo && ServerRunning == true;
    public bool CanRestart => CanAct && ServerRunning == true;
    public bool CanRestore => CanAct && ServerRunning == false;
    private bool installedBuildKnown;
    public bool InstalledBuildKnown { get => installedBuildKnown; set { Set(ref installedBuildKnown, value); RefreshActions(); } }
    public bool CanUpdate => CanAct && InstalledBuildKnown;
    public string UpdateHint => InstalledBuildKnown ? "Game updates require a verified backup and no connected players. Unknown player activity defers the update." : "Apply game update is unavailable until the installed Steam build is known. Check for updates to review build information; no server files are changed by the check.";
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
    private void RefreshActions() { foreach (var name in new[] { nameof(CanAct), nameof(CanStart), nameof(CanStop), nameof(CanRestart), nameof(CanRestore), nameof(CanConfigure), nameof(CanManage), nameof(CanRecover), nameof(CanUpdate), nameof(UpdateHint), nameof(ConfigurationHint), nameof(ActionHint) }) Refresh(name); }
    private bool showServerPicker = true;
    public bool ShowServerPicker { get => showServerPicker; set => Set(ref showServerPicker, value); }
    private string pageTitle = "Servers";
    public string PageTitle { get => pageTitle; set => Set(ref pageTitle, value); }
    private string status = "Not connected";
    public string Status { get => status; set => Set(ref status, value); }
    private string players = "—", cpu = "—", memory = "—", uptime = "—", diskFree = "—", build = "Not checked yet", backup = "No recovery points yet", automation = "Automation is off", notice = "Ready. Connect a server to get started.", activity = "", diagnostics = "Select a server, then refresh diagnostics.", nextUpdate = "Not scheduled", nextBackup = "Not scheduled";
    public string Players { get => players; set => Set(ref players, value); }
    public string Cpu { get => cpu; set => Set(ref cpu, value); }
    public string Memory { get => memory; set => Set(ref memory, value); }
    public string Uptime { get => uptime; set => Set(ref uptime, value); }
    public string DiskFree { get => diskFree; set => Set(ref diskFree, value); }
    public string BuildSummary { get => build; set => Set(ref build, value); }
    public string BackupSummary { get => backup; set => Set(ref backup, value); }
    public string AutomationSummary { get => automation; set => Set(ref automation, value); }
    public string Notice { get => notice; set => Set(ref notice, value); }
    public string Activity { get => activity; set => Set(ref activity, value); }
    public string Diagnostics { get => diagnostics; set => Set(ref diagnostics, value); }
    public string NextUpdate { get => nextUpdate; set => Set(ref nextUpdate, value); }
    public string NextBackup { get => nextBackup; set => Set(ref nextBackup, value); }
}
