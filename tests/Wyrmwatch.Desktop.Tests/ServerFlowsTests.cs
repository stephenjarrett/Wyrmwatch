using System.Net.Http.Headers;
using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.LogicalTree;
using Avalonia.Threading;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Wyrmwatch.Agent;
using Wyrmwatch.Core;
using Wyrmwatch.Desktop;

namespace Wyrmwatch.Desktop.Tests;

// Real desktop events -> AgentClient -> authenticated HTTP routes -> persisted files.
// Steam and the game process are the only substitutes; every path is a disposable fixture.
public class ServerFlowsTests
{
    [AvaloniaFact]
    public async Task ImportWizardRecoversOwnedPreparedSnapshotAfterOriginalSourceDisappears()
    {
        await using var fixture = await Lab.StartAsync();
        var plan = new ServerCreationPlan("Prepared import", "Fallback", "dummy-owner", "dummy-private", "", 7777,
            Path.Combine(fixture.Store.DirectoryPath, "WyrmwatchServers"), "prepared-import", Path.Combine(fixture.Store.DirectoryPath, "WyrmwatchBackups"));
        var staged = plan.Profile with { InstallPath = plan.Profile.InstallPath + ".setup-" + Guid.NewGuid().ToString("N") };
        await fixture.Steam.InstallAsync(staged, false, _ => { });
        AtomicFile.Write(staged.ConfigPath, GameConfiguration.Merge("", plan.Configuration));
        var original = fixture.Existing("Resume source"); var originalFile = Path.Combine(original.SavedPath, "SaveGames", "beta.sav");
        var source = await WorldImport.InspectAsync(originalFile); var expected = File.ReadAllBytes(originalFile);
        await WorldImport.CopyAsync(source, Path.Combine(staged.SavedPath, "SaveGames"));
        await ManagedSetupRecovery.WriteAsync(plan.Profile, staged, source, fixture.Store);
        Directory.Move(staged.InstallPath, plan.Profile.InstallPath); File.Delete(originalFile);
        var config = File.ReadAllBytes(plan.Profile.ConfigPath);
        Click(fixture.Window, Field<Button>(fixture.Window, "ImportServerButton"));
        var dialog = await Owned<CreateServerDialog>(fixture.Window);
        var picker = Field<ComboBox>(dialog, "PreparedSetupPicker");
        await Until(() => picker.Items.Count == 1); Capture(dialog, "prepared-setup-discovery.png");
        picker.SelectedIndex = 0;
        await Until(() => Equals(Field<Button>(dialog, "CreateNext").Content, "Resume prepared setup") && Field<Button>(dialog, "CreateNext").IsEnabled);
        Assert.True(Field<TextBox>(dialog, "CreateOwnerId").IsReadOnly);
        Assert.Equal("dummy-private", Field<TextBox>(dialog, "CreateAdminPassword").Text);
        Assert.Contains("Original source is not read", Field<TextBlock>(dialog, "PreparedWorldNote").Text);
        Assert.False(Field<TextBlock>(dialog, "CreateReview").IsVisible);
        Assert.True(Field<StackPanel>(dialog, "PreparedSetupReview").IsVisible);
        Assert.Contains("VERIFIED", Field<TextBlock>(dialog, "PreparedVerifiedStatus").Text);
        Assert.Equal("Prepared import", Field<TextBlock>(dialog, "PreparedServerName").Text);
        Assert.Equal("beta.sav", Field<TextBlock>(dialog, "PreparedWorldName").Text);
        Assert.Equal("7777 · UDP", Field<TextBlock>(dialog, "PreparedGamePort").Text);
        Assert.DoesNotContain("dummy-private", Field<TextBlock>(dialog, "PreparedAccess").Text);
        Assert.Equal(plan.Profile.SavedPath, Field<TextBlock>(dialog, "PreparedSavedPath").Text);
        Assert.Equal(Avalonia.Media.TextWrapping.Wrap, Field<TextBlock>(dialog, "PreparedSavedPath").TextWrapping);
        Assert.True(Field<Button>(dialog, "PreparedSetupNew").IsVisible);
        var theme = Application.Current!.RequestedThemeVariant;
        Application.Current.RequestedThemeVariant = Avalonia.Styling.ThemeVariant.Dark;
        Capture(dialog, "prepared-setup-review.png");
        Application.Current.RequestedThemeVariant = Avalonia.Styling.ThemeVariant.Light;
        Capture(dialog, "prepared-setup-review-light.png");
        Application.Current.RequestedThemeVariant = theme;
        Click(dialog, Field<Button>(dialog, "PreparedSetupNew"));
        Assert.False(Field<StackPanel>(dialog, "PreparedSetupReview").IsVisible);
        Assert.False(Field<TextBox>(dialog, "CreateOwnerId").IsReadOnly);
        Assert.Equal("Continue", Field<Button>(dialog, "CreateNext").Content);
        Assert.True(Field<Expander>(dialog, "PreparedSetupRecovery").IsExpanded);
        picker.SelectedIndex = 0;
        await Until(() => Equals(Field<Button>(dialog, "CreateNext").Content, "Resume prepared setup") && Field<Button>(dialog, "CreateNext").IsEnabled);
        Click(dialog, Field<Button>(dialog, "CreateNext"));
        await Until(() => Equals(Field<Button>(dialog, "CreateNext").Content, "Close"));
        var profile = Assert.Single(((WorkspaceModel)fixture.Window.DataContext!).Profiles);
        Assert.Equal(plan.Profile.Id, profile.Id); Assert.Equal(config, File.ReadAllBytes(profile.ConfigPath));
        Assert.Equal(expected, File.ReadAllBytes(Path.Combine(profile.SavedPath, "SaveGames", "beta.sav")));
        Assert.False(File.Exists(originalFile)); Assert.Equal(1, fixture.Steam.Installs); Assert.Equal(0, fixture.Runtime.Starts);
        Assert.False(profile.AutoUpdate); Assert.False(profile.AutoBackup); Assert.False(ManagedSetupRecovery.HasReceipt(profile.InstallPath));
        Click(dialog, Field<Button>(dialog, "CreateNext"));
    }
    [AvaloniaFact]
    public async Task CreateImportEditBackupRestoreAndRemoveKeepServersIsolated()
    {
        await using var fixture = await Lab.StartAsync();
        var window = fixture.Window;
        var model = (WorkspaceModel)window.DataContext!;
        var created = await CreateAsync(fixture, "Test Alpha");
        Assert.False(created.AutoBackup); Assert.False(created.AutoUpdate);
        Assert.Equal("dummy-owner", GameConfiguration.Read(created.ConfigPath)["OwnerId"]);
        Assert.False(fixture.Runtime.Running(created)); Assert.Equal(0, fixture.Runtime.Starts);
        var alphaWorld = Path.Combine(created.SavedPath, "SaveGames", "alpha.sav");
        Directory.CreateDirectory(Path.GetDirectoryName(alphaWorld)!); File.WriteAllText(alphaWorld, "alpha progress");

        var sourceProfile = fixture.Existing("Source Beta");
        var sourceWorld = Path.Combine(sourceProfile.SavedPath, "SaveGames", "beta.sav");
        var betaBytes = File.ReadAllBytes(sourceWorld); var sourceConfig = File.ReadAllBytes(sourceProfile.ConfigPath);
        Click(window, Field<Button>(window, "ImportServerButton"));
        var import = await Owned<CreateServerDialog>(window);
        await FillImportAsync(import, fixture, "Test Beta", sourceWorld);
        Click(import, Field<Button>(import, "CreateNext"));
        await Until(() => Equals(Field<Button>(import, "CreateNext").Content, "Close"));
        Click(import, Field<Button>(import, "CreateNext"));
        Assert.Equal(2, model.Profiles.Count); Assert.Equal(2, model.ServerRows.Count);
        var beta = model.SelectedProfile!;
        var betaWorld = Path.Combine(beta.SavedPath, "SaveGames", "beta.sav");
        var betaConfig = File.ReadAllBytes(beta.ConfigPath);
        Assert.Equal(betaBytes, File.ReadAllBytes(betaWorld));
        Assert.Equal(betaBytes, File.ReadAllBytes(sourceWorld)); Assert.Equal(sourceConfig, File.ReadAllBytes(sourceProfile.ConfigPath));
        Assert.NotEqual(sourceProfile.InstallPath, beta.InstallPath);
        Assert.False(beta.AutoBackup); Assert.False(beta.AutoUpdate); Assert.Equal(0, fixture.Runtime.Starts);
        await WaitState(fixture, false);
        Capture(window, "servers-dark.png");
        var theme = Application.Current!.RequestedThemeVariant;
        Application.Current.RequestedThemeVariant = Avalonia.Styling.ThemeVariant.Light;
        Capture(window, "servers-light.png");
        Application.Current.RequestedThemeVariant = theme;

        Click(window, Field<Button>(window, "EditServerButton"));
        Assert.True(Field<TextBox>(window, "InstallFolder").IsReadOnly);
        Assert.Equal(beta.InstallPath, Field<TextBox>(window, "InstallFolder").Text);
        Assert.Equal(beta.SavedPath, Field<TextBox>(window, "DataFolder").Text);
        Capture(window, "server-settings-paths.png");
        Type(window, "ProfileName", "Test Beta edited");
        Click(window, Field<Button>(window, "SaveConnectionButton"));
        await Until(() => model.SelectedProfile?.Name == "Test Beta edited");
        beta = model.SelectedProfile!;
        Assert.Equal("Test Beta edited", fixture.Store.Read("settings.json", () => new ManagerSettings()).Servers.Single(p => p.Id == beta.Id).Name);
        Assert.Equal(Path.GetDirectoryName(betaWorld), Path.Combine(beta.SavedPath, "SaveGames"));
        Assert.Equal(betaBytes, File.ReadAllBytes(betaWorld));
        await WaitState(fixture, false);
        Type(window, "GameServerName", "Beta game name edited");
        Click(window, Field<Button>(window, "SaveGameConfigurationButton")); await Accept(window, "Write configuration");
        await Until(() => model.Notice.StartsWith("Game configuration saved") && !model.Busy);
        Assert.Equal("Beta game name edited", GameConfiguration.Read(beta.ConfigPath)["ServerName"]);
        Assert.Equal(betaBytes, File.ReadAllBytes(betaWorld)); Assert.Equal("alpha progress", File.ReadAllText(alphaWorld));
        betaConfig = File.ReadAllBytes(beta.ConfigPath);
        window.ShowPage(WorkspacePage.Servers);

        await WaitState(fixture, false);
        Click(window, Field<Button>(window, "StartButton"));
        await Until(() => fixture.Runtime.Running(beta) && !model.Busy);
        await fixture.Window.RefreshServersAsync(); Assert.True(model.CanStop); Assert.False(model.CanStart);
        // Selecting another saved server cannot launch a second process while Beta runs.
        await SelectAsync(fixture, 0);
        Click(window, Field<Button>(window, "StartButton"));
        await Until(() => model.Notice.Contains("Another saved server is running"));
        Assert.False(fixture.Runtime.Running(created)); Assert.Equal(1, fixture.Runtime.Starts);
        await SelectAsync(fixture, 1);
        Click(window, Field<Button>(window, "StopButton")); await Accept(window, "Stop server");
        await Until(() => !fixture.Runtime.Running(beta) && !model.Busy);
        await WaitState(fixture, false);
        Assert.False(Field<Button>(window, "StopButton").IsEnabled); Assert.False(Field<Button>(window, "RestartButton").IsEnabled);
        Assert.Equal(1, fixture.Runtime.Starts); Assert.Equal(1, fixture.Runtime.Stops);

        Click(window, Field<Button>(window, "BackupNowButton"));
        await Until(() => model.Notice.StartsWith("Verified backup saved") && !model.Busy);
        var engine = new BackupEngine(); var betaBackups = engine.List(beta); Assert.NotEmpty(betaBackups);
        File.WriteAllText(betaWorld, "new beta progress");
        window.ShowPage(WorkspacePage.Backups);
        var list = Field<ListBox>(window, "BackupList"); list.SelectedIndex = 0;
        var selectedArchive = Assert.IsType<BackupRow>(list.SelectedItem).Info.Path;
        await window.RefreshServersAsync();
        Assert.Equal(selectedArchive, Assert.IsType<BackupRow>(list.SelectedItem).Info.Path);
        Capture(window, "backups-dark.png");
        Click(window, Field<Button>(window, "RestoreBackupButton")); await Accept(window, "Restore backup");
        await Until(() => model.Notice.StartsWith("Backup restored") && !model.Busy);
        Assert.Equal(betaBytes, File.ReadAllBytes(betaWorld));
        Assert.Equal("alpha progress", File.ReadAllText(alphaWorld));
        Assert.All(betaBackups, b => Assert.True(File.Exists(b.Path)));

        window.ShowPage(WorkspacePage.ServerSettings);
        Click(window, Field<Button>(window, "RemoveServerButton")); await Accept(window, "Disconnect server");
        await Until(() => model.Profiles.Count == 1);
        Assert.Equal(created.Id, model.SelectedProfile!.Id);
        Assert.Equal(betaBytes, File.ReadAllBytes(betaWorld)); Assert.Equal(betaConfig, File.ReadAllBytes(beta.ConfigPath));
        Assert.All(betaBackups, b => Assert.True(File.Exists(b.Path)));
        Assert.Equal("alpha progress", File.ReadAllText(alphaWorld));
        Assert.Single(fixture.Store.Read("settings.json", () => new ManagerSettings()).Servers);
        Assert.Equal(1, fixture.Runtime.Starts); // Import, edit, restore and remove never launch anything.
        window.Close();
        using var reopenedClient = fixture.Client();
        var reopened = new MainWindow(reopenedClient); reopened.Show();
        try { Assert.Equal(created.Id, ((WorkspaceModel)reopened.DataContext!).SelectedProfile!.Id); }
        finally { reopened.Close(); }
    }

