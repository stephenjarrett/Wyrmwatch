using System.Diagnostics;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Interactivity;
using Dragonwilds.Core;

namespace Wyrmwatch.Desktop;

public partial class MainWindow
{
    private AppRelease? appRelease;
    private PreparedUpdate? preparedUpdate;
    private bool checkingApp, downloadingApp;
    private CancellationTokenSource? appDownloadCancellation;
    private DateTimeOffset nextAppCheck = DateTimeOffset.MinValue;
    private static string AppRuntime => OperatingSystem.IsWindows() ? "win-x64" : "linux-x64";
    private async void CheckManagerUpdate(object? sender, RoutedEventArgs e) => await CheckManagerUpdateAsync();
    private async Task CheckManagerUpdateAsync()
    {
        if (checkingApp || downloadingApp || Program.Demo || Program.HeadlessTest) return;
        checkingApp = true; nextAppCheck = DateTimeOffset.UtcNow.AddHours(6);
        try
        {
            AppUpdateStatus.Text = "Checking published releases…";
            var version = typeof(MainWindow).Assembly.GetName().Version!;
            appRelease = await new AppUpdates().CheckAsync(new Version(version.Major, version.Minor, version.Build), AppRuntime, closing.Token);
            AppUpdateStatus.Text = appRelease is null ? "No newer published release is available." : $"Wyrmwatch {appRelease.Version} is available · {appRelease.Bytes / 1048576d:0.0} MB";
            AppReleaseNotes.Text = appRelease?.Notes ?? "Updates appear here when a newer stable release is published to stephenjarrett/Wyrmwatch.";
            DownloadAppButton.IsEnabled = appRelease is not null;
        }
        catch (Exception error) { AppUpdateStatus.Text = "Update check: " + error.Message; }
        finally { checkingApp = false; }
    }
    private async void DownloadManagerUpdate(object? sender, RoutedEventArgs e)
    {
        if (appRelease is null || downloadingApp || Program.Demo) return;
        downloadingApp = true; DownloadAppButton.IsEnabled = false; SwitchAppButton.IsEnabled = false;
        appDownloadCancellation = CancellationTokenSource.CreateLinkedTokenSource(closing.Token);
        try
        {
            AppUpdateStatus.Text = "Downloading and verifying the update…";
            var progress = new Progress<double>(value => AppDownloadProgress.Value = value);
            preparedUpdate = await Task.Run(() => new AppUpdates().DownloadAsync(appRelease, Path.Combine(Program.DataDirectory, "app-updates"), AppRuntime, progress, appDownloadCancellation.Token));
            AppUpdateStatus.Text = $"Version {preparedUpdate.Version} is verified and ready. Your current version is retained."; SwitchAppButton.IsEnabled = true;
        }
        catch (OperationCanceledException) { AppUpdateStatus.Text = "Download cancelled. Your current app is unchanged."; }
        catch (Exception error) { AppUpdateStatus.Text = "Update download: " + error.Message; }
        finally { downloadingApp = false; DownloadAppButton.IsEnabled = appRelease is not null; appDownloadCancellation.Dispose(); appDownloadCancellation = null; }
    }
    private void CancelManagerDownload(object? sender, RoutedEventArgs e) => appDownloadCancellation?.Cancel();
    private async void SwitchManagerVersion(object? sender, RoutedEventArgs e)
    {
        if (preparedUpdate is null || Program.Demo || downloadingApp) return;
        try
        {
            await service!.RefreshAsync();
            if (service.PersistentHost) { model.Notice = "The update is staged. Stop the service when idle, update its registered application path, and restart it before opening the new desktop app."; return; }
            if (!await Confirm("Open the updated manager?", $"Wyrmwatch {preparedUpdate.Version} will open with your existing connections. The background manager will reconnect. Your game server will keep running, and the previous app is retained.", "Open updated version")) return;
            agentTransition = true;
            await service!.StopAgentAsync();
            using var current = Process.GetCurrentProcess();
            var launch = new ProcessStartInfo(preparedUpdate.Executable) { UseShellExecute = false, WorkingDirectory = preparedUpdate.Directory };
            foreach (var argument in new[] { "--data-dir", Program.DataDirectory, "--wait-for-parent", current.Id.ToString(), current.StartTime.ToUniversalTime().Ticks.ToString(), "--apply-startup" }) launch.ArgumentList.Add(argument);
            using var child = Process.Start(launch) ?? throw new IOException("Could not open the updated manager.");
            exitRequested = true; closing.Cancel(); tray?.Dispose(); (Avalonia.Application.Current!.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.Shutdown();
        }
        catch (Exception error) { model.Notice = "Update handoff: " + error.Message; service!.Resume(); try { await service.EnsureStartedAsync(); } catch (Exception reconnect) { model.Notice += " " + reconnect.Message; } }
        finally { agentTransition = false; }
    }
    private void OpenManagerReleases(object? sender, RoutedEventArgs e) => OpenPath("https://github.com/stephenjarrett/Wyrmwatch/releases");
}
