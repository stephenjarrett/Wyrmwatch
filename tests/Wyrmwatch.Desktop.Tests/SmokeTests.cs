using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Styling;
using Avalonia.LogicalTree;
using Avalonia.Media;
using Avalonia.Controls.Presenters;
using Avalonia.VisualTree;
using Wyrmwatch.Core;
using Wyrmwatch.Desktop;

[assembly: AvaloniaTestApplication(typeof(Wyrmwatch.Desktop.Tests.TestApp))]
[assembly: CollectionBehavior(DisableTestParallelization = true)]

namespace Wyrmwatch.Desktop.Tests;

public class TestApp
{
    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<App>().WithInterFont().UseSkia().UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false });
}
public class SmokeTests
{
    [AvaloniaFact]
    public async Task WorldImportRequiresStoppedSourceAndRejectsConfigurationSaveWithoutSideEffects()
    {
        var root = Path.Combine(Path.GetTempPath(), "wyrmwatch-ui-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root); var source = Path.Combine(root, "InputSettings.sav"); File.WriteAllText(source, "GVASconfiguration fixture");
        var bytes = File.ReadAllBytes(source); var calls = 0;
        var dialog = new CreateServerDialog([], (_, _) => throw new InvalidOperationException("Import must not invoke Create"), root,
            (_, _, _) => { calls++; return Task.CompletedTask; }); dialog.Show();
        try
        {
            Field<TextBox>(dialog, "CreateOwnerId").Text = "dummy-owner";
            Field<TextBox>(dialog, "ImportWorldSource").Text = source;
            await dialog.AdvanceAsync(); Assert.Contains("closed the game", Field<TextBlock>(dialog, "CreateMessage").Text);
            Field<CheckBox>(dialog, "ImportSourceStopped").IsChecked = true;
            await dialog.AdvanceAsync(); Assert.False(string.IsNullOrEmpty(Field<TextBlock>(dialog, "CreateMessage").Text));
            Assert.Equal(0, calls); Assert.Equal(bytes, File.ReadAllBytes(source));
            Assert.False(Directory.Exists(Path.Combine(root, "WyrmwatchServers"))); Assert.False(Directory.Exists(Path.Combine(root, "WyrmwatchBackups")));
            Field<TextBox>(dialog, "ImportWorldSource").Text = Path.Combine(root, "another.sav");
            Assert.False(Field<CheckBox>(dialog, "ImportSourceStopped").IsChecked);
        }
        finally { dialog.Close(); Directory.Delete(root, true); }
    }

    [AvaloniaFact]
    public void ResourceValuesKeepReadableContrastInBothThemes()
    {
        var root = Path.Combine(Path.GetTempPath(), "wyrmwatch-ui-" + Guid.NewGuid().ToString("N"));
        Program.HeadlessTest = true; Program.Demo = false; Program.DataDirectory = root;
        var originalTheme = Application.Current!.RequestedThemeVariant;
        var window = new MainWindow(); window.Show();
        try
        {
            var model = (WorkspaceModel)window.DataContext!;
            var profile = new ServerProfile { Name = "Resource display fixture", InstallPath = Path.Combine(root, "server"), BackupPath = Path.Combine(root, "backups") };
            model.Profiles.Add(profile); model.SelectedProfile = profile; model.SyncRows();
            model.Cpu = "12.5%"; model.Memory = "1.4 GB"; model.Notice = "Headless fixture — illustrative resource values; no game process is running.";
            window.ShowPage(WorkspacePage.Resources);
            foreach (var theme in new[] { ThemeVariant.Dark, ThemeVariant.Light })
            {
                Application.Current.RequestedThemeVariant = theme; window.UpdateLayout(); Avalonia.Threading.Dispatcher.UIThread.RunJobs();
                foreach (var name in new[] { "CpuMetricText", "MemoryMetricText" })
                {
                    var value = Field<TextBlock>(window, name);
                    var foreground = Assert.IsAssignableFrom<ISolidColorBrush>(value.Foreground).Color;
                    var background = Assert.IsAssignableFrom<ISolidColorBrush>(value.GetLogicalAncestors().OfType<Border>().First().Background).Color;
                    var first = Luminance(foreground); var second = Luminance(background);
                    Assert.True((Math.Max(first, second) + 0.05) / (Math.Min(first, second) + 0.05) >= 4.5, $"{theme} {name} contrast below 4.5:1.");
                }
                var directory = Environment.GetEnvironmentVariable("WYRM_TEST_SCREENSHOTS");
                if (!string.IsNullOrWhiteSpace(directory))
                {
                    Directory.CreateDirectory(directory); AvaloniaHeadlessPlatform.ForceRenderTimerTick();
                    using var frame = window.CaptureRenderedFrame(); Assert.NotNull(frame);
                    frame.Save(Path.Combine(directory, theme == ThemeVariant.Dark ? "resources-dark.png" : "resources-light.png"), Avalonia.Media.Imaging.PngBitmapEncoderOptions.Default);
                }
            }
        }
        finally { window.Close(); Application.Current.RequestedThemeVariant = originalTheme; if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [AvaloniaFact]
    public void RecoveryStateBlocksStartAndRestartAndShowsAStoppedRecoveryActionInBothThemes()
    {
        var root = Path.Combine(Path.GetTempPath(), "wyrmwatch-ui-" + Guid.NewGuid().ToString("N"));
        Program.HeadlessTest = true; Program.Demo = false; Program.DataDirectory = root;
        var originalTheme = Application.Current!.RequestedThemeVariant;
        var window = new MainWindow(); window.Show();
        try
        {
            var model = (WorkspaceModel)window.DataContext!;
            var profile = new ServerProfile { Name = "Interrupted restore fixture", InstallPath = Path.Combine(root, "long installation folder", "server"), BackupPath = Path.Combine(root, "backups") };
            model.Profiles.Add(profile); model.SelectedProfile = profile; model.SyncRows();
            model.ServerRunning = false; model.RecoveryPending = true;
            model.Status = "Recovery required"; model.Notice = "Headless interrupted-restore fixture — no game process is running.";
            Assert.False(Field<Button>(window, "StartButton").IsEnabled);
            Assert.False(Field<Button>(window, "RestartButton").IsEnabled);
            Assert.Contains("interrupted restore", model.ActionHint);
            window.ShowPage(WorkspacePage.Backups);
            Assert.True(Field<Button>(window, "RecoverRestoreButton").IsEffectivelyVisible);
            Assert.True(Field<Button>(window, "RecoverRestoreButton").IsEnabled);
            Assert.False(Field<Button>(window, "RestoreBackupButton").IsEnabled);
            foreach (var theme in new[] { ThemeVariant.Dark, ThemeVariant.Light })
            {
                Application.Current.RequestedThemeVariant = theme;
                window.UpdateLayout(); Avalonia.Threading.Dispatcher.UIThread.RunJobs();
                var directory = Environment.GetEnvironmentVariable("WYRM_TEST_SCREENSHOTS");
                if (!string.IsNullOrWhiteSpace(directory))
                {
                    Directory.CreateDirectory(directory); AvaloniaHeadlessPlatform.ForceRenderTimerTick();
                    using var frame = window.CaptureRenderedFrame(); Assert.NotNull(frame);
                    frame.Save(Path.Combine(directory, theme == ThemeVariant.Dark ? "recovery-dark.png" : "recovery-light.png"), Avalonia.Media.Imaging.PngBitmapEncoderOptions.Default);
                }
            }
            model.ServerRunning = true;
            Assert.False(Field<Button>(window, "RecoverRestoreButton").IsEnabled);
            Assert.True(Field<Button>(window, "StopButton").IsEnabled);
            Assert.False(Field<Button>(window, "RestartButton").IsEnabled);
            model.RecoveryPending = false; model.ServerRunning = false;
            Assert.True(Field<Button>(window, "StartButton").IsEnabled);
            Assert.False(Field<Button>(window, "ApplyGameUpdateButton").IsEnabled);
            Assert.Contains("installed Steam build", model.UpdateHint);
            model.InstalledBuildKnown = true;
            Assert.True(Field<Button>(window, "ApplyGameUpdateButton").IsEnabled);
            model.Busy = true;
            Assert.False(Field<Button>(window, "ApplyGameUpdateButton").IsEnabled);
            Assert.Contains("in progress", model.ActionHint);
        }
        finally { window.Close(); Application.Current.RequestedThemeVariant = originalTheme; if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [AvaloniaFact]
    public async Task ImportReviewsFoldersAndPastedLauncherPathsWithoutExecutingOrChangingFiles()
    {
        var root = Path.Combine(Path.GetTempPath(), "wyrmwatch-ui-" + Guid.NewGuid().ToString("N"));
        var install = Path.Combine(root, "existing-server");
        var saved = Path.Combine(install, "RSDragonwilds", "Saved"); Directory.CreateDirectory(saved);
        var launcher = Path.Combine(install, OperatingSystem.IsWindows() ? "RSDragonwildsServer.exe" : "RSDragonwildsServer.sh");
        File.WriteAllText(launcher, "read-only import fixture, not an executable");
        var saveGames = Path.Combine(saved, "SaveGames"); Directory.CreateDirectory(saveGames);
        var world = Path.Combine(saveGames, "world.sav"); File.WriteAllText(world, "preserved world");
        var backup = Path.Combine(root, "backups");
        var dialog = new ImportServerDialog("", []); dialog.Show();
        try
        {
            var fields = dialog.GetLogicalDescendants().OfType<TextBox>().ToArray();
            var location = fields.Single(t => t.Name == "ImportLocation");
            fields.Single(t => t.Name == "ImportBackupFolder").Text = backup;
            var import = dialog.GetLogicalDescendants().OfType<Button>().Single(b => Equals(b.Content, "Import server"));
            var confirmation = Field<CheckBox>(dialog, "ImportConfirmed");
            foreach (var input in new[] { install, "\"" + launcher + "\"" })
            {
                location.Text = input;
                Assert.False(import.IsEnabled); Assert.False(confirmation.IsChecked);
                await dialog.ReviewAsync();
                Assert.Equal(saved, fields.Single(t => t.Name == "ImportSavedFolder").Text);
                Assert.True(fields.Single(t => t.Name == "ImportSavedFolder").IsReadOnly);
                Assert.Contains("world.sav", Field<TextBlock>(dialog, "ImportDetails").Text);
                confirmation.IsChecked = true; Assert.True(import.IsEnabled);
                Assert.Equal("preserved world", File.ReadAllText(world));
                Assert.Equal("read-only import fixture, not an executable", File.ReadAllText(launcher));
                Assert.False(Directory.Exists(backup));
                Assert.Equal(2, Directory.EnumerateFiles(install, "*", SearchOption.AllDirectories).Count());
            }
        }
        finally { dialog.Close(); if (SafePaths.Within(root, Path.GetTempPath())) Directory.Delete(root, true); }
    }

    [AvaloniaFact]
    public async Task CreateWizardRequiresOwnerAndCancelAfterReviewChangesNothing()
    {
        var root = Path.Combine(Path.GetTempPath(), "wyrmwatch-ui-" + Guid.NewGuid().ToString("N"));
        var calls = 0;
        var dialog = new CreateServerDialog([], (_, _) => { calls++; return Task.CompletedTask; }, root); dialog.Show();
        try
        {
            Assert.Equal(7777, Field<NumericUpDown>(dialog, "CreatePort").Value);
            Assert.Equal('●', Field<TextBox>(dialog, "CreateAdminPassword").PasswordChar);
            Assert.True(Field<TextBox>(dialog, "CreateAdminPassword").Text!.Length >= 20);
            await dialog.AdvanceAsync();
            Assert.Contains("Player ID", Field<TextBlock>(dialog, "CreateMessage").Text);
            Field<TextBox>(dialog, "CreateOwnerId").Text = "owner-fixture";
            Field<TextBox>(dialog, "CreateServerName").Text = "Carter's New World";
            await dialog.AdvanceAsync();
            Assert.Equal("carter-s-new-world", Field<TextBox>(dialog, "CreateFolderName").Text);
            await dialog.AdvanceAsync();
            var review = Field<TextBlock>(dialog, "CreateReview").Text!;
            Assert.Contains(Path.Combine(root, "WyrmwatchServers", "carter-s-new-world"), review);
            Assert.Contains(Path.Combine(root, "WyrmwatchServers", "carter-s-new-world", "RSDragonwilds", "Saved"), review);
            Assert.Contains("automatic updates off", review);
            Assert.DoesNotContain(Field<TextBox>(dialog, "CreateAdminPassword").Text!, review);
            dialog.Close();
            Assert.Equal(0, calls); Assert.False(Directory.Exists(root));
        }
        finally { dialog.Close(); if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [AvaloniaFact]
    public async Task CreateWizardUsesAChildOfPopulatedParentAndSubmitsOnlyAfterConfirmation()
    {
        var root = Path.Combine(Path.GetTempPath(), "wyrmwatch-ui-" + Guid.NewGuid().ToString("N"));
        var parent = Path.Combine(root, "WyrmwatchServers"); Directory.CreateDirectory(parent);
        var existing = Path.Combine(parent, "existing.sav"); File.WriteAllText(existing, "preserved");
        ServerCreationPlan? submitted = null;
        var dialog = new CreateServerDialog([], (plan, report) => { submitted = plan; report("Fixture download complete"); return Task.CompletedTask; }, root); dialog.Show();
        try
        {
            Field<TextBox>(dialog, "CreateOwnerId").Text = "owner-fixture";
            await dialog.AdvanceAsync();
            Assert.True(Field<TextBox>(dialog, "CreateManagedLocations").IsReadOnly);
            Assert.DoesNotContain(dialog.GetLogicalDescendants().OfType<Button>(), b => Equals(b.Content, "Choose folder…"));
            await dialog.AdvanceAsync(); Assert.Null(submitted);
            Assert.Single(Directory.EnumerateFileSystemEntries(parent));
            Assert.Equal("Create Server", Field<Button>(dialog, "CreateNext").Content);
            await dialog.AdvanceAsync(); Assert.NotNull(submitted);
            Assert.Equal(Path.Combine(parent, "my-dragonwilds-server"), submitted.Profile.InstallPath);
            Assert.Equal("owner-fixture", submitted.Configuration["OwnerId"]);
            Assert.False(submitted.Profile.AutoUpdate); Assert.False(submitted.Profile.AutoBackup);
            Assert.Contains("ready and stopped", Field<TextBlock>(dialog, "CreateReview").Text);
            Assert.Equal("preserved", File.ReadAllText(existing));
        }
        finally { dialog.Close(); Directory.Delete(root, true); }
    }

    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CreateWizardRejectsOccupiedTargetAtReviewAndAgainAtConfirmation(bool populateAfterReview)
    {
        var root = Path.Combine(Path.GetTempPath(), "wyrmwatch-ui-" + Guid.NewGuid().ToString("N"));
        var target = Path.Combine(root, "WyrmwatchServers", "my-dragonwilds-server");
        var calls = 0;
        var dialog = new CreateServerDialog([], (_, _) => { calls++; return Task.CompletedTask; }, root); dialog.Show();
        try
        {
            Field<TextBox>(dialog, "CreateOwnerId").Text = "owner-fixture";
            await dialog.AdvanceAsync();
            if (populateAfterReview) await dialog.AdvanceAsync();
            Directory.CreateDirectory(target); var world = Path.Combine(target, "existing.sav"); File.WriteAllText(world, "keep world");
            await dialog.AdvanceAsync();
            Assert.Equal(0, calls);
            Assert.Contains("already contains files", Field<TextBlock>(dialog, "CreateMessage").Text);
            Assert.Contains("Import a world", Field<TextBlock>(dialog, "CreateMessage").Text);
            Assert.Equal("keep world", File.ReadAllText(world)); Assert.Single(Directory.EnumerateFiles(target));
            Assert.False(Directory.Exists(Path.Combine(root, "WyrmwatchBackups")));
        }
        finally { dialog.Close(); Directory.Delete(root, true); }
    }

    [AvaloniaFact]
    public async Task CreateWizardKeepsEntriesAndAllowsRetryAfterFailure()
    {
        var root = Path.Combine(Path.GetTempPath(), "wyrmwatch-ui-" + Guid.NewGuid().ToString("N"));
        var calls = 0;
        var dialog = new CreateServerDialog([], (_, _) => { calls++; throw new IOException("Download failed"); }, root); dialog.Show();
        try
        {
            Field<TextBox>(dialog, "CreateOwnerId").Text = "owner-fixture";
            await dialog.AdvanceAsync(); await dialog.AdvanceAsync(); await dialog.AdvanceAsync();
            Assert.Contains("Download failed", Field<TextBlock>(dialog, "CreateMessage").Text);
            Assert.Contains("Setup did not finish", Field<TextBlock>(dialog, "CreateReview").Text);
            Assert.Equal("Create Server", Field<Button>(dialog, "CreateNext").Content);
            await dialog.AdvanceAsync(); Assert.Equal(2, calls); Assert.False(Directory.Exists(root));
        }
        finally { dialog.Close(); if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    private static T Field<T>(Window dialog, string name) where T : Control => dialog.GetLogicalDescendants().OfType<T>().Single(c => c.Name == name);

    [AvaloniaFact]
    public void SelectedNavigationRemainsReadableAcrossThemeChanges()
    {
        var root = Path.Combine(Path.GetTempPath(), "wyrmwatch-ui-" + Guid.NewGuid().ToString("N"));
        Program.HeadlessTest = true; Program.Demo = false; Program.DataDirectory = root;
        var originalTheme = Application.Current!.RequestedThemeVariant;
        var window = new MainWindow(); window.Show();
        try
        {
            var nav = window.FindControl<ListBox>("Navigation")!;
            var model = (WorkspaceModel)window.DataContext!;
            var backupList = Field<ListBox>(window, "BackupList");
            model.Backups.Add(new(new BackupInfo(Path.Combine(root, "fixture.zip"), new BackupManifest(1, BackupEngine.Product, "fixture installation", "fixture profile", "fixture saves", DateTimeOffset.UtcNow, "Contrast fixture", false, []), 0)));
            foreach (var theme in new[] { ThemeVariant.Dark, ThemeVariant.Light, ThemeVariant.Dark })
            {
                Application.Current.RequestedThemeVariant = theme;
                nav.SelectedIndex = (int)WorkspacePage.ServerSettings;
                Avalonia.Threading.Dispatcher.UIThread.RunJobs();
                var selected = Assert.IsType<ListBoxItem>(nav.SelectedItem);
                var presenter = selected.GetVisualDescendants().OfType<ContentPresenter>().First();
                var background = Assert.IsAssignableFrom<ISolidColorBrush>(presenter.Background).Color;
                foreach (var label in selected.GetLogicalDescendants().OfType<TextBlock>())
                {
                    var foreground = Assert.IsAssignableFrom<ISolidColorBrush>(label.Foreground).Color;
                    var first = Luminance(foreground); var second = Luminance(background);
                    var contrast = (Math.Max(first, second) + 0.05) / (Math.Min(first, second) + 0.05);
                    Assert.True(contrast >= 4.5, $"{theme} navigation contrast was {contrast:F2}:1.");
                }
                window.ShowPage(WorkspacePage.Backups); backupList.SelectedIndex = 0;
                window.UpdateLayout(); Avalonia.Threading.Dispatcher.UIThread.RunJobs();
                var backupItem = Assert.IsType<ListBoxItem>(backupList.ContainerFromIndex(0));
                var backupBackground = Assert.IsAssignableFrom<ISolidColorBrush>(backupItem.GetVisualDescendants().OfType<ContentPresenter>().First().Background).Color;
                foreach (var label in backupItem.GetLogicalDescendants().OfType<TextBlock>())
                {
                    var foreground = Assert.IsAssignableFrom<ISolidColorBrush>(label.Foreground).Color;
                    var first = Luminance(foreground); var second = Luminance(backupBackground);
                    var contrast = (Math.Max(first, second) + 0.05) / (Math.Min(first, second) + 0.05);
                    Assert.True(contrast >= 4.5, $"{theme} selected backup contrast was {contrast:F2}:1.");
                }
            }
        }
        finally { window.Close(); Application.Current.RequestedThemeVariant = originalTheme; if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    private static double Luminance(Color color)
    {
        static double Linear(byte value) { var channel = value / 255d; return channel <= 0.04045 ? channel / 12.92 : Math.Pow((channel + 0.055) / 1.055, 2.4); }
        return 0.2126 * Linear(color.R) + 0.7152 * Linear(color.G) + 0.0722 * Linear(color.B);
    }

    [AvaloniaFact]
    public async Task ImportRequiresFolderReviewAndConfirmationAfterEveryChange()
    {
        var root = Path.Combine(Path.GetTempPath(), "wyrmwatch-ui-" + Guid.NewGuid().ToString("N"));
        var launcher = Path.Combine(root, "RSDragonwildsServer.exe");
        var saved = Path.Combine(root, "RSDragonwilds", "Saved"); Directory.CreateDirectory(saved); File.WriteAllText(launcher, "fixture");
        var dialog = new ImportServerDialog("", []);
        dialog.Show();
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        Field<TextBox>(dialog, "ImportLocation").Text = launcher;
        try
        {
            await dialog.ReviewAsync();
            var controls = dialog.GetLogicalDescendants().OfType<Control>().ToArray();
            var import = controls.OfType<Button>().Single(b => Equals(b.Content, "Import server"));
            var confirmation = Field<CheckBox>(dialog, "ImportConfirmed");
            Assert.False(import.IsEnabled); confirmation.IsChecked = true;
            Assert.True(import.IsEnabled, string.Join("\n", controls.OfType<TextBlock>().Select(t => t.Text)));
            Field<CheckBox>(dialog, "ImportCustomSaved").IsChecked = true;
            Field<TextBox>(dialog, "ImportSavedFolder").Text = Path.Combine(root, "missing");
            Assert.False(import.IsEnabled); Assert.False(confirmation.IsChecked);
            await dialog.ReviewAsync(); confirmation.IsChecked = true; Assert.False(import.IsEnabled);
            Assert.Equal("fixture", File.ReadAllText(launcher));
        }
        finally { dialog.Close(); if (SafePaths.Within(root, Path.GetTempPath())) Directory.Delete(root, true); }
    }
    [AvaloniaFact]
    public async Task ImportCustomLocationIsExplicitAndNeverCreatesOrMovesSaves()
    {
        var root = Path.Combine(Path.GetTempPath(), "wyrmwatch-ui-" + Guid.NewGuid().ToString("N"));
        var install = Path.Combine(root, "install"); Directory.CreateDirectory(install);
        var launcher = Path.Combine(install, OperatingSystem.IsWindows() ? "RSDragonwildsServer.exe" : "RSDragonwildsServer.sh");
        File.WriteAllText(launcher, "fixture only");
        var saved = Path.Combine(root, "existing-data", "Saved");
        var worlds = Path.Combine(saved, "SaveGames"); Directory.CreateDirectory(worlds);
        var world = Path.Combine(worlds, "existing.sav"); File.WriteAllText(world, "preserve custom world");
        var defaultSaved = Path.Combine(install, "RSDragonwilds", "Saved");
        var backup = Path.Combine(root, "backups");
        var dialog = new ImportServerDialog("", []); dialog.Show();
        try
        {
            Field<TextBox>(dialog, "ImportLocation").Text = install;
            Field<TextBox>(dialog, "ImportBackupFolder").Text = backup;
            await dialog.ReviewAsync();
            Assert.True(Field<TextBox>(dialog, "ImportSavedFolder").IsReadOnly);
            Assert.False(Field<Button>(dialog, "ImportAccept").IsEnabled);
            Assert.False(Directory.Exists(defaultSaved));
            var custom = Field<CheckBox>(dialog, "ImportCustomSaved"); custom.IsChecked = true;
            Assert.False(Field<TextBox>(dialog, "ImportSavedFolder").IsReadOnly);
            Field<TextBox>(dialog, "ImportSavedFolder").Text = saved;
            await dialog.ReviewAsync();
            Assert.Contains("existing.sav", Field<TextBlock>(dialog, "ImportDetails").Text);
            Field<CheckBox>(dialog, "ImportConfirmed").IsChecked = true;
            Assert.True(Field<Button>(dialog, "ImportAccept").IsEnabled);
            custom.IsChecked = false;
            Assert.True(Field<TextBox>(dialog, "ImportSavedFolder").IsReadOnly);
            Assert.False(Field<Button>(dialog, "ImportAccept").IsEnabled);
            await dialog.ReviewAsync();
            Assert.False(Field<Button>(dialog, "ImportAccept").IsEnabled);
            Assert.Equal("preserve custom world", File.ReadAllText(world));
            Assert.False(Directory.Exists(defaultSaved)); Assert.False(Directory.Exists(backup));
            Assert.Equal(2, Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories).Count());
        }
        finally { dialog.Close(); Directory.Delete(root, true); }
    }

    [AvaloniaFact]
    public void PagesThemesAndFocusedFormSurviveStatusUpdates()
    {
        var root = Path.Combine(Path.GetTempPath(), "wyrmwatch-ui-" + Guid.NewGuid().ToString("N"));
        Program.HeadlessTest = true; Program.Demo = true; Program.DataDirectory = root;
        var window = new MainWindow(); window.Show();
        try
        {
            Assert.IsType<WorkspaceModel>(window.DataContext);
            var nav = window.FindControl<ListBox>("Navigation")!;
            for (var i = 0; i < nav.ItemCount; i++) { nav.SelectedIndex = i; Assert.False(string.IsNullOrWhiteSpace(((WorkspaceModel)window.DataContext!).PageTitle)); }
            nav.SelectedIndex = (int)WorkspacePage.ServerSettings;
            var field = window.FindControl<TextBox>("ProfileName")!; field.Text = "Unsaved user input"; field.Focus();
            var model = (WorkspaceModel)window.DataContext!; model.Cpu = "12.3%"; model.Players = "3"; model.Status = "Online";
            Assert.Equal("Unsaved user input", field.Text);
            window.ShowPage(WorkspacePage.AppSettings);
            var language = new Wyrmwatch.Core.LanguagePack("xx", "Test language", new() { [Localization.Key("App settings")] = "Translated settings" });
            var languages = window.FindControl<ComboBox>("LanguagePicker")!; languages.ItemsSource = new[] { Localization.English, language }; languages.SelectedIndex = 1;
            Assert.Equal("Translated settings", model.PageTitle); Assert.Equal("Unsaved user input", field.Text);
            languages.SelectedIndex = 0;
            window.FindControl<ComboBox>("ThemePicker")!.SelectedIndex = 1;
            Assert.Equal(ThemeVariant.Light, Application.Current!.RequestedThemeVariant);
            nav.SelectedIndex = 0; nav.SelectedIndex = (int)WorkspacePage.ServerSettings; Assert.Equal("Unsaved user input", field.Text);
        }
        finally { window.Close(); if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
    [AvaloniaFact]
    public void LifecycleButtonsFollowVerifiedStateAndServerEditorIsSeparateFromAppSettings()
    {
        var root = Path.Combine(Path.GetTempPath(), "wyrmwatch-ui-" + Guid.NewGuid().ToString("N"));
        Program.HeadlessTest = true; Program.Demo = false; Program.DataDirectory = root;
        var profile = new ServerProfile { InstallPath = Path.Combine(root, "server"), BackupPath = Path.Combine(root, "backups") };
        new JsonStore(root).Write("settings.json", new ManagerSettings { Servers = [profile] });
        var window = new MainWindow(); window.Show();
        try
        {
            var model = (WorkspaceModel)window.DataContext!;
            var start = window.FindControl<Button>("StartButton")!;
            var stop = window.FindControl<Button>("StopButton")!;
            var restart = window.FindControl<Button>("RestartButton")!;
            var save = window.FindControl<Button>("SaveGameConfigurationButton")!;
            foreach (var state in new bool?[] { null, false, true, null, false })
            {
                model.ServerRunning = state; Avalonia.Threading.Dispatcher.UIThread.RunJobs();
                Assert.Equal(state == false, start.IsEnabled);
                Assert.Equal(state == true, stop.IsEnabled); Assert.Equal(state == true, restart.IsEnabled);
                Assert.Equal(state == false, save.IsEnabled);
            }
            // Even direct routed events must not show a stop/restart confirmation for a stopped server.
            stop.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
            restart.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
            Assert.Empty(window.OwnedWindows);
            model.Busy = true; Assert.False(model.CanStart); Assert.False(model.CanStop); model.Busy = false;
            window.FindControl<Button>("EditServerButton")!.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
            Assert.True(window.FindControl<StackPanel>("ServerSettingsPage")!.IsVisible);
            Assert.False(window.FindControl<StackPanel>("SettingsPage")!.IsVisible);
            var owner = window.FindControl<TextBox>("OwnerId")!; owner.Text = "unsaved owner";
            Assert.Contains(window.FindControl<StackPanel>("ServerSettingsPage")!, owner.GetLogicalAncestors());
            Assert.True(window.FindControl<TextBox>("DataFolder")!.IsReadOnly);
            Assert.DoesNotContain(window.FindControl<StackPanel>("SettingsPage")!, owner.GetLogicalAncestors());
            window.ShowPage(WorkspacePage.AppSettings); Assert.False(model.ShowServerPicker);
            window.ShowPage(WorkspacePage.ServerSettings); Assert.True(model.ShowServerPicker); Assert.Equal("unsaved owner", owner.Text);
            window.ShowPage(WorkspacePage.Automation);
            var interval = window.FindControl<NumericUpDown>("UpdateInterval")!;
            Assert.Equal(60, interval.Value); Assert.Equal(30, interval.Minimum);
            model.SelectedProfile = profile with { Id = "another-server" };
            Assert.Null(model.ServerRunning); Assert.False(model.CanStop); Assert.False(model.CanStart);
        }
        finally { window.Close(); Directory.Delete(root, true); }
    }

    [AvaloniaFact]
    public void FirstRunHasNoConnectedServerOrEnabledActions()
    {
        var root = Path.Combine(Path.GetTempPath(), "wyrmwatch-ui-" + Guid.NewGuid().ToString("N"));
        Program.HeadlessTest = true; Program.Demo = false; Program.DataDirectory = root;
        var window = new MainWindow(); window.Show();
        try { var model = Assert.IsType<WorkspaceModel>(window.DataContext); Assert.Empty(model.Profiles); Assert.True(model.NoServer); Assert.False(model.CanAct); }
        finally { window.Close(); if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
}