    [AvaloniaFact]
    public async Task CancelInvalidImportAndChangedOccupiedTargetHaveNoFileEffects()
    {
        await using var fixture = await Lab.StartAsync();
        Click(fixture.Window, Field<Button>(fixture.Window, "CreateServerButton"));
        var create = await Owned<CreateServerDialog>(fixture.Window);
        Click(create, Field<Button>(create, "CreateNext"));
        await Until(() => Field<TextBlock>(create, "CreateMessage").Text!.Contains("Player ID"));
        Type(create, "CreateOwnerId", "dummy-owner");
        Click(create, Field<Button>(create, "CreateNext"));
        await Until(() => Field<TextBox>(create, "CreateManagedLocations").IsEffectivelyVisible);
        Click(create, Field<Button>(create, "CreateNext"));
        await Until(() => Equals(Field<Button>(create, "CreateNext").Content, "Create Server"));
        var target = Path.Combine(fixture.Store.DirectoryPath, "WyrmwatchServers", "my-dragonwilds-server"); Directory.CreateDirectory(target);
        var sentinel = Path.Combine(target, "preserve.sav"); File.WriteAllText(sentinel, "occupied");
        Click(create, Field<Button>(create, "CreateNext"));
        await Until(() => Field<TextBlock>(create, "CreateMessage").Text!.Contains("already contains files"));
        Click(create, Field<Button>(create, "CreateCancel"));
        Assert.Empty(fixture.Store.Read("settings.json", () => new ManagerSettings()).Servers);
        Assert.Equal("occupied", File.ReadAllText(sentinel)); Assert.Equal(0, fixture.Steam.Installs);

        var existing = fixture.Existing("Cancel Me");
        var before = Directory.EnumerateFiles(existing.InstallPath, "*", SearchOption.AllDirectories).ToDictionary(p => p, File.ReadAllBytes);
        Click(fixture.Window, Field<Button>(fixture.Window, "ImportServerButton"));
        var import = await Owned<CreateServerDialog>(fixture.Window);
        Type(import, "CreateServerName", "Cancel Import");
        Type(import, "CreateOwnerId", "dummy-owner"); Type(import, "ImportWorldSource", Path.Combine(fixture.Root, "missing.sav"));
        Click(import, Field<CheckBox>(import, "ImportSourceStopped")); Click(import, Field<Button>(import, "CreateNext"));
        await Until(() => Field<Button>(import, "CreateNext").IsEnabled && !string.IsNullOrEmpty(Field<TextBlock>(import, "CreateMessage").Text));
        Type(import, "ImportWorldSource", Path.Combine(existing.SavedPath, "SaveGames", "beta.sav"));
        Click(import, Field<CheckBox>(import, "ImportSourceStopped")); Click(import, Field<Button>(import, "CreateNext"));
        await Until(() => Field<TextBox>(import, "CreateManagedLocations").IsEffectivelyVisible);
        Click(import, Field<Button>(import, "CreateNext"));
        await Until(() => Equals(Field<Button>(import, "CreateNext").Content, "Import World"));
        Click(import, Field<Button>(import, "CreateCancel"));
        Assert.Empty(fixture.Store.Read("settings.json", () => new ManagerSettings()).Servers);
        Assert.All(before, pair => Assert.Equal(pair.Value, File.ReadAllBytes(pair.Key)));
        Assert.False(Directory.Exists(existing.BackupPath)); Assert.Equal(0, fixture.Runtime.Starts); Assert.Equal(0, fixture.Steam.Installs);
    }

