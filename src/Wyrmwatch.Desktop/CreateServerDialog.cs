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
    private readonly TextBox parent = new() { Name = "CreateParentFolder" };
    private readonly TextBox folder = new() { Name = "CreateFolderName" };
    private readonly TextBox backupParent = new() { Name = "CreateBackupParent" };
    private readonly TextBox admin = new() { Name = "CreateAdminPassword", PasswordChar = '●', Text = ServerCreationPlan.GeneratePassword() };
    private readonly TextBox password = new() { Name = "CreateWorldPassword", PasswordChar = '●' };
    private readonly NumericUpDown port = new() { Name = "CreatePort", Value = 7777, Minimum = 1024, Maximum = 65535, FormatString = "0" };
    private readonly TextBlock stepLabel = Hint("");
    private readonly TextBlock summary = new() { Name = "CreateReview", TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock message = new() { Name = "CreateMessage", TextWrapping = TextWrapping.Wrap };
    private readonly ProgressBar progress = new() { IsIndeterminate = true, IsVisible = false, Height = 4 };
    private readonly Button next = new() { Name = "CreateNext", Content = "Continue", Classes = { "primary" } };
    private readonly Button back = new() { Content = "Back", IsVisible = false };
    private readonly Button cancel = new() { Content = "Cancel" };
    private readonly StackPanel[] pages;
    private readonly ScrollViewer scroll;
    private readonly IReadOnlyList<ServerProfile> connections;
    private readonly Func<ServerCreationPlan, Action<string>, Task> create;
    private ServerCreationPlan? reviewed;
    private int step;
    private bool working, finished;
    private string suggestedFolder;

    public CreateServerDialog(IReadOnlyList<ServerProfile> connections, Func<ServerCreationPlan, Action<string>, Task> create, string? home = null)
    {
        this.connections = connections; this.create = create;
        home ??= Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        parent.Text = Path.Combine(home, "WyrmwatchServers");
        backupParent.Text = Path.Combine(home, "WyrmwatchBackups");
        suggestedFolder = ServerCreationPlan.SuggestFolderName(serverName.Text!); folder.Text = suggestedFolder;
        Title = "Create Server"; Width = 700; Height = 760; MinWidth = 540; MinHeight = 480;
        WindowStartupLocation = WindowStartupLocation.CenterOwner; ShowInTaskbar = false;

        var reveal = new CheckBox { Content = "Show passwords" };
        reveal.IsCheckedChanged += (_, _) => admin.PasswordChar = password.PasswordChar = reveal.IsChecked == true ? '\0' : '●';
        var regenerate = new Button { Content = "Generate another admin password" };
        regenerate.Click += (_, _) => admin.Text = ServerCreationPlan.GeneratePassword();
        pages =
        [
            Page("Make room for a new world",
                Hint("Wyrmwatch will download the dedicated server and prepare its settings. Have an existing world? Cancel and choose Import existing server."),
                Field("Server name", serverName, "The name shown in your saved servers list."),
                Field("World name", world, "Players search for this exact name in the game's Public Worlds tab."),
                Field("Owner Player ID · required", owner, "Open Dragonwilds → Settings. Scroll to the bottom and use Copy beside your Player ID. This is not your Steam ID.")),
            Page("Folders & access",
                Field("Install parent folder", parent, "A populated parent such as C:\\Games is fine. A separate child folder below will hold this server."),
                Browse(parent, "Choose the parent folder for the new server"),
                Field("New server folder name", folder, "Use a new name to keep each server's installation and saves separate."),
                Field("Backup parent folder", backupParent, "Backups use a matching child folder here. Scheduled backups start off."),
                Browse(backupParent, "Choose the parent folder for backups"),
                Field("Game port · UDP", port, "7777 is the default. Internet players need this UDP port allowed through your firewall and router."),
                Field("Admin password · generated for you", admin, "Keep this private: it grants server administration. You can view or change it later in Settings."),
                regenerate,
                Field("World password · optional", password, "Share this with players. Leave blank to allow anyone who can reach the server to join."), reveal),
            Page("Ready to create?", summary,
                Hint("Create Server downloads the game through SteamCMD and writes these settings. The download may take several minutes. Your world is created when you first press Start; automation starts off."))
        ];
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
        back.Click += (_, _) => { if (!working && step > 0) { step--; reviewed = null; message.Text = ""; UpdatePage(); } };
        cancel.Click += (_, _) => Close();
        Closing += (_, e) => { if (working) e.Cancel = true; };
        UpdatePage();
    }

    private static TextBlock Hint(string text) => new() { Text = text, TextWrapping = TextWrapping.Wrap, Classes = { "muted" }, FontSize = 13 };
    private static StackPanel Field(string label, Control input, string help) => new() { Spacing = 6, Children = { new TextBlock { Text = label, FontWeight = FontWeight.SemiBold }, input, Hint(help) } };
    private static StackPanel Page(string title, params Control[] controls)
    {
        var panel = new StackPanel { Spacing = 18 };
        panel.Children.Add(new TextBlock { Text = title, FontSize = 24, FontWeight = FontWeight.SemiBold, TextWrapping = TextWrapping.Wrap });
        foreach (var control in controls) panel.Children.Add(control);
        return panel;
    }
    private Button Browse(TextBox field, string title)
    {
        var button = new Button { Content = "Choose folder…" };
        button.Click += async (_, _) =>
        {
            try
            {
                var path = (await StorageProvider.OpenFolderPickerAsync(new() { Title = title, AllowMultiple = false })).FirstOrDefault()?.TryGetLocalPath();
                if (path is not null) field.Text = path;
            }
            catch (Exception error) { message.Text = error.Message; }
        };
        return button;
    }
    private ServerCreationPlan ReadPlan() => new(serverName.Text ?? "", world.Text ?? "", owner.Text ?? "", admin.Text ?? "", password.Text ?? "",
        checked((int)(port.Value ?? 7777)), parent.Text?.Trim() ?? "", folder.Text ?? "", backupParent.Text?.Trim() ?? "");
    private void UpdatePage()
    {
        for (var i = 0; i < pages.Length; i++) pages[i].IsVisible = i == step;
        stepLabel.Text = $"STEP {step + 1} OF 3  ·  " + new[] { "WORLD & OWNER", "FOLDERS & ACCESS", "REVIEW" }[step];
        back.IsVisible = step > 0; next.Content = step == 2 ? "Create Server" : step == 1 ? "Review setup" : "Continue";
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
                var values = ReadPlan().Configuration;
                if (string.IsNullOrWhiteSpace(values["OwnerId"])) throw new ArgumentException("Paste your Player ID from the bottom of Dragonwilds Settings to continue.");
                _ = GameConfiguration.Merge("", values);
                step = 1; UpdatePage(); return;
            }
            if (step == 1)
            {
                var plan = ReadPlan(); await Task.Run(() => plan.Validate(connections)); reviewed = plan;
                var p = plan.Profile;
                summary.Text = $"Server: {p.Name}\nWorld: {plan.Configuration["DefaultWorldName"]}\nOwner Player ID: {plan.Configuration["OwnerId"]}\nUDP port: {p.Port}\n\nInstall: {p.InstallPath}\n\nSave data: {p.SavedPath}\n\nBackups: {p.BackupPath}\n\nAdmin password: set\nWorld access: {(string.IsNullOrEmpty(plan.Configuration["WorldPassword"]) ? "no password required" : "password required")}\n\nAfter setup: stopped · automatic updates off · scheduled backups off";
                step = 2; UpdatePage(); return;
            }
            if (reviewed is null) throw new InvalidOperationException("Review the setup before creating the server.");
            // Recheck immediately before submitting; another app may have populated the folder since review.
            await Task.Run(() => reviewed.Validate(connections));
            progress.IsVisible = true; next.Content = "Creating…";
            await create(reviewed, status => message.Text = status);
            finished = true;
            stepLabel.Text = "SETUP COMPLETE";
            ((TextBlock)pages[2].Children[0]).Text = "Your server is ready";
            summary.Text = $"{reviewed.Profile.Name} is ready and stopped.\n\n1. Close this window and press Start on Overview.\n2. In Dragonwilds, open Public Worlds and search for \"{reviewed.Configuration["DefaultWorldName"]}\".\n3. After your first session, create a backup from Backups. Enable schedules in Automation when ready.\n\nFor internet players, see Help & diagnostics for network setup. Your admin password is available in Settings.";
            message.Text = "Server downloaded and configured. No game process was started.";
        }
        catch (Exception error)
        {
            message.Text = error.Message;
            if (progress.IsVisible)
            {
                finished = true; stepLabel.Text = "SETUP NEEDS ATTENTION";
                ((TextBlock)pages[2].Children[0]).Text = "Setup did not finish";
                summary.Text = "Setup did not finish. Any downloaded files and saved connection have been kept.\n\nCheck Activity for details. If the download completed, finish the game settings in Settings. If it was interrupted, keep the partial folder and choose a new empty server folder when retrying.\n\nFolder: " + reviewed!.Profile.InstallPath;
            }
        }
        finally
        {
            working = false; progress.IsVisible = false;
            next.IsEnabled = back.IsEnabled = cancel.IsEnabled = true;
            foreach (var page in pages) page.IsEnabled = true;
            if (finished) { next.Content = "Close"; back.IsVisible = cancel.IsVisible = false; }
        }
    }
}
