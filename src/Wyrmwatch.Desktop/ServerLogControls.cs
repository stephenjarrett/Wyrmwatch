using Avalonia.Interactivity;
using Dragonwilds.Core;

namespace Wyrmwatch.Desktop;

public partial class MainWindow
{
    private string latestServerLog = "";
    private bool loadingServerLog;
    private async void RefreshServerLog(object? sender, RoutedEventArgs e) => await RefreshServerLogAsync();
    private async Task RefreshServerLogAsync()
    {
        if (loadingServerLog || Program.Demo || Program.HeadlessTest || agentTransition || model.SelectedProfile is not { } profile) return;
        loadingServerLog = true;
        try
        {
            var result = await service!.GetAsync<ActionResult>($"admin/servers/{Uri.EscapeDataString(profile.Id)}/log", closing.Token);
            if (profile.Id != model.SelectedProfile?.Id) return;
            latestServerLog = result.Message; FilterServerLog(null, null!);
        }
        catch (Exception error) { if (!closing.IsCancellationRequested) model.Notice = "Game log: " + error.Message; }
        finally { loadingServerLog = false; }
    }
    private void FilterServerLog(object? sender, Avalonia.Controls.TextChangedEventArgs e)
    {
        if (ServerLogText is null) return;
        var filter = ServerLogFilter.Text?.Trim() ?? "";
        var text = filter.Length == 0 ? latestServerLog : string.Join('\n', latestServerLog.Split('\n').Where(line => line.Contains(filter, StringComparison.OrdinalIgnoreCase)));
        if (ServerLogText.Text != text) ServerLogText.Text = text;
    }
}
