using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Input.Raw;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;
using Avalonia.Styling;
using Avalonia.Threading;
using System.Net;
using System.Net.Http.Json;
using Wyrmwatch.Core;
using Wyrmwatch.Desktop;

namespace Wyrmwatch.Desktop.Tests;

public class WorkspaceUxTests
{
    [AvaloniaFact]
    public async Task RefusedStartKeepsConfirmedStoppedStateAndPointsToObservedRunningServer()
    {
        using var handler = new PrerequisiteRefusalHandler();
        using var http = new HttpClient(handler) { BaseAddress = new Uri("http://127.0.0.1/") };
        using var fixture = new Fixture(http); handler.Profile = fixture.Profile;
        Field<Button>(fixture.Window, "StartButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        for (var i = 0; i < 200 && (handler.Actions == 0 || fixture.Model.Busy); i++) { Dispatcher.UIThread.RunJobs(); await Task.Delay(10); }
        Assert.Equal(1, handler.Actions); Assert.False(fixture.Model.Busy);
        Assert.False(fixture.Model.InspectionUnknown); Assert.Equal(false, fixture.Model.ServerRunning);
        Assert.False(fixture.Model.CanStart); Assert.Equal("RunningServer", fixture.Model.StartHelpDestination);
        Assert.Contains("Running prerequisite fixture", fixture.Model.StartHint);
        Assert.Contains("Another saved server is running", fixture.Model.ErrorDetail);
    }

    private sealed class PrerequisiteRefusalHandler : HttpMessageHandler
    {
        public ServerProfile Profile = null!;
        public int Actions;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            if (request.Method == HttpMethod.Post && request.RequestUri!.AbsolutePath.EndsWith("/actions"))
            {
                Interlocked.Increment(ref Actions);
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.BadRequest) { Content = JsonContent.Create(new ActionResult("Another saved server is running. Stop it before starting this server.")) });
            }
            if (request.Method == HttpMethod.Get && request.RequestUri!.AbsolutePath == "/api/status")
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(new AgentStatus(false, "fixture", [
                    new(Profile.Id, Profile.Name, ServerSnapshot.Offline, new(), null),
                    new("running-fixture", "Running prerequisite fixture", ServerSnapshot.Offline with { Running = true }, new(), null)
                ], [], [], false, null)) });
            throw new IOException("Unexpected disposable prerequisite request.");
        }
    }

    [AvaloniaFact]
    public void RecoveryPrerequisiteIsKeyboardReachableAndRoutesToRecoveryWithoutStarting()
    {
        using var fixture = new Fixture(); var model = fixture.Model; var window = fixture.Window;
        model.ServerRunning = false; model.RecoveryPending = true;
        var help = Field<Button>(window, "StartPrerequisiteButton");
        Assert.False(Field<Button>(window, "StartButton").IsEnabled); Assert.True(help.IsEffectivelyEnabled);
        Assert.True(help.Focus()); help.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Assert.True(Field<StackPanel>(window, "BackupsPage").IsVisible);
        Assert.True(Field<Button>(window, "RecoverRestoreButton").IsFocused);
        Assert.False(Field<Button>(window, "RestoreBackupButton").IsEnabled);
        Assert.Contains("Recover here", model.RestoreHelpText);
        Assert.Empty(window.OwnedWindows);
    }

    [AvaloniaFact]
    public void InvalidFieldsKeepInputAndFocusAndKeyboardLabelsAreAssociated()
    {
        using var fixture = new Fixture(); var window = fixture.Window;
        window.ShowPage(WorkspacePage.ServerSettings);
        var name = Field<TextBox>(window, "ProfileName"); name.Text = "";
        var backup = Field<TextBox>(window, "BackupFolder"); backup.Text = fixture.Profile.BackupPath;
        Assert.False(window.ValidateConnectionFields(fixture.Profile)); Assert.True(name.IsFocused);
        Assert.True(Field<TextBlock>(window, "ConnectionValidationError").IsEffectivelyVisible);
        Assert.Equal("", name.Text); Assert.Equal(fixture.Profile.BackupPath, backup.Text);
        Assert.NotNull(AutomationProperties.GetLabeledBy(name)); Assert.Equal("Connection display name", AutomationProperties.GetName(name));
        foreach (var pathField in new[] { "InstallFolder", "DataFolder", "BackupFolder" })
            Assert.NotNull(AutomationProperties.GetLabeledBy(Field<TextBox>(window, pathField)));
        name.Text = "Keep this name";
        window.KeyPressQwerty(PhysicalKey.Tab, RawInputModifiers.None); window.KeyReleaseQwerty(PhysicalKey.Tab, RawInputModifiers.None);
        Assert.True(Field<TextBox>(window, "InstallFolder").IsFocused);
        window.KeyPressQwerty(PhysicalKey.Tab, RawInputModifiers.Shift); window.KeyReleaseQwerty(PhysicalKey.Tab, RawInputModifiers.Shift);
        Assert.True(name.IsFocused); Assert.Equal("Keep this name", name.Text);
        var values = new Dictionary<string, string> { ["OwnerId"] = "", ["ServerName"] = "Fixture", ["DefaultWorldName"] = "World", ["AdminPassword"] = "fixture-password", ["WorldPassword"] = "", ["Port"] = "7777" };
        Assert.False(window.ValidateGameFields(values)); Assert.True(Field<TextBox>(window, "OwnerId").IsFocused);
        Assert.Empty(window.OwnedWindows); Assert.False(Directory.Exists(fixture.Profile.InstallPath));
    }

    [AvaloniaFact]
    public async Task PathCopyShowsAcknowledgementAndReducedMotionHasNoTransitionsByDefault()
    {
        using var fixture = new Fixture(); var window = fixture.Window;
        window.ShowPage(WorkspacePage.ServerSettings);
        Assert.True(Field<ToggleSwitch>(window, "ReduceMotion").IsChecked);
        Assert.Null(Field<Button>(window, "StartButton").Transitions);
        Field<ToggleSwitch>(window, "ReduceMotion").IsChecked = false;
        Assert.NotEmpty(Field<Button>(window, "StartButton").Transitions!);
        Field<ToggleSwitch>(window, "ReduceMotion").IsChecked = true;
        Assert.Null(Field<Button>(window, "StartButton").Transitions);
        var button = Field<Button>(window, "CopyInstallPathButton"); var original = button.Content;
        var copy = window.CopyPathAsync(fixture.Profile.InstallPath, button, "Installation path copied.");
        Assert.StartsWith("Copied", button.Content?.ToString());
        Assert.Equal(fixture.Profile.InstallPath, await window.Clipboard!.TryGetValueAsync(DataFormat.Text));
        Assert.Equal("Installation path copied.", fixture.Model.Announcement);
        var second = window.CopyPathAsync(fixture.Profile.BackupPath, button, "Backup path copied.");
        Assert.Equal(fixture.Profile.BackupPath, await window.Clipboard!.TryGetValueAsync(DataFormat.Text));
        await Task.WhenAll(copy, second); Assert.Equal(original, button.Content);
    }

    [AvaloniaFact]
    public void InterruptedAndDeferredManagerResultsNeverClaimSuccessfulCompletion()
    {
        using var fixture = new Fixture();
        foreach (var status in new[] { "Interrupted", "Deferred", "Failed", "Succeeded" })
        {
            fixture.Model.ClearError();
            fixture.Model.BeginOperation("Restore backup", fixture.Profile.Name);
            fixture.Window.FinishObservedOperation(new OperationRecord("fixture", fixture.Profile.Id, fixture.Profile.Name, "Restore", DateTimeOffset.Now, DateTimeOffset.Now, status, "Disposable operation result."));
            Assert.Contains(status == "Succeeded" ? "Completed" : status, fixture.Model.LastCompletion);
            Assert.Equal(status is "Interrupted" or "Failed", fixture.Model.HasError);
            if (status != "Succeeded") Assert.DoesNotContain("✔", fixture.Model.LastCompletion);
        }
    }

    [AvaloniaFact]
    public async Task CompletedRequestWithFailedObservationRetainsResultAndOffersReadOnlyRetry()
    {
        using var handler = new ObservationFailureHandler();
        using var http = new HttpClient(handler) { BaseAddress = new Uri("http://127.0.0.1/") };
        using var fixture = new Fixture(http);
        Field<Button>(fixture.Window, "BackupNowButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        for (var i = 0; i < 200 && (handler.Actions == 0 || fixture.Model.Busy); i++) { Dispatcher.UIThread.RunJobs(); await Task.Delay(10); }
        Assert.Equal(1, handler.Actions); Assert.False(fixture.Model.Busy);
        Assert.Equal("Verified fixture backup created.", fixture.Model.Notice);
        Assert.Contains("State unverified", fixture.Model.LastCompletion); Assert.DoesNotContain("Failed", fixture.Model.LastCompletion);
        Assert.Equal("Refresh", fixture.Model.ErrorDestination); Assert.Null(fixture.Model.ServerRunning); Assert.False(fixture.Model.CanStart);
    }

    private sealed class ObservationFailureHandler : HttpMessageHandler
    {
        public int Actions;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            if (request.Method == HttpMethod.Post && request.RequestUri!.AbsolutePath.EndsWith("/actions"))
            {
                Interlocked.Increment(ref Actions);
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(new ActionResult("Verified fixture backup created.")) });
            }
            throw new IOException("Disposable observation failure.");
        }
    }

    [AvaloniaFact]
    public void OperationErrorAndEmptyStatesRemainVisibleAndReadableInBothThemes()
    {
        using var fixture = new Fixture(); var window = fixture.Window; var model = fixture.Model;
        var original = Application.Current!.RequestedThemeVariant;
        try
        {
            foreach (var theme in new[] { ThemeVariant.Dark, ThemeVariant.Light })
            {
                Application.Current.RequestedThemeVariant = theme;
                var suffix = theme == ThemeVariant.Dark ? "dark" : "light";
                model.ServerRunning = false; model.CompleteRefresh(true); model.ClearError();
                window.ShowPage(WorkspacePage.Servers); Capture(window, "ux-servers-" + suffix + ".png");
                model.BeginOperation("Verify backup", fixture.Profile.Name, "Verifying archived files and hashes");
                model.UpdateOperationPhase("Verifying archived files and hashes", "Disposable UI fixture; no archive was opened.");
                window.UpdateLayout(); Assert.True(Field<Border>(window, "OperationCard").IsEffectivelyVisible);
                Assert.False(Field<Button>(window, "StartButton").IsEnabled);
                Capture(window, "ux-operation-" + suffix + ".png");
                model.FinishOperation("Fixture verification stopped", false);
                model.ReportError("Backup verification needs attention", "Disposable error fixture. No user files were read.", "Backups", "Open Backups");
                Assert.True(Field<Border>(window, "OperationErrorCard").IsEffectivelyVisible);
                Assert.Equal(AutomationLiveSetting.Polite, AutomationProperties.GetLiveSetting(Field<TextBlock>(window, "WorkspaceAnnouncement")));
                Capture(window, "ux-error-" + suffix + ".png");
                model.ClearError(); window.ShowPage(WorkspacePage.Backups);
                Assert.True(Field<Button>(window, "CreateFirstBackupButton").IsEffectivelyVisible);
                Assert.False(Field<Button>(window, "RestoreBackupButton").IsEnabled);
                Capture(window, "ux-empty-backups-" + suffix + ".png");
                window.ShowPage(WorkspacePage.Resources); Assert.Contains("Server stopped", model.ResourcesHint);
                Capture(window, "ux-resources-" + suffix + ".png");
            }
        }
        finally { Application.Current.RequestedThemeVariant = original; }
    }
    private static T Field<T>(Window window, string name) where T : Control => window.GetLogicalDescendants().OfType<T>().Single(c => c.Name == name);
    private static void Capture(Window window, string name)
    {
        var directory = Environment.GetEnvironmentVariable("WYRM_TEST_SCREENSHOTS"); if (string.IsNullOrWhiteSpace(directory)) return;
        Directory.CreateDirectory(directory); window.UpdateLayout(); Dispatcher.UIThread.RunJobs(); AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        using var frame = window.CaptureRenderedFrame(); Assert.NotNull(frame); frame.Save(Path.Combine(directory, name), Avalonia.Media.Imaging.PngBitmapEncoderOptions.Default);
    }
    private sealed class Fixture : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "wyrmwatch-ux-" + Guid.NewGuid().ToString("N"));
        public MainWindow Window { get; }
        public WorkspaceModel Model => (WorkspaceModel)Window.DataContext!;
        public ServerProfile Profile { get; }
        public Fixture(HttpClient? connection = null)
        {
            Program.HeadlessTest = true; Program.Demo = false; Program.DataDirectory = Root;
            Window = new MainWindow(connection is null ? null : new AgentClient(Root, connection)); Window.Show();
            Profile = new() { Name = "Wyrmwatch UX fixture", InstallPath = Path.Combine(Root, "managed servers", "ash-grove"), BackupPath = Path.Combine(Root, "backups") };
            Model.Profiles.Add(Profile); Model.SelectedProfile = Profile; Model.SyncRows(); Model.ServerRunning = false;
            Model.ServerRows[0].Update(ServerSnapshot.Offline);
            Model.Notice = "Headless UX fixture. No game process or user save is in use.";
        }
        public void Dispose() { Window.Close(); if (Directory.Exists(Root)) Directory.Delete(Root, true); }
    }
}
