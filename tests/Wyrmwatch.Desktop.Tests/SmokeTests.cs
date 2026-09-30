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

namespace Wyrmwatch.Desktop.Tests;

public class TestApp
{
    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<App>().UseHeadless(new AvaloniaHeadlessPlatformOptions());
}
public class SmokeTests
{
    [AvaloniaFact]
    public async Task ImportReviewsFoldersAndPastedLauncherPathsWithoutExecutingOrChangingFiles()
    {
        var root = Path.Combine(Path.GetTempPath(), "wyrmwatch-ui-" + Guid.NewGuid().ToString("N"));
        var install = Path.Combine(root, "existing-server");
        var saved = Path.Combine(install, "RSDragonwilds", "Saved"); Directory.CreateDirectory(saved);
        var launcher = Path.Combine(install, OperatingSystem.IsWindows() ? "RSDragonwildsServer.exe" : "RSDragonwildsServer.sh");
        File.WriteAllText(launcher, "read-only import fixture, not an executable");
        var world = Path.Combine(saved, "world.sav"); File.WriteAllText(world, "preserved world");
        var backup = Path.Combine(root, "backups");
        var dialog = new ImportServerDialog("", []); dialog.Show();
        try
        {
            var fields = dialog.GetLogicalDescendants().OfType<TextBox>().ToArray();
            var location = fields.Single(t => t.Name == "ImportLocation");
            fields.Single(t => t.Name == "ImportBackupFolder").Text = backup;
            var import = dialog.GetLogicalDescendants().OfType<Button>().Single(b => Equals(b.Content, "Import server"));
            var confirmation = dialog.GetLogicalDescendants().OfType<CheckBox>().Single();
            foreach (var input in new[] { install, "\"" + launcher + "\"" })
            {
                location.Text = input;
                Assert.False(import.IsEnabled); Assert.False(confirmation.IsChecked);
                await dialog.ReviewAsync();
                Assert.Equal(saved, fields.Single(t => t.Name == "ImportSavedFolder").Text);
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
        var parent = Path.Combine(root, "games"); Directory.CreateDirectory(parent);
        var existing = Path.Combine(parent, "existing.sav"); File.WriteAllText(existing, "preserved");
        ServerCreationPlan? submitted = null;
        var dialog = new CreateServerDialog([], (plan, report) => { submitted = plan; report("Fixture download complete"); return Task.CompletedTask; }, root); dialog.Show();
        try
        {
            Field<TextBox>(dialog, "CreateOwnerId").Text = "owner-fixture";
            await dialog.AdvanceAsync();
            Field<TextBox>(dialog, "CreateParentFolder").Text = parent;
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
            Assert.Contains("Import existing server", Field<TextBlock>(dialog, "CreateMessage").Text);
            Assert.Equal("keep world", File.ReadAllText(world)); Assert.Single(Directory.EnumerateFiles(target));
            Assert.False(Directory.Exists(Path.Combine(root, "WyrmwatchBackups")));
        }
        finally { dialog.Close(); Directory.Delete(root, true); }
    }

    [AvaloniaFact]
    public async Task CreateWizardReportsFailureWithoutRepeatingInstallation()
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
            Assert.Equal("Close", Field<Button>(dialog, "CreateNext").Content);
            await dialog.AdvanceAsync(); Assert.Equal(1, calls); Assert.False(Directory.Exists(root));
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
        var dialog = new ImportServerDialog(launcher, []);
        dialog.Show();
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        try
        {
            await dialog.ReviewAsync();
            var controls = dialog.GetLogicalDescendants().OfType<Control>().ToArray();
            var import = controls.OfType<Button>().Single(b => Equals(b.Content, "Import server"));
            var confirmation = controls.OfType<CheckBox>().Single();
            Assert.False(import.IsEnabled); confirmation.IsChecked = true;
            Assert.True(import.IsEnabled, string.Join("\n", controls.OfType<TextBlock>().Select(t => t.Text)));
            controls.OfType<TextBox>().Single(t => t.Name == "ImportSavedFolder").Text = Path.Combine(root, "missing");
            Assert.False(import.IsEnabled); Assert.False(confirmation.IsChecked);
            await dialog.ReviewAsync(); confirmation.IsChecked = true; Assert.False(import.IsEnabled);
            Assert.Equal("fixture", File.ReadAllText(launcher));
        }
        finally { dialog.Close(); if (SafePaths.Within(root, Path.GetTempPath())) Directory.Delete(root, true); }
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
            var language = new Wyrmwatch.Core.LanguagePack("xx", "Test language", new() { [Localization.Key("Make yourself at home.")] = "Translated settings" });
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
