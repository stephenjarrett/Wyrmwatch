using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Interactivity;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Styling;
using Avalonia.Threading;
using Wyrmwatch.Core;
using Wyrmwatch.Platform;

namespace Wyrmwatch.Desktop;

public partial class MainWindow : Window
{
    private readonly WorkspaceModel model = new();
    private readonly JsonStore store;
    private readonly BackupEngine backups = new();
    private readonly CancellationTokenSource closing = new();
    private readonly Queue<string> activity = new();
    private ManagerSettings settings = new();
    private AgentClient? service;
    private ISteamClient? steam;
    private TrayIcon? tray;
    private bool ready, exitRequested, changingProfile, agentTransition;
    private string historyFingerprint = "";
    private int backupRefreshVersion;

    public MainWindow() : this(null) { }
    internal MainWindow(AgentClient? connection)
    {
        store = new(Program.DataDirectory);
        InitializeComponent(); DataContext = model; InitializeUx(); InitializeAppUpdateControls();
        if (Program.Demo) Title = "Wyrmwatch · Demo preview";
        MemoryChart.Color = Color.Parse("#88C8AD"); MemoryChart.Maximum = 1024;
        try
        {
            settings = store.Read("settings.json", () => new ManagerSettings());
            if (Program.ApplyStartup && settings.LaunchAtLogin) DesktopIntegration.SetStartup(true);
            steam = new SteamClient(Path.Combine(Program.DataDirectory, "tools"));
            service = connection ?? new(Program.DataDirectory);
            service.Log += Log;
            service.Changed += () => Dispatcher.UIThread.Post(() => { SyncManagerOperation(); RefreshHistory(); });
            if (Program.Demo) settings = new() { Servers = [new() { Name = "The Ashen Reach", InstallPath = Path.Combine(Program.DataDirectory, "demo-server"), BackupPath = Path.Combine(Program.DataDirectory, "demo-backups") }] };
            foreach (var profile in settings.Servers) model.Profiles.Add(profile);
            model.SelectedProfile = model.Profiles.FirstOrDefault(p => p.Id == settings.SelectedServerId) ?? model.Profiles.FirstOrDefault();
            ThemePicker.SelectedIndex = settings.Theme switch { "Light" => 1, "System" => 2, _ => 0 };
            ApplyTheme(); CloseToTray.IsChecked = settings.CloseToTray; LaunchAtLogin.IsChecked = settings.LaunchAtLogin; KeepBackground.IsChecked = settings.BackgroundMode; ReduceMotion.IsChecked = settings.ReduceMotion; ApplyMotionPreference();
            LanguagePicker.ItemsSource = new[] { Localization.English }; LanguagePicker.SelectedIndex = 0;
            ready = true; if (!Program.HeadlessTest) SetupTray(); LoadProfile(); RefreshHistory(); Navigate(Navigation, new SelectionChangedEventArgs(ListBox.SelectionChangedEvent, Array.Empty<object>(), Array.Empty<object>()));
            Opened += async (_, _) => { try { if (Program.Minimized) WindowState = WindowState.Minimized; if (!Program.HeadlessTest) { await LoadLanguagesAsync(settings.Language); await PollLoopAsync(); } } catch (OperationCanceledException) { } };
            Closing += OnClosing;
            Closed += (_, _) => { closing.Cancel(); tray?.Dispose(); };
        }
        catch (Exception e)
        {
            model.Notice = "Startup stopped: " + e.Message; model.Busy = true;
            Content = StartupFailureView(e);
            Closed += (_, _) => (Application.Current!.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.Shutdown();
        }
    }
    private void Navigate(object? sender, SelectionChangedEventArgs e)
    {
        if (ServersPage is null) return;
        Control[] pages = [ServersPage, ServerSettingsPage, ResourcesPage, AutomationPage, BackupsPage, ActivityPage, SettingsPage, HelpPage, UpdatesPage];
        var index = Navigation.SelectedIndex; if (index < 0 || index >= pages.Length) return;
        for (var i = 0; i < pages.Length; i++) pages[i].IsVisible = i == index;
        if (PageScroll is not null) PageScroll.Offset = default;
        string[] titles = ["Servers", "Server settings", "Resources", "Automation", "Backups", "Activity", "App settings", "Help & diagnostics", "App updates"];
        model.PageTitle = Localization.Text(titles[index]);
        model.ShowServerPicker = (WorkspacePage)index is not (WorkspacePage.Servers or WorkspacePage.AppSettings or WorkspacePage.Help or WorkspacePage.AppUpdates);
    }
    private async void ServerSelected(object? sender, SelectionChangedEventArgs e)
    {
        if (!ready || changingProfile) return;
        var profile = e.AddedItems.OfType<ServerProfile>().FirstOrDefault();
        if (profile is null || profile.Id == model.SelectedProfile?.Id) return;
        try { model.SelectedProfile = profile; LoadProfile(); settings = settings with { SelectedServerId = profile.Id }; await SaveSettingsAsync(); await RefreshServersAsync(); }
        catch (Exception error) { model.Notice = error.Message; model.ReportError("Request needs attention", error.Message); }
    }
    private void LoadProfile()
    {
        model.SyncRows();
        model.ServerRunning = null;
        model.ResetResourceSamples(); model.BeginRefresh();
        model.RecoveryPending = false; model.InstalledBuildKnown = false;
        latestServerLog = ""; ServerLogText.Text = "";
        var p = model.SelectedProfile;
        if (p is null)
        {
            model.Status = "Not connected"; model.Players = model.Cpu = model.Memory = model.Uptime = model.DiskFree = "—";
            model.Backups.Clear(); model.BackupSummary = "No recovery points yet"; model.BuildSummary = "Not checked yet"; model.AutomationSummary = "Automation is off";
            CpuChart.Clear(); MemoryChart.Clear(); return;
        }
        ProfileName.Text = p.Name; InstallFolder.Text = p.InstallPath; DataFolder.Text = p.SavedPath; BackupFolder.Text = p.BackupPath;
        AutoUpdate.IsChecked = p.AutoUpdate; UpdateInterval.Value = p.UpdateMinutes; AutoBackup.IsChecked = p.AutoBackup; BackupInterval.Value = p.BackupHours; Retention.Value = p.RetainBackups;
        UseWindow.IsChecked = p.UseMaintenanceWindow; WindowStart.Text = p.WindowStart.ToString("HH:mm"); WindowEnd.Text = p.WindowEnd.ToString("HH:mm"); GamePort.Value = p.Port;
        if (!Program.Demo)
        {
            try { var values = GameConfiguration.Read(p.ConfigPath); OwnerId.Text = values.GetValueOrDefault("OwnerId", ""); GameServerName.Text = values.GetValueOrDefault("ServerName", ""); WorldName.Text = values.GetValueOrDefault("DefaultWorldName", ""); AdminPassword.Text = values.GetValueOrDefault("AdminPassword", ""); WorldPassword.Text = values.GetValueOrDefault("WorldPassword", ""); if (int.TryParse(values.GetValueOrDefault("Port"), out var port)) GamePort.Value = port; }
            catch (Exception e) { model.Notice = e.Message; }
        }
        model.Status = "Checking…"; model.Players = model.Cpu = model.Memory = model.Uptime = "—";
        CpuChart.Clear(); MemoryChart.Clear();
        try { var installed = steam?.InstalledBuild(p); model.InstalledBuildKnown = installed is not null; model.BuildSummary = "Installed build: " + (Program.Demo ? "20681432 · Demo" : installed ?? "Unknown"); }
        catch (IOException) { model.InstalledBuildKnown = false; model.BuildSummary = "Installed build: Unavailable"; }
        UpdateScheduleLabels(); _ = RefreshBackupsAsync();
    }
    private async Task PollLoopAsync()
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(3));
        do
        {
            if (agentTransition) continue;
            var observedId = model.SelectedProfile?.Id;
            try
            {
                var p = model.SelectedProfile;
                if (p is not null)
                {
                    if (Program.Demo)
                    {
                        model.ServerRunning = true; model.Status = "Demo · online"; model.Players = "2"; model.Cpu = "8.4%"; model.Memory = "3.2 GB"; model.Uptime = "4h 12m"; model.DiskFree = "142.8 GB"; model.Notice = "Demo workspace · illustrative data · server actions are disabled";
                        if (CpuChart is not null) { CpuChart.Add(8 + Random.Shared.NextDouble() * 2); MemoryChart.Add(3200 + Random.Shared.NextDouble() * 100); }
                    }
                    else
                    {
                        model.BeginRefresh();
                        var state = await service!.ObserveAsync(p, closing.Token);
                        if (p.Id != model.SelectedProfile?.Id) continue;
                        ApplyServerState(state);
                        if (state.Accessible && state.Running) { CpuChart!.Add(state.CpuPercent); MemoryChart.Add(state.MemoryBytes / 1048576d); model.RecordResourceSample(); }
                        model.DiskFree = DiskSpace(p.InstallPath);
                    }
                }
                if (!Program.Demo)
                {
                    if (p is null) await service!.RefreshAsync(closing.Token);
                    if (Navigation.SelectedIndex == (int)WorkspacePage.Activity && LiveServerLog.IsChecked == true) await RefreshServerLogAsync();
                    UpdateRows();
                    UpdateScheduleLabels();
                    AgentStatusText.Text = service!.Background ? "Background manager active · schedules continue when the window closes" : "Background manager connected · closes with this app";
                }
            }
            catch (OperationCanceledException) { break; }
            catch (Exception e) { if (observedId == model.SelectedProfile?.Id) { model.FailRefresh(e.Message); model.Status = "Unavailable"; } model.Notice = e.Message; }
        } while (!closing.IsCancellationRequested && await timer.WaitForNextTickAsync(closing.Token));
    }
    internal async Task RefreshServersAsync()
    {
        var observedId = model.SelectedProfile?.Id; model.BeginRefresh();
        try
        {
            await service!.RefreshAsync(closing.Token);
            UpdateRows();
            if (service.Servers.FirstOrDefault(s => s.Id == model.SelectedProfile?.Id) is { } selected) ApplyServerState(selected.State);
            await RefreshBackupsAsync();
        }
        catch (Exception error) { if (observedId == model.SelectedProfile?.Id) model.FailRefresh(error.Message); throw; }
    }
    private void UpdateRows()
    {
        model.SyncRows();
        model.UpdateStartPrerequisites(service!.Servers);
        foreach (var row in model.ServerRows)
        {
            var managed = service!.Servers.FirstOrDefault(s => s.Id == row.Id);
            row.Update(managed?.State, managed?.RecoveryRequired == true);
        }
        var selected = service!.Servers.FirstOrDefault(s => s.Id == model.SelectedProfile?.Id);
        model.RecoveryPending = selected?.RecoveryRequired == true;
        model.InstalledBuildKnown = !string.IsNullOrWhiteSpace(selected?.Build);
        if (selected is not null && (model.BuildSummary.StartsWith("Installed build:", StringComparison.Ordinal) || !model.InstalledBuildKnown)) model.BuildSummary = "Installed build: " + (selected.Build ?? "Unknown");
        if (selected is not null) model.Status = selected.RecoveryRequired ? "Recovery required" : !selected.State.Accessible ? "Needs attention" : selected.State.Running ? "Online" : "Stopped";
    }
    private async void ServerRowSelected(object? sender, SelectionChangedEventArgs e)
    {
        var row = e.AddedItems.OfType<ServerRow>().FirstOrDefault();
        if (!ready || changingProfile || row is null || row.Id == model.SelectedProfile?.Id) return;
        try
        {
            model.SelectedProfile = model.Profiles.First(p => p.Id == row.Id); LoadProfile();
            settings = settings with { SelectedServerId = row.Id }; await SaveSettingsAsync(); await RefreshServersAsync();
        }
        catch (Exception error) { model.Notice = error.Message; model.ReportError("Request needs attention", error.Message); }
    }
    private static string DiskSpace(string path) { try { return $"{new DriveInfo(Path.GetPathRoot(path)!).AvailableFreeSpace / 1073741824d:0.0} GB"; } catch { return "Unavailable"; } }
    private void UpdateScheduleLabels()
    {
        var p = model.SelectedProfile; if (p is null) return; var schedule = service?.Schedule(p.Id);
        model.AutomationSummary = $"Automatic updates {(p.AutoUpdate ? "on" : "off")} · Scheduled backups {(p.AutoBackup ? "on" : "off")}";
        model.NextUpdate = p.AutoUpdate ? schedule?.NextUpdate?.LocalDateTime.ToString("MMM d, h:mm tt") ?? "Scheduled after saving" : "Not scheduled";
        model.NextBackup = p.AutoBackup ? "Next backup: " + (schedule?.NextBackup?.LocalDateTime.ToString("MMM d, h:mm tt") ?? "Scheduled after saving") : "Not scheduled";
    }
    private void Log(string message)
    {
        Dispatcher.UIThread.Post(() =>
        {
            activity.Enqueue(message); while (activity.Count > 250) activity.Dequeue(); model.Activity = string.Join(Environment.NewLine, activity);
            if (model.OperationActive) model.UpdateOperationPhase(model.OperationPhase, message);
            activeSetupReport?.Invoke(message);
        });
    }
    private void RefreshHistory()
    {
        var records = service!.History.Snapshot().Take(100).ToArray();
        var fingerprint = string.Join('|', records.Select(r => r.Id + r.Status + r.Detail));
        if (fingerprint == historyFingerprint) return;
        historyFingerprint = fingerprint; model.Operations.Clear(); foreach (var item in records) model.Operations.Add(new(item));
    }
    private async Task RefreshBackupsAsync()
    {
        var refreshVersion = ++backupRefreshVersion;
        var p = model.SelectedProfile; if (p is null || Program.Demo) return;
        try
        {
            var list = await Task.Run(() => backups.List(p)); if (refreshVersion != backupRefreshVersion || p.Id != model.SelectedProfile?.Id) return;
            var selectedArchive = (BackupList.SelectedItem as BackupRow)?.Info.Path;
            model.Backups.Clear(); foreach (var item in list) model.Backups.Add(new(item));
            BackupList.SelectedItem = model.Backups.FirstOrDefault(row => row.Info.Path == selectedArchive);
            model.BackupSummary = list.Count == 0 ? "No recovery points yet" : $"{list.Count} recovery points · latest {list[0].Manifest.Created.LocalDateTime:MMM d, h:mm tt}";
        }
        catch (Exception e) { if (refreshVersion == backupRefreshVersion && p.Id == model.SelectedProfile?.Id) model.Notice = e.Message; }
    }
    private async Task Run(Func<ServerProfile, Task<string>> action, string title = "Server operation")
    {
        if (Program.Demo || model.SelectedProfile is null || model.Busy || service!.Busy) return;
        var profile = model.SelectedProfile;
        string? result = null;
        try
        {
            BeginTrackedOperation(title, profile.Name); model.Notice = title + " in progress.";
            model.UpdateOperationPhase("Processing server request"); result = await Task.Run(() => action(profile)); model.Notice = result;
            model.UpdateOperationPhase("Checking resulting server state");
            var state = await service.ObserveAsync(profile, closing.Token);
            if (model.SelectedProfile?.Id == profile.Id) ApplyServerState(state);
            UpdateRows();
            FinishTrackedOperation(model.Notice, true);
        }
        catch (Exception e)
        {
            if (result is null)
            {
                // A refused request does not establish an inspection failure.
                // Read current state without retrying the action or relaxing its gate.
                try
                {
                    var state = await service.ObserveAsync(profile, closing.Token);
                    if (model.SelectedProfile?.Id == profile.Id) ApplyServerState(state);
                    UpdateRows();
                }
                catch (Exception observationError) { if (model.SelectedProfile?.Id == profile.Id) model.FailRefresh(observationError.Message); }
                model.Notice = e.Message; ReportOperationFailure(title, e); FinishTrackedOperation(title + " failed", false);
            }
            else
            {
                if (model.SelectedProfile?.Id == profile.Id) model.FailRefresh(e.Message);
                model.Notice = result;
                model.ReportError(title + " request completed; resulting state could not be verified.", e.Message, "Refresh", "Retry server check");
                FinishTrackedOperation(result, false, "State unverified");
            }
        }
        finally { model.Busy = service.Busy; await RefreshBackupsAsync(); RefreshHistory(); }
    }
    private async Task SaveSettingsAsync() { if (!Program.Demo) await service!.SavePreferencesAsync(settings); }
    private async Task ReplaceProfileAsync(ServerProfile p)
    {
        p.Validate(); var index = model.Profiles.ToList().FindIndex(s => s.Id == p.Id);
        await service!.SaveProfileAsync(p);
        changingProfile = true;
        try { model.Profiles[index] = p; model.SelectedProfile = p; }
        finally { changingProfile = false; }
        model.SyncRows(); UpdateScheduleLabels(); model.Notice = "Preferences saved.";
    }
    private async Task<string?> Folder(string title) => (await StorageProvider.OpenFolderPickerAsync(new() { Title = title, AllowMultiple = false })).FirstOrDefault()?.TryGetLocalPath();
    private async void ConnectServer(object? sender, RoutedEventArgs e)
    {
        if (Program.Demo) { model.Notice = "Exit demo mode to connect a real server."; return; }
        if (!model.CanManage) return;
        try
        {
            var dialog = new CreateServerDialog(model.Profiles.ToArray(), CreateServerAsync, Program.HeadlessTest ? store.DirectoryPath : null, ImportWorldAsync, PreparedActions());
            await dialog.ShowDialog(this); await ReturnFromSetupAsync(dialog);
        }
        catch (Exception error) { await ShowSetupErrorAsync("Could not import world", error.Message); }
    }
    private async void NewServer(object? sender, RoutedEventArgs e)
    {
        if (Program.Demo) { model.Notice = "Exit demo mode to create a real server."; return; }
        if (!model.CanManage) return;
        try
        {
            var dialog = new CreateServerDialog(model.Profiles.ToArray(), CreateServerAsync, Program.HeadlessTest ? store.DirectoryPath : null, preparedActions: PreparedActions());
            await dialog.ShowDialog(this); await ReturnFromSetupAsync(dialog);
        }
        catch (Exception error) { await ShowSetupErrorAsync("Could not create server", error.Message); }
    }
    private async Task CreateServerAsync(ServerCreationPlan plan, Action<string> report)
    {
        await Task.Run(() => plan.Validate(model.Profiles.ToArray()));
        if (service!.Busy) throw new IOException("Another server operation is still running. Wait for it to finish.");
        BeginTrackedOperation("Create a server", plan.Profile.Name, "Downloading and configuring managed server");
        activeSetupReport = report;
        try
        {
            report("Downloading and configuring the new server… Progress is recorded in Activity.");
            var profile = await service.CreateServerAsync(plan);
            await AddProfileAsync(profile);
            model.ClearErrorFor(SetupErrorOwner(plan.Profile.InstallPath));
            model.Notice = "Server created. Press Start when ready. Automation is off.";
            FinishTrackedOperation(model.Notice, true);
        }
        catch (Exception error) { ReportOperationFailure("Server setup", error, SetupErrorOwner(plan.Profile.InstallPath)); FinishTrackedOperation("Server setup failed", false); throw; }
        finally { activeSetupReport = null; model.Busy = service.Busy; RefreshHistory(); }
    }
    private async Task ImportWorldAsync(ServerCreationPlan plan, WorldImportPlan source, Action<string> report)
    {
        if (service!.Busy) throw new IOException("Another server operation is still running. Wait for it to finish.");
        BeginTrackedOperation("Import a world", plan.Profile.Name, "Provisioning managed server and copying world");
        activeSetupReport = report;
        try
        {
            report("Downloading a separate server and copying the reviewed world. Source files are preserved. Progress is recorded in Activity.");
            await AddProfileAsync(await service!.ImportWorldAsync(plan, source, true));
            model.ClearErrorFor(SetupErrorOwner(plan.Profile.InstallPath));
            model.Notice = "World imported into a new, stopped connection. Verify the world after Start; automation is off.";
            FinishTrackedOperation(model.Notice, true);
        }
        catch (Exception error) { ReportOperationFailure("World import", error, SetupErrorOwner(plan.Profile.InstallPath)); FinishTrackedOperation("World import failed", false); throw; }
        finally { activeSetupReport = null; model.Busy = service.Busy; }
    }
    private async Task AddProfileAsync(ServerProfile profile)
    {
        changingProfile = true;
        try { model.Profiles.Add(profile); model.SelectedProfile = profile; }
        finally { changingProfile = false; }
        settings = settings with { SelectedServerId = profile.Id };
        await SaveSettingsAsync(); LoadProfile(); ShowPage(WorkspacePage.Servers);
        await RefreshServersAsync();
    }
    private PreparedSetupActions PreparedActions() => new(service!.ListPreparedSetupsAsync, service!.ReviewPreparedSetupAsync, ResumePreparedSetupAsync);
    private async Task ResumePreparedSetupAsync(RecoveredSetup setup, Action<string> report)
    {
        if (service!.Busy) throw new IOException("Another server operation is still running.");
        BeginTrackedOperation("Resume prepared setup", setup.Profile.Name, "Verifying prepared files");
        activeSetupReport = report;
        try
        {
            report("Verifying the prepared files and saving their connection. No files are copied or downloaded.");
            await AddProfileAsync(await service.ResumePreparedSetupAsync(setup.Profile.InstallPath, setup.ReceiptToken));
            model.ClearErrorFor(SetupErrorOwner(setup.Profile.InstallPath));
            model.Notice = "Prepared setup recovered. Server stopped; files preserved and automation off.";
            FinishTrackedOperation(model.Notice, true);
        }
        catch (Exception error) { ReportOperationFailure("Prepared setup recovery", error, SetupErrorOwner(setup.Profile.InstallPath)); FinishTrackedOperation("Prepared setup recovery failed", false); throw; }
        finally { activeSetupReport = null; model.Busy = service.Busy; }
    }
    private async Task ShowSetupErrorAsync(string title, string message)
    {
        model.Notice = message; model.ReportError(title, message, "Servers", "Back to Servers");
        var dialog = new Window { Title = title, Width = 520, SizeToContent = SizeToContent.Height, CanResize = false, WindowStartupLocation = WindowStartupLocation.CenterOwner, ShowInTaskbar = false };
        var close = new Button { Content = "OK", Classes = { "primary" }, HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right };
        close.Click += (_, _) => dialog.Close();
        dialog.Content = new StackPanel { Margin = new Thickness(28), Spacing = 20, Children = { new TextBlock { Text = title, FontSize = 22, FontWeight = FontWeight.SemiBold }, new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap }, close } };
        await dialog.ShowDialog(this);
    }
    private void OpenDataFolder(object? sender, RoutedEventArgs e) { if (model.SelectedProfile is { } p) OpenPath(p.SavedPath); }
    private async void BrowseBackups(object? sender, RoutedEventArgs e) { var folder = await Folder("Choose a backup folder outside the server"); if (folder is not null) BackupFolder.Text = folder; }
    private async void SaveConnection(object? sender, RoutedEventArgs e)
    {
        if (model.SelectedProfile is not { } p || Program.Demo) return;
        if (!ValidateConnectionFields(p)) return;
        try { await ReplaceProfileAsync(p with { Name = ProfileName.Text?.Trim() ?? "", BackupPath = BackupFolder.Text?.Trim() ?? "" }); LoadProfile(); }
        catch (Exception error) { model.Notice = error.Message; model.ReportError("Request needs attention", error.Message); }
    }
    private async void SaveAutomation(object? sender, RoutedEventArgs e)
    {
        if (model.SelectedProfile is not { } p || Program.Demo) return;
        try
        {
            AutomationValidationError.IsVisible = false;
            if (!TimeOnly.TryParseExact(WindowStart.Text, "HH:mm", out var start)) { ShowFieldError(AutomationValidationError, WindowStart, "Use HH:mm for the maintenance-window start, for example 03:00. Nothing has been saved."); return; }
            if (!TimeOnly.TryParseExact(WindowEnd.Text, "HH:mm", out var end)) { ShowFieldError(AutomationValidationError, WindowEnd, "Use HH:mm for the maintenance-window end, for example 05:00. Nothing has been saved."); return; }
            if (UseWindow.IsChecked == true && start == end) { ShowFieldError(AutomationValidationError, WindowEnd, "The maintenance-window end must differ from its start. Nothing has been saved."); return; }
            await ReplaceProfileAsync(p with { AutoUpdate = AutoUpdate.IsChecked == true, AutoBackup = AutoBackup.IsChecked == true, UpdateMinutes = (int)(UpdateInterval.Value ?? 60), BackupHours = (int)(BackupInterval.Value ?? 6), RetainBackups = (int)(Retention.Value ?? 20), UseMaintenanceWindow = UseWindow.IsChecked == true, WindowStart = start, WindowEnd = end });
            await service!.RefreshAsync(); UpdateScheduleLabels();
        }
        catch (Exception error) { model.Notice = error.Message; model.ReportError("Request needs attention", error.Message); }
    }
    private async void WriteConfiguration(object? sender, RoutedEventArgs e)
    {
        if (!model.CanConfigure) { model.Notice = model.ConfigurationHint; return; }
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["OwnerId"] = OwnerId.Text ?? "", ["ServerName"] = GameServerName.Text ?? "", ["DefaultWorldName"] = WorldName.Text ?? "", ["AdminPassword"] = AdminPassword.Text ?? "", ["WorldPassword"] = WorldPassword.Text ?? "", ["Port"] = ((int)(GamePort.Value ?? 7777)).ToString() };
        if (!ValidateGameFields(values)) return;
        if (!await Confirm("Write game configuration?", "The server must be stopped. Existing settings will be backed up before these changes are written.", "Write configuration")) return;
        await Run(async p =>
        {
            var result = await service!.SaveConfigurationAsync(p, values);
            await Dispatcher.UIThread.InvokeAsync(async () => { await ReplaceProfileAsync(p with { Port = int.Parse(values["Port"]) }); model.Notice = result; });
            return result;
        }, "Save game configuration");
    }
    private async void StartServer(object? sender, RoutedEventArgs e) { if (model.CanStart) await Run(p => service!.StartAsync(p), "Start server"); }
    private async void StopServer(object? sender, RoutedEventArgs e) { if (model.CanStop && await Confirm("Stop this server?", "Connected players will be disconnected. Wyrmwatch will back up first and request a graceful shutdown.", "Stop server")) await Run(p => service!.StopAsync(p), "Back up and stop server"); }
    private async void RestartServer(object? sender, RoutedEventArgs e) { if (model.CanRestart && await Confirm("Restart this server?", "Connected players will be disconnected. A backup is required before restarting.", "Restart server")) await Run(p => service!.StopAsync(p, true), "Back up and restart server"); }
    private async void CreateBackup(object? sender, RoutedEventArgs e) => await Run(p => service!.BackupAsync(p), "Create verified backup");
    private async void CheckUpdates(object? sender, RoutedEventArgs e) { await Run(async p => { var result = await service!.CheckAsync(p); Dispatcher.UIThread.Post(() => { if (model.SelectedProfile?.Id == p.Id) model.BuildSummary = result; }); return result; }, "Check game update"); }
    private async void ApplyUpdate(object? sender, RoutedEventArgs e) { if (model.CanUpdate) await Run(p => service!.UpdateAsync(p), "Back up and apply game update"); else model.Notice = model.UpdateHint; }
    internal void ShowPage(WorkspacePage page) => Navigation.SelectedIndex = (int)page;
    private void GoServers(object? sender, RoutedEventArgs e) => ShowPage(WorkspacePage.Servers);
    private void GoBackups(object? sender, RoutedEventArgs e) => ShowPage(WorkspacePage.Backups);
    private void GoServerSettings(object? sender, RoutedEventArgs e) => ShowPage(WorkspacePage.ServerSettings);
    private void GoAutomation(object? sender, RoutedEventArgs e) => ShowPage(WorkspacePage.Automation);
    private void ApplyServerState(ServerSnapshot state)
    {
        model.ServerRunning = state.Accessible ? state.Running : null;
        model.CompleteRefresh(state.Accessible, state.ActivityReason);
        model.Status = model.RecoveryPending ? "Recovery required" : !state.Accessible ? "Needs attention" : state.Running ? "Online" : "Stopped";
        if (!state.Accessible) return;
        model.Players = state.Players?.ToString() ?? "?";
        model.Cpu = state.Running ? $"{state.CpuPercent:0.0}%" : "—";
        model.Memory = state.Running ? $"{state.MemoryBytes / 1073741824d:0.0} GB" : "—";
        model.Uptime = !state.Running ? "—" : state.Uptime.TotalDays >= 2 ? $"{(int)state.Uptime.TotalDays}d {state.Uptime.Hours}h" : $"{(int)state.Uptime.TotalHours}h {state.Uptime.Minutes:00}m";
    }
    private async void VerifyBackup(object? sender, RoutedEventArgs e)
    {
        if (BackupList.SelectedItem is not BackupRow row || model.SelectedProfile is not { } p) { model.Notice = "Select a backup first."; return; }
        if (!model.CanVerifySelected) return;
        try { BeginTrackedOperation("Verify backup", p.Name, "Verifying archived files and hashes"); await Task.Run(() => backups.VerifyAsync(row.Info.Path, p)); model.Notice = "Integrity verified. Every archived file matches its recorded hash."; FinishTrackedOperation(model.Notice, true); }
        catch (Exception error) { model.Notice = error.Message; ReportOperationFailure("Backup verification", error); FinishTrackedOperation("Backup verification failed", false); }
        finally { model.Busy = service?.Busy == true; }
    }
    private async void RestoreBackup(object? sender, RoutedEventArgs e)
    {
        if (!model.CanRestoreSelected || BackupList.SelectedItem is not BackupRow row) { model.Notice = model.RestoreHint; return; }
        var coverage = row.IncludesWorld ? "This restores world progress and configuration from the archive. Newer world progress will no longer be active." : "This archive contains configuration only. Current world files will stay active.";
        if (await Confirm("Restore this recovery point?", $"{row.Title}\n\n{coverage}\n\nThe server must be stopped. Existing data is retained for recovery; an empty save folder can also be restored. The server will remain stopped.", "Restore backup")) await Run(p => service!.RestoreAsync(p, row.Info.Path), "Restore backup");
    }
    private async void RecoverRestore(object? sender, RoutedEventArgs e)
    {
        if (!model.CanRecover) return;
        if (await Confirm("Recover the interrupted restore?", "Reconcile the interrupted restore. An incomplete swap is rolled back to the previous files; a completed restore is verified before recovery is cleared. Retained files remain available for inspection. The server stays stopped.", "Recover interrupted restore")) await Run(p => service!.RecoverAsync(p), "Recover interrupted restore");
    }
    private async Task<bool> Confirm(string title, string message, string accept)
    {
        var dialog = new Window { Title = title, Width = 480, SizeToContent = SizeToContent.Height, CanResize = false, WindowStartupLocation = WindowStartupLocation.CenterOwner, ShowInTaskbar = false };
        var yes = new Button { Content = accept, Classes = { "primary" } }; var no = new Button { Content = "Cancel", IsCancel = true };
        yes.Click += (_, _) => dialog.Close(true); no.Click += (_, _) => dialog.Close(false);
        dialog.Content = new StackPanel { Margin = new Thickness(28), Spacing = 20, Children = { new TextBlock { Text = title, FontSize = 22, FontWeight = FontWeight.SemiBold }, new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap }, new StackPanel { Orientation = Avalonia.Layout.Orientation.Horizontal, HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right, Spacing = 10, Children = { no, yes } } } };
        dialog.Opened += (_, _) => no.Focus();
        return await dialog.ShowDialog<bool>(this);
    }
    private void ThemeChanged(object? sender, SelectionChangedEventArgs e) { if (ready) ApplyTheme(); }
    private void ApplyTheme() => Application.Current!.RequestedThemeVariant = ThemePicker.SelectedIndex switch { 1 => ThemeVariant.Light, 2 => ThemeVariant.Default, _ => ThemeVariant.Dark };
    private async void SaveDesktop(object? sender, RoutedEventArgs e)
    {
        try
        {
            if (Program.Demo) { model.Notice = "Appearance previewed. Demo preferences are not saved."; return; }
            DesktopIntegration.SetStartup(LaunchAtLogin.IsChecked == true);
            settings = settings with { Theme = ThemePicker.SelectedIndex switch { 1 => "Light", 2 => "System", _ => "Dark" }, CloseToTray = CloseToTray.IsChecked == true, LaunchAtLogin = LaunchAtLogin.IsChecked == true, BackgroundMode = KeepBackground.IsChecked == true, ReduceMotion = ReduceMotion.IsChecked == true, Language = (LanguagePicker.SelectedItem as LanguagePack)?.Id ?? "en" }; await SaveSettingsAsync(); model.Notice = "Desktop preferences saved.";
        }
        catch (Exception error) { model.Notice = error.Message; model.ReportError("Request needs attention", error.Message); }
    }
    private void SetupTray()
    {
        var show = new NativeMenuItem("Open Wyrmwatch"); show.Click += (_, _) => { Show(); WindowState = WindowState.Normal; Activate(); };
        var exit = new NativeMenuItem("Quit Wyrmwatch"); exit.Click += async (_, _) => await QuitAsync();
        tray = new TrayIcon { ToolTipText = "Wyrmwatch · Dragonwilds server manager", Icon = new WindowIcon(Avalonia.Platform.AssetLoader.Open(new Uri("avares://Wyrmwatch/Assets/wyrmwatch.ico"))), Menu = new NativeMenu { Items = { show, new NativeMenuItemSeparator(), exit } }, IsVisible = true };
        tray.Clicked += (_, _) => { Show(); WindowState = WindowState.Normal; Activate(); };
        TrayIcon.SetIcons(Application.Current!, new TrayIcons { tray }); Icon = tray.Icon;
    }
    private void OnClosing(object? sender, WindowClosingEventArgs e)
    {
        if (Program.HeadlessTest) return;
        if (exitRequested) return;
        if (settings.CloseToTray && tray is not null && OperatingSystem.IsWindows()) { e.Cancel = true; Hide(); return; }
        e.Cancel = true; _ = QuitAsync();
    }
    private async Task QuitAsync()
    {
        if (agentTransition) return;
        if (downloadingApp) { model.Notice = "Finish or cancel the app download before quitting."; return; }
        if (!Program.Demo && !Program.HeadlessTest)
            try { await service!.RefreshAsync(); } catch (Exception error) { model.Notice = error.Message; return; }
        if (service?.Busy == true) { Show(); model.Notice = "An operation is still running. Wait for it to finish before quitting."; return; }
        if (!settings.BackgroundMode && service?.PersistentHost != true && model.Profiles.Any(p => p.AutoBackup || p.AutoUpdate))
            if (!await Confirm("Quit Wyrmwatch?", "Scheduled maintenance pauses while Wyrmwatch is closed. Your game server will keep running.", "Quit")) return;
        if (!Program.Demo && !Program.HeadlessTest && !settings.BackgroundMode && service?.PersistentHost != true)
        {
            agentTransition = true;
            try { await service!.StopAgentAsync(); }
            catch (Exception error) { agentTransition = false; model.Notice = error.Message; return; }
        }
        exitRequested = true; closing.Cancel(); tray?.Dispose(); (Application.Current!.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.Shutdown();
    }
    private async void CopyActivity(object? sender, RoutedEventArgs e) { if (Clipboard is not null) await Clipboard.SetValueAsync(DataFormat.Text, model.Activity); }
    private async void CopyInstallPath(object? sender, RoutedEventArgs e) { if (model.SelectedProfile is { } p) await CopyPathAsync(p.InstallPath, CopyInstallPathButton, "Installation path copied."); }
    private async void CopySavedPath(object? sender, RoutedEventArgs e) { if (model.SelectedProfile is { } p) await CopyPathAsync(p.SavedPath, CopySavedPathButton, "Save path copied."); }
    private async void DisconnectServer(object? sender, RoutedEventArgs e)
    {
        if (Program.Demo || model.SelectedProfile is not { } p) return;
        if (!await Confirm("Disconnect this server?", $"Disconnect {p.Name} from Wyrmwatch and stop its scheduled maintenance. Its game process, saves, installation, and backups stay in place. The connection is retained to protect its save location. You can import its world into a new connection later.", "Disconnect server")) return;
        try
        {
            await service!.SendAsync("admin/profiles/" + Uri.EscapeDataString(p.Id), HttpMethod.Delete);
            changingProfile = true;
            try { model.Profiles.Remove(p); model.SelectedProfile = model.Profiles.FirstOrDefault(); settings = settings with { Servers = model.Profiles.ToList(), SelectedServerId = model.SelectedProfile?.Id }; }
            finally { changingProfile = false; }
            ProfileName.Text = InstallFolder.Text = DataFolder.Text = BackupFolder.Text = "";
            OwnerId.Text = GameServerName.Text = WorldName.Text = AdminPassword.Text = WorldPassword.Text = "";
            model.Backups.Clear(); LoadProfile(); ShowPage(WorkspacePage.Servers); model.Notice = "Server disconnected. Server files and backups are preserved.";
        }
        catch (Exception error) { model.Notice = error.Message; model.ReportError("Request needs attention", error.Message); }
    }
    private void OpenBackupFolder(object? sender, RoutedEventArgs e) { if (model.SelectedProfile is { } p) OpenPath(p.BackupPath); }
    private void OpenServerLog(object? sender, RoutedEventArgs e) { if (model.SelectedProfile is { } p) OpenPath(p.LogPath); }
    private void OpenGuide(object? sender, RoutedEventArgs e) => OpenPath("https://runescapedragonwilds.help.jagex.com/hc/en-gb/articles/45365343055249-Dedicated-Servers-How-to-Guide");
    private void OpenProject(object? sender, RoutedEventArgs e) => OpenPath("https://github.com/stephenjarrett/Wyrmwatch");
    private void OpenLicense(object? sender, RoutedEventArgs e) => OpenDistributionDocument("LICENSE");
    private void OpenNotices(object? sender, RoutedEventArgs e) => OpenDistributionDocument("THIRD-PARTY-NOTICES.md");
    private void OpenDistributionDocument(string name)
    {
        var path = Path.Combine(AppContext.BaseDirectory, name == "LICENSE" ? "LICENSE.txt" : name);
        OpenPath(File.Exists(path) ? path : $"https://github.com/stephenjarrett/Wyrmwatch/blob/master/{name}");
    }
    private void OpenPath(string path) { try { if (!path.StartsWith("https://") && !File.Exists(path) && !Directory.Exists(path)) throw new IOException("The file or folder does not exist yet."); Process.Start(new ProcessStartInfo(path) { UseShellExecute = true }); } catch (Exception e) { model.Notice = e.Message; } }
    private async void RefreshDiagnostics(object? sender, RoutedEventArgs e)
    {
        var p = model.SelectedProfile;
        if (p is null) { model.Diagnostics = "No server is connected."; return; }
        if (Program.Demo) { model.Diagnostics = "Demo workspace. No real server is inspected."; return; }
        try { var state = await service!.ObserveAsync(p); model.Diagnostics = $"OS: {System.Runtime.InteropServices.RuntimeInformation.OSDescription}\nLauncher: {(File.Exists(p.Launcher) ? "Found" : "Missing")}\nSave data: {(Directory.Exists(p.SavedPath) ? "Found" : "Missing")}\nConfig: {(File.Exists(p.ConfigPath) ? "Found" : "Missing")}\nInstalled build: {steam!.InstalledBuild(p) ?? "Unknown"}\nFree space: {DiskSpace(p.InstallPath)}\nProcess access: {(state.Accessible ? "OK" : "Unavailable")}\nPlayer activity: {state.ActivityReason}\nBackup folder: {p.BackupPath}\nWorkspace: {Program.DataDirectory}"; }
        catch (Exception error) { model.Diagnostics = error.Message; }
    }
}
