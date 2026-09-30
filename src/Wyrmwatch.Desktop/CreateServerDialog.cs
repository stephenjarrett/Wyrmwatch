using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Wyrmwatch.Core;

namespace Wyrmwatch.Desktop;

public sealed record PreparedSetupActions(
    Func<CancellationToken, Task<IReadOnlyList<PreparedSetupCandidate>>> List,
    Func<string, CancellationToken, Task<RecoveredSetup>> Review,
    Func<RecoveredSetup, Action<string>, Task> Resume);

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
    private readonly StackPanel preparedReview = new() { Name = "PreparedSetupReview", Spacing = 12, IsVisible = false };
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
    private readonly PreparedSetupActions? preparedActions;
    private readonly CancellationTokenSource lifetime = new();
    private readonly ComboBox preparedPicker = new() { Name = "PreparedSetupPicker", HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly TextBlock preparedStatus = new() { Name = "PreparedSetupStatus", TextWrapping = TextWrapping.Wrap, Classes = { "muted" } };
    private readonly Button newSetup = new() { Name = "PreparedSetupNew", Content = "Setup options", IsVisible = false, Classes = { "subtle" } };
    private readonly Expander preparedRecovery = new() { Name = "PreparedSetupRecovery", Header = "Prepared setup recovery", HorizontalAlignment = HorizontalAlignment.Stretch, IsVisible = false };
    private RecoveredSetup? recoveredSetup;
    private bool closed, selectingPrepared;
    private int preparedReviewVersion;
    private ServerCreationPlan? reviewed;
    private WorldImportPlan? reviewedSource;
    private bool IsWorldImport => importWorld is not null;
    private int step;
    private bool working, finished;
    private string suggestedFolder;

    public CreateServerDialog(IReadOnlyList<ServerProfile> connections, Func<ServerCreationPlan, Action<string>, Task> create, string? home = null,
        Func<ServerCreationPlan, WorldImportPlan, Action<string>, Task>? importWorld = null, PreparedSetupActions? preparedActions = null)
    {
        this.connections = connections; this.create = create; this.importWorld = importWorld; this.preparedActions = preparedActions;
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
        pages[2].Children.Add(preparedReview);
        preparedRecovery.Content = new StackPanel { Spacing = 10, Children = { preparedStatus, preparedPicker } };
        var body = new StackPanel { Margin = new Thickness(28, 24, 28, 16), Spacing = 18, Children = { stepLabel, preparedRecovery, pageGrid, progress, message } };
        scroll = new ScrollViewer { Content = body, HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled };
        var footer = new StackPanel { Margin = new Thickness(28, 12, 28, 24), Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Spacing = 10, Children = { cancel, newSetup, back, next } };
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
        Closing += (_, e) => { if (working && progress.IsVisible) e.Cancel = true; };
        Closed += (_, _) => { closed = true; lifetime.Cancel(); lifetime.Dispose(); };
        Opened += async (_, _) => await LoadPreparedSetupsAsync();
        preparedPicker.SelectionChanged += async (_, _) => { if (!selectingPrepared && preparedPicker.SelectedItem is PreparedChoice choice) await SelectPreparedSetupAsync(choice.InstallPath); };
        newSetup.Click += (_, _) => ClearPreparedSetup();
        UpdatePage();
    }

    private static TextBlock Hint(string text) => new() { Text = text, TextWrapping = TextWrapping.Wrap, Classes = { "muted" }, FontSize = 13 };
    private sealed record PreparedChoice(PreparedSetupCandidate Candidate)
    {
        public string InstallPath => Candidate.InstallPath;
        public override string ToString() => Candidate.Name + " (" + Candidate.StorageName + ")";
    }
    private async Task LoadPreparedSetupsAsync()
    {
        if (preparedActions is null || closed) return;
        try
        {
            var candidates = await preparedActions.List(lifetime.Token);
            if (closed) return;
            var managedParent = parent.Text!;
            var choices = candidates.Where(p => SafePaths.Same(Path.GetDirectoryName(p.InstallPath)!, managedParent)).Take(100).Select(p => new PreparedChoice(p)).ToArray();
            selectingPrepared = true;
            try { preparedPicker.ItemsSource = choices; preparedPicker.SelectedIndex = -1; }
            finally { selectingPrepared = false; }
            preparedRecovery.IsVisible = choices.Length > 0;
            preparedStatus.Text = "Setup files were prepared but their connection was not saved. Select a setup to verify its files and review the original settings. Resuming does not download, copy or start the game.";
            preparedRecovery.IsExpanded = choices.Length > 0;
        }
        catch (OperationCanceledException) when (closed) { }
        catch (Exception error) { if (!closed) { preparedRecovery.IsVisible = true; preparedStatus.Text = "Could not check prepared setups: " + error.Message; } }
    }
    private async Task ApplyPreparedSetupAsync(string installPath)
    {
        if (preparedActions is null) throw new IOException("Prepared setup recovery is unavailable.");
        var version = ++preparedReviewVersion;
        var recovered = await preparedActions.Review(installPath, lifetime.Token);
        if (closed || version != preparedReviewVersion) return;
        if (!SafePaths.Same(Path.GetDirectoryName(recovered.Profile.InstallPath)!, parent.Text!)) throw new IOException("Prepared setup is outside the managed server location.");
        recoveredSetup = recovered;
        serverName.Text = recovered.Profile.Name; folder.Text = Path.GetFileName(recovered.Profile.InstallPath);
        world.Text = recovered.Configuration.GetValueOrDefault("DefaultWorldName", "");
        owner.Text = recovered.Configuration.GetValueOrDefault("OwnerId", "");
        admin.Text = recovered.Configuration.GetValueOrDefault("AdminPassword", "");
        password.Text = recovered.Configuration.GetValueOrDefault("WorldPassword", ""); port.Value = recovered.Profile.Port;
        source.Text = recovered.Source?.SourcePath ?? "";
        foreach (var field in new[] { serverName, folder, world, owner, admin, password, source }) field.IsReadOnly = true;
        port.IsEnabled = sourceStopped.IsEnabled = false;
        newSetup.IsVisible = true; preparedRecovery.IsVisible = false; preparedRecovery.IsExpanded = false;
        preparedStatus.Text = "This workspace's setup receipt and recorded files verified. Original settings are retained. Resume saves only the connection; the copied snapshot can be recovered even if its original source has changed or disappeared.";
        ShowPreparedReview(recovered);
        step = 2; UpdateLocations(); UpdatePage();
    }
    private async Task SelectPreparedSetupAsync(string installPath)
    {
        if (working || finished) return;
        working = true; next.IsEnabled = back.IsEnabled = preparedPicker.IsEnabled = newSetup.IsEnabled = false;
        try { await ApplyPreparedSetupAsync(installPath); }
        catch (Exception error) { if (!closed) message.Text = error.Message; }
        finally { working = false; if (!closed) { next.IsEnabled = preparedPicker.IsEnabled = newSetup.IsEnabled = true; UpdatePage(); } }
    }
    private void ClearPreparedSetup()
    {
        if (working || finished) return;
        ++preparedReviewVersion; recoveredSetup = null; reviewed = null; reviewedSource = null;
        preparedReview.IsVisible = false; summary.IsVisible = true; pages[2].Children[2].IsVisible = true;
        ((TextBlock)pages[2].Children[0]).Text = "Review setup";
        preparedRecovery.IsVisible = preparedRecovery.IsExpanded = preparedPicker.Items.Count > 0;
        foreach (var field in new[] { serverName, folder, world, owner, admin, password, source }) field.IsReadOnly = false;
        port.IsEnabled = sourceStopped.IsEnabled = true;
        serverName.Text = "My Dragonwilds server"; world.Text = "My World"; owner.Text = "";
        admin.Text = ServerCreationPlan.GeneratePassword(); password.Text = ""; source.Text = ""; port.Value = 7777;
        folder.Text = suggestedFolder = ServerCreationPlan.SuggestFolderName(serverName.Text);
        selectingPrepared = true; preparedPicker.SelectedIndex = -1; selectingPrepared = false;
        newSetup.IsVisible = false; message.Text = ""; step = 0; UpdatePage();
        ((TextBlock)pages[2].Children[2]).Text = IsWorldImport ? "Downloads a fresh server through SteamCMD and copies only the reviewed world file. The game stays stopped." : "Downloads through SteamCMD. The world is created on first start.";
    }
    private static StackPanel Field(string label, Control input, string? help = null)
    {
        var field = new StackPanel { Spacing = 6, Children = { new TextBlock { Text = label, FontWeight = FontWeight.SemiBold }, input } };
        if (help is not null) field.Children.Add(Hint(help));
        return field;
    }
    private void ShowPreparedReview(RecoveredSetup recovered)
    {
        summary.IsVisible = false; pages[2].Children[2].IsVisible = false;
        ((TextBlock)pages[2].Children[0]).Text = "Resume prepared setup";
        preparedReview.Children.Clear();
        var status = new StackPanel { Spacing = 6, Children =
        {
            new TextBlock { Name = "PreparedVerifiedStatus", Text = "VERIFIED · READY TO RESUME", Classes = { "eyebrow", "setup-status" } },
            new TextBlock { Text = "Your prepared files are safe to reconnect", FontWeight = FontWeight.SemiBold, FontSize = 17, TextWrapping = TextWrapping.Wrap },
            Hint("Resume saves the connection only. No download, world copy or game start. Automation stays off.")
        }};
        preparedReview.Children.Add(ReviewCard(status));
        var details = new StackPanel { Spacing = 8, Children =
        {
            new TextBlock { Text = "Server & world", Classes = { "section" } },
            ReviewDetail("Server", recovered.Profile.Name, "PreparedServerName"),
            ReviewDetail("World save", recovered.Source?.FileName ?? "Fresh world on first Start", "PreparedWorldName"),
            ReviewDetail("Owner Player ID", owner.Text ?? "", "PreparedOwnerId"),
            ReviewDetail("Game port", $"{recovered.Profile.Port} · UDP", "PreparedGamePort"),
            ReviewDetail("Access", string.IsNullOrEmpty(password.Text) ? "No world password · admin password retained" : "World & admin passwords retained", "PreparedAccess"),
            new TextBlock { Name = "PreparedWorldNote", Text = recovered.Source is null ? "Original settings are retained. The world will be created on first Start." : "Original source is not read or copied again. Check the copied world's game compatibility after Start.", TextWrapping = TextWrapping.Wrap, FontSize = 13, Classes = { "muted" } }
        }};
        preparedReview.Children.Add(ReviewCard(details));
        preparedReview.Children.Add(ReviewCard(new StackPanel { Spacing = 8, Children =
        {
            new TextBlock { Text = "Managed storage", Classes = { "section" } },
            ReviewDetail("Server files", recovered.Profile.InstallPath, "PreparedInstallPath", true),
            ReviewDetail("Save data", recovered.Profile.SavedPath, "PreparedSavedPath", true),
            ReviewDetail("Backups", recovered.Profile.BackupPath, "PreparedBackupPath", true)
        }}));
        preparedReview.IsVisible = true;
    }
    private static Border ReviewCard(Control content) => new() { Classes = { "card" }, Padding = new Thickness(16), Child = content };
    private static Grid ReviewDetail(string label, string value, string name, bool path = false)
    {
        var text = new TextBlock { Name = name, Text = value, TextWrapping = TextWrapping.Wrap, FontSize = path ? 12 : 14 };
        if (path) text.FontFamily = new FontFamily("Cascadia Mono, Consolas, monospace");
        ToolTip.SetTip(text, value);
        Grid.SetColumn(text, 1);
        return new Grid { ColumnDefinitions = new ColumnDefinitions("120,*"), Children = { Hint(label), text } };
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
        back.IsVisible = step > 0 && recoveredSetup is null; next.Content = recoveredSetup is not null ? "Resume prepared setup" : step == 2 ? (IsWorldImport ? "Import World" : "Create Server") : step == 1 ? "Review setup" : "Continue";
        scroll.Offset = default;
    }
    internal async Task AdvanceAsync()
    {
        if (working) return;
        if (finished) { Close(); return; }
        message.Text = "";
        working = true; next.IsEnabled = back.IsEnabled = cancel.IsEnabled = false;
        preparedPicker.IsEnabled = newSetup.IsEnabled = false;
        foreach (var page in pages) page.IsEnabled = false;
        try
        {
            if (recoveredSetup is null && preparedActions is not null)
            {
                var target = ReadPlan().Profile.InstallPath;
                if (await Task.Run(() => ManagedSetupRecovery.HasReceipt(target))) { await ApplyPreparedSetupAsync(target); return; }
            }
            if (recoveredSetup is not null)
            {
                var current = await preparedActions!.Review(recoveredSetup.Profile.InstallPath, lifetime.Token);
                if (current.ReceiptToken != recoveredSetup.ReceiptToken) throw new IOException("Prepared setup changed after review. Select it again before resuming.");
                progress.IsVisible = true; next.Content = "Resuming.";
                await preparedActions.Resume(current, text => message.Text = text);
                finished = true; stepLabel.Text = "SETUP RECOVERED";
                preparedReview.IsVisible = false; summary.IsVisible = true; newSetup.IsVisible = false;
                summary.Text = current.Profile.Name + " is connected and stopped. Its prepared world and settings were preserved. Close this window and Start only when ready; automation remains off.";
                message.Text = "Connection saved. No download, copy or game start occurred.";
                return;
            }
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
                preparedReview.IsVisible = false; summary.IsVisible = true;
                summary.Text = "Setup did not finish. Existing files were preserved. Check the message below and Activity for details. Reopen the wizard to review any prepared setup, or use Back to adjust a failed download.\n\nDestination: " + (reviewed?.Profile.InstallPath ?? recoveredSetup?.Profile.InstallPath);
            }
        }
        finally
        {
            working = false; progress.IsVisible = false;
            next.IsEnabled = back.IsEnabled = cancel.IsEnabled = true;
            preparedPicker.IsEnabled = newSetup.IsEnabled = true;
            foreach (var page in pages) page.IsEnabled = true;
            if (!finished && step == 2) next.Content = recoveredSetup is not null ? "Resume prepared setup" : IsWorldImport ? "Import World" : "Create Server";
            if (finished) { next.Content = "Close"; back.IsVisible = cancel.IsVisible = false; }
        }
    }
}
