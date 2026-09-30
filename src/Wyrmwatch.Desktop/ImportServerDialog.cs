using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Wyrmwatch.Core;

namespace Wyrmwatch.Desktop;

public sealed class ImportServerDialog : Window
{
    private readonly TextBox location = new() { Name = "ImportLocation" };
    private readonly TextBox saved = new() { Name = "ImportSavedFolder" };
    private readonly TextBox backups = new() { Name = "ImportBackupFolder" };
    private readonly TextBlock details = new() { TextWrapping = TextWrapping.Wrap };
    private readonly CheckBox confirmed = new() { Content = "This is the correct Saved folder for this server." };
    private readonly Button import = new() { Content = "Import server", Classes = { "primary" }, IsEnabled = false };
    private readonly IReadOnlyList<ServerProfile> connections;
    private ServerProfile? reviewed;
    private int revision;

    public ImportServerDialog(string launcher, IReadOnlyList<ServerProfile> connections)
    {
        this.connections = connections;
        Title = "Import existing server"; Width = 640; Height = 740; MinHeight = 480;
        ShowInTaskbar = false; WindowStartupLocation = WindowStartupLocation.CenterOwner;
        location.Text = launcher;
        saved.PlaceholderText = "Detected from the server folder when reviewed";
        saved.Text = string.IsNullOrEmpty(launcher) ? "" : Path.Combine(Path.GetDirectoryName(launcher)!, "RSDragonwilds", "Saved");
        backups.Text = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "WyrmwatchBackups");
        var review = new Button { Content = "Review folders" };
        var cancel = new Button { Content = "Cancel" };
        var browseInstallation = new Button { Content = "Choose server folder…" };
        var browseSaved = new Button { Content = "Browse…" };
        var browseBackups = new Button { Content = "Browse…" };
        browseSaved.Click += async (_, _) => await BrowseAsync(saved, "Choose this server's existing Saved folder");
        browseBackups.Click += async (_, _) => await BrowseAsync(backups, "Choose a backup folder outside all server folders");
        browseInstallation.Click += async (_, _) => await BrowseAsync(location, "Choose the existing server installation folder");
        review.Click += async (_, _) => await ReviewAsync();
        cancel.Click += (_, _) => Close();
        import.Click += (_, _) => { if (reviewed is not null && confirmed.IsChecked == true) Close(reviewed); };
        location.PropertyChanged += (_, change) => { if (change.Property == TextBox.TextProperty) { saved.Text = ""; InvalidateReview(); } };
        saved.PropertyChanged += (_, change) => { if (change.Property == TextBox.TextProperty) InvalidateReview(); };
        backups.PropertyChanged += (_, change) => { if (change.Property == TextBox.TextProperty) InvalidateReview(); };
        confirmed.IsCheckedChanged += (_, _) => import.IsEnabled = reviewed is not null && confirmed.IsChecked == true;
        var body = new StackPanel
        {
            Margin = new Thickness(28, 28, 28, 12), Spacing = 14, Children =
            {
                new TextBlock { Text = "Bring your existing world", FontSize = 24, FontWeight = FontWeight.SemiBold },
                new TextBlock { Text = "Connect in place. Your installation, saves, and game settings stay where they are. Automatic updates and backups start off.", TextWrapping = TextWrapping.Wrap },
                new TextBlock { Text = "Server installation folder or launcher path" }, location, browseInstallation,
                new TextBlock { Text = "Paste a folder or launcher path into the field above. Reviewing reads files without running the launcher.", TextWrapping = TextWrapping.Wrap, Classes = { "muted" } },
                new TextBlock { Text = "Save-data folder (Saved)" }, saved, browseSaved,
                new TextBlock { Text = "Backup destination" }, backups, browseBackups,
                review, details, confirmed
            }
        };
        var footer = new StackPanel { Margin = new Thickness(28, 12, 28, 20), Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Spacing = 10, Children = { cancel, import } };
        Grid.SetRow(footer, 1);
        Content = new Grid { RowDefinitions = new RowDefinitions("*,Auto"), Children = { new ScrollViewer { Content = body, HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled }, footer } };
        Opened += async (_, _) => await ReviewAsync();
    }

    private void InvalidateReview()
    {
        revision++; reviewed = null; confirmed.IsChecked = false; import.IsEnabled = false;
        details.Text = "Review these folders before importing.";
    }
    private async Task BrowseAsync(TextBox field, string title)
    {
        var folder = (await StorageProvider.OpenFolderPickerAsync(new() { Title = title, AllowMultiple = false })).FirstOrDefault()?.TryGetLocalPath();
        if (folder is not null) { field.Text = folder; await ReviewAsync(); }
    }
    internal async Task ReviewAsync()
    {
        InvalidateReview(); var current = revision;
        var selectedLocation = location.Text?.Trim() ?? "";
        var data = saved.Text?.Trim() ?? ""; var backup = backups.Text?.Trim() ?? "";
        try
        {
            var profile = await Task.Run(() =>
            {
                var launcher = ExistingServerImport.ResolveLauncher(selectedLocation);
                var savedPath = string.IsNullOrEmpty(data) ? Path.Combine(Path.GetDirectoryName(launcher)!, "RSDragonwilds", "Saved") : data;
                var candidate = ExistingServerImport.Inspect(launcher, savedPath, backup);
                ServerConnections.Validate(candidate, connections); return candidate;
            });
            if (current != revision) return;
            if (string.IsNullOrEmpty(data)) saved.Text = profile.SavedPath;
            reviewed = profile;
            details.Text = $"{profile.Name} · Port {profile.Port}\nLauncher: {profile.Launcher}\nSave data: {profile.SavedPath}\nAutomation: off\nImporting does not start or stop the server.";
        }
        catch (Exception error) when (error is IOException or ArgumentException or UnauthorizedAccessException)
        { if (current == revision) details.Text = error.Message; }
    }
}
