using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Wyrmwatch.Core;

namespace Wyrmwatch.Desktop;

public sealed class ImportServerDialog : Window
{
    private readonly TextBox saved = new() { Name = "ImportSavedFolder" };
    private readonly TextBox backups = new() { Name = "ImportBackupFolder" };
    private readonly TextBlock details = new() { TextWrapping = TextWrapping.Wrap };
    private readonly CheckBox confirmed = new() { Content = "This is the correct Saved folder for this server." };
    private readonly Button import = new() { Content = "Import server", Classes = { "primary" }, IsEnabled = false };
    private readonly string launcher;
    private readonly IReadOnlyList<ServerProfile> connections;
    private ServerProfile? reviewed;
    private int revision;

    public ImportServerDialog(string launcher, IReadOnlyList<ServerProfile> connections)
    {
        this.launcher = launcher; this.connections = connections;
        Title = "Import existing server"; Width = 620; SizeToContent = SizeToContent.Height;
        CanResize = false; ShowInTaskbar = false; WindowStartupLocation = WindowStartupLocation.CenterOwner;
        saved.Text = Path.Combine(Path.GetDirectoryName(launcher)!, "RSDragonwilds", "Saved");
        backups.Text = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "WyrmwatchBackups");
        var review = new Button { Content = "Review folders" };
        var cancel = new Button { Content = "Cancel" };
        var browseSaved = new Button { Content = "Browse…" };
        var browseBackups = new Button { Content = "Browse…" };
        browseSaved.Click += async (_, _) => await BrowseAsync(saved, "Choose this server's existing Saved folder");
        browseBackups.Click += async (_, _) => await BrowseAsync(backups, "Choose a backup folder outside all server folders");
        review.Click += async (_, _) => await ReviewAsync();
        cancel.Click += (_, _) => Close();
        import.Click += (_, _) => { if (reviewed is not null && confirmed.IsChecked == true) Close(reviewed); };
        saved.PropertyChanged += (_, change) => { if (change.Property == TextBox.TextProperty) InvalidateReview(); };
        backups.PropertyChanged += (_, change) => { if (change.Property == TextBox.TextProperty) InvalidateReview(); };
        confirmed.IsCheckedChanged += (_, _) => import.IsEnabled = reviewed is not null && confirmed.IsChecked == true;
        Content = new StackPanel
        {
            Margin = new Thickness(28), Spacing = 14, Children =
            {
                new TextBlock { Text = "Bring your existing world", FontSize = 24, FontWeight = FontWeight.SemiBold },
                new TextBlock { Text = "Connect in place. Your installation, saves, and game settings stay where they are. Automatic updates and backups start off.", TextWrapping = TextWrapping.Wrap },
                new TextBlock { Text = launcher, TextWrapping = TextWrapping.Wrap, Classes = { "muted" } },
                new TextBlock { Text = "Save-data folder (Saved)" }, saved, browseSaved,
                new TextBlock { Text = "Backup destination" }, backups, browseBackups,
                review, details, confirmed,
                new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Spacing = 10, Children = { cancel, import } }
            }
        };
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
        var data = saved.Text?.Trim() ?? ""; var backup = backups.Text?.Trim() ?? "";
        try
        {
            var profile = await Task.Run(() =>
            {
                var candidate = ExistingServerImport.Inspect(launcher, data, backup);
                ServerConnections.Validate(candidate, connections); return candidate;
            });
            if (current != revision) return;
            reviewed = profile;
            details.Text = $"{profile.Name} · Port {profile.Port}\nSave data: {profile.SavedPath}\nAutomation: off\nImporting does not start or stop the server.";
        }
        catch (Exception error) when (error is IOException or ArgumentException or UnauthorizedAccessException)
        { if (current == revision) details.Text = error.Message; }
    }
}
