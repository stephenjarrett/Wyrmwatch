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
    private bool checkingApp, downloadingApp, switchingApp, appServiceHandoffRequired, appRestartFailed;
    private CancellationTokenSource? appDownloadCancellation;
    private readonly Stopwatch appUpdateClock = new();
    private readonly DispatcherTimer appUpdateTimer = new() { Interval = TimeSpan.FromSeconds(1) };
    private DateTimeOffset appUpdateLastActivity;
    private TimeSpan appUpdateLastActivityElapsed;
    private int appDownloadAttempt;
    internal Func<AppUpdates> AppUpdateClientFactory { private get; set; } = () => new(installedWindows: OperatingSystem.IsWindows());
    internal Func<PreparedUpdate, Task>? AppUpdateRestartOverride { private get; set; }
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
        DownloadAppButton.IsEnabled = !busy && !Program.Demo && appRelease is not null && preparedUpdate is null;
        SwitchAppButton.IsEnabled = !busy && !Program.Demo && preparedUpdate is not null && !appServiceHandoffRequired;
        SwitchAppButton.IsVisible = preparedUpdate is not null && appRestartFailed && !appServiceHandoffRequired;
        CancelAppDownloadButton.IsEnabled = downloadingApp && appDownloadCancellation is { IsCancellationRequested: false };
        AppDownloadProgress.IsVisible = busy;
        AppUpdateHint.Text = Program.Demo ? "App update actions are disabled in the demo workspace. Open published releases to review versions."
            : switchingApp ? "Wait for the app handoff to finish. This step cannot be cancelled here."
            : checkingApp ? "Checking the official release."
            : downloadingApp && appDownloadCancellation?.IsCancellationRequested == true ? "Cancellation requested. Wait for the download or verification to stop before trying again."
            : downloadingApp ? "Downloading and verifying the update. Wyrmwatch will restart when it is ready."
            : appServiceHandoffRequired ? preparedUpdate?.Installer == true
                ? "Stop the service when idle, run the verified installer at the existing installation location, then restart the service and open Wyrmwatch."
                : "Stop the service when idle, update its registered application path to the verified version, and restart it before opening the new desktop app."
            : preparedUpdate is not null ? $"Wyrmwatch {preparedUpdate.Version} is verified. Retry the app restart when the manager is idle."
            : appRelease is not null ? "Download and update will verify the package and restart Wyrmwatch. Your game server keeps running."
            : "Check for an app update first.";
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
    internal async Task CheckManagerUpdateAsync()
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
        if (appRelease is null || preparedUpdate is not null || checkingApp || downloadingApp || switchingApp || Program.Demo) return;
        var release = appRelease;
        var attempt = ++appDownloadAttempt;
        downloadingApp = true;
        preparedUpdate = null;
        appServiceHandoffRequired = false;
        appRestartFailed = false;
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
            if (preparedUpdate.Installer)
            {
                await VerifyInstallerAsync(preparedUpdate, release, appDownloadCancellation.Token);
            }
            SetAppUpdateActivity($"Version {preparedUpdate.Version} is verified and ready. Your current version is retained.");
            model.Notice = $"Wyrmwatch {preparedUpdate.Version} is verified and ready in App updates.";
        }
        catch (OperationCanceledException) when (appDownloadCancellation.IsCancellationRequested)
        {
            preparedUpdate = null;
            SetAppUpdateActivity("Download preparation cancelled. Your current app is unchanged. You can download the release again.");
            model.Notice = "App update preparation cancelled. Your current app is unchanged.";
        }
        catch (Exception error)
        {
            preparedUpdate = null;
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
        if (preparedUpdate is not null && !closing.IsCancellationRequested) await SwitchManagerVersionAsync();
    }
    private void CancelManagerDownload(object? sender, RoutedEventArgs e)
    {
        if (!downloadingApp || appDownloadCancellation?.IsCancellationRequested != false) return;
        appDownloadCancellation.Cancel();
        SetAppUpdateActivity("Cancellation requested · waiting for download preparation to stop.");
        UpdateAppUpdateControls();
    }
    private async void SwitchManagerVersion(object? sender, RoutedEventArgs e) => await SwitchManagerVersionAsync();
    internal async Task SwitchManagerVersionAsync()
    {
        if (preparedUpdate is null || Program.Demo || checkingApp || downloadingApp || switchingApp || appServiceHandoffRequired) return;
        switchingApp = true;
        appRestartFailed = false;
        if (model.ErrorDestination == "App updates") model.ClearError();
        BeginAppUpdate("Checking the background manager before opening the verified app.");
        try
        {
            await service!.RefreshAsync();
            if (service.PersistentHost)
            {
                appServiceHandoffRequired = true;
                SetAppUpdateActivity("The verified app is staged. The installed service needs a manual handoff.");
                model.Notice = preparedUpdate.Installer
                    ? $"The verified installer is staged at {preparedUpdate.Executable}. Stop the Wyrmwatch service when idle, run this installer at its existing installation location, then restart the service and open Wyrmwatch with the same workspace."
                    : $"The update is staged at {preparedUpdate.Directory}. Stop the service when idle, update its registered application path, and restart it before opening the new desktop app.";
                return;
            }
            if (service.Busy) throw new IOException("The manager is completing a server operation. The update is verified; use Restart to update when that operation finishes.");
            if (AppUpdateRestartOverride is { } restart)
            {
                await restart(preparedUpdate);
                SetAppUpdateActivity("Updated manager opened.");
                return;
            }
            SetAppUpdateActivity("Opening the verified app and reconnecting the background manager.");
            agentTransition = true;
            if (preparedUpdate.Installer)
            {
                var root = AppContext.BaseDirectory;
                if (!File.Exists(Path.Combine(root, "unins000.exe"))) throw new IOException("Install this release once with its Windows setup program to enable updates at a stable shortcut path.");
                if (Path.GetFullPath(Program.DataDirectory).StartsWith(Path.TrimEndingDirectorySeparator(root) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                    throw new IOException("The workspace must be outside the application installation before updating.");
                foreach (var profile in settings.Servers.Concat(settings.DisconnectedServers))
                    foreach (var path in new[] { profile.InstallPath, profile.SavedPath, profile.BackupPath })
                        if (!string.IsNullOrWhiteSpace(path) && (SafePaths.Same(root, path) || SafePaths.Within(path, root)))
                            throw new IOException("Move game installations, saves and backups outside the app installation before updating.");
                await VerifyInstallerAsync(preparedUpdate, appRelease ?? throw new IOException("Check the release again before retrying."), closing.Token);
                await service!.StopAgentAsync();
                using var parent = Process.GetCurrentProcess();
                var setup = new ProcessStartInfo(preparedUpdate.Executable) { UseShellExecute = false, WorkingDirectory = preparedUpdate.Directory };
                foreach (var argument in new[] { "/SILENT", "/SUPPRESSMSGBOXES", "/NORESTART", "/SP-", "/DIR=" + Path.TrimEndingDirectorySeparator(root),
                    "/UPDATEPARENT=" + parent.Id, "/UPDATEPARENTTOKEN=" + ProcessLifetime.Token(parent), "/UPDATEWORKSPACE=" + Program.DataDirectory }) setup.ArgumentList.Add(argument);
                using var installer = Process.Start(setup) ?? throw new IOException("Could not start the verified update installer.");
                exitRequested = true; closing.Cancel(); tray?.Dispose(); (Avalonia.Application.Current!.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.Shutdown();
                return;
            }
            await service!.StopAgentAsync();
            using var current = Process.GetCurrentProcess();
            var launch = new ProcessStartInfo(preparedUpdate.Executable) { UseShellExecute = false, WorkingDirectory = preparedUpdate.Directory };
            foreach (var argument in new[] { "--data-dir", Program.DataDirectory, "--wait-for-parent", current.Id.ToString(), ProcessLifetime.Token(current), "--apply-startup", "--previous-executable", Environment.ProcessPath! }) launch.ArgumentList.Add(argument);
            using var child = Process.Start(launch) ?? throw new IOException("Could not open the updated manager.");
            exitRequested = true; closing.Cancel(); tray?.Dispose(); (Avalonia.Application.Current!.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.Shutdown();
        }
        catch (Exception error)
        {
            appRestartFailed = true;
            var detail = error.Message;
            if (AppUpdateRestartOverride is null)
            {
                service!.Resume();
                try { await service.EnsureStartedAsync(); }
                catch (Exception reconnect) { detail += Environment.NewLine + "Manager reconnection: " + reconnect.Message; }
            }
            SetAppUpdateActivity("Could not open the updated app. The verified package is retained. Review diagnostics before trying again.");
            model.ReportError("Wyrmwatch could not restart to apply the update. The verified package is retained; retry when the manager is idle.", detail, "App updates", "Open App updates");
        }
        finally { agentTransition = false; switchingApp = false; EndAppUpdate(); }
    }
    private void OpenManagerReleases(object? sender, RoutedEventArgs e) => OpenPath("https://github.com/stephenjarrett/Wyrmwatch/releases");

    private static async Task VerifyInstallerAsync(PreparedUpdate update, AppRelease release, CancellationToken token)
    {
        var version = typeof(MainWindow).Assembly.GetName().Version!;
        var catalog = SignedUpdateCatalog.Verify(await File.ReadAllBytesAsync(update.Catalog!, token),
            (await File.ReadAllTextAsync(update.Signature!, token)).Trim(), new Version(version.Major, version.Minor, version.Build));
        await SignedUpdateCatalog.VerifyPackageAsync(update.Executable,
            catalog.Require($"Wyrmwatch-{release.Version}-win-x64-setup.exe", release), token);
    }
}
