using Avalonia;
using Avalonia.Animation;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;
using Avalonia.Media;
using Avalonia.Threading;
using Wyrmwatch.Core;

namespace Wyrmwatch.Desktop;

public partial class MainWindow
{
    private bool localOperation;
    private Action<string>? activeSetupReport;
    private string? observedOperationId;
    private DispatcherTimer? operationClock;
    private void InitializeUx()
    {
        AssociateLabels(this);
        operationClock = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        operationClock.Tick += (_, _) => { if (model.OperationActive) model.TickOperation(); };
        Opened += (_, _) => operationClock.Start();
        Closed += (_, _) => operationClock.Stop();
    }
    internal static void AssociateLabels(Control root)
    {
        foreach (var input in root.GetLogicalDescendants().OfType<Control>().Where(c => c is TextBox or NumericUpDown or ComboBox))
        {
            TextBlock? label = null;
            Control node = input;
            while (label is null && node.GetLogicalParent() is Control parent)
            {
                var siblings = parent.GetLogicalChildren().OfType<Control>().ToArray();
                var index = Array.IndexOf(siblings, node);
                label = siblings.Take(Math.Max(0, index)).OfType<TextBlock>().LastOrDefault();
                node = parent;
            }
            if (label is null || string.IsNullOrWhiteSpace(label.Text)) continue;
            AutomationProperties.SetLabeledBy(input, label);
            AutomationProperties.SetName(input, label.Text);
        }
        if (root.FindControl<ListBox>("Navigation") is { } navigation) AutomationProperties.SetName(navigation, "Workspace navigation");
    }
    private void BeginTrackedOperation(string title, string server, string phase = "Waiting for manager")
    {
        localOperation = true; model.BeginOperation(title, server, phase);
    }
    private void FinishTrackedOperation(string summary, bool succeeded, string? outcome = null)
    {
        model.FinishOperation(summary, succeeded, outcome: outcome); localOperation = false;
    }
    private void SyncManagerOperation()
    {
        if (service is null) return;
        if (!localOperation)
        {
            if (service.Busy && !model.OperationActive)
            {
                var record = service.History.Snapshot().FirstOrDefault(r => r.Status == "Running");
                observedOperationId = record?.Id;
                model.BeginOperation(record?.Action ?? "Manager operation", record?.ServerName ?? "Background manager", "Manager operation in progress", record?.Started);
            }
            else if (!service.Busy && model.OperationActive)
            {
                var result = service.History.Snapshot().FirstOrDefault(r => r.Id == observedOperationId && r.Finished is not null);
                FinishObservedOperation(result);
                observedOperationId = null;
            }
        }
        model.Busy = service.Busy || model.OperationActive;
    }
    internal void FinishObservedOperation(OperationRecord? result)
    {
        var outcome = result?.Status switch { "Succeeded" or "Failed" => null, "Interrupted" => "Interrupted", "Deferred" => "Deferred", _ => "Finished" };
        model.FinishOperation(result?.Detail ?? "Manager operation ended. Review Activity for its result.", result?.Status == "Succeeded", outcome: outcome);
        if (result?.Status is "Failed" or "Interrupted") model.ReportError("Manager operation needs attention", result.Detail);
    }
    private void ReportOperationFailure(string title, Exception error)
    {
        var recovery = model.RecoveryPending || title.Contains("restore", StringComparison.OrdinalIgnoreCase) || title.Contains("recovery", StringComparison.OrdinalIgnoreCase);
        model.ReportError(title + " stopped. " + (recovery ? "Check retained files and recovery here before retrying." : "Review Activity and verify server state before retrying."), error.Message, recovery ? "Backups" : "Activity", recovery ? "Open recovery in Backups" : "Open Activity");
    }
    private async Task ReturnFromSetupAsync(CreateServerDialog dialog)
    {
        if (!dialog.ReturnToServerRequested) return;
        if (dialog.CompletedProfile is { } created && model.Profiles.FirstOrDefault(p => p.Id == created.Id) is { } saved) model.SelectedProfile = saved;
        ShowPage(WorkspacePage.Servers);
        await RefreshServersAsync();
        if (model.CanStart) StartButton.Focus(); else StartPrerequisiteButton.Focus();
    }
    private void GoActivity(object? sender, RoutedEventArgs e) => ShowPage(WorkspacePage.Activity);
    private void GoStartPrerequisite(object? sender, RoutedEventArgs e) => GoDestination(model.StartHelpDestination);
    private void GoRestorePrerequisite(object? sender, RoutedEventArgs e) => GoDestination(model.RestoreHelpDestination);
    private void GoUpdatePrerequisite(object? sender, RoutedEventArgs e) => GoDestination(model.UpdateHelpDestination);
    private void GoErrorDestination(object? sender, RoutedEventArgs e) => GoDestination(model.ErrorDestination);
    private void DismissError(object? sender, RoutedEventArgs e) => model.ClearError();
    private async void GoDestination(string destination)
    {
        switch (destination)
        {
            case "Backups": ShowPage(WorkspacePage.Backups); if (model.RecoveryPending) RecoverRestoreButton.Focus(); break;
            case "Servers": ShowPage(WorkspacePage.Servers); break;
            case "ServerSettings": ShowPage(WorkspacePage.ServerSettings); break;
            case "AppUpdates": case "App updates": ShowPage(WorkspacePage.AppUpdates); CheckAppButton.Focus(); break;
            case "Help": case "Help & diagnostics": ShowPage(WorkspacePage.Help); break;
            case "Refresh":
                try { await RefreshServersAsync(); model.Announce("Server check finished."); }
                catch (Exception error) { model.ReportError("Server check failed. Open Help & diagnostics before retrying.", error.Message, "Help", "Open Help & diagnostics"); }
                break;
            case "CheckUpdates": CheckUpdates(this, new RoutedEventArgs()); break;
            case "Stop": StopServer(this, new RoutedEventArgs()); break;
            case "SelectBackup": ShowPage(WorkspacePage.Backups); if (model.NoBackups) CreateFirstBackupButton.Focus(); else { BackupList.Focus(); BackupList.BringIntoView(); } break;
            default: ShowPage(WorkspacePage.Activity); break;
        }
    }
    private void BackupSelected(object? sender, SelectionChangedEventArgs e) => model.BackupSelected = BackupList.SelectedItem is BackupRow;
    private readonly Dictionary<Button, (object? Original, int Generation)> copyAcknowledgements = new();
    internal async Task CopyPathAsync(string path, Button button, string message)
    {
        if (Clipboard is null) { model.ReportError("Clipboard unavailable", "Select and copy the displayed path manually.", "ServerSettings", "Open server settings"); return; }
        try
        {
            await Clipboard.SetValueAsync(DataFormat.Text, path);
            var previous = copyAcknowledgements.GetValueOrDefault(button, (Original: button.Content, Generation: 0));
            var generation = previous.Generation + 1;
            copyAcknowledgements[button] = (previous.Original, generation);
            button.SetCurrentValue(ContentControl.ContentProperty, "Copied ✓"); model.Notice = message; model.Announce(message);
            try { await Task.Delay(1800, closing.Token); }
            catch (OperationCanceledException) { }
            finally
            {
                if (copyAcknowledgements.TryGetValue(button, out var current) && current.Generation == generation)
                {
                    button.SetCurrentValue(ContentControl.ContentProperty, current.Original);
                    copyAcknowledgements.Remove(button);
                }
            }
        }
        catch (Exception error) { model.ReportError("Could not copy this path", error.Message, "ServerSettings", "Open server settings"); }
    }
    private void MotionChanged(object? sender, RoutedEventArgs e) { if (ready) ApplyMotionPreference(); }
    private void ApplyMotionPreference()
    {
        foreach (var button in this.GetLogicalDescendants().OfType<Button>())
            button.Transitions = ReduceMotion.IsChecked == true ? null : new Transitions { new DoubleTransition { Property = OpacityProperty, Duration = TimeSpan.FromMilliseconds(120) } };
    }
    private Control StartupFailureView(Exception error)
    {
        var open = new Button { Name = "OpenStartupWorkspace", Content = "Open workspace folder" }; open.Click += (_, _) => OpenPath(Program.DataDirectory);
        var copy = new Button { Content = "Copy diagnostics" }; copy.Click += async (_, _) => { if (Clipboard is not null) await Clipboard.SetValueAsync(DataFormat.Text, "Workspace: " + Program.DataDirectory + "\n" + error.Message); };
        return new StackPanel { Margin = new Thickness(40), Spacing = 18, Children = {
            new TextBlock { Text = "Wyrmwatch could not load this workspace.", FontSize = 24 },
            new TextBlock { Text = "Existing files have been preserved. Review the workspace or copy these diagnostics before retrying.", TextWrapping = TextWrapping.Wrap },
            new TextBlock { Text = Program.DataDirectory, TextWrapping = TextWrapping.Wrap, FontFamily = new FontFamily("Cascadia Mono, Consolas, monospace") },
            new Expander { Header = "Technical details", IsExpanded = true, Content = new TextBlock { Text = error.Message, TextWrapping = TextWrapping.Wrap } },
            new WrapPanel { Children = { open, copy } } } };
    }
    private static void ShowFieldError(TextBlock message, Control field, string text)
    {
        message.Text = text; message.IsVisible = true; AutomationProperties.SetHelpText(field, text); field.Focus(); field.BringIntoView();
    }
    internal bool ValidateConnectionFields(ServerProfile profile)
    {
        ConnectionValidationError.IsVisible = BackupFolderValidationError.IsVisible = false;
        if (string.IsNullOrWhiteSpace(ProfileName.Text)) { ShowFieldError(ConnectionValidationError, ProfileName, "Give this connection a display name. Nothing has been saved."); return false; }
        try { (profile with { Name = ProfileName.Text!.Trim(), BackupPath = BackupFolder.Text?.Trim() ?? "" }).Validate(); return true; }
        catch (ArgumentException error) { ShowFieldError(BackupFolderValidationError, BackupFolder, error.Message + " Nothing has been saved."); return false; }
    }
    internal bool ValidateGameFields(IReadOnlyDictionary<string, string> values)
    {
        ConfigurationValidationError.IsVisible = false;
        var fields = new Dictionary<string, Control> { ["OwnerId"] = OwnerId, ["ServerName"] = GameServerName, ["DefaultWorldName"] = WorldName, ["AdminPassword"] = AdminPassword, ["WorldPassword"] = WorldPassword, ["Port"] = GamePort };
        foreach (var key in new[] { "OwnerId", "ServerName", "DefaultWorldName", "AdminPassword" })
            if (string.IsNullOrWhiteSpace(values.GetValueOrDefault(key))) { ShowFieldError(ConfigurationValidationError, fields[key], AutomationProperties.GetName(fields[key]) + " is required. Nothing has been saved."); return false; }
        try { GameConfiguration.Merge("", values); return true; }
        catch (ArgumentException error)
        {
            var invalid = values.FirstOrDefault(v => v.Value.IndexOfAny(['\r', '\n', '\0', '"']) >= 0);
            ShowFieldError(ConfigurationValidationError, fields.GetValueOrDefault(invalid.Key ?? "Port", GamePort), error.Message + " Nothing has been saved."); return false;
        }
    }
}
