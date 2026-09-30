using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Wyrmwatch.Core;

namespace Wyrmwatch.Desktop;

public sealed class CreateServerDialog : Window
{
    private readonly TextBox serverName = new() { Name = "CreateServerName", Text = "My Dragonwilds server" };
    private readonly TextBox world = new() { Name = "CreateWorldName", Text = "My World" };
    private readonly TextBox owner = new() { Name = "CreateOwnerId", PlaceholderText = "Paste your Dragonwilds Player ID" };
    private readonly TextBox parent = new() { Name = "CreateParentFolder", IsReadOnly = true };
    private readonly TextBox folder = new() { Name = "CreateFolderName" };
    private readonly TextBox backupParent = new() { Name = "CreateBackupParent", IsReadOnly = true };
    private readonly TextBox source = new() { Name = "ImportWorldSource", PlaceholderText = "Select the world .sav file", TextWrapping = TextWrapping.Wrap };
    private readonly CheckBox sourceStopped = new() { Name = "ImportSourceStopped", Content = "I closed the game or server that writes this save." };
    private readonly TextBox locations = new() { Name = "CreateManagedLocations", IsReadOnly = true, TextWrapping = TextWrapping.Wrap };
    private readonly TextBox admin = new() { Name = "CreateAdminPassword", PasswordChar = '●', Text = ServerCreationPlan.GeneratePassword() };
    private readonly TextBox password = new() { Name = "CreateWorldPassword", PasswordChar = '●' };
    private readonly NumericUpDown port = new() { Name = "CreatePort", Value = 7777, Minimum = 1024, Maximum = 65535, FormatString = "0" };
    private readonly TextBlock stepLabel = Hint("");
    private readonly TextBlock summary = new() { Name = "CreateReview", TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock message = new() { Name = "CreateMessage", TextWrapping = TextWrapping.Wrap };
    private readonly ProgressBar progress = new() { IsIndeterminate = true, IsVisible = false, Height = 4 };
    private readonly Button next = new() { Name = "CreateNext", Content = "Continue", Classes = { "primary" } };
    private readonly Button back = new() { Name = "CreateBack", Content = "Back", IsVisible = false };
    private readonly Button cancel = new() { Name = "CreateCancel", Content = "Cancel" };
    private readonly StackPanel[] pages;
    private readonly ScrollViewer scroll;
    private readonly IReadOnlyList<ServerProfile> connections;
    private readonly Func<ServerCreationPlan, Action<string>, Task> create;
    private readonly Func<ServerCreationPlan, WorldImportPlan, Action<string>, Task>? importWorld;
    private ServerCreationPlan? reviewed;
    private WorldImportPlan? reviewedSource;
    private bool IsWorldImport => importWorld is not null;
    private int step;
    private bool working, finished;
    private string suggestedFolder;

    public CreateServerDialog(IReadOnlyList<ServerProfile> connections, Func<ServerCreationPlan, Action<string>, Task> create, string? home = null,
        Func<ServerCreationPlan, WorldImportPlan, Action<string>, Task>? importWorld = null)
    {
        this.connections = connections; this.create = create; this.importWorld = importWorld;
        home ??= Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        parent.Text = Path.Combine(home, "WyrmwatchServers");
        backupParent.Text = Path.Combine(home, "WyrmwatchBackups");
        suggestedFolder = ServerCreationPlan.SuggestFolderName(serverName.Text!); folder.Text = suggestedFolder;
        Title = IsWorldImport ? "Import World" : "Create Server"; Width = 700; Height = 760; MinWidth = 540; MinHeight = 480;
        WindowStartupLocation = WindowStartupLocation.CenterOwner; ShowInTaskbar = false;

        var reveal = new CheckBox { Content = "Show passwords" };
        reveal.IsCheckedChanged += (_, _) => admin.PasswordChar = password.PasswordChar = reveal.IsChecked == true ? '\0' : '●';
        var regenerate = new Button { Content = "Generate another admin password" };
        regenerate.Click += (_, _) => admin.Text = ServerCreationPlan.GeneratePassword();
        pages =
        [
            Page("World & owner",
                Field("Server name", serverName),
                Field(IsWorldImport ? "Fallback world name" : "World name", world, IsWorldImport ? "Used only if the server creates a fresh world. Import preserves the copied world's embedded name; this field does not rename it." : "Players search for this exact name in the game's Public Worlds tab."),
                Field("Owner Player ID · required", owner, "Open Dragonwilds → Settings. Scroll to the bottom and use Copy beside your Player ID. This is not your Steam ID.")),
            Page("Access & storage",
                Field("Game port · UDP", port, "7777 is the default. Internet players need this UDP port allowed through your firewall and router."),
                Field("Admin password · generated", admin, "Keep this private. You can view or change it in Server settings."),
                regenerate,
                Field("World password · optional", password, "Share this with players. Leave blank to allow anyone who can reach the server to join."), reveal,
                Field("Storage name", folder, "A new, separate connection folder managed by Wyrmwatch. Existing folders cannot be reused."),
                Field("Managed locations", locations, "Wyrmwatch keeps server files and backups in separate standard locations. Your source world is left in place.")),
            Page("Review setup", summary,
                Hint(IsWorldImport ? "Downloads a fresh server through SteamCMD and copies only the reviewed world file. The game stays stopped." : "Downloads through SteamCMD. The world is created on first start."))
        ];
        if (IsWorldImport)
        {
            pages[0].Children.Insert(1, Hint("Close Dragonwilds and stop the source server before selecting its world save. Import copies one .sav file into a new connection; character files, configuration, passwords and backups are not copied."));
            pages[0].Children.Insert(2, Field("World save (.sav)", source));
            pages[0].Children.Insert(3, BrowseWorld());
            pages[0].Children.Insert(4, sourceStopped);
        }
        folder.PropertyChanged += (_, change) => { if (change.Property == TextBox.TextProperty) UpdateLocations(); };
        source.PropertyChanged += (_, change) => { if (change.Property == TextBox.TextProperty) { sourceStopped.IsChecked = false; reviewedSource = null; ToolTip.SetTip(source, source.Text); } };
        UpdateLocations();
        var pageGrid = new Grid(); foreach (var page in pages) pageGrid.Children.Add(page);
        var body = new StackPanel { Margin = new Thickness(28, 24, 28, 16), Spacing = 18, Children = { stepLabel, pageGrid, progress, message } };
        scroll = new ScrollViewer { Content = body, HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled };
        var footer = new StackPanel { Margin = new Thickness(28, 12, 28, 24), Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Spacing = 10, Children = { cancel, back, next } };
        Grid.SetRow(footer, 1);
        Content = new Grid { RowDefinitions = new RowDefinitions("*,Auto"), Children = { scroll, footer } };
        serverName.PropertyChanged += (_, change) =>
        {
            if (change.Property != TextBox.TextProperty) return;
            var suggestion = ServerCreationPlan.SuggestFolderName(serverName.Text ?? "");
            if (folder.Text == suggestedFolder) folder.Text = suggestion;
            suggestedFolder = suggestion;
        };
        next.Click += async (_, _) => await AdvanceAsync();
        back.Click += (_, _) => { if (!working && step > 0) { step--; reviewed = null; reviewedSource = null; message.Text = ""; UpdatePage(); } };
        cancel.Click += (_, _) => Close();
        Closing += (_, e) => { if (working) e.Cancel = true; };
        UpdatePage();
    }

    private static TextBlock Hint(string text) => new() { Text = text, TextWrapping = TextWrapping.Wrap, Classes = { "muted" }, FontSize = 13 };
    private static StackPanel Field(string label, Control input, string? help = null)
    {
        var field = new StackPanel { Spacing = 6, Children = { new TextBlock { Text = label, FontWeight = FontWeight.SemiBold }, input } };
        if (help is not null) field.Children.Add(Hint(help));
        return field;
    }
    private static StackPanel Page(string title, params Control[] controls)
    {
        var panel = new StackPanel { Spacing = 18 };
        panel.Children.Add(new TextBlock { Text = title, FontSize = 24, FontWeight = FontWeight.SemiBold, TextWrapping = TextWrapping.Wrap });
        foreach (var control in controls) panel.Children.Add(control);
        return panel;
    }
    private void UpdateLocations()
    {
        try { var p = ReadPlan().Profile; locations.Text = $"Server: {p.InstallPath}\nSaves: {p.SavedPath}\nBackups: {p.BackupPath}"; }
        catch (ArgumentException) { locations.Text = "Enter a valid storage name to preview the managed locations."; }
    }
    private Button BrowseWorld()
    {
        var button = new Button { Name = "ImportBrowseWorld", Content = "Choose world save…" };
        button.Click += async (_, _) =>
        {
            try
            {
                var path = (await StorageProvider.OpenFilePickerAsync(new() { Title = "Choose the stopped world's .sav file", AllowMultiple = false, FileTypeFilter = [new FilePickerFileType("Dragonwilds world save") { Patterns = ["*.sav"] }] })).FirstOrDefault()?.TryGetLocalPath();
                if (path is not null) source.Text = path;
            }
            catch (Exception error) { message.Text = error.Message; }
        };
        return button;
    }
    private ServerCreationPlan ReadPlan() => new(serverName.Text ?? "", world.Text ?? "", owner.Text ?? "", admin.Text ?? "", password.Text ?? "",
        checked((int)(port.Value ?? 7777)), parent.Text?.Trim() ?? "", folder.Text ?? "", backupParent.Text?.Trim() ?? "");
    private Task<WorldImportPlan> InspectSourceAsync()
    {
        var selectedSource = source.Text?.Trim() ?? "";
        return Task.Run(() => WorldImport.InspectAsync(selectedSource));
    }
    private void UpdatePage()
    {
        for (var i = 0; i < pages.Length; i++) pages[i].IsVisible = i == step;
        stepLabel.Text = $"STEP {step + 1} OF 3  ·  " + new[] { "WORLD & OWNER", "ACCESS & STORAGE", "REVIEW" }[step];
        back.IsVisible = step > 0; next.Content = step == 2 ? (IsWorldImport ? "Import World" : "Create Server") : step == 1 ? "Review setup" : "Continue";
        scroll.Offset = default;
    }
    internal async Task AdvanceAsync()
    {
        if (working) return;
        if (finished) { Close(); return; }
        message.Text = "";
        working = true; next.IsEnabled = back.IsEnabled = cancel.IsEnabled = false;
        foreach (var page in pages) page.IsEnabled = false;
        try
        {
            if (step == 0)
            {
                // Validate the required game fields before moving away from their help text.
                var values = new Dictionary<string, string> { ["OwnerId"] = owner.Text?.Trim() ?? "", ["ServerName"] = serverName.Text?.Trim() ?? "", ["DefaultWorldName"] = world.Text?.Trim() ?? "", ["AdminPassword"] = admin.Text ?? "", ["WorldPassword"] = password.Text ?? "", ["Port"] = "7777" };
                if (string.IsNullOrWhiteSpace(values["OwnerId"])) throw new ArgumentException("Paste your Player ID from the bottom of Dragonwilds Settings to continue.");
                _ = GameConfiguration.Merge("", values);
                if (IsWorldImport)
                {
                    if (sourceStopped.IsChecked != true) throw new ArgumentException("Confirm that you closed the game or server that writes this save before continuing.");
                    reviewedSource = await InspectSourceAsync();
                }
                step = 1; UpdatePage(); return;
            }
            if (step == 1)
            {
                var plan = ReadPlan(); await Task.Run(() => plan.Validate(connections)); reviewed = plan;
                if (IsWorldImport) reviewedSource = await InspectSourceAsync();
                var p = plan.Profile;
                summary.Text = $"Server: {p.Name}\nWorld: {plan.Configuration["DefaultWorldName"]}\nOwner Player ID: {plan.Configuration["OwnerId"]}\nUDP port: {p.Port}\n\nInstall: {p.InstallPath}\n\nSave data: {p.SavedPath}\n\nBackups: {p.BackupPath}\n\nAdmin password: set\nWorld access: {(string.IsNullOrEmpty(plan.Configuration["WorldPassword"]) ? "no password required" : "password required")}\n\nAfter setup: stopped · automatic updates off · scheduled backups off";
                if (IsWorldImport) summary.Text = $"Source: {reviewedSource!.FileName}\n{reviewedSource.Length:N0} bytes — world container checked\n\nThe copied world keeps its embedded name; fallback name is only for a fresh world. Game compatibility must be verified after Start.\n\n" + summary.Text.Replace("World:", "Fallback world name:");
                step = 2; UpdatePage(); return;
            }
            if (reviewed is null) throw new InvalidOperationException("Review the setup before creating the server.");
            // Recheck immediately before submitting; another app may have populated the folder since review.
            await Task.Run(() => reviewed.Validate(connections));
            if (IsWorldImport)
            {
                if (sourceStopped.IsChecked != true || reviewedSource is null) throw new ArgumentException("Review the stopped world source before importing.");
                var current = await InspectSourceAsync();
                if (current.SourcePath != reviewedSource.SourcePath || current.Length != reviewedSource.Length || current.LastWriteUtc != reviewedSource.LastWriteUtc || current.Sha256 != reviewedSource.Sha256) throw new IOException("The source world changed after review. Use Back, close its writer, and review it again. No files were copied.");
            }
            progress.IsVisible = true; next.Content = "Creating…";
            if (IsWorldImport) await importWorld!(reviewed, reviewedSource!, status => message.Text = status);
            else await create(reviewed, status => message.Text = status);
            finished = true;
            stepLabel.Text = "SETUP COMPLETE";
            ((TextBlock)pages[2].Children[0]).Text = "Your server is ready";
            summary.Text = $"{reviewed.Profile.Name} is ready and stopped.\n\n1. Close this window and press Start on Servers.\n2. In Dragonwilds, open Public Worlds and search for \"{reviewed.Configuration["DefaultWorldName"]}\".\n3. After your first session, create a backup. Enable schedules when ready.\n\nYour admin password is available in Edit server.";
            if (IsWorldImport) summary.Text = $"{reviewed.Profile.Name} is ready and stopped.\n\nThe reviewed world file was copied into this new connection; your source was left in place. Its embedded world name is preserved.\n\n1. Close this window and Start when ready.\n2. Verify the imported world in Dragonwilds before enabling schedules. Container checks do not prove in-game compatibility.\n3. Create a backup after verifying the world.\n\nYour admin password is available in Edit server.";
            message.Text = IsWorldImport ? "World file copied and configuration written. No game process was started." : "Server downloaded and configured. No game process was started.";
        }
        catch (Exception error)
        {
            message.Text = error.Message;
            if (progress.IsVisible)
            {
                stepLabel.Text = "SETUP NEEDS ATTENTION";
                ((TextBlock)pages[2].Children[0]).Text = "Setup did not finish";
                summary.Text = "Setup did not finish. Existing files were preserved. Check the message below and Activity for details. You can retry here, or use Back to adjust the setup.\n\nDestination: " + reviewed!.Profile.InstallPath;
            }
        }
        finally
        {
            working = false; progress.IsVisible = false;
            next.IsEnabled = back.IsEnabled = cancel.IsEnabled = true;
            foreach (var page in pages) page.IsEnabled = true;
            if (!finished && step == 2) next.Content = IsWorldImport ? "Import World" : "Create Server";
            if (finished) { next.Content = "Close"; back.IsVisible = cancel.IsVisible = false; }
        }
    }
}