    [AvaloniaFact]
    public async Task ImportErrorKeepsWizardOpenAndCanBeReviewedAndRetried()
    {
        await using var fixture = await Lab.StartAsync();
        var existing = fixture.Existing("Retry Import Source");
        var source = Path.Combine(existing.SavedPath, "SaveGames", "beta.sav");
        Click(fixture.Window, Field<Button>(fixture.Window, "ImportServerButton"));
        var dialog = await Owned<CreateServerDialog>(fixture.Window);
        await FillImportAsync(dialog, fixture, "Retry Import", source);
        var bytes = File.ReadAllBytes(source); bytes[^1] = 2; File.WriteAllBytes(source, bytes);
        Click(dialog, Field<Button>(dialog, "CreateNext"));
        await Until(() => Field<TextBlock>(dialog, "CreateMessage").Text!.Contains("changed after review"));
        Assert.Empty(fixture.Store.Read("settings.json", () => new ManagerSettings()).Servers); Assert.Equal(0, fixture.Steam.Installs);
        Click(dialog, Field<CheckBox>(dialog, "ImportSourceStopped")); Click(dialog, Field<Button>(dialog, "CreateNext"));
        await Until(() => Field<TextBox>(dialog, "CreateManagedLocations").IsEffectivelyVisible);
        Click(dialog, Field<Button>(dialog, "CreateNext"));
        await Until(() => Equals(Field<Button>(dialog, "CreateNext").Content, "Import World"));
        Click(dialog, Field<Button>(dialog, "CreateNext"));
        await Until(() => Equals(Field<Button>(dialog, "CreateNext").Content, "Close"));
        var profile = ((WorkspaceModel)fixture.Window.DataContext!).SelectedProfile!;
        Assert.Equal(bytes, File.ReadAllBytes(Path.Combine(profile.SavedPath, "SaveGames", "beta.sav"))); Assert.Equal(bytes, File.ReadAllBytes(source));
        Assert.Single(fixture.Store.Read("settings.json", () => new ManagerSettings()).Servers); Assert.Equal(0, fixture.Runtime.Starts);
        Click(dialog, Field<Button>(dialog, "CreateNext"));
    }
    [AvaloniaFact]
    public async Task FailedDownloadCanRetryWithoutStaleConnectionOrReplacingFiles()
    {
        await using var fixture = await Lab.StartAsync();
        fixture.Steam.FailNext = true;
        Click(fixture.Window, Field<Button>(fixture.Window, "CreateServerButton"));
        var dialog = await Owned<CreateServerDialog>(fixture.Window);
        await FillCreateAsync(dialog, fixture, "Retry Test");
        Click(dialog, Field<Button>(dialog, "CreateNext"));
        await Until(() => Field<TextBlock>(dialog, "CreateMessage").Text!.Contains("fixture download failed"));
        Assert.Empty(fixture.Store.Read("settings.json", () => new ManagerSettings()).Servers);
        var partial = Directory.GetDirectories(Path.Combine(fixture.Store.DirectoryPath, "WyrmwatchServers"), "retry-test.setup-*").Single();
        Assert.Equal("partial download", File.ReadAllText(Path.Combine(partial, "partial.txt")));
        Click(dialog, Field<Button>(dialog, "CreateNext"));
        await Until(() => Equals(Field<Button>(dialog, "CreateNext").Content, "Close"));
        Assert.Single(fixture.Store.Read("settings.json", () => new ManagerSettings()).Servers);
        Assert.Equal("partial download", File.ReadAllText(Path.Combine(partial, "partial.txt")));
        Assert.Equal(2, fixture.Steam.Installs); Assert.Equal(0, fixture.Runtime.Starts);
        Click(dialog, Field<Button>(dialog, "CreateNext"));
    }

