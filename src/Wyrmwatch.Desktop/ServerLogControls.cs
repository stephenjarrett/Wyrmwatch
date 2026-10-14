using Avalonia.Interactivity;
using Wyrmwatch.Core;

namespace Wyrmwatch.Desktop;

public partial class MainWindow
{
    private string latestServerLog = "";
    private bool loadingServerLog;
    private bool savingFollowLog;
    internal async Task SaveFollowLogAsync(bool follow)
    {
        if (!ready || savingFollowLog) return;
        savingFollowLog = true;
        var previous = settings;
        LiveServerLog.IsEnabled = false;
        try
        {
            var updated = settings with { FollowLog = follow };
            if (!Program.Demo) await service!.SavePreferencesAsync(updated);
            settings = updated;
            if (follow) ServerLogText.CaretIndex = ServerLogText.Text?.Length ?? 0;
        }
        catch (Exception error)
        {
            LiveServerLog.IsChecked = previous.FollowLog;
            model.ReportError("Follow log preference could not be saved.", error.Message, "Activity", "Open Activity");
        }
        finally { savingFollowLog = false; LiveServerLog.IsEnabled = true; }
    }
    private async void FollowLogChanged(object? sender, RoutedEventArgs e) => await SaveFollowLogAsync(LiveServerLog.IsChecked == true);
    private async void RefreshServerLog(object? sender, RoutedEventArgs e) => await RefreshServerLogAsync();
    private async Task RefreshServerLogAsync()
    {
        if (loadingServerLog || Program.Demo || Program.HeadlessTest || agentTransition || model.SelectedProfile is not { } profile) return;
        loadingServerLog = true;
        try
        {
            var result = await service!.GetAsync<ActionResult>($"admin/servers/{Uri.EscapeDataString(profile.Id)}/log", closing.Token);
            if (profile.Id != model.SelectedProfile?.Id) return;
            DisplayServerLog(result.Message);
        }
        catch (Exception error) { if (!closing.IsCancellationRequested) model.Notice = "Game log: " + error.Message; }
        finally { loadingServerLog = false; }
    }
    internal void DisplayServerLog(string text)
    {
        latestServerLog = text;
        FilterServerLog(null, null!);
    }
    private void FilterServerLog(object? sender, Avalonia.Controls.TextChangedEventArgs e)
    {
        if (ServerLogText is null) return;
        var filter = ServerLogFilter.Text?.Trim() ?? "";
        var text = filter.Length == 0 ? latestServerLog : string.Join('\n', latestServerLog.Split('\n').Where(line => line.Contains(filter, StringComparison.OrdinalIgnoreCase)));
        if (ServerLogText.Text != text)
        {
            ServerLogText.Text = text;
            if (LiveServerLog.IsChecked == true) ServerLogText.CaretIndex = text.Length;
        }
    }
}
