using System.Diagnostics;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Wyrmwatch.Core;

namespace Wyrmwatch.Desktop;

public partial class MainWindow
{
    private AppRelease? appRelease;
    private PreparedUpdate? preparedUpdate;
    private bool checkingApp, downloadingApp, switchingApp, appServiceHandoffRequired;
    private CancellationTokenSource? appDownloadCancellation;
    private readonly Stopwatch appUpdateClock = new();
    private readonly DispatcherTimer appUpdateTimer = new() { Interval = TimeSpan.FromSeconds(1) };
    private DateTimeOffset appUpdateLastActivity;
    private TimeSpan appUpdateLastActivityElapsed;
    private int appDownloadAttempt;
    internal Func<AppUpdates> AppUpdateClientFactory { private get; set; } = () => new();
    private static string AppRuntime => OperatingSystem.IsWindows() ? "win-x64" : "linux-x64";

    private void InitializeAppUpdateControls()
    {
        AutomationProperties.SetName(AppDownloadProgress, "App update progress");
        appUpdateTimer.Tick += (_, _) => UpdateAppUpdateTiming();
        Closed += (_, _) => appUpdateTimer.Stop();
        UpdateAppUpdateControls();
    }
    private void UpdateAppUpdateControls()
    {
        var busy = checkingApp || downloadingApp || switchingApp;
        CheckAppButton.IsEnabled = !busy && !Program.Demo;
        DownloadAppButton.IsEnabled = !busy && !Program.Demo && appRelease is not null;
        SwitchAppButton.IsEnabled = !busy && !Program.Demo && preparedUpdate is not null && !appServiceHandoffRequired;
        CancelAppDownloadButton.IsEnabled = downloadingApp && appDownloadCancellation is { IsCancellationRequested: false };
        AppDownloadProgress.IsVisible = busy;
        AppUpdateHint.Text = Program.Demo ? "App update actions are disabled in the demo workspace. Open published releases to review versions."
            : switchingApp ? "Wait for the app handoff to finish. This step cannot be cancelled here."
            : checkingApp ? "Checking the official release. Download and open become available after this check finishes."
            : downloadingApp && appDownloadCancellation?.IsCancellationRequested == true ? "Cancellation requested. Wait for the download or verification to stop before trying again."
            : downloadingApp ? "Download and verification are in progress. You can cancel this preparation; your current app is retained."
            : appServiceHandoffRequired ? "Stop the service when idle, update its registered application path to the verified version, and restart it before opening the new desktop app."
            : preparedUpdate is not null ? $"Wyrmwatch {preparedUpdate.Version} is verified. Open the updated version when ready; your current app is retained."
            : appRelease is not null ? "Download this release to verify and prepare it before opening the updated version."
            : "Check for an app update first. Download requires an available release; opening requires a verified package.";
    }
    private void BeginAppUpdate(string status)
    {
        appUpdateClock.Restart();
        AppDownloadProgress.Value = 0;
        AppDownloadProgress.IsIndeterminate = true;
        SetAppUpdateActivity(status);
        appUpdateTimer.Start();
        UpdateAppUpdateControls();
    }
    private void SetAppUpdateActivity(string status, bool announce = true)
    {
        AppUpdateStatus.Text = status;
        if (announce) model.Announce(status);
        appUpdateLastActivity = DateTimeOffset.Now;
        appUpdateLastActivityElapsed = appUpdateClock.Elapsed;
        UpdateAppUpdateTiming();
    }
    private void UpdateAppUpdateTiming()
    {
        if (appUpdateLastActivity == default) return;
        var elapsed = appUpdateClock.Elapsed;
        var sinceActivity = elapsed - appUpdateLastActivityElapsed;
        AppUpdateTiming.Text = $"{(appUpdateClock.IsRunning ? "Elapsed" : "Finished in")} {(int)elapsed.TotalMinutes}m {elapsed.Seconds:00}s · Last activity {appUpdateLastActivity:HH:mm:ss}";
        if (appUpdateClock.IsRunning) AppUpdateTiming.Text += $" ({sinceActivity.TotalSeconds:0}s ago)";
    }
    private void EndAppUpdate()
    {
        appUpdateClock.Stop();
        appUpdateTimer.Stop();
        AppDownloadProgress.IsIndeterminate = false;
        UpdateAppUpdateTiming();
        UpdateAppUpdateControls();
    }
    private async void CheckManagerUpdate(object? sender, RoutedEventArgs e) => await CheckManagerUpdateAsync();
    private async Task CheckManagerUpdateAsync()
    {
        if (checkingApp || downloadingApp || switchingApp || Program.Demo || Program.HeadlessTest) return;
        checkingApp = true;
        appRelease = null;
        if (model.ErrorDestination == "App updates") model.ClearError();
        BeginAppUpdate("Checking published releases.");
        try
        {
            var version = typeof(MainWindow).Assembly.GetName().Version!;
            appRelease = await AppUpdateClientFactory().CheckAsync(new Version(version.Major, version.Minor, version.Build), AppRuntime, closing.Token);
            SetAppUpdateActivity(appRelease is null ? "No newer published release is available." : $"Wyrmwatch {appRelease.Version} is available · {appRelease.Bytes / 1048576d:0.0} MB");
            AppReleaseNotes.Text = appRelease?.Notes ?? "Updates appear here when a newer stable release is published to stephenjarrett/Wyrmwatch.";
        }
        catch (OperationCanceledException) when (closing.IsCancellationRequested) { }
        catch (Exception error)
        {
            SetAppUpdateActivity("Update check failed. Check your connection and try Check for app updates again, or open published releases.");
            model.ReportError("The app update check failed. Your current app is unchanged. Check your connection and retry from App updates.", error.Message, "App updates", "Open App updates");
        }
        finally { checkingApp = false; EndAppUpdate(); }
    }
    private async void DownloadManagerUpdate(object? sender, RoutedEventArgs e) => await DownloadManagerUpdateAsync();
    internal async Task DownloadManagerUpdateAsync()
    {
        if (appRelease is null || checkingApp || downloadingApp || switchingApp || Program.Demo) return;
        var release = appRelease;
        var attempt = ++appDownloadAttempt;
        downloadingApp = true;
        preparedUpdate = null;
        appServiceHandoffRequired = false;
        appDownloadCancellation = CancellationTokenSource.CreateLinkedTokenSource(closing.Token);
        if (model.ErrorDestination == "App updates") model.ClearError();
        BeginAppUpdate($"Connecting to download Wyrmwatch {release.Version}.");
        try
        {
            var progress = new Progress<double>(value =>
            {
                if (!downloadingApp || attempt != appDownloadAttempt || appDownloadCancellation?.IsCancellationRequested != false || !double.IsFinite(value)) return;
                var firstMeasurement = AppDownloadProgress.Value == 0;
                AppDownloadProgress.Value = Math.Clamp(value, 0, 100);
                AppDownloadProgress.IsIndeterminate = value >= 100;
                SetAppUpdateActivity(value >= 100 ? "Package bytes received · awaiting verification and app preparation."
                    : $"Downloading Wyrmwatch {release.Version} · {value:0}% of package bytes received.", firstMeasurement || value >= 100);
            });
            preparedUpdate = await Task.Run(() => AppUpdateClientFactory().DownloadAsync(release, Path.Combine(Program.DataDirectory, "app-updates"), AppRuntime, progress, appDownloadCancellation.Token));
            SetAppUpdateActivity($"Version {preparedUpdate.Version} is verified and ready. Your current version is retained.");
            model.Notice = $"Wyrmwatch {preparedUpdate.Version} is verified and ready in App updates.";
        }
        catch (OperationCanceledException) when (appDownloadCancellation.IsCancellationRequested)
        {
            SetAppUpdateActivity("Download preparation cancelled. Your current app is unchanged. You can download the release again.");
            model.Notice = "App update preparation cancelled. Your current app is unchanged.";
        }
        catch (Exception error)
        {
            SetAppUpdateActivity("Update preparation failed. Your current app is unchanged. Retry Download update, or open published releases.");
            model.ReportError("The app update could not be verified and prepared. Your current app is unchanged. Retry the download from App updates.", error.Message, "App updates", "Open App updates");
        }
        finally
        {
            downloadingApp = false;
            appDownloadCancellation.Dispose();
            appDownloadCancellation = null;
            EndAppUpdate();
        }
    }
    private void CancelManagerDownload(object? sender, RoutedEventArgs e)
    {
        if (!downloadingApp || appDownloadCancellation?.IsCancellationRequested != false) return;
        appDownloadCancellation.Cancel();
        SetAppUpdateActivity("Cancellation requested · waiting for download preparation to stop.");
        UpdateAppUpdateControls();
    }
    private async void SwitchManagerVersion(object? sender, RoutedEventArgs e)
    {
        if (preparedUpdate is null || Program.Demo || checkingApp || downloadingApp || switchingApp || appServiceHandoffRequired) return;
        switchingApp = true;
        BeginAppUpdate("Checking the background manager before opening the verified app.");
        try
        {
            await service!.RefreshAsync();
            if (service.PersistentHost)
            {
                appServiceHandoffRequired = true;
                SetAppUpdateActivity("The verified app is staged. The installed service needs a manual handoff.");
                model.Notice = $"The update is staged at {preparedUpdate.Directory}. Stop the service when idle, update its registered application path, and restart it before opening the new desktop app.";
                return;
            }
            if (!await Confirm("Open the updated manager?", $"Wyrmwatch {preparedUpdate.Version} will open with your existing connections. The background manager will reconnect. Your game server will keep running, and the previous app is retained.", "Open updated version"))
            {
                SetAppUpdateActivity($"Version {preparedUpdate.Version} remains verified and ready. Open it when ready.");
                return;
            }
            SetAppUpdateActivity("Opening the verified app and reconnecting the background manager.");
            agentTransition = true;
            await service!.StopAgentAsync();
            using var current = Process.GetCurrentProcess();
            var launch = new ProcessStartInfo(preparedUpdate.Executable) { UseShellExecute = false, WorkingDirectory = preparedUpdate.Directory };
            foreach (var argument in new[] { "--data-dir", Program.DataDirectory, "--wait-for-parent", current.Id.ToString(), ProcessLifetime.Token(current), "--apply-startup" }) launch.ArgumentList.Add(argument);
            using var child = Process.Start(launch) ?? throw new IOException("Could not open the updated manager.");
            exitRequested = true; closing.Cancel(); tray?.Dispose(); (Avalonia.Application.Current!.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.Shutdown();
        }
        catch (Exception error)
        {
            var detail = error.Message;
            service!.Resume();
            try { await service.EnsureStartedAsync(); }
            catch (Exception reconnect) { detail += Environment.NewLine + "Manager reconnection: " + reconnect.Message; }
            SetAppUpdateActivity("Could not open the updated app. The verified package is retained. Review diagnostics before trying again.");
            model.ReportError("The app update handoff failed. The verified package is retained; review diagnostics before trying again.", detail, "Help & diagnostics", "Open diagnostics");
        }
        finally { agentTransition = false; switchingApp = false; EndAppUpdate(); }
    }
    private void OpenManagerReleases(object? sender, RoutedEventArgs e) => OpenPath("https://github.com/stephenjarrett/Wyrmwatch/releases");
}