    private static async Task<ServerProfile> CreateAsync(Lab fixture, string name)
    {
        Click(fixture.Window, Field<Button>(fixture.Window, "CreateServerButton"));
        var dialog = await Owned<CreateServerDialog>(fixture.Window);
        await FillCreateAsync(dialog, fixture, name);
        Click(dialog, Field<Button>(dialog, "CreateNext"));
        await Until(() => Equals(Field<Button>(dialog, "CreateNext").Content, "Close"));
        Assert.Contains("ready and stopped", Field<TextBlock>(dialog, "CreateReview").Text);
        Click(dialog, Field<Button>(dialog, "CreateNext"));
        return ((WorkspaceModel)fixture.Window.DataContext!).SelectedProfile!;
    }
    private static async Task FillCreateAsync(CreateServerDialog dialog, Lab fixture, string name)
    {
        Type(dialog, "CreateServerName", name); Type(dialog, "CreateOwnerId", "dummy-owner");
        Capture(dialog, "create-world.png");
        Click(dialog, Field<Button>(dialog, "CreateNext"));
        await Until(() => Field<TextBox>(dialog, "CreateManagedLocations").IsEffectivelyVisible);
        Capture(dialog, "create-folders.png");
        Click(dialog, Field<Button>(dialog, "CreateNext"));
        await Until(() => Equals(Field<Button>(dialog, "CreateNext").Content, "Create Server"));
    }
    private static async Task FillImportAsync(CreateServerDialog dialog, Lab fixture, string name, string source)
    {
        Type(dialog, "CreateServerName", name); Type(dialog, "CreateOwnerId", "dummy-owner"); Type(dialog, "ImportWorldSource", source);
        Click(dialog, Field<CheckBox>(dialog, "ImportSourceStopped")); Capture(dialog, "import-world-source.png");
        Click(dialog, Field<Button>(dialog, "CreateNext"));
        await Until(() => Field<TextBox>(dialog, "CreateManagedLocations").IsEffectivelyVisible);
        Assert.True(Field<TextBox>(dialog, "CreateManagedLocations").IsReadOnly);
        Capture(dialog, "import-world-access.png"); Click(dialog, Field<Button>(dialog, "CreateNext"));
        await Until(() => Equals(Field<Button>(dialog, "CreateNext").Content, "Import World"));
        Assert.Contains("embedded world name", Field<TextBlock>(dialog, "ReviewImportNote").Text);
        Capture(dialog, "import-world-review.png");
    }
    private static T Field<T>(Window window, string name) where T : Control => window.GetLogicalDescendants().OfType<T>().Single(c => c.Name == name);
    private static void Capture(Window window, string name)
    {
        var directory = Environment.GetEnvironmentVariable("WYRM_TEST_SCREENSHOTS");
        if (string.IsNullOrWhiteSpace(directory)) return;
        window.UpdateLayout(); Dispatcher.UIThread.RunJobs();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        Directory.CreateDirectory(directory);
        using var frame = window.CaptureRenderedFrame(); Assert.NotNull(frame); frame.Save(Path.Combine(directory, name), Avalonia.Media.Imaging.PngBitmapEncoderOptions.Default);
    }
    private static void Type(Window window, string name, string text)
    {
        var field = Field<TextBox>(window, name); field.BringIntoView(); window.UpdateLayout(); field.Focus(); field.SelectAll();
        window.KeyTextInput(text); Assert.Equal(text, field.Text);
    }
    private static void Click(Window window, Control control)
    {
        Assert.True(control.IsEffectivelyEnabled); window.UpdateLayout(); Dispatcher.UIThread.RunJobs(); control.BringIntoView(); window.UpdateLayout(); Dispatcher.UIThread.RunJobs();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        var point = control.TranslatePoint(new Point(control.Bounds.Width / 2, control.Bounds.Height / 2), window)!.Value;
        window.MouseDown(point, MouseButton.Left); window.MouseUp(point, MouseButton.Left); Dispatcher.UIThread.RunJobs();
    }
    private static async Task Until(Func<bool> condition)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        while (!condition()) { Dispatcher.UIThread.RunJobs(); await Task.Delay(25, deadline.Token); }
        Dispatcher.UIThread.RunJobs();
    }
    private static async Task<T> Owned<T>(Window owner) where T : Window { await Until(() => owner.OwnedWindows.OfType<T>().Any()); return owner.OwnedWindows.OfType<T>().Single(); }
    private static async Task Accept(Window owner, string label)
    {
        var dialog = await Owned<Window>(owner);
        if (label == "Restore backup") Capture(dialog, "restore-confirmation.png");
        if (label == "Disconnect server") Capture(dialog, "disconnect-confirmation.png");
        Click(dialog, dialog.GetLogicalDescendants().OfType<Button>().Single(b => Equals(b.Content, label)));
    }
    private static async Task SelectAsync(Lab fixture, int index)
    {
        var list = Field<ListBox>(fixture.Window, "ServersList");
        var model = (WorkspaceModel)fixture.Window.DataContext!;
        Click(fixture.Window, list.ContainerFromIndex(index)!);
        Assert.Equal(index, list.SelectedIndex);
        Assert.Equal(model.ServerRows[index].Id, model.SelectedProfile?.Id);
        await WaitState(fixture, fixture.Runtime.Running(model.SelectedProfile!));
    }
    private static async Task WaitState(Lab fixture, bool running)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        do { await fixture.Window.RefreshServersAsync(); var model = (WorkspaceModel)fixture.Window.DataContext!; Dispatcher.UIThread.RunJobs(); if (model.ServerRunning == running && !model.Busy) return; await Task.Delay(50, deadline.Token); } while (true);
    }

    private sealed class Lab : IAsyncDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "wyrmwatch-e2e-" + Guid.NewGuid().ToString("N"));
        public JsonStore Store => new(Path.Combine(Root, "workspace"));
        public DummyRuntime Runtime { get; } = new();
        public DummySteam Steam { get; } = new();
        public MainWindow Window { get; private set; } = null!;
        private WebApplication app = null!;
        private Uri address = null!;
        private readonly string secret = AccessPolicy.NewSecret();
        private readonly CancellationTokenSource stopping = new();
        private Task monitor = null!;
        private AgentClient connection = null!;
        public AgentClient Client()
        {
            var http = new HttpClient { BaseAddress = address, Timeout = TimeSpan.FromSeconds(15) };
            http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", secret);
            return new(Store.DirectoryPath, http);
        }
        public static async Task<Lab> StartAsync()
        {
            var lab = new Lab(); Directory.CreateDirectory(lab.Root);
            var manager = new ManagerHost(lab.Store, lab.Runtime, lab.Steam) { PersistentHost = true };
            lab.app = AgentServer.Create(manager, lab.secret); await lab.app.StartAsync();
            lab.address = new(lab.app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single());
            lab.monitor = manager.MonitorAsync(() => { }, lab.stopping.Token);
            Program.HeadlessTest = true; Program.Demo = false; Program.DataDirectory = lab.Store.DirectoryPath;
            lab.connection = lab.Client(); lab.Window = new(lab.connection); lab.Window.Show(); return lab;
        }
        public ServerProfile Existing(string name)
        {
            var folder = ServerCreationPlan.SuggestFolderName(name);
            var p = new ServerProfile { Name = name, InstallPath = Path.Combine(Root, folder), BackupPath = Path.Combine(Root, "backups", folder) };
            Directory.CreateDirectory(Path.Combine(p.SavedPath, "SaveGames"));
            File.WriteAllText(p.Launcher, "dummy launcher, never execute");
            WriteWorldSave(Path.Combine(p.SavedPath, "SaveGames", "beta.sav"));
            AtomicFile.Write(p.ConfigPath, "[/Script/Dominion.DedicatedServerSettings]\nOwnerId=dummy-owner\nServerName=" + name + "\nDefaultWorldName=Beta\nAdminPassword=dummy-private\nPort=7777\n");
            return p;
        }
        public async ValueTask DisposeAsync()
        {
            foreach (var dialog in Window.OwnedWindows.ToArray()) dialog.Close(); Window.Close();
            stopping.Cancel(); await monitor; await app.StopAsync(); await app.DisposeAsync(); connection.Dispose(); stopping.Dispose();
            if (!SafePaths.Within(Root, Path.GetTempPath()) || !Path.GetFileName(Root).StartsWith("wyrmwatch-e2e-")) throw new InvalidOperationException("Unsafe fixture cleanup");
            Directory.Delete(Root, true);
        }
    }
    private static void WriteWorldSave(string path)
    {
        using var body = new MemoryStream(); using var writer = new BinaryWriter(body, Encoding.UTF8, true);
        writer.Write(Encoding.ASCII.GetBytes("INFO")); writer.Write(4); writer.Write(1);
        writer.Write(Encoding.ASCII.GetBytes("GLOB")); writer.Write(12); writer.Write(new byte[12]);
        using var output = new BinaryWriter(File.Create(path)); output.Write(Encoding.ASCII.GetBytes("SAVE")); output.Write((int)body.Length); output.Write(body.ToArray());
    }
    private sealed class DummyRuntime : IServerRuntime
    {
        private readonly HashSet<string> running = [];
        public int Starts { get; private set; }
        public int Stops { get; private set; }
        public bool Running(ServerProfile p) { lock (running) return running.Contains(p.Id); }
        public Task<ServerSnapshot> InspectAsync(ServerProfile p, CancellationToken token = default) => Task.FromResult(ServerSnapshot.Offline with { Running = Running(p) });
        public Task StartAsync(ServerProfile p, CancellationToken token = default) { lock (running) { running.Add(p.Id); Starts++; } return Task.CompletedTask; }
        public Task StopAsync(ServerProfile p, CancellationToken token = default) { lock (running) { running.Remove(p.Id); Stops++; } return Task.CompletedTask; }
    }
    private sealed class DummySteam : ISteamClient
    {
        public bool FailNext { get; set; }
        public int Installs { get; private set; }
        public string? InstalledBuild(ServerProfile p) => File.Exists(p.Launcher) ? "fixture-build" : null;
        public Task<BuildStatus> CheckAsync(ServerProfile p, Action<string> log, CancellationToken token = default) => Task.FromResult(new BuildStatus(InstalledBuild(p), "fixture-build"));
        public Task InstallAsync(ServerProfile p, bool repair, Action<string> log, CancellationToken token = default)
        {
            Installs++; Directory.CreateDirectory(p.InstallPath);
            if (FailNext) { FailNext = false; File.WriteAllText(Path.Combine(p.InstallPath, "partial.txt"), "partial download"); throw new IOException("fixture download failed"); }
            File.WriteAllText(p.Launcher, "dummy launcher, never execute"); log("Dummy installation complete"); return Task.CompletedTask;
        }
    }
}
