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
    private readonly TextBox saved = new() { Name = "ImportSavedFolder", IsReadOnly = true };
    private readonly CheckBox customSaved = new() { Name = "ImportCustomSaved", Content = "Use a different existing Saved folder" };
    private readonly TextBox backups = new() { Name = "ImportBackupFolder" };
    private readonly TextBlock details = new() { Name = "ImportDetails", Text = "Choose the folder containing RSDragonwildsServer.", TextWrapping = TextWrapping.Wrap };
    private readonly CheckBox confirmed = new() { Name = "ImportConfirmed", Content = "This is the correct Saved folder for this server." };
    private readonly Button import = new() { Name = "ImportAccept", Content = "Import server", Classes = { "primary" }, IsEnabled = false };
    private readonly Func<ServerProfile, Task>? submit;
    private bool working, finished, fillingSaved;
    private StackPanel body = null!;
    private Button review = null!, cancel = null!;
    private readonly IReadOnlyList<ServerProfile> connections;
    private ServerProfile? reviewed;
    private int revision;

    public ImportServerDialog(string launcher, IReadOnlyList<ServerProfile> connections, Func<ServerProfile, Task>? submit = null)
    {
        this.connections = connections; this.submit = submit;
        Title = "Import existing server"; Width = 640; Height = 740; MinHeight = 480;
        ShowInTaskbar = false; WindowStartupLocation = WindowStartupLocation.CenterOwner;
        location.Text = launcher;
        saved.PlaceholderText = "Detected from the server folder when reviewed";
        saved.Text = string.IsNullOrEmpty(launcher) ? "" : Path.Combine(Path.GetDirectoryName(launcher)!, "RSDragonwilds", "Saved");
        backups.Text = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "WyrmwatchBackups");
        review = new Button { Name = "ImportReview", Content = "Review folders" };
        cancel = new Button { Name = "ImportCancel", Content = "Cancel" };
        var browseInstallation = new Button { Content = "Choose server folder…" };
        var browseSaved = new Button { Name = "ImportBrowseSaved", Content = "Locate existing Saved folder…", IsVisible = false };
        var browseBackups = new Button { Content = "Browse…" };
        browseSaved.Click += async (_, _) => await BrowseAsync(saved, "Choose this server's existing Saved folder");
        browseBackups.Click += async (_, _) => await BrowseAsync(backups, "Choose a backup folder outside all server folders");
        browseInstallation.Click += async (_, _) => await BrowseAsync(location, "Choose the existing server installation folder");
        review.Click += async (_, _) => await ReviewAsync();
        cancel.Click += (_, _) => Close();
        import.Click += async (_, _) => await SubmitAsync();
        location.PropertyChanged += (_, change) => { if (change.Property == TextBox.TextProperty) { customSaved.IsChecked = false; saved.Text = ""; InvalidateReview(); } };
        customSaved.IsCheckedChanged += (_, _) =>
        {
            var enabled = customSaved.IsChecked == true;
            saved.IsReadOnly = !enabled; browseSaved.IsVisible = enabled;
            if (!enabled) saved.Text = "";
            InvalidateReview();
        };
        saved.PropertyChanged += (_, change) => { if (change.Property == TextBox.TextProperty && !fillingSaved) InvalidateReview(); };
        backups.PropertyChanged += (_, change) => { if (change.Property == TextBox.TextProperty) InvalidateReview(); };
        confirmed.IsCheckedChanged += (_, _) => import.IsEnabled = !working && reviewed is not null && confirmed.IsChecked == true;
        body = new StackPanel
        {
            Margin = new Thickness(28, 28, 28, 12), Spacing = 14, Children =
            {
                new TextBlock { Text = "Import existing server", FontSize = 24, FontWeight = FontWeight.SemiBold },
                new TextBlock { Text = "Adds the server to your list without moving files or starting it.", TextWrapping = TextWrapping.Wrap },
                new TextBlock { Text = "Server installation folder or launcher path" }, location, browseInstallation,
                new TextBlock { Text = "Save-data folder (Saved)" }, saved,
                new Expander { Name = "ImportAdvanced", Header = "Advanced save location", HorizontalAlignment = HorizontalAlignment.Stretch, Content = new StackPanel { Spacing = 10, Children =
                {
                    customSaved,
                    new TextBlock { Text = "Use this only if the server already saves elsewhere. This does not move saves or change where the game writes them.", TextWrapping = TextWrapping.Wrap, Classes = { "muted" } },
                    browseSaved
                } } },
                new TextBlock { Text = "Backup destination" }, backups, browseBackups,
                review, details, confirmed
            }
        };
        var footer = new StackPanel { Margin = new Thickness(28, 12, 28, 20), Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Spacing = 10, Children = { cancel, import } };
        Grid.SetRow(footer, 1);
        Content = new Grid { RowDefinitions = new RowDefinitions("*,Auto"), Children = { new ScrollViewer { Content = body, HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled }, footer } };
        Closing += (_, e) => { if (working) e.Cancel = true; };
        Opened += async (_, _) => { if (!string.IsNullOrWhiteSpace(location.Text)) await ReviewAsync(); };
    }

    private void InvalidateReview()
    {
        revision++; reviewed = null; confirmed.IsChecked = false; import.IsEnabled = false;
        details.Text = "Review these folders before importing.";
    }
    private async Task BrowseAsync(TextBox field, string title)
    {
        try
        {
            var folder = (await StorageProvider.OpenFolderPickerAsync(new() { Title = title, AllowMultiple = false })).FirstOrDefault()?.TryGetLocalPath();
            if (folder is not null) { field.Text = folder; await ReviewAsync(); }
        }
        catch (Exception error) { details.Text = "Could not choose folder: " + error.Message; }
    }
    internal async Task ReviewAsync()
    {
        if (working || finished) return;
        InvalidateReview(); var current = revision;
        var selectedLocation = location.Text?.Trim() ?? "";
        var useCustom = customSaved.IsChecked == true;
        var data = useCustom ? saved.Text?.Trim() ?? "" : ""; var backup = backups.Text?.Trim() ?? "";
        try
        {
            working = true; body.IsEnabled = false; review.IsEnabled = cancel.IsEnabled = false;
            details.Text = "Reading server folders…";
            if (useCustom && string.IsNullOrEmpty(data)) throw new ArgumentException("Choose the server's existing Saved folder.");
            var result = await Task.Run(() =>
            {
                var launcher = ExistingServerImport.ResolveLauncher(selectedLocation);
                var savedPath = string.IsNullOrEmpty(data) ? Path.Combine(Path.GetDirectoryName(launcher)!, "RSDragonwilds", "Saved") : data;
                var candidate = ExistingServerImport.Inspect(launcher, savedPath, backup);
                ServerConnections.Validate(candidate, connections);
                var saveGames = Path.Combine(candidate.SavedPath, "SaveGames");
                SafePaths.NoLinks(saveGames);
                var worlds = Directory.Exists(saveGames) ? Directory.EnumerateFiles(saveGames, "*.sav").Select(Path.GetFileName).Order().Take(6).ToArray() : [];
                return (Profile: candidate, Worlds: worlds);
            });
            if (current != revision) return;
            var profile = result.Profile;
            fillingSaved = true; saved.Text = profile.SavedPath; fillingSaved = false;
            reviewed = profile;
            var worldsText = result.Worlds.Length == 0 ? "No .sav files found. Starting may create a new world." : "World saves: " + string.Join(", ", result.Worlds);
            details.Text = $"{profile.Name} · Port {profile.Port}\n{worldsText}\nAutomation: off";
        }
        catch (Exception error) when (error is IOException or ArgumentException or UnauthorizedAccessException)
        { if (current == revision) details.Text = error.Message; }
        finally { working = false; body.IsEnabled = review.IsEnabled = cancel.IsEnabled = true; }
    }
    private async Task SubmitAsync()
    {
        if (working) return;
        if (finished) { Close(); return; }
        if (reviewed is null || confirmed.IsChecked != true) return;
        if (submit is null) { Close(reviewed); return; }
        working = true; body.IsEnabled = false; import.IsEnabled = cancel.IsEnabled = false;
        try
        {
            details.Text = "Adding this server to Wyrmwatch…";
            await submit(reviewed);
            finished = true; import.Content = "Done"; cancel.IsVisible = false;
            details.Text = $"{reviewed.Name} added to Servers.\nAutomatic updates and backups are off.";
        }
        catch (Exception error) { details.Text = "Import did not finish: " + error.Message + "\nYour entries are kept. Review the folders again, then retry."; reviewed = null; confirmed.IsChecked = false; }
        finally { working = false; body.IsEnabled = !finished; cancel.IsEnabled = true; import.IsEnabled = finished; }
    }
}
