using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using Wyrmwatch.Core;

namespace Wyrmwatch.Desktop;

public sealed record BackupRow(BackupInfo Info)
{
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
    public ObservableCollection<BackupRow> Backups { get; } = [];
    public ObservableCollection<OperationRow> Operations { get; } = [];
    private ServerProfile? selectedProfile;
    public ServerProfile? SelectedProfile { get => selectedProfile; set { Set(ref selectedProfile, value); Refresh(null); } }
    public bool HasServer => SelectedProfile is not null;
    public bool NoServer => !HasServer;
    public string ServerName => SelectedProfile?.Name ?? "No server connected";
    public string ServerPath => SelectedProfile?.InstallPath ?? "";
    private bool busy;
    public bool Busy { get => busy; set { Set(ref busy, value); Refresh(nameof(CanAct)); } }
    public bool CanAct => HasServer && !Busy && !Program.Demo;
    private string pageTitle = "Your server, at a glance.";
    public string PageTitle { get => pageTitle; set => Set(ref pageTitle, value); }
    private string pageSubtitle = "A quieter way to keep your world running.";
    public string PageSubtitle { get => pageSubtitle; set => Set(ref pageSubtitle, value); }
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
